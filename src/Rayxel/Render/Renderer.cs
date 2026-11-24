using System.Drawing;
using System.Drawing.Imaging;
using SharpGen.Runtime;
using Rayxel.Diagnostics;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Rayxel.Render;

/// <summary>
/// D3D11 presenter: converts the captured YUV plane pair to RGB, letterboxes it into
/// the window, and presents through a waitable flip-model swapchain.
/// <para>
/// The latency-critical choices are all here — max frame latency of 1, waiting on the
/// swapchain's waitable object rather than free-running, tearing allowed, and no
/// composition of any kind over the window so DXGI can reach independent flip.
/// </para>
/// </summary>
public sealed class Renderer : IDisposable
{
    // 10-bit throughout. It costs nothing in SDR — the colour space, not the format, is
    // what switches the output between sRGB and PQ — and it is the only way RTX Video HDR
    // can produce anything: in an 8-bit sRGB swapchain the extra range has nowhere to go.
    private const Format BackBufferFormat = Format.R10G10B10A2_UNorm;

    /// <summary>True when Windows reports the output is currently in HDR mode.</summary>
    public bool DisplaySupportsHdr { get; private set; }

    /// <summary>The GPU this is running on, as DXGI names it.</summary>
    public string AdapterName { get; private set; } = "-";

    /// <summary>
    /// The user-mode driver version, formatted the way NVIDIA and Intel control panels
    /// show it. There is no DXGI field for "driver version" — this comes out of
    /// CheckInterfaceSupport, which reports the UMD's file version as a packed 64-bit
    /// number and is the only place it is exposed.
    /// </summary>
    public string DriverVersion { get; private set; } = "-";

    private void ReadAdapterInfo()
    {
        try
        {
            using var dxgiDevice = _device.QueryInterfaceOrNull<IDXGIDevice>();
            if (dxgiDevice is null) return;

            using var adapter = dxgiDevice.GetAdapter();
            AdapterName = adapter.Description.Description.Trim();

            using var adapter1 = adapter.QueryInterfaceOrNull<IDXGIAdapter1>();
            if (adapter1 is null) return;

            adapter1.CheckInterfaceSupport<IDXGIDevice>(out long umd);
            if (umd > 0)
            {
                // Packed as four 16-bit fields, most significant first.
                ulong v = (ulong)umd;
                DriverVersion = $"{(v >> 48) & 0xFFFF}.{(v >> 32) & 0xFFFF}." +
                                $"{(v >> 16) & 0xFFFF}.{v & 0xFFFF}";
            }
        }
        catch (Exception ex) { Log.Debug($"adapter info unavailable: {ex.Message}"); }
    }

    /// <summary>True while the swapchain is presenting in PQ / BT.2020.</summary>
    public bool HdrOutput { get; private set; }

    private ID3D11Device _device = null!;
    private ID3D11DeviceContext _context = null!;
    private IDXGISwapChain1 _swapChain = null!;
    private IDXGISwapChain2? _swapChain2;
    private ID3D11RenderTargetView _backBufferView = null!;

    private ID3D11VertexShader _vs = null!;
    private ID3D11PixelShader _ps = null!;
    private ID3D11Buffer _constants = null!;
    private ID3D11SamplerState _sampler = null!;
    private ID3D11BlendState _alphaBlend = null!;

    // Video plane pair, recreated whenever the capture format changes.
    private ID3D11Texture2D? _videoTexture;
    private ID3D11Texture2D? _vpTexture;
    private ID3D11ShaderResourceView? _lumaView;
    private ID3D11ShaderResourceView? _chromaView;
    private int _videoWidth, _videoHeight;
    private Format _videoFormat = Format.Unknown;

    private ID3D11Texture2D? _hudTexture;
    private ID3D11ShaderResourceView? _hudView;
    private int _hudWidth, _hudHeight;

    // GPU timing, three deep so reading last frame's result never stalls the pipeline.
    private const int QueryDepth = 3;
    private readonly ID3D11Query[] _disjoint = new ID3D11Query[QueryDepth];
    private readonly ID3D11Query[] _tsStart = new ID3D11Query[QueryDepth];
    private readonly ID3D11Query[] _tsEnd = new ID3D11Query[QueryDepth];
    private long _queryFrame;

    /// <summary>GPU milliseconds for the last completed render pass. This is the budget an upscaler eats into.</summary>
    public double LastGpuMs { get; private set; }

    /// <summary>QPC timestamp of the most recent vblank DXGI reported, or 0 if unavailable.</summary>
    public long LastScanoutQpc { get; private set; }

    /// <summary>
    /// DXGI's count of display refreshes at <see cref="LastScanoutQpc"/>. Two readings a
    /// second apart give the rate the display actually ran at, which is how VRR shows up.
    /// </summary>
    public uint LastSyncRefreshCount { get; private set; }

    /// <summary>
    /// The driver's video processor, which is where RTX Video HDR and RTX Video Super
    /// Resolution live: neither can be applied to a texture, only switched on for a video
    /// stream. Null until <see cref="Initialize"/> runs. While either is on, frames go
    /// through VideoProcessorBlt instead of our own shader.
    /// </summary>
    public VideoProcessor? Processor { get; private set; }

    /// <summary>
    /// True while a frame should be routed through the video processor: RTX Video HDR or
    /// RTX VSR is on, and the source is an SDR one for them to work on. An HDR source is
    /// never the driver's - shown as it is, it is ours to pass through untouched, and
    /// converted to SDR the driver's conversion clips everything above about 100 nits to
    /// white (measured on the HDR test pattern).
    /// </summary>
    public bool UseProcessor => Processor is { Available: true } p && (p.HdrEnabled || p.SuperResolutionEnabled) && !SourceIsHdr;

    /// <summary>True while RTX Video Super Resolution has the frame: the driver enlarges it on the processor's path.</summary>
    public bool UseVsr => UseProcessor && Processor!.SuperResolutionEnabled;

    /// <summary>DLSS Super Resolution, fed by <see cref="Flow"/>. Null until <see cref="Initialize"/> runs.</summary>
    public DlssScaler? Dlss { get; private set; }

    /// <summary>Requested by the user; see <see cref="UseDlss"/> for whether it is what runs.</summary>
    public bool DlssEnabled { get; set; }

    /// <summary>
    /// True while DLSS has the frame: asked for, the driver's path not in the way, and
    /// not yet found to be impossible here.
    /// </summary>
    public bool UseDlss => DlssEnabled && !UseProcessor && Dlss is { Possible: true };

    /// <summary>
    /// GPU milliseconds the last DLSS pass took, with the colour conversion and the
    /// spreading of the vectors that feed it. Part of <see cref="LastGpuMs"/>, not on top of it.
    /// </summary>
    public double LastDlssMs { get; private set; }

    /// <summary>AMD's FSR, fed the same way as DLSS. Null until <see cref="Initialize"/> runs.</summary>
    public FsrScaler? Fsr { get; private set; }

    /// <summary>Requested by the user; see <see cref="UseFsr"/> for whether it is what runs.</summary>
    public bool FsrEnabled { get; set; }

    /// <summary>
    /// True while FSR has the frame: asked for, neither DLSS nor the driver's path in the
    /// way, and its library found and not yet seen to fail.
    /// </summary>
    public bool UseFsr => FsrEnabled && !UseProcessor && !DlssEnabled && Fsr is { Possible: true };

    /// <summary>
    /// GPU milliseconds the last FSR pass took, counted as <see cref="LastDlssMs"/> is,
    /// and with the crossing to Direct3D 12 and back that FSR needs.
    /// </summary>
    public double LastFsrMs { get; private set; }

    /// <summary>
    /// Keeps whichever of DLSS and FSR is not on screen fed with the same frames as the
    /// one that is, so that <see cref="SaveComparison"/> can show both for one frame.
    /// Each builds its picture from the frames before, and that cannot be made up
    /// afterwards. Costs the second scaler's whole time: for a comparison, not for watching.
    /// </summary>
    public bool CompareScalers { get; set; }

    private long _dlssSerial;
    private int _dlssSettling;
    private int _fsrShift;
    // Which of the two the last pass was for, and whether each timed pass was FSR's.
    private bool _temporalWasFsr;
    private readonly bool[] _fsrTimed = new bool[QueryDepth];
    private readonly ID3D11Query[] _tsDlssStart = new ID3D11Query[QueryDepth];
    private readonly ID3D11Query[] _tsDlssEnd = new ID3D11Query[QueryDepth];
    private readonly bool[] _dlssTimed = new bool[QueryDepth];

    // DLSS settles over several frames. When the picture stops arriving - a source that
    // only sends what changed - the last one is put through this many times more, as
    // "nothing moved", so that what stays on screen is the settled result.
    private const int DlssSettleFrames = 8;

    // How many times over a network is shown the one frame before SaveComparison takes its
    // picture of it.
    private const int ComparisonStillFrames = 24;

    /// <summary>
    /// Loads DLSS and finds out whether it can run here, ahead of the first frame that
    /// needs it. The reason is in <see cref="DlssScaler.LastError"/> when it cannot.
    /// </summary>
    public bool StartDlss()
    {
        if (Dlss is not { } dlss) return false;
        lock (_context) return dlss.Start();
    }

    /// <summary>The same for FSR; the reason is in <see cref="FsrScaler.LastError"/>.</summary>
    public bool StartFsr()
    {
        if (Fsr is not { } fsr) return false;
        lock (_context) return fsr.Start();
    }

    /// <summary>
    /// Motion estimated between consecutive frames. It only runs while something asks for
    /// it; see <see cref="OpticalFlow.View"/>.
    /// </summary>
    public OpticalFlow? Flow { get; private set; }

    /// <summary>
    /// <c>--fg-probe</c>: frame generation tried on the real frames as they arrive; see
    /// <see cref="FrameGenProbe"/>. Null otherwise.
    /// </summary>
    public FrameGenProbe? FrameGenProbe { get; set; }
    private long _probeSerial;

    // ── frame generation ────────────────────────────────────────────────────────

    /// <summary>DLSS Frame Generation; see <see cref="DlssFrameGen"/>.</summary>
    public DlssFrameGen? FrameGen { get; private set; }

    /// <summary>Asked for by the user; see <see cref="UseFrameGen"/> for whether it runs.</summary>
    public bool FrameGenEnabled { get; set; }

    /// <summary>Frames shown for each real one: 2 to 4. Set by whoever knows the display's rate.</summary>
    public int FrameGenFactor { get; set; } = 2;

    /// <summary>When the newest real frame reached this process (QPC), set before each <see cref="Render"/>.</summary>
    public long FrameArrivalQpc { get; set; }

    /// <summary>The source's own frame interval in milliseconds, set before each <see cref="Render"/>.</summary>
    public double FrameIntervalMs { get; set; } = 1000.0 / 60;

    /// <summary>
    /// Why frame generation is asked for and does not run: a key of the interface's strings
    /// ("fgNoHdrAsIs", "fgNoRtxHdr") or a sentence of the generator's own. Null when it
    /// runs, or is not asked for.
    /// </summary>
    public string? FrameGenBlocked => !FrameGenEnabled ? null
        : FrameGen is not { Possible: true } ? FrameGen?.LastError ?? FrameGen?.Unsupported ?? "unavailable"
        // Generated frames are 8-bit SDR. A picture going out as HDR, or being made HDR by the
        // driver, would lose what it is being shown for.
        : PassingHdrThrough ? "fgNoHdrAsIs"
        : UseProcessor ? "fgNoRtxHdr"
        : null;

    public bool UseFrameGen => FrameGenEnabled && FrameGenBlocked is null && FrameGenFactor > 1;

    // The frames to show for the newest real one: the generated ones in order, then the
    // real one. While the real one has not been shown, a new frame waits - it was copied in
    // already, but is only put through once the one before it is on the screen.
    private readonly ID3D11ShaderResourceView?[] _fgQueue = new ID3D11ShaderResourceView?[DlssFrameGen.MaxGenerated + 1];
    private int _fgCount, _fgShown;
    private bool _fgRealShown = true;
    private long _fgSerial, _fgLastArrival, _fgFirstPresent;
    private long _fgSpacingTicks;
    private bool _fgWasOn;
    private readonly ID3D11Query[] _tsFgStart = new ID3D11Query[QueryDepth];
    private readonly ID3D11Query[] _tsFgEnd = new ID3D11Query[QueryDepth];
    private readonly bool[] _fgTimed = new bool[QueryDepth];
    private long _renderedSerial;

    /// <summary>
    /// Distinct pictures put on the screen so far: each new real frame, and with frame
    /// generation each generated one. A frame presented again - the display is faster than
    /// the source - does not count. Divided by time, what the screen actually shows.
    /// </summary>
    public long PicturesShown { get; private set; }

    private long _lastShownKey = -1;

    /// <summary>GPU milliseconds the last generating took, measured as the time the picture waited for it.</summary>
    public double LastFrameGenMs { get; private set; }

    /// <summary>
    /// What generation adds to the delay, in milliseconds: from the first frame shown for a
    /// real one to the real one itself. Measured from the presents, averaged.
    /// </summary>
    public double FrameGenDelayMs { get; private set; }

    /// <summary>When set, every frame's added delay is put here too: for --bench.</summary>
    public List<double>? FrameGenDelaySamples { get; set; }

    /// <summary>When set, every generating's GPU time is put here too: for --bench.</summary>
    public List<double>? FrameGenGpuSamples { get; set; }

    /// <summary>True when a frame has arrived that has not been put through yet.</summary>
    public bool HasUnrenderedFrame => _uploadSerial != _renderedSerial;

    /// <summary>
    /// When the next frame of the queue is due (QPC), or null when there is nothing left to
    /// show before the next real frame. The frames are spaced evenly across the time the
    /// source took between the two real frames they lie between.
    /// </summary>
    public long? FrameGenDue => UseFrameGen && _fgCount > 1 && _fgShown < _fgCount - 1 && _fgFirstPresent != 0
        ? _fgFirstPresent + (_fgShown + 1) * _fgSpacingTicks
        : null;

    /// <summary>Shows the next frame of the queue at the next <see cref="Render"/>.</summary>
    public void FrameGenAdvance()
    {
        if (_fgShown < _fgCount - 1) _fgShown++;
    }

    /// <summary>
    /// A new real frame came before the queue was through: the generated frames still to come
    /// are dropped and the real one is shown at once, so the new one does not wait behind them.
    /// </summary>
    public void FrameGenCatchUp()
    {
        if (_fgCount > 0) _fgShown = _fgCount - 1;
    }

    /// <summary>
    /// Says when the frame just rendered was presented. The first of a queue sets the
    /// schedule for the rest; the real one, last, says what the generated ones cost in delay.
    /// </summary>
    public void FrameGenPresented(long qpc)
    {
        if (!UseFrameGen || _fgCount < 2) return;
        if (_fgShown == 0) _fgFirstPresent = qpc;
        else if (_fgShown == _fgCount - 1 && _fgFirstPresent != 0)
        {
            double ms = (qpc - _fgFirstPresent) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            FrameGenDelayMs = FrameGenDelayMs == 0 ? ms : FrameGenDelayMs * 0.9 + ms * 0.1;
            FrameGenDelaySamples?.Add(ms);
            _fgFirstPresent = 0;
        }
    }

    /// <summary>Starts NGX for frame generation, on a thread of its own. Says why not when it cannot be had.</summary>
    public string? StartFrameGen()
    {
        if (FrameGen is not { } fg) return "unavailable";
        if (fg.Unsupported is { } why) return why;
        fg.StartInBackground();
        return null;
    }

    /// <summary>
    /// Puts the newest real frame through frame generation, and lines up what is to be shown
    /// for it: the generated frames first, then the frame itself. The picture is what would
    /// otherwise be drawn - an upscaler's output, or the frame converted to colour.
    /// </summary>
    private void BuildFrameGenQueue(int slot, ID3D11ShaderResourceView? upscaled)
    {
        _fgSerial = _uploadSerial;
        _fgCount = 0;
        _fgShown = 0;
        _fgRealShown = false;
        _fgFirstPresent = 0;

        ID3D11ShaderResourceView? picture = upscaled;
        if (picture is null)
        {
            if (!EnsureIntermediate(_videoWidth, _videoHeight)) return;
            BindPipeline();
            if (!ConvertYuvToIntermediate()) return;
            _context.OMSetRenderTargets((ID3D11RenderTargetView?)null);
            picture = _rgbIntermediateSrv;
        }
        if (picture is null) return;

        // Between two real frames lies the time the source took between them. Much more
        // than its own interval is a stall - a paused game, a lost packet, a mode change -
        // and nothing is generated across one: the frames on either side are not neighbours.
        long arrival = FrameArrivalQpc;
        double gapMs = _fgLastArrival != 0 ? (arrival - _fgLastArrival) * 1000.0 / System.Diagnostics.Stopwatch.Frequency : 0;
        _fgLastArrival = arrival;
        bool neighbours = gapMs > 0 && gapMs < FrameIntervalMs * 2.5;
        if (!neighbours) FrameGen!.Reset();

        int count = FrameGenFactor - 1;
        bool generated = false;
        var motion = neighbours && Flow is { Ready: true } flow ? flow.PerPixel(_context, _sampler) : null;
        if (motion is not null)
        {
            using var resource = picture.Resource;
            using var color = resource.QueryInterface<ID3D11Texture2D>();
            _context.End(_tsFgStart[slot]);
            generated = FrameGen!.Generate(_context, color, motion, _videoWidth, _videoHeight, count, reset: false);
            _context.End(_tsFgEnd[slot]);
            _fgTimed[slot] = generated;
        }

        if (generated)
        {
            for (int i = 0; i < count; i++) _fgQueue[_fgCount++] = FrameGen!.Output(i);
            // The spacing follows the gap this pair really had, within reason: a source's
            // frames come unevenly, and the motion between them took the time it took.
            double spacing = Math.Clamp(gapMs, FrameIntervalMs * 0.5, FrameIntervalMs * 1.5) / FrameGenFactor;
            _fgSpacingTicks = (long)(spacing * System.Diagnostics.Stopwatch.Frequency / 1000.0);
        }
        _fgQueue[_fgCount++] = picture;
        BindPipeline();
    }

    /// <summary>GPU milliseconds the last motion estimate took. Part of <see cref="LastGpuMs"/>, not on top of it.</summary>
    public double LastFlowMs { get; private set; }

    // Counts frames copied in, so the estimate runs once per new frame and never twice on
    // the same one when the window is redrawn without a new picture.
    private long _uploadSerial, _flowSerial;
    private readonly ID3D11Query[] _tsFlowStart = new ID3D11Query[QueryDepth];
    private readonly ID3D11Query[] _tsFlowEnd = new ID3D11Query[QueryDepth];
    private readonly bool[] _flowTimed = new bool[QueryDepth];

    // Intermediate RGB surface: DLSS and FSR consume colour, not YUV planes, so the
    // conversion shader has to run into a texture before either can see it.
    private ID3D11Texture2D? _rgbIntermediate;
    private ID3D11RenderTargetView? _rgbIntermediateRtv;
    private ID3D11ShaderResourceView? _rgbIntermediateSrv;

    /// <summary>
    /// The UI, drawn into this same swapchain. Null only if it failed to start, which
    /// costs the overlay and nothing else.
    /// </summary>
    public ImGuiLayer? Ui { get; private set; }

    private bool _uiFrameOpen;

    /// <summary>
    /// Level the overlay is encoded at while the swapchain is PQ. Only meaningful in HDR;
    /// in SDR the UI is written as ordinary sRGB and this does nothing.
    /// </summary>
    public float OverlayNits { get; set; } = 230f;

    /// <summary>
    /// Level the <em>video</em> is encoded at when our own shader converts it into a PQ
    /// swapchain. Separate from <see cref="OverlayNits"/> because they are different
    /// pictures with different jobs: the interface must stay dim enough not to burn an
    /// OLED, the video should look like the console intended.
    /// <para>
    /// Has no effect while RTX Video HDR is on — the driver produces the PQ values then and
    /// our shader does not touch them.
    /// </para>
    /// </summary>
    public float VideoNits { get; set; } = 200f;

    public IntPtr FrameLatencyWaitableObject { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool TearingSupported { get; private set; }
    public ID3D11Device Device => _device;

    /// <summary>Where the HUD sits, in client pixels. Clicking inside it opens the mode menu.</summary>
    public Rectangle HudRect => new(16, 16, _hudWidth, _hudHeight);

    public void Initialize(IntPtr hwnd, int width, int height)
    {
        Width = width; Height = height;

        using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        TearingSupported = CheckTearing(factory);

        var flags = DeviceCreationFlags.BgraSupport;
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
            out var device, out var level, out var context).CheckError();
        _device = device!;
        _context = context!;
        Log.Info($"D3D11 device created, feature level {level}");

        // Media Foundation hands samples to us from its own thread while we render,
        // so the device must serialise access internally.
        using (var mt = _device.QueryInterfaceOrNull<ID3D11Multithread>())
        {
            if (mt is not null) { mt.SetMultithreadProtected(true); Log.Debug("D3D11 multithread protection on"); }
            else Log.Warn("ID3D11Multithread unavailable — capture/render races possible");
        }

        var desc = new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = BackBufferFormat,
            BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
            Flags = SwapChainFlags.FrameLatencyWaitableObject
                    | (TearingSupported ? SwapChainFlags.AllowTearing : SwapChainFlags.None)
        };

        _swapChain = factory.CreateSwapChainForHwnd(_device, hwnd, desc);
        factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);

        _swapChain2 = _swapChain.QueryInterfaceOrNull<IDXGISwapChain2>();
        if (_swapChain2 is not null)
        {
            // One frame of latency is the whole point: never let DXGI queue ahead.
            _swapChain2.MaximumFrameLatency = 1;
            FrameLatencyWaitableObject = _swapChain2.FrameLatencyWaitableObject;
            Log.Info($"waitable swapchain: max frame latency 1, tearing={TearingSupported}");
        }
        else Log.Warn("IDXGISwapChain2 unavailable — running without a frame latency wait");

        CreateBackBufferView();
        CreatePipeline();
        Processor = new VideoProcessor(_device);
        Flow = new OpticalFlow(_device);
        Dlss = new DlssScaler(_device);
        FrameGen = new DlssFrameGen(_device);
        Fsr = new FsrScaler(_device);

        ReadAdapterInfo();
        Log.Info($"adapter: {AdapterName}, driver {DriverVersion}");

        DisplaySupportsHdr = DetectHdrDisplay();
        Log.Info($"display HDR: {(DisplaySupportsHdr ? "enabled in Windows" : "off or unsupported")}");
        ApplySwapChainColorSpace();

        var ui = new ImGuiLayer(_device);
        if (ui.Initialize(width, height)) Ui = ui;
        else ui.Dispose();
    }

    /// <summary>
    /// Opens a UI frame. Everything drawn between this and the next <see cref="Render"/>
    /// lands on that frame; skipping it simply means no overlay this frame.
    /// </summary>
    public bool BeginUi(float deltaSeconds)
    {
        if (Ui is null || !Ui.Available) return false;
        Ui.NewFrame(Width, Height, deltaSeconds);
        _uiFrameOpen = true;
        return true;
    }

    /// <summary>
    /// Asks DXGI what colour space the output is actually running in. This reports the
    /// current Windows HDR *state*, not merely panel capability — which is what we want:
    /// switching our swapchain to PQ while the desktop is in SDR would look wrong.
    /// </summary>
    private bool DetectHdrDisplay()
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
            for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
            {
                using (adapter)
                {
                    for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                    {
                        using (output)
                        using (var output6 = output.QueryInterfaceOrNull<IDXGIOutput6>())
                        {
                            if (output6 is null) continue;
                            var space = output6.Description1.ColorSpace;
                            if (space == ColorSpaceType.RgbFullG2084NoneP2020) return true;
                        }
                    }
                }
            }
        }
        catch (Exception ex) { Log.Warn($"HDR detection failed: {ex.Message}"); }
        return false;
    }

    /// <summary>
    /// Switches the swapchain between sRGB and PQ output. Refuses when Windows has the
    /// display in SDR: presenting PQ to an SDR desktop produces a washed-out mess.
    /// </summary>
    public bool SetHdrOutput(bool enable)
    {
        if (enable && !DisplaySupportsHdr)
        {
            Log.Warn("HDR output refused: Windows reports this display is not in HDR mode");
            return false;
        }

        // The swapchain colour space deliberately does NOT follow this toggle. While the
        // Windows desktop is in HDR the swapchain stays in PQ; flipping it back to sRGB
        // left the picture washed out with lifted blacks, because the desktop kept
        // compositing in HDR. What the toggle changes is who produces the PQ values:
        // the driver's HDR filter, or our own shader encoding SDR into PQ.
        HdrOutput = enable;
        Processor?.SetOutputHdr(enable);
        Log.Info($"RTX HDR filter {(enable ? "on" : "off")}; swapchain stays in " +
                 $"{(SwapChainIsPq ? "PQ / BT.2020" : "sRGB / BT.709")}");
        return true;
    }

    /// <summary>True while the swapchain declares PQ / BT.2020 to DXGI.</summary>
    public bool SwapChainIsPq { get; private set; }

    /// <summary>
    /// True when the device itself says its picture is HDR10: PQ over BT.2020. Capture
    /// cards do not all say, so <see cref="SourceRange"/> can overrule it either way.
    /// </summary>
    public bool SourceReportsHdr { get; set; }

    /// <summary>0 = believe the device, 1 = the source is SDR, 2 = the source is HDR10.</summary>
    public int SourceRange { get; set; }

    /// <summary>What the picture is taken to be: HDR10, or an ordinary SDR one.</summary>
    public bool SourceIsHdr => TestPattern ? TestPatternHdr : SourceRange == 2 || (SourceRange == 0 && SourceReportsHdr);

    /// <summary>
    /// For an HDR source: shown as it is, its own values going to the display untouched,
    /// rather than converted to SDR. Asked for; <see cref="PassingHdrThrough"/> says
    /// whether that is what happens.
    /// </summary>
    public bool HdrAsIs { get; set; } = true;

    /// <summary>
    /// True while an HDR source really is going through untouched. That needs a display
    /// in HDR to take it, and nothing that works on the picture in the way: DLSS and FSR
    /// are given an SDR picture, so with one of them on the source is converted first.
    /// </summary>
    public bool PassingHdrThrough => SourceIsHdr && HdrAsIs && SwapChainIsPq && !UseDlss && !UseFsr;

    /// <summary>
    /// Puts the swapchain into PQ once, at startup, when the desktop is already in HDR.
    /// Everything drawn afterwards is encoded to PQ in the pixel shader.
    /// </summary>
    private void ApplySwapChainColorSpace()
    {
        if (!DisplaySupportsHdr) return;
        using var swapChain3 = _swapChain.QueryInterfaceOrNull<IDXGISwapChain3>();
        if (swapChain3 is null) { Log.Warn("IDXGISwapChain3 unavailable, staying in sRGB"); return; }
        try
        {
            swapChain3.SetColorSpace1(ColorSpaceType.RgbFullG2084NoneP2020);
            SwapChainIsPq = true;
            Log.Info("swapchain colour space: PQ / BT.2020 (desktop is in HDR)");
        }
        catch (Exception ex) { Log.Warn($"SetColorSpace1 failed: {ex.Message}"); }
    }

    private static bool CheckTearing(IDXGIFactory2 factory)
    {
        using var f5 = factory.QueryInterfaceOrNull<IDXGIFactory5>();
        return f5?.PresentAllowTearing ?? false;
    }

    private void CreateBackBufferView()
    {
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _backBufferView = _device.CreateRenderTargetView(backBuffer);
    }

    /// <summary>Resizes the swapchain to a new client size. Ignores degenerate sizes (minimise).</summary>
    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0 || (width == Width && height == Height)) return;

        lock (_context)
        {
            _context.OMSetRenderTargets((ID3D11RenderTargetView?)null);
            _backBufferView.Dispose();

            var flags = SwapChainFlags.FrameLatencyWaitableObject
                        | (TearingSupported ? SwapChainFlags.AllowTearing : SwapChainFlags.None);
            _swapChain.ResizeBuffers(2, (uint)width, (uint)height, BackBufferFormat, flags);

            Width = width; Height = height;
            CreateBackBufferView();
        }
        Log.Debug($"swapchain resized to {width}x{height}");
    }

    private void CreatePipeline()
    {
        Compiler.Compile(ShaderSource, [], null, "VS", "shader.hlsl", "vs_5_0", out var vsBlob, out var vsErr);
        if (vsBlob is null) throw new InvalidOperationException($"VS compile failed: {vsErr?.AsString()}");
        Compiler.Compile(ShaderSource, [], null, "PS", "shader.hlsl", "ps_5_0", out var psBlob, out var psErr);
        if (psBlob is null) throw new InvalidOperationException($"PS compile failed: {psErr?.AsString()}");

        _vs = _device.CreateVertexShader(vsBlob.AsSpan());
        _ps = _device.CreatePixelShader(psBlob.AsSpan());
        vsBlob.Dispose(); psBlob.Dispose();

        _constants = _device.CreateBuffer(new BufferDescription(32, BindFlags.ConstantBuffer, ResourceUsage.Dynamic,
            CpuAccessFlags.Write));

        _sampler = _device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MaxLOD = float.MaxValue
        });

        var blend = new BlendDescription();
        blend.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = Blend.SourceAlpha,
            DestinationBlend = Blend.InverseSourceAlpha,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One,
            DestinationBlendAlpha = Blend.InverseSourceAlpha,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All
        };
        _alphaBlend = _device.CreateBlendState(blend);

        for (int i = 0; i < QueryDepth; i++)
        {
            _disjoint[i] = _device.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint));
            _tsStart[i] = _device.CreateQuery(new QueryDescription(QueryType.Timestamp));
            _tsEnd[i] = _device.CreateQuery(new QueryDescription(QueryType.Timestamp));
            _tsFlowStart[i] = _device.CreateQuery(new QueryDescription(QueryType.Timestamp));
            _tsFgStart[i] = _device.CreateQuery(new QueryDescription(QueryType.Timestamp));
            _tsFgEnd[i] = _device.CreateQuery(new QueryDescription(QueryType.Timestamp));
            _tsFlowEnd[i] = _device.CreateQuery(new QueryDescription(QueryType.Timestamp));
            _tsDlssStart[i] = _device.CreateQuery(new QueryDescription(QueryType.Timestamp));
            _tsDlssEnd[i] = _device.CreateQuery(new QueryDescription(QueryType.Timestamp));
        }
    }

    /// <summary>
    /// Reads back the GPU timestamps issued <see cref="QueryDepth"/> frames ago. The
    /// disjoint flag marks intervals where the GPU clock changed frequency mid-measure,
    /// which makes the reading meaningless — those are discarded rather than reported.
    /// </summary>
    private void CollectGpuTiming()
    {
        if (_queryFrame < QueryDepth) return;
        int slot = (int)((_queryFrame - QueryDepth + 1) % QueryDepth);

        if (!_context.GetData(_disjoint[slot], out QueryDataTimestampDisjoint dj)) return;
        if (dj.Disjoint || dj.Frequency == 0) return;
        if (!_context.GetData(_tsStart[slot], out ulong start)) return;
        if (!_context.GetData(_tsEnd[slot], out ulong end)) return;
        if (end <= start) return;

        LastGpuMs = (end - start) * 1000.0 / dj.Frequency;

        if (_flowTimed[slot]
            && _context.GetData(_tsFlowStart[slot], out ulong flowStart)
            && _context.GetData(_tsFlowEnd[slot], out ulong flowEnd)
            && flowEnd > flowStart)
            LastFlowMs = (flowEnd - flowStart) * 1000.0 / dj.Frequency;

        if (_fgTimed[slot]
            && _context.GetData(_tsFgStart[slot], out ulong fgStart)
            && _context.GetData(_tsFgEnd[slot], out ulong fgEnd)
            && fgEnd > fgStart)
        {
            LastFrameGenMs = (fgEnd - fgStart) * 1000.0 / dj.Frequency;
            FrameGenGpuSamples?.Add(LastFrameGenMs);
        }

        if (_dlssTimed[slot]
            && _context.GetData(_tsDlssStart[slot], out ulong dlssStart)
            && _context.GetData(_tsDlssEnd[slot], out ulong dlssEnd)
            && dlssEnd > dlssStart)
        {
            double ms = (dlssEnd - dlssStart) * 1000.0 / dj.Frequency;
            if (_fsrTimed[slot]) LastFsrMs = ms;
            else LastDlssMs = ms;
        }
    }

    /// <summary>
    /// Allocates the plane-pair texture for a capture format. NV12 and P010 differ only
    /// in the SRV formats: 8-bit planes become R8/R8G8, 10-bit planes R16/R16G16, and the
    /// same shader maths covers both because P010 stores its 10 bits left-aligned in 16.
    /// </summary>
    public void ConfigureVideo(int width, int height, bool isTenBit)
    {
        // Under the same lock as the copies into these textures. A capture card's thread is
        // stopped before its surface is rebuilt; the network thread is not, because there
        // the size is only learned from the frames it is already receiving.
        lock (_context) ConfigureVideoLocked(width, height, isTenBit);
    }

    private void ConfigureVideoLocked(int width, int height, bool isTenBit)
    {
        // The pattern decides its own surface: it is drawn in place of whatever the source sends.
        if (TestPattern) isTenBit = TestPatternHdr;
        var format = isTenBit ? Format.P010 : Format.NV12;
        if (_videoTexture is not null && _videoWidth == width && _videoHeight == height && _videoFormat == format)
            return;

        ReleaseVideoResources();

        _videoTexture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            // ShaderResource only. Adding RenderTarget here to please the video processor
            // made the app exit immediately on the NV12 path, so it stays off until the
            // video-processor route is debugged properly.
            BindFlags = BindFlags.ShaderResource
        });

        // The video processor cannot take a ShaderResource-only plane pair as an input
        // view — it needs a decoder-bindable surface. One texture cannot serve both roles,
        // so the video-processor path gets its own and the frame is copied into whichever
        // one is active. Creating it is best-effort: without it, RTX Video HDR is simply unavailable.
        try
        {
            _vpTexture = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.Decoder
            });
            Log.Debug("video-processor input texture created (BindFlags.Decoder)");
        }
        catch (Exception ex)
        {
            _vpTexture = null;
            Log.Warn($"decoder-bindable texture unavailable, RTX Video HDR disabled: {ex.Message}");
        }

        var lumaFormat = isTenBit ? Format.R16_UNorm : Format.R8_UNorm;
        var chromaFormat = isTenBit ? Format.R16G16_UNorm : Format.R8G8_UNorm;
        _lumaView = _device.CreateShaderResourceView(_videoTexture,
            new ShaderResourceViewDescription(_videoTexture, ShaderResourceViewDimension.Texture2D, lumaFormat));
        _chromaView = _device.CreateShaderResourceView(_videoTexture,
            new ShaderResourceViewDescription(_videoTexture, ShaderResourceViewDimension.Texture2D, chromaFormat));

        _videoWidth = width; _videoHeight = height; _videoFormat = format;
        Log.Info($"video surface configured: {width}x{height} {format} ({(isTenBit ? "10" : "8")}-bit)");

        if (TestPattern) FillTestPattern();
    }

    /// <summary>
    /// Diagnostic: a picture of known colours shown in place of the video, so that what
    /// each path does to colour can be measured instead of judged by eye. Set before the
    /// source starts. Frames from the source are then ignored.
    /// <para>
    /// The picture is what an SDR source would send: 8-bit NV12, BT.709, 16-235. Three
    /// rows of eight flat patches: the colour bars at full strength, a grey ramp from
    /// black to white, and the bars again at three quarters. <see cref="SavePatternReadings"/>
    /// writes out what reached the back buffer for each.
    /// </para>
    /// </summary>
    public bool TestPattern { get; set; }

    /// <summary>
    /// With <see cref="TestPattern"/>: the picture an HDR source would send instead - HDR10,
    /// which is 10-bit P010, BT.2020, PQ. A grey scale from black to 4000 nits, and the
    /// BT.709 colour bars at 203 nits, HDR's reference white: the colours an SDR picture
    /// has, as an HDR source carries them.
    /// </summary>
    public bool TestPatternHdr { get; set; }

    /// <summary>A patch: for the SDR pattern its sRGB values out of 255, for the HDR one nits in BT.2020 primaries.</summary>
    private readonly record struct Patch(string Name, float A, float B, float C);

    private static readonly string[] BarNames = ["white", "yellow", "cyan", "green", "magenta", "red", "blue", "black"];
    private static readonly (int R, int G, int B)[] BarColours =
        [(1, 1, 1), (1, 1, 0), (0, 1, 1), (0, 1, 0), (1, 0, 1), (1, 0, 0), (0, 0, 1), (0, 0, 0)];

    // The rows, as a share of the picture's height, and the patches of each.
    private static readonly (float Top, float Bottom, Patch[] Patches)[] SdrRows =
    [
        (0f, 0.375f, [.. BarColours.Select((c, i) => new Patch(BarNames[i], c.R * 255, c.G * 255, c.B * 255))]),
        (0.375f, 0.6875f, [.. Enumerable.Range(0, 8).Select(i => { float v = MathF.Round(i * 255 / 7f); return new Patch($"grey {v:0}", v, v, v); })]),
        (0.6875f, 0.875f, [.. BarColours.Select((c, i) => new Patch(BarNames[i] + " 75%", c.R * 191, c.G * 191, c.B * 191))]),
    ];

    private static readonly (float Top, float Bottom, Patch[] Patches)[] HdrRows =
    [
        (0f, 0.375f, [.. new float[] { 0, 25, 50, 100, 203, 400, 1000, 4000 }.Select(n => new Patch($"grey {n:0} nits", n, n, n))]),
        (0.375f, 0.6875f, [.. BarColours.Select((c, i) => new Patch(BarNames[i] + " 709",
            203f * (0.6274f * c.R + 0.3293f * c.G + 0.0433f * c.B),
            203f * (0.0691f * c.R + 0.9195f * c.G + 0.0114f * c.B),
            203f * (0.0164f * c.R + 0.0880f * c.G + 0.8956f * c.B)))]),
    ];

    private (float Top, float Bottom, Patch[] Patches)[] PatternRows => TestPatternHdr ? HdrRows : SdrRows;

    private unsafe void FillTestPattern()
    {
        bool hdr = _videoFormat == Format.P010;
        if (_videoTexture is null || hdr != TestPatternHdr) return;
        int width = _videoWidth, height = _videoHeight;
        var rows = PatternRows;

        using var staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = _videoFormat,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Write
        });

        Patch At(int x, int y)
        {
            float v = y / (float)height;
            foreach (var row in rows)
                if (v >= row.Top && v < row.Bottom)
                    return row.Patches[Math.Min(row.Patches.Length - 1, x * row.Patches.Length / width)];
            return default;
        }

        // SMPTE ST 2084: nits to the encoded value.
        static double Pq(double nits)
        {
            const double m1 = 0.1593017578125, m2 = 78.84375, c1 = 0.8359375, c2 = 18.8515625, c3 = 18.6875;
            double l = Math.Pow(Math.Clamp(nits / 10000.0, 0, 1), m1);
            return Math.Pow((c1 + c2 * l) / (1 + c3 * l), m2);
        }

        // A patch as Y' in 0..1 and Cb, Cr in -0.5..0.5: BT.709 for the SDR picture,
        // BT.2020 over PQ for the HDR one.
        (double Y, double Cb, double Cr) ToYuv(Patch p)
        {
            if (hdr)
            {
                double r = Pq(p.A), g = Pq(p.B), b = Pq(p.C);
                double y = 0.2627 * r + 0.6780 * g + 0.0593 * b;
                return (y, (b - y) / 1.8814, (r - y) / 1.4746);
            }
            else
            {
                double r = p.A / 255.0, g = p.B / 255.0, b = p.C / 255.0;
                double y = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                return (y, (b - y) / 1.8556, (r - y) / 1.5748);
            }
        }

        var map = _context.Map(staging, 0, MapMode.Write);
        try
        {
            byte* luma = (byte*)map.DataPointer;
            // The chroma plane follows the luma plane, at the same pitch.
            byte* chroma = luma + (long)map.RowPitch * height;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    double v = ToYuv(At(x, y)).Y;
                    // Ten bits sit in the top of sixteen.
                    if (hdr) ((ushort*)(luma + y * (long)map.RowPitch))[x] = (ushort)((int)Math.Round(64 + 876 * v) << 6);
                    else luma[y * (long)map.RowPitch + x] = (byte)Math.Round(16 + 219 * v);
                }
            for (int y = 0; y < height / 2; y++)
                for (int x = 0; x < width / 2; x++)
                {
                    var c = ToYuv(At(x * 2, y * 2));
                    if (hdr)
                    {
                        var line = (ushort*)(chroma + y * (long)map.RowPitch);
                        line[x * 2] = (ushort)((int)Math.Round(512 + 896 * c.Cb) << 6);
                        line[x * 2 + 1] = (ushort)((int)Math.Round(512 + 896 * c.Cr) << 6);
                    }
                    else
                    {
                        chroma[y * (long)map.RowPitch + x * 2] = (byte)Math.Round(128 + 224 * c.Cb);
                        chroma[y * (long)map.RowPitch + x * 2 + 1] = (byte)Math.Round(128 + 224 * c.Cr);
                    }
                }
        }
        finally { _context.Unmap(staging, 0); }

        _context.CopyResource(_videoTexture, staging);
        if (_vpTexture is not null) _context.CopyResource(_vpTexture, staging);
        _uploadSerial++;
        Log.Info($"test pattern {width}x{height} ({(hdr ? "HDR10" : "SDR")}) in place of the video");
    }

    /// <summary>
    /// For each patch of the test pattern, what it was meant to be and what is in the
    /// back buffer where it was drawn: the swapchain's own 10-bit values, averaged over
    /// the middle of the patch. One line a patch: row, column, name, the three numbers
    /// it stands for (sRGB out of 255 for the SDR pattern, nits in BT.2020 for the HDR
    /// one), then red, green and blue out of 1023. The first line says which pattern,
    /// whether the swapchain is PQ or sRGB, and the level SDR video is encoded at.
    /// </summary>
    public bool SavePatternReadings(string path)
    {
        if (!TestPattern || _videoWidth <= 0) return false;
        try
        {
            using var back = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            var desc = back.Description;
            desc.Usage = ResourceUsage.Staging;
            desc.BindFlags = BindFlags.None;
            desc.CPUAccessFlags = CpuAccessFlags.Read;
            desc.MiscFlags = ResourceOptionFlags.None;
            using var staging = _device.CreateTexture2D(desc);

            var rows = PatternRows;
            var (left, top, shownWidth, shownHeight) = FitPixels(_videoWidth, _videoHeight, Width, Height);
            using var file = new StreamWriter(path);
            file.WriteLine(FormattableString.Invariant(
                $"# pattern {(TestPatternHdr ? "hdr" : "sdr")}, swapchain {(SwapChainIsPq ? "PQ BT.2020" : "sRGB")}, video at {VideoNits:0} nits, {Width}x{Height}, picture {shownWidth}x{shownHeight}"));
            file.WriteLine("row,column,name,a,b,c,r10,g10,b10");

            lock (_context)
            {
                _context.CopyResource(staging, back);
                var map = _context.Map(staging, 0, MapMode.Read);
                try
                {
                    unsafe
                    {
                        for (int r = 0; r < rows.Length; r++)
                        {
                            var row = rows[r];
                            int cy = top + (int)((row.Top + row.Bottom) / 2 * shownHeight);
                            for (int c = 0; c < row.Patches.Length; c++)
                            {
                                int cx = left + (int)((c + 0.5f) / row.Patches.Length * shownWidth);
                                double red = 0, green = 0, blue = 0;
                                const int Half = 6;
                                for (int y = cy - Half; y <= cy + Half; y++)
                                {
                                    var line = (uint*)((byte*)map.DataPointer + y * (long)map.RowPitch);
                                    for (int x = cx - Half; x <= cx + Half; x++)
                                    {
                                        uint v = line[x];
                                        red += v & 0x3FF; green += (v >> 10) & 0x3FF; blue += (v >> 20) & 0x3FF;
                                    }
                                }
                                double n = (2 * Half + 1) * (2 * Half + 1);
                                var p = row.Patches[c];
                                file.WriteLine(FormattableString.Invariant(
                                    $"{r},{c},{p.Name},{p.A:0.##},{p.B:0.##},{p.C:0.##},{red / n:0.0},{green / n:0.0},{blue / n:0.0}"));
                            }
                        }
                    }
                }
                finally { _context.Unmap(staging, 0); }
            }
            Log.Info($"pattern readings written to {path}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"pattern readings not written: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Copies one MF frame into our sampleable texture and, while RTX Video HDR is on,
    /// into the one the video processor reads.
    /// </summary>
    public void UploadFrame(ID3D11Texture2D source, int subresource)
    {
        if (_videoTexture is null || TestPattern) return;
        // Copy only. The video processor blt deliberately does NOT run here: it shares the
        // immediate context with the render thread, and issuing it from the capture thread
        // put both threads in contention on the same lock for tens of milliseconds a frame.
        lock (_context)
        {
            _uploadSerial++;
            _context.CopySubresourceRegion(_videoTexture, 0, 0, 0, 0, source, (uint)subresource);
            if (UseProcessor && _vpTexture is not null)
                _context.CopySubresourceRegion(_vpTexture, 0, 0, 0, 0, source, (uint)subresource);
            KeepForClip();
        }
    }

    /// <summary>
    /// The same copy for a source that may be larger than the picture. A decoder's texture
    /// is as big as the size the stream is coded at, which is the picture rounded up to
    /// whole blocks, so only the picture's own rectangle is taken. Skipped when the surface
    /// is not this size yet — the frame is one late, not one to stretch.
    /// <para>
    /// With <paramref name="mark"/> a marker is left in the GPU's queue right behind the
    /// copy, for <see cref="WaitForUpload"/>. Returns whether one was left.
    /// </para>
    /// </summary>
    public bool UploadFrame(ID3D11Texture2D source, int subresource, int width, int height, bool mark = false)
    {
        var picture = new Vortice.Mathematics.Box(0, 0, 0, width, height, 1);
        lock (_context)
        {
            if (_videoTexture is null || width != _videoWidth || height != _videoHeight) return false;
            if (TestPattern) return false;
            _uploadSerial++;
            _context.CopySubresourceRegion(_videoTexture, 0, 0, 0, 0, source, (uint)subresource, picture);
            if (UseProcessor && _vpTexture is not null)
                _context.CopySubresourceRegion(_vpTexture, 0, 0, 0, 0, source, (uint)subresource, picture);
            KeepForClip();

            if (!mark) return false;
            _uploadMark ??= _device.CreateQuery(new QueryDescription(QueryType.Event));
            _context.End(_uploadMark);
            return true;
        }
    }

    // Reached by the GPU when it has finished the copy it was left behind.
    private ID3D11Query? _uploadMark;

    /// <summary>
    /// Waits until the GPU has really done the upload last marked — and so the decode that
    /// had to produce the picture before it could be copied. Handing a frame to a hardware
    /// decoder returns at once; the decoding happens afterwards, on the GPU, and this is
    /// the only way to see how long it took. False if it is not done within the limit.
    /// <para>
    /// It spins rather than sleeps, because the wait is a millisecond or two and a sleep
    /// cannot be that short. That costs a little CPU, so it is only asked for while the
    /// figure is on screen or being recorded.
    /// </para>
    /// </summary>
    public bool WaitForUpload(double limitMs)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var spin = new SpinWait();
        while (true)
        {
            lock (_context)
            {
                if (_uploadMark is null) return false;
                if (_context.GetData(_uploadMark, out int reached) && reached != 0) return true;
            }
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds > limitMs) return false;
            spin.SpinOnce(sleep1Threshold: -1);
        }
    }

    /// <summary>Renders one frame: black background, letterboxed video, HUD on top.</summary>
    public void Render()
    {
        lock (_context)
        {
            int slot = (int)(_queryFrame % QueryDepth);
            _context.Begin(_disjoint[slot]);
            _context.End(_tsStart[slot]);

            bool fsr = UseFsr && _lumaView is not null && _chromaView is not null && _videoWidth > 0;
            bool dlss = !fsr && UseDlss && _lumaView is not null && _chromaView is not null && _videoWidth > 0;

            // Frame generation shows a queue of frames for each real one. Until the last of
            // them, the real one, is on the screen, a frame that has arrived meanwhile waits:
            // nothing below touches it, and what is drawn comes from the queue.
            bool fg = UseFrameGen && _lumaView is not null && _videoWidth > 0;
            if (fg != _fgWasOn)
            {
                _fgWasOn = fg;
                _fgCount = 0;
                _fgRealShown = true;
                _fgLastArrival = 0;
                FrameGen?.Reset();
            }
            bool holding = fg && _fgCount > 0 && !_fgRealShown;
            _fgTimed[slot] = false;

            // Motion since the previous frame: once per new frame, and only while something
            // is using it - the view of it, DLSS, FSR or frame generation. Done first because
            // it leaves its own targets and shaders set, and everything below sets its own again.
            _flowTimed[slot] = false;
            bool probing = FrameGenProbe is { Done: false };
            if (!holding && Flow is { } flow && (flow.View != FlowView.Off || dlss || fsr || fg || probing)
                && _lumaView is not null && _uploadSerial != _flowSerial)
            {
                _flowSerial = _uploadSerial;
                _context.End(_tsFlowStart[slot]);
                flow.Estimate(_context, _lumaView, _videoWidth, _videoHeight, _sampler);
                _context.End(_tsFlowEnd[slot]);
                _flowTimed[slot] = true;
            }

            // The probe takes each new frame as the upscalers do: in colour, with its motion.
            if (probing && Flow is { Ready: true } probeFlow && _uploadSerial != _probeSerial
                && _lumaView is not null && EnsureIntermediate(_videoWidth, _videoHeight))
            {
                _probeSerial = _uploadSerial;
                var motion = probeFlow.PerPixel(_context, _sampler);
                BindPipeline();
                if (ConvertYuvToIntermediate())
                {
                    _context.OMSetRenderTargets((ID3D11RenderTargetView?)null);
                    FrameGenProbe!.Feed(_context, _rgbIntermediate!, motion, _videoWidth, _videoHeight);
                }
            }

            _context.OMSetRenderTargets(_backBufferView);
            _context.RSSetViewport(0, 0, Width, Height);
            _context.ClearRenderTargetView(_backBufferView, new Color4(0f, 0f, 0f, 1f));

            BindPipeline();

            var (fx, fy, fw, fh) = FitRect(_videoWidth, _videoHeight, Width, Height);

            // DLSS and FSR want colour in and write colour out, and they want the motion
            // besides. Both passes that prepare those leave their own state set.
            ID3D11ShaderResourceView? dlssResult = null;
            _dlssTimed[slot] = false;
            if ((dlss || fsr) && !holding)
            {
                var (_, _, fitW, fitH) = FitPixels(_videoWidth, _videoHeight, Width, Height);
                _context.End(_tsDlssStart[slot]);
                dlssResult = RunDlss(fitW, fitH, fsr);
                _context.End(_tsDlssEnd[slot]);
                _dlssTimed[slot] = true;
                _fsrTimed[slot] = fsr;

                BindPipeline();
                _context.OMSetRenderTargets(_backBufferView);
                _context.RSSetViewport(0, 0, Width, Height);
            }

            // Build the processor even while its filter is off: an extension is set on an
            // existing processor, so it must exist before RTX Video HDR can be switched on.
            if (Processor is { Available: true } proc && _vpTexture is not null && _videoWidth > 0)
            {
                var (_, _, fitW, fitH) = FitPixels(_videoWidth, _videoHeight, Width, Height);
                // RTX Video HDR makes HDR out of SDR; a source that is HDR already has no use for it.
                if (SourceIsHdr && proc.HdrEnabled) { proc.ToggleHdr(); HdrOutput = false; }
                proc.Configure(_videoWidth, _videoHeight, _videoFormat, fitW, fitH);
                if (UseProcessor) proc.Process(_vpTexture);
            }

            if (fg && !holding && _uploadSerial != _fgSerial)
            {
                BuildFrameGenQueue(slot, dlssResult);
                _context.OMSetRenderTargets(_backBufferView);
                _context.RSSetViewport(0, 0, Width, Height);
            }
            if (!holding) _renderedSerial = _uploadSerial;

            // Which picture this one is: a place in the queue with generation, the real frame
            // without. A new one counts towards what the screen shows.
            long shownKey = fg && _fgCount > 0 ? (_fgSerial << 4) | (uint)Math.Min(_fgShown, _fgCount - 1) : (_renderedSerial << 4) | 15;
            if (shownKey != _lastShownKey && _renderedSerial != 0)
            {
                _lastShownKey = shownKey;
                PicturesShown++;
            }

            if (fg && _fgCount > 0)
            {
                // A generated frame, or the real one after them: SDR RGB, like an upscaler's.
                SetConstants(fx, fy, fw, fh, mode: 1);
                _context.PSSetShaderResource(2, _fgQueue[Math.Min(_fgShown, _fgCount - 1)]);
                _context.OMSetBlendState(null);
                _context.Draw(4, 0);
                if (_fgShown >= _fgCount - 1) _fgRealShown = true;
            }
            else if (dlssResult is not null)
            {
                // SDR RGB at the size it was built for, so it takes the encoding path too.
                SetConstants(fx, fy, fw, fh, mode: 1);
                _context.PSSetShaderResource(2, dlssResult);
                _context.OMSetBlendState(null);
                _context.Draw(4, 0);
            }
            else if (UseProcessor && Processor!.OutputView is { } processed)
            {
                // Already RGB, already scaled, already in the output colour space.
                SetConstants(fx, fy, fw, fh, mode: 2);
                _context.PSSetShaderResource(2, processed);
                _context.OMSetBlendState(null);
                _context.Draw(4, 0);
            }
            else if (_lumaView is not null && _chromaView is not null)
            {
                SetConstants(fx, fy, fw, fh, mode: 0);
                _context.PSSetShaderResource(0, _lumaView);
                _context.PSSetShaderResource(1, _chromaView);
                _context.OMSetBlendState(null);
                _context.Draw(4, 0);
            }

            if (_hudView is not null)
            {
                var r = HudRect;
                var (x, y, w, h) = ToNdc(r.X, r.Y, r.Width, r.Height, Width, Height);
                SetConstants(x, y, w, h, mode: 1);
                _context.PSSetShaderResource(2, _hudView);
                _context.OMSetBlendState(_alphaBlend);
                _context.Draw(4, 0);
                _context.OMSetBlendState(null);
            }

            // The estimated motion, drawn over the rectangle the video occupies.
            if (Flow is { View: not FlowView.Off, Ready: true } shown)
            {
                var (_, _, shownW, shownH) = FitPixels(_videoWidth, _videoHeight, Width, Height);
                shown.Draw(_context, fx, fy, fw, fh, shownW, shownH, SwapChainIsPq, OverlayNits, _sampler, _alphaBlend);
                _context.OMSetBlendState(null);
            }

            // Last, into the same render target. Inside the swapchain, never over it:
            // a separate window or layer here would cost a whole frame of latency.
            if (_uiFrameOpen && Ui is not null)
            {
                _uiFrameOpen = false;
                Ui.Render(_context, SwapChainIsPq, OverlayNits);
            }

            _context.End(_tsEnd[slot]);
            _context.End(_disjoint[slot]);
            _queryFrame++;
            CollectGpuTiming();
        }
    }

    /// <summary>The shaders and state every draw into the back buffer shares.</summary>
    private void BindPipeline()
    {
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        _context.VSSetShader(_vs);
        _context.PSSetShader(_ps);
        _context.PSSetSampler(0, _sampler);
        _context.VSSetConstantBuffer(0, _constants);
        _context.PSSetConstantBuffer(0, _constants);
    }

    /// <summary>
    /// Puts the newest frame through DLSS, or through FSR, and gives back what there is to
    /// show, or null when the scaler has nothing for this frame and the plain picture
    /// should be drawn. The two are fed alike, so all that differs is which is called.
    /// <para>
    /// Once per new frame. A frame that has been through already is shown again as it
    /// came out, except for the first few times, when it goes through once more as a
    /// picture in which nothing moved: see <see cref="DlssSettleFrames"/>.
    /// </para>
    /// </summary>
    private ID3D11ShaderResourceView? RunDlss(int fitWidth, int fitHeight, bool fsr)
    {
        var dlss = Dlss!;
        ID3D11ShaderResourceView? Shown() => fsr ? Fsr!.OutputView : dlss.OutputView;

        // The other of the two has just taken over. What it remembers is from whenever it
        // last ran, so it starts again, and with this frame even if it is not a new one.
        bool tookOver = fsr != _temporalWasFsr;
        _temporalWasFsr = fsr;
        if (tookOver)
        {
            _dlssSettling = 0;
            if (fsr && !CompareScalers) Fsr!.ResetHistory();
        }

        // FSR builds its upscaler on a thread of its own, which for FSR 4 can take many
        // seconds. Until it has one the picture is drawn as it came, and nothing is
        // prepared for it; the frame it is finished on goes through whether new or not.
        if (fsr && !CompareScalers && Fsr!.Building) return null;
        bool builtNow = fsr && Fsr!.BuildFinished;
        if (builtNow) _dlssSettling = 0;

        // FSR is made for the same range of enlargement as DLSS, 1 to 3 times each way.
        var (outWidth, outHeight) = DlssScaler.OutputSize(_videoWidth, _videoHeight, fitWidth, fitHeight);

        // FSR goes on being fed a picture that has stopped for as long as its round of
        // shifts takes: each pass puts the same picture on another part of the finer grid.
        int shifts = FsrShifts(_videoWidth, outWidth);
        int settle = fsr ? Math.Max(DlssSettleFrames, shifts) : DlssSettleFrames;

        bool fresh = _uploadSerial != _dlssSerial;
        if (!fresh && !tookOver && !builtNow && (Shown() is null || _dlssSettling >= settle)) return Shown();

        if (!EnsureIntermediate(_videoWidth, _videoHeight)) return null;

        // No field yet means this is the first frame, which has nothing before it to have
        // moved from; the same picture again has not moved either.
        var motion = fresh && Flow is { Ready: true } flow ? flow.PerPixel(_context, _sampler) : null;

        _dlssSettling = fresh ? 0 : Math.Min(_dlssSettling + 1, TemporalHeld);
        _dlssSerial = _uploadSerial;

        // The colour, for whoever is about to read it. Spreading the vectors left its own
        // shaders set, and so may a scaler that has been through before this one.
        bool Colour(float shiftX, float shiftY)
        {
            BindPipeline();
            if (!ConvertYuvToIntermediate(shiftX, shiftY)) return false;
            // Still bound as a render target, and about to be read.
            _context.OMSetRenderTargets((ID3D11RenderTargetView?)null);
            return true;
        }

        bool both = CompareScalers && (fsr ? dlss.Possible : Fsr is { Possible: true });
        bool dlssDone = true, fsrDone = true;

        if (!fsr || both)
            dlssDone = Colour(0f, 0f)
                       && dlss.Process(_context, _rgbIntermediate!, motion, _videoWidth, _videoHeight, outWidth, outHeight);

        if (fsr || both)
        {
            // A game moves its camera by part of a pixel from frame to frame, and that is
            // where FSR finds what lies between the pixels: without it, it has the same
            // few samples every time and the enlarged picture comes out in steps. A video
            // was never moved, so the picture is, here, by FSR's own round of shifts -
            // which adds nothing that was not sent, but lets FSR place what there is.
            _fsrShift = _fsrShift % shifts + 1;
            float shiftX = Halton(_fsrShift, 2) - 0.5f, shiftY = Halton(_fsrShift, 3) - 0.5f;
            fsrDone = Colour(shiftX, shiftY)
                      && Fsr!.Process(_context, _rgbIntermediate!, motion, shiftX, shiftY,
                                      _videoWidth, _videoHeight, outWidth, outHeight);
        }

        return (fsr ? fsrDone : dlssDone) ? Shown() : null;
    }

    // More than any count of passes: "this frame is not to go through again".
    private const int TemporalHeld = 1 << 20;

    /// <summary>
    /// How many different shifts FSR goes round, by its own rule: eight for a picture
    /// shown at its own size, and as many times more as the enlarged one has pixels.
    /// </summary>
    private static int FsrShifts(int inWidth, int outWidth)
    {
        double scale = outWidth / (double)Math.Max(1, inWidth);
        return Math.Clamp((int)(8 * scale * scale), 8, 72);
    }

    /// <summary>
    /// The Halton sequence: points between 0 and 1 that keep apart from one another however
    /// many are taken, which is what makes a short round of shifts cover the pixel evenly.
    /// </summary>
    private static float Halton(int index, int radix)
    {
        float fraction = 1f, result = 0f;
        for (int i = index; i > 0; i /= radix)
        {
            fraction /= radix;
            result += fraction * (i % radix);
        }
        return result;
    }

    public void Present()
    {
        var flags = TearingSupported ? PresentFlags.AllowTearing : PresentFlags.None;
        _swapChain.Present(0, flags);

        // SyncQPCTime is the vblank DXGI associated with the last presented frame —
        // the closest thing to "when the pixels actually went out" that software can see.
        try
        {
            _swapChain.GetFrameStatistics(out var stats);
            LastScanoutQpc = stats.SyncQPCTime;
            LastSyncRefreshCount = stats.SyncRefreshCount;
        }
        catch (SharpGenException) { LastScanoutQpc = 0; LastSyncRefreshCount = 0; }
    }

    /// <summary>Blocks until DXGI is ready for the next frame. No-op without a waitable swapchain.</summary>
    public void WaitForNextFrame(int timeoutMs = 100)
    {
        if (FrameLatencyWaitableObject != IntPtr.Zero)
            _ = Native.Kernel.WaitForSingleObject(FrameLatencyWaitableObject, (uint)timeoutMs);
    }

    /// <summary>
    /// Asks DXGI whether this swapchain reached an overlay plane. "Composed" means DWM
    /// is in the path and we are paying a frame for it.
    /// </summary>
    public string DescribeCompositionMode()
    {
        using var media = _swapChain.QueryInterfaceOrNull<IDXGISwapChainMedia>();
        if (media is null) return "unknown (IDXGISwapChainMedia unavailable)";
        try
        {
            var stats = media.FrameStatisticsMedia;
            return stats.CompositionMode.ToString();
        }
        catch (SharpGenException ex) { return $"unavailable ({ex.ResultCode})"; }
    }

    /// <summary>
    /// Reads the back buffer back and writes it as a PNG. Diagnostic only, and slow —
    /// it stalls the pipeline waiting on the copy.
    /// <para>
    /// Worth having because it captures what was actually rendered rather than what is
    /// on screen, so the overlay can be inspected on a machine where the window is
    /// occluded, minimised or on a display nobody is looking at.
    /// </para>
    /// </summary>
    public bool SaveBackBuffer(string path)
    {
        try
        {
            using var back = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            var desc = back.Description;
            desc.Usage = ResourceUsage.Staging;
            desc.BindFlags = BindFlags.None;
            desc.CPUAccessFlags = CpuAccessFlags.Read;
            desc.MiscFlags = ResourceOptionFlags.None;

            using var staging = _device.CreateTexture2D(desc);
            lock (_context)
            {
                _context.CopyResource(staging, back);
                var map = _context.Map(staging, 0, MapMode.Read);
                try { WritePng(map, (int)desc.Width, (int)desc.Height, SwapChainIsPq, VideoNits, path); }
                finally { _context.Unmap(staging, 0); }
            }
            Log.Info($"back buffer written to {path}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"back buffer capture failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The frame now on screen, drawn each way there is - as it arrived, through DLSS and
    /// through FSR - and each written beside <paramref name="path"/> with the way in its
    /// name. Diagnostic only.
    /// <para>
    /// Two captures taken one after another are of two different frames, and a picture
    /// that moves at all cannot be compared across them. Here nothing new is let in while
    /// they are drawn, so they differ in the scaler and in nothing else. DLSS or FSR has
    /// to have been the scaler in use up to this point: what either shows is built from
    /// the frames before as well, and that cannot be made up on the spot. The other of
    /// the two is written as well when <see cref="CompareScalers"/> has kept it fed.
    /// </para>
    /// <para>
    /// After those come DLSS's networks K, L and M on the same frame held still, with
    /// "-still" in the name.
    /// </para>
    /// </summary>
    public bool SaveComparison(string path)
    {
        string Named(string way) => Path.Combine(Path.GetDirectoryName(path) ?? "",
            Path.GetFileNameWithoutExtension(path) + "-" + way + Path.GetExtension(path));

        // Held throughout, so that no frame arrives in between.
        lock (_context)
        {
            bool dlssWas = DlssEnabled, fsrWas = FsrEnabled;
            bool vsrWas = Processor?.SuperResolutionEnabled == true;
            bool ok = true;

            // The one in use first, exactly as it would have been shown: a frame that has
            // been through already is not put through again for this.
            // Nothing to compare while the one in use has no picture of its own yet:
            // what would be written under its name is the plain one.
            bool liveReady = UseFsr ? Fsr is { OutputView: not null, Building: false } : UseDlss && Dlss!.OutputView is not null;
            if ((UseDlss || UseFsr) && !liveReady)
                Log.Warn("comparison: the scaler in use has not produced a picture yet, so it is left out");

            if (liveReady)
            {
                bool fsrLive = UseFsr;
                int settlingWas = _dlssSettling;
                if (_uploadSerial == _dlssSerial) _dlssSettling = TemporalHeld;
                Render();
                ok &= SaveBackBuffer(Named(fsrLive ? "fsr" : "dlss"));

                // Then the other, as it stands after the same frames. It is only drawn:
                // this frame has been through it already.
                bool otherReady = fsrLive ? Dlss is { Possible: true, OutputView: not null }
                                          : Fsr is { Possible: true, OutputView: not null };
                if (CompareScalers && otherReady)
                {
                    _dlssSettling = TemporalHeld;
                    FsrEnabled = !fsrLive; DlssEnabled = fsrLive;
                    _temporalWasFsr = !fsrLive;
                    Render();
                    ok &= SaveBackBuffer(Named(fsrLive ? "dlss" : "fsr"));
                    _temporalWasFsr = fsrLive;
                }
                _dlssSettling = settlingWas;
            }

            // RTX VSR, when it is what has the picture, exactly as it is shown: it has been
            // running for as long as the app has, and the driver brings it in when it sees
            // fit - switched on for a moment it may not be in effect at all.
            bool vsrLive = vsrWas && UseVsr;
            if (vsrLive)
            {
                Render();
                ok &= SaveBackBuffer(Named("vsr"));
            }

            DlssEnabled = false;
            FsrEnabled = false;
            if (vsrWas) Processor!.ToggleSuperResolution();
            Render();
            ok &= SaveBackBuffer(Named("none"));

            // Otherwise switched on for the same frame. Its filter reads its own copy of
            // the frame, which is only kept up to date while the driver's path is in use.
            // Given time between frames: whether the filter is in effect on the first one
            // after it is switched on is the driver's business.
            if (!vsrLive && Processor is { Available: true, SuperResolutionSupported: true } processor
                && _vpTexture is not null && _videoTexture is not null && !SourceIsHdr)
            {
                _context.CopyResource(_vpTexture, _videoTexture);
                if (processor.ToggleSuperResolution())
                {
                    for (int i = 0; i < 20; i++)
                    {
                        Render();
                        Thread.Sleep(50);
                    }
                    ok &= SaveBackBuffer(Named("vsr-switched"));
                    processor.ToggleSuperResolution();
                }
            }

            // DLSS's networks beside each other. Each is given this same frame over and
            // over, as a picture in which nothing moves, until it has settled. That shows
            // what each makes of a frame at rest; how they differ in coping with movement
            // does not show here, and cannot on a single frame.
            if (dlssWas && Dlss is { Possible: true } dlss)
            {
                var modelWas = dlss.Model;
                DlssEnabled = true;
                foreach (var model in (DlssModel[])[DlssModel.K, DlssModel.L, DlssModel.M])
                {
                    dlss.Model = model;
                    for (int i = 0; i < ComparisonStillFrames; i++)
                    {
                        _dlssSettling = 0;
                        Render();
                    }
                    // Named for the network that ran, which the driver may have chosen instead.
                    ok &= SaveBackBuffer(Named("dlss-" + (dlss.ModelInUse ?? model.ToString()) + "-still"));
                }
                dlss.Model = modelWas;
            }

            DlssEnabled = dlssWas;
            FsrEnabled = fsrWas;
            if (vsrWas && Processor is { SuperResolutionEnabled: false } p) p.ToggleSuperResolution();
            return ok;
        }
    }

    // The frames a clip is made of, as they arrived, and how many have been kept so far.
    private ID3D11Texture2D[]? _clip;
    private volatile int _clipCount;

    /// <summary>
    /// Frames kept for <see cref="SaveClip"/> so far, and how many it is to hold. Wanted
    /// falls back to 0 when the clip had to be dropped: the source changed size on the way.
    /// </summary>
    public (int Taken, int Wanted) ClipProgress
    {
        get { lock (_context) return (_clipCount, _clip?.Length ?? 0); }
    }

    /// <summary>
    /// Starts keeping the next <paramref name="frames"/> frames as they arrive, on the card,
    /// for <see cref="SaveClip"/>. False when there is no picture yet to size them by, or
    /// they do not fit.
    /// </summary>
    public bool StartClip(int frames)
    {
        lock (_context)
        {
            if (_videoTexture is null || _videoWidth <= 0 || TestPattern) return false;
            DropClip();
            var desc = _videoTexture.Description;
            var clip = new ID3D11Texture2D[frames];
            try
            {
                for (int i = 0; i < frames; i++) clip[i] = _device.CreateTexture2D(desc);
            }
            catch (Exception ex)
            {
                foreach (var t in clip) t?.Dispose();
                Log.Warn($"clip: {frames} frames of {_videoWidth}x{_videoHeight} do not fit on the card: {ex.Message}");
                return false;
            }
            _clip = clip;
            _clipCount = 0;
            Log.Info($"clip: keeping the next {frames} frames, {_videoWidth}x{_videoHeight} {_videoFormat}");
            return true;
        }
    }

    private void DropClip()
    {
        if (_clip is null) return;
        foreach (var t in _clip) t?.Dispose();
        _clip = null;
        _clipCount = 0;
    }

    // With the context held, right after a frame has been copied in.
    private void KeepForClip()
    {
        if (_clip is { } clip && _clipCount < clip.Length && _videoTexture is not null)
        {
            _context.CopyResource(clip[_clipCount], _videoTexture);
            _clipCount++;
        }
    }

    /// <summary>
    /// The clip kept by <see cref="StartClip"/>, put through each way there is - as it
    /// arrived, RTX VSR, DLSS's networks K, L and M, FSR - frame by frame in the order it
    /// came, and every picture written into a folder of its own beside
    /// <paramref name="path"/>: "name-clip/dlss-K/0000.png" and so on. Only the rectangle
    /// the picture takes is written. Diagnostic only.
    /// <para>
    /// One frame held still shows what each makes of a picture at rest, which is the best
    /// case for DLSS and FSR: they are given it over and over. Movement is where the ways
    /// part - what smears, what flickers, what is drawn that was never sent - and here each
    /// is given the same movement, from the same start.
    /// </para>
    /// </summary>
    public bool SaveClip(string path)
    {
        string root = Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path) + "-clip");

        // Held throughout: nothing new comes in while the clip goes round.
        lock (_context)
        {
            if (_clip is not { } clip || _clipCount == 0 || _videoTexture is null)
            {
                Log.Warn("clip: no frames were kept");
                return false;
            }

            int frames = _clipCount;
            bool dlssWas = DlssEnabled, fsrWas = FsrEnabled, compareWas = CompareScalers;
            bool vsrWas = Processor?.SuperResolutionEnabled == true;
            var modelWas = Dlss?.Model;
            if (Processor is { HdrEnabled: true })
                Log.Warn("clip: RTX Video HDR is on and takes the picture from DLSS and FSR; run with --no-auto-hdr");

            void VsrOff()
            {
                if (Processor is { SuperResolutionEnabled: true } p) p.ToggleSuperResolution();
            }

            // Each way, and how it is switched on. Each starts from nothing it remembers.
            var ways = new List<(string Name, Func<bool> On)>
            {
                ("none", () => { VsrOff(); DlssEnabled = false; FsrEnabled = false; return true; })
            };
            if (Processor is { Available: true, SuperResolutionSupported: true } && _vpTexture is not null && !SourceIsHdr)
                ways.Add(("vsr", () =>
                {
                    DlssEnabled = false; FsrEnabled = false;
                    return Processor!.SuperResolutionEnabled || Processor.ToggleSuperResolution();
                }));
            if (Dlss is { Possible: true } dlss)
                foreach (var model in (DlssModel[])[DlssModel.K, DlssModel.L, DlssModel.M])
                    ways.Add(("dlss-" + model, () =>
                    {
                        VsrOff(); FsrEnabled = false; DlssEnabled = true;
                        dlss.Model = model;
                        return true;
                    }));
            if (Fsr is { Possible: true, Building: false })
                ways.Add(("fsr", () => { VsrOff(); DlssEnabled = false; FsrEnabled = true; return true; }));
            else if (Fsr is { Possible: true })
                Log.Warn("clip: FSR is still being built, so it is left out");

            // The other temporal one is not to be fed alongside: each way stands alone.
            CompareScalers = false;

            void Feed(ID3D11Texture2D frame)
            {
                _context.CopyResource(_videoTexture!, frame);
                if (UseProcessor && _vpTexture is not null) _context.CopyResource(_vpTexture, frame);
                _uploadSerial++;
            }

            var (px, py, pw, ph) = FitPixels(_videoWidth, _videoHeight, Width, Height);
            using var back = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            var desc = back.Description;
            desc.Width = (uint)pw;
            desc.Height = (uint)ph;
            desc.Usage = ResourceUsage.Staging;
            desc.BindFlags = BindFlags.None;
            desc.CPUAccessFlags = CpuAccessFlags.Read;
            desc.MiscFlags = ResourceOptionFlags.None;
            using var staging = _device.CreateTexture2D(desc);
            var area = new Box(px, py, 0, px + pw, py + ph, 1);

            // Encoding is the slow part, so it runs beside the next frames, a few at a time.
            var gate = new SemaphoreSlim(Math.Max(2, Environment.ProcessorCount / 2));
            var writing = new List<Task>();
            int failed = 0;
            bool ok = true;
            var started = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                foreach (var (name, on) in ways)
                {
                    if (!on())
                    {
                        Log.Warn($"clip: {name} could not be switched on, so it is left out");
                        ok = false;
                        continue;
                    }

                    // The first frame shown a few times before the run: DLSS and FSR are built
                    // for it, and the driver brings RTX VSR in when it sees fit. What the motion
                    // estimate saw last is then this frame, so the run's first one has not moved;
                    // and whatever DLSS and FSR gathered on the way is let go.
                    bool vsr = name == "vsr";
                    for (int i = 0; i < (vsr ? 20 : 3); i++)
                    {
                        Feed(clip[0]);
                        Render();
                        if (vsr) Thread.Sleep(50);
                    }
                    Dlss?.ResetHistory();
                    Fsr?.ResetHistory();

                    // Named for the network that ran, which the driver may have chosen instead.
                    string folder = name;
                    if (name.StartsWith("dlss-", StringComparison.Ordinal) && Dlss?.ModelInUse is { } used && "dlss-" + used != name)
                        folder = name + "-ran-" + used;
                    string dir = Path.Combine(root, folder);
                    Directory.CreateDirectory(dir);

                    for (int i = 0; i < frames; i++)
                    {
                        Feed(clip[i]);
                        Render();
                        _context.CopySubresourceRegion(staging, 0, 0, 0, 0, back, 0, area);
                        var pixels = new byte[pw * ph * 4];
                        var map = _context.Map(staging, 0, MapMode.Read);
                        try { ToBgra(map, pw, ph, SwapChainIsPq, VideoNits, pixels); }
                        finally { _context.Unmap(staging, 0); }

                        string file = Path.Combine(dir, $"{i:D4}.png");
                        gate.Wait();
                        writing.Add(Task.Run(() =>
                        {
                            try { EncodePng(pixels, pw, ph, file); }
                            catch (Exception ex)
                            {
                                if (Interlocked.Increment(ref failed) == 1) Log.Warn($"clip: {file} not written: {ex.Message}");
                            }
                            finally { gate.Release(); }
                        }));
                    }
                    Log.Info($"clip: {frames} frames through {folder}");
                }
                Task.WaitAll([.. writing]);
            }
            finally
            {
                CompareScalers = compareWas;
                DlssEnabled = dlssWas;
                FsrEnabled = fsrWas;
                if (Dlss is { } d && modelWas is { } m) d.Model = m;
                if (vsrWas != (Processor?.SuperResolutionEnabled == true)) Processor?.ToggleSuperResolution();
            }

            Log.Info($"clip: {frames} frames of {pw}x{ph}, {ways.Count} ways, written to {root} in {started.Elapsed.TotalSeconds:F1} s");
            return ok && failed == 0;
        }
    }

    /// <summary>The estimated motion as numbers, beside a diagnostic capture; see <see cref="OpticalFlow.Save"/>.</summary>
    public bool SaveFlow(string path)
    {
        if (Flow is not { Ready: true } flow) return false;
        try
        {
            bool written;
            lock (_context) written = flow.Save(_context, path);
            if (written) Log.Info($"motion vectors written to {path}");
            return written;
        }
        catch (Exception ex)
        {
            Log.Warn($"motion vectors not written: {ex.Message}");
            return false;
        }
    }

    private static void WritePng(MappedSubresource map, int width, int height, bool pq, float nits, string path)
    {
        var pixels = new byte[width * height * 4];
        ToBgra(map, width, height, pq, nits, pixels);
        EncodePng(pixels, width, height, path);
    }

    private static unsafe void EncodePng(byte[] pixels, int width, int height, string path)
    {
        fixed (byte* p = pixels)
        {
            using var bmp = new Bitmap(width, height, width * 4, PixelFormat.Format32bppRgb, (nint)p);
            bmp.Save(path, ImageFormat.Png);
        }
    }

    /// <summary>
    /// The back buffer's 10-bit pixels as 8-bit BGRA, the way a PNG holds them, with the
    /// output encoding undone. Rows in parallel: a whole screen is millions of pixels.
    /// </summary>
    private static unsafe void ToBgra(MappedSubresource map, int width, int height, bool pq, float nits, byte[] pixels)
    {
        nint data = map.DataPointer;
        long pitch = map.RowPitch;
        fixed (byte* dst0 = pixels)
        {
            nint dst = (nint)dst0;
            Parallel.For(0, height, y =>
            {
                var src = (uint*)((byte*)data + y * pitch);
                var row = (byte*)dst + y * (long)width * 4;
                for (int x = 0; x < width; x++)
                {
                    uint v = src[x];
                    // R10G10B10A2 is least-significant-first, so red occupies bits 0-9 and
                    // blue 20-29 - the opposite way round from the name's reading order.
                    float r = (v & 0x3FF) / 1023f;
                    float g = ((v >> 10) & 0x3FF) / 1023f;
                    float b = ((v >> 20) & 0x3FF) / 1023f;

                    if (pq)
                    {
                        // Undo what the shaders did, in their order backwards: PQ to
                        // light, BT.2020 primaries to BT.709, the level SDR video is shown
                        // at back to 1, and the gamma on again. An SDR picture comes back
                        // as it went in; whatever is brighter than that level, or outside
                        // BT.709, clips, a PNG having no way to hold it.
                        float lr = PqToLight(r) / nits, lg = PqToLight(g) / nits, lb = PqToLight(b) / nits;
                        r = Gamma(1.6605f * lr - 0.5876f * lg - 0.0728f * lb);
                        g = Gamma(-0.1246f * lr + 1.1329f * lg - 0.0083f * lb);
                        b = Gamma(-0.0182f * lr - 0.1006f * lg + 1.1187f * lb);
                    }

                    row[x * 4 + 0] = ToByte(b);
                    row[x * 4 + 1] = ToByte(g);
                    row[x * 4 + 2] = ToByte(r);
                    row[x * 4 + 3] = 255;
                }
            });
        }

        static byte ToByte(float f) => (byte)Math.Clamp(f * 255f + 0.5f, 0, 255);

        // SMPTE ST 2084 backwards: the encoded value to nits.
        static float PqToLight(float e)
        {
            const float m1 = 0.1593017578125f, m2 = 78.84375f;
            const float c1 = 0.8359375f, c2 = 18.8515625f, c3 = 18.6875f;
            float p = MathF.Pow(MathF.Max(e, 0f), 1f / m2);
            float num = MathF.Max(p - c1, 0f);
            return 10000f * MathF.Pow(num / (c2 - c3 * p), 1f / m1);
        }

        static float Gamma(float linear) => MathF.Pow(Math.Clamp(linear, 0f, 1f), 1f / 2.2f);
    }

    /// <summary>Uploads a freshly drawn HUD bitmap. Called only when the text changes.</summary>
    public void UpdateHud(Bitmap bitmap)
    {
        if (_hudTexture is null || _hudWidth != bitmap.Width || _hudHeight != bitmap.Height)
        {
            _hudView?.Dispose(); _hudTexture?.Dispose();
            _hudTexture = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)bitmap.Width,
                Height = (uint)bitmap.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.Write
            });
            _hudView = _device.CreateShaderResourceView(_hudTexture);
            _hudWidth = bitmap.Width; _hudHeight = bitmap.Height;
        }

        // Straight (not premultiplied) alpha, matching the SrcAlpha/InvSrcAlpha blend state.
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            lock (_context)
            {
                var mapped = _context.Map(_hudTexture!, 0, Vortice.Direct3D11.MapMode.WriteDiscard);
                unsafe
                {
                    for (int row = 0; row < bitmap.Height; row++)
                    {
                        Buffer.MemoryCopy(
                            (byte*)data.Scan0 + row * data.Stride,
                            (byte*)mapped.DataPointer + row * (int)mapped.RowPitch,
                            mapped.RowPitch, bitmap.Width * 4);
                    }
                }
                _context.Unmap(_hudTexture!, 0);
            }
        }
        finally { bitmap.UnlockBits(data); }
    }

    /// <summary>
    /// Whether this draw's source pixels are SDR and therefore need PQ encoding. Only the
    /// video-processor output arrives already in PQ, and only while it was built to make
    /// an HDR picture.
    /// </summary>
    private uint NeedsPqEncode(uint mode) =>
        SwapChainIsPq && !(mode == 2 && Processor?.OutputIsHdr == true) ? 1u : 0u;

    private void SetConstants(float x, float y, float w, float h, uint mode, bool forceNoPq = false)
    {
        lock (_context)
        {
            var mapped = _context.Map(_constants, 0, Vortice.Direct3D11.MapMode.WriteDiscard);
            unsafe
            {
                var p = (float*)mapped.DataPointer;
                p[0] = x; p[1] = y; p[2] = w; p[3] = h;
                ((uint*)p)[4] = mode;
                ((uint*)p)[5] = forceNoPq ? 0u : NeedsPqEncode(mode);
                p[6] = VideoNits;
                // What the YUV planes hold. A picture made for a scaler (forceNoPq) is
                // always an SDR one.
                ((uint*)p)[7] = mode != 0 || !SourceIsHdr ? 0u : !forceNoPq && PassingHdrThrough ? 1u : 2u;
            }
            _context.Unmap(_constants, 0);
        }
    }

    private bool EnsureIntermediate(int width, int height)
    {
        if (width <= 0 || height <= 0) return false;
        if (_rgbIntermediate is not null
            && _rgbIntermediate.Description.Width == width
            && _rgbIntermediate.Description.Height == height)
            return true;

        _rgbIntermediateSrv?.Dispose(); _rgbIntermediateRtv?.Dispose(); _rgbIntermediate?.Dispose();
        _rgbIntermediate = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
        });
        _rgbIntermediateRtv = _device.CreateRenderTargetView(_rgbIntermediate);
        _rgbIntermediateSrv = _device.CreateShaderResourceView(_rgbIntermediate);
        Log.Debug($"RGB intermediate surface {width}x{height}");
        return true;
    }

    /// <summary>
    /// Runs the YUV conversion shader full-screen into the intermediate surface. With a
    /// shift, the picture lands that many of its own pixels to the right and down - parts
    /// of a pixel, in practice - which leaves a sliver along two edges as it was before.
    /// </summary>
    private bool ConvertYuvToIntermediate(float shiftX = 0f, float shiftY = 0f)
    {
        if (_rgbIntermediateRtv is null) return false;

        _context.OMSetRenderTargets(_rgbIntermediateRtv);
        _context.RSSetViewport(0, 0, _videoWidth, _videoHeight);
        // DLSS and FSR apply their own colour handling, so feed them plain SDR: mode 0 with no
        // PQ encoding, which is what SetConstants produces while SwapChainIsPq is false
        // and what the explicit mode-0 path does anyway.
        SetConstants(-1f + 2f * shiftX / _videoWidth, 1f - 2f * shiftY / _videoHeight, 2f, -2f, mode: 0, forceNoPq: true);
        _context.PSSetShaderResource(0, _lumaView);
        _context.PSSetShaderResource(1, _chromaView);
        _context.OMSetBlendState(null);
        _context.Draw(4, 0);
        return true;
    }

    /// <summary>Aspect-preserving fit of the source into the window, in client pixels.</summary>
    private static (int X, int Y, int W, int H) FitPixels(int srcW, int srcH, int dstW, int dstH)
    {
        if (srcW <= 0 || srcH <= 0) return (0, 0, Math.Max(1, dstW), Math.Max(1, dstH));
        double scale = Math.Min(dstW / (double)srcW, dstH / (double)srcH);
        int w = Math.Max(1, (int)Math.Round(srcW * scale)), h = Math.Max(1, (int)Math.Round(srcH * scale));
        return ((dstW - w) / 2, (dstH - h) / 2, w, h);
    }

    /// <summary>Same fit, expressed in NDC for the vertex shader.</summary>
    private static (float X, float Y, float W, float H) FitRect(int srcW, int srcH, int dstW, int dstH)
    {
        var (x, y, w, h) = FitPixels(srcW, srcH, dstW, dstH);
        return ToNdc(x, y, w, h, dstW, dstH);
    }

    private static (float X, float Y, float W, float H) ToNdc(int x, int y, int w, int h, int dstW, int dstH)
    {
        float nx = x / (float)dstW * 2f - 1f;
        float ny = 1f - y / (float)dstH * 2f;
        float nw = w / (float)dstW * 2f;
        float nh = -(h / (float)dstH * 2f);
        return (nx, ny, nw, nh);
    }

    private void ReleaseVideoResources()
    {
        _lumaView?.Dispose(); _lumaView = null;
        _chromaView?.Dispose(); _chromaView = null;
        _videoTexture?.Dispose(); _videoTexture = null;
        _vpTexture?.Dispose(); _vpTexture = null;
        _videoFormat = Format.Unknown;
        // Frames of another size or format cannot be put back into these.
        DropClip();
    }

    public void Dispose()
    {
        Ui?.Dispose();
        Processor?.Dispose();
        Dlss?.Dispose();
        FrameGen?.Dispose();
        Fsr?.Dispose();
        Flow?.Dispose();
        _rgbIntermediateSrv?.Dispose(); _rgbIntermediateRtv?.Dispose(); _rgbIntermediate?.Dispose();
        ReleaseVideoResources();
        _hudView?.Dispose(); _hudTexture?.Dispose();
        _alphaBlend?.Dispose(); _sampler?.Dispose(); _constants?.Dispose();
        _ps?.Dispose(); _vs?.Dispose();
        _backBufferView?.Dispose();
        _swapChain2?.Dispose(); _swapChain?.Dispose();
        _context?.Dispose(); _device?.Dispose();
    }

    private const string ShaderSource = """
        cbuffer Params : register(b0)
        {
            float4 Rect;   // xy = top-left in NDC, zw = size in NDC
            uint   Mode;   // 0 = YUV video, 1 = HUD overlay, 2 = pre-converted RGB
            uint   IsHdr;  // 1 when the swapchain is presenting PQ / BT.2020
            float  Nits;   // level SDR video is encoded at for a PQ swapchain
            uint   Source; // video: 0 = SDR, 1 = HDR10 shown as it is, 2 = HDR10 converted to SDR
        };

        // SMPTE ST 2084. Input is absolute luminance normalised to 10000 nits.
        float3 LinearToPQ(float3 L)
        {
            const float m1 = 0.1593017578125, m2 = 78.84375;
            const float c1 = 0.8359375, c2 = 18.8515625, c3 = 18.6875;
            float3 Lm = pow(saturate(L), m1);
            return pow((c1 + c2 * Lm) / (1.0 + c3 * Lm), m2);
        }

        // The HUD is authored in sRGB. Dropping those values straight into a PQ swapchain
        // would read as several thousand nits, so it is re-encoded at a readable level.
        // BT.709 primaries, which SDR video and the interface are in, to the BT.2020 ones
        // the swapchain declares. Left out, the same numbers are read as BT.2020 colours
        // and every colour comes out stronger than it was meant: a plain red as a red no
        // SDR picture can hold.
        static const float3x3 Rec709To2020 = {
            0.6274, 0.3293, 0.0433,
            0.0691, 0.9195, 0.0114,
            0.0164, 0.0880, 0.8956 };

        float3 SdrToPQ(float3 srgb, float nits)
        {
            return LinearToPQ(mul(Rec709To2020, pow(srgb, 2.2)) * (nits / 10000.0));
        }

        // SMPTE ST 2084 backwards: the encoded value to nits.
        float3 PQToNits(float3 e)
        {
            const float m1 = 0.1593017578125, m2 = 78.84375;
            const float c1 = 0.8359375, c2 = 18.8515625, c3 = 18.6875;
            float3 p = pow(saturate(e), 1.0 / m2);
            return 10000.0 * pow(max(p - c1, 0.0) / (c2 - c3 * p), 1.0 / m1);
        }

        static const float3x3 Rec2020To709 = {
             1.6605, -0.5876, -0.0728,
            -0.1246,  1.1329, -0.0083,
            -0.0182, -0.1006,  1.1187 };

        // An HDR10 picture as an SDR one. HDR's reference white, 203 nits, is where SDR
        // white belongs, so what an HDR picture shows at ordinary brightness - faces, the
        // interface - comes out as an SDR picture would have it. Up to three quarters of
        // that nothing is changed; above it the rest of the range, up to 1000 nits, is
        // folded into the quarter that is left, by brightness and not channel by channel,
        // so that a highlight keeps its colour as it is brought down.
        float3 HdrToSdr(float3 pq)
        {
            const float White = 203.0;
            const float Most = 1000.0 / White;
            const float Knee = 0.75;

            float3 light = PQToNits(pq) / White;
            float  lum = dot(light, float3(0.2627, 0.6780, 0.0593));
            if (lum > Knee)
            {
                float x = (lum - Knee) / (1.0 - Knee);
                float top = (Most - Knee) / (1.0 - Knee);
                float folded = Knee + (1.0 - Knee) * x / (1.0 + x) * (1.0 + top) / top;
                light *= min(folded, 1.0) / lum;
            }
            return pow(saturate(mul(Rec2020To709, light)), 1.0 / 2.2);
        }

        Texture2D<float>  LumaTex   : register(t0);
        Texture2D<float2> ChromaTex : register(t1);
        Texture2D<float4> HudTex    : register(t2);
        SamplerState      Samp      : register(s0);

        struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

        VSOut VS(uint vid : SV_VertexID)
        {
            float2 t = float2(vid & 1, (vid >> 1) & 1);
            VSOut o;
            o.pos = float4(Rect.xy + t * Rect.zw, 0.0, 1.0);
            o.uv  = t;
            return o;
        }

        float4 PS(VSOut i) : SV_Target
        {
            // NOTE: keep this shader pure ASCII. Non-ASCII bytes truncate the source.
            // Video processor output. Already PQ when its HDR filter ran, otherwise SDR
            // that still has to be encoded for a PQ swapchain.
            if (Mode == 2)
            {
                float3 vp = HudTex.Sample(Samp, i.uv).rgb;
                if (IsHdr != 0) vp = SdrToPQ(vp, Nits);
                return float4(vp, 1.0);
            }

            if (Mode == 1)
            {
                float4 hud = HudTex.Sample(Samp, i.uv);
                if (IsHdr != 0) hud.rgb = SdrToPQ(hud.rgb, Nits);
                return hud;
            }

            // BT.709, limited range. P010 keeps its 10 bits left-aligned in 16, so the
            // normalised values land on the same scale as 8-bit and need no special case.
            float  y  = LumaTex.Sample(Samp, i.uv);
            float2 cb = ChromaTex.Sample(Samp, i.uv) - 0.5;

            if (Source != 0)
            {
                // HDR10: BT.2020, ten bits in 64-940 and 64-960, PQ.
                float  yh = (y - 0.0625) * 1.16893;
                float3 pq = saturate(float3(yh + 1.68523 * cb.y,
                                            yh - 0.18806 * cb.x - 0.65296 * cb.y,
                                            yh + 2.15013 * cb.x));
                // As it is: the swapchain is PQ / BT.2020 too, so these are its values already.
                if (Source == 1) return float4(pq, 1.0);

                float3 sdr = HdrToSdr(pq);
                if (IsHdr != 0) sdr = SdrToPQ(sdr, Nits);
                return float4(sdr, 1.0);
            }

            y = (y - 0.0625) * 1.1643836;
            float r = y + 1.7927410 * cb.y;
            float g = y - 0.2132486 * cb.x - 0.5329093 * cb.y;
            float b = y + 2.1124018 * cb.x;

            float3 rgb = saturate(float3(r, g, b));
            // Our own shader only ever produces SDR; map it into PQ so the picture stays
            // correct when the swapchain is in HDR and RTX Video HDR is switched off.
            if (IsHdr != 0) rgb = SdrToPQ(rgb, Nits);
            return float4(rgb, 1.0);
        }
        """;
}
