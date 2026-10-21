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
    /// The VSR/RTX-HDR path. Null until <see cref="Initialize"/> runs. When either
    /// feature is on, frames go through VideoProcessorBlt instead of our own shader,
    /// because that is the only place the driver exposes them.
    /// </summary>
    public VideoProcessor? Processor { get; private set; }

    /// <summary>True while a frame should be routed through the video processor.</summary>
    public bool UseProcessor => Processor is { Available: true } p && (p.SuperResolutionEnabled || p.HdrEnabled);

    /// <summary>Vendor-neutral upscaler. Mutually exclusive with the driver path.</summary>
    public NisScaler? Nis { get; private set; }

    /// <summary>Requested by the user; only actually used when the driver path is off.</summary>
    public bool NisEnabled { get; set; }

    public bool UseNis => NisEnabled && !UseProcessor && Nis is not null;

    // Intermediate RGB surface: NIS consumes colour, not YUV planes, so the conversion
    // shader has to run into a texture before the compute pass can see it.
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
        Nis = new NisScaler(_device);

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
    }

    /// <summary>
    /// Allocates the plane-pair texture for a capture format. NV12 and P010 differ only
    /// in the SRV formats: 8-bit planes become R8/R8G8, 10-bit planes R16/R16G16, and the
    /// same shader maths covers both because P010 stores its 10 bits left-aligned in 16.
    /// </summary>
    public void ConfigureVideo(int width, int height, bool isTenBit)
    {
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
        // one is active. Creating it is best-effort: without it, VSR is simply unavailable.
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
            Log.Warn($"decoder-bindable texture unavailable, VSR/HDR disabled: {ex.Message}");
        }

        var lumaFormat = isTenBit ? Format.R16_UNorm : Format.R8_UNorm;
        var chromaFormat = isTenBit ? Format.R16G16_UNorm : Format.R8G8_UNorm;
        _lumaView = _device.CreateShaderResourceView(_videoTexture,
            new ShaderResourceViewDescription(_videoTexture, ShaderResourceViewDimension.Texture2D, lumaFormat));
        _chromaView = _device.CreateShaderResourceView(_videoTexture,
            new ShaderResourceViewDescription(_videoTexture, ShaderResourceViewDimension.Texture2D, chromaFormat));

        _videoWidth = width; _videoHeight = height; _videoFormat = format;
        Log.Info($"video surface configured: {width}x{height} {format} ({(isTenBit ? "10" : "8")}-bit)");
    }

    /// <summary>
    /// Copies one MF frame into our sampleable texture, and — when VSR or RTX HDR is on
    /// — immediately runs it through the video processor so the upscale happens on the
    /// capture thread's schedule rather than the present thread's.
    /// </summary>
    public void UploadFrame(ID3D11Texture2D source, int subresource)
    {
        if (_videoTexture is null) return;
        // Copy only. The video processor blt deliberately does NOT run here: it shares the
        // immediate context with the render thread, and issuing it from the capture thread
        // put both threads in contention on the same lock for tens of milliseconds a frame.
        lock (_context)
        {
            _context.CopySubresourceRegion(_videoTexture, 0, 0, 0, 0, source, (uint)subresource);
            if (UseProcessor && _vpTexture is not null)
                _context.CopySubresourceRegion(_vpTexture, 0, 0, 0, 0, source, (uint)subresource);
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

            _context.OMSetRenderTargets(_backBufferView);
            _context.RSSetViewport(0, 0, Width, Height);
            _context.ClearRenderTargetView(_backBufferView, new Color4(0f, 0f, 0f, 1f));

            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
            _context.VSSetShader(_vs);
            _context.PSSetShader(_ps);
            _context.PSSetSampler(0, _sampler);
            _context.VSSetConstantBuffer(0, _constants);
            _context.PSSetConstantBuffer(0, _constants);

            var (fx, fy, fw, fh) = FitRect(_videoWidth, _videoHeight, Width, Height);

            // Build the processor even while both toggles are off: extensions are set on an
            // existing processor, so it must exist before VSR can be switched on at all.
            if (Processor is { Available: true } proc && _vpTexture is not null && _videoWidth > 0)
            {
                var (_, _, fitW, fitH) = FitPixels(_videoWidth, _videoHeight, Width, Height);
                proc.Configure(_videoWidth, _videoHeight, _videoFormat, fitW, fitH);
                if (UseProcessor) proc.Process(_vpTexture);
            }

            // NIS wants colour in and writes colour out, so the YUV conversion runs first
            // into an offscreen surface and the compute pass upscales from there.
            ID3D11ShaderResourceView? nisResult = null;
            if (UseNis && _lumaView is not null && _chromaView is not null)
            {
                var (_, _, fitW, fitH) = FitPixels(_videoWidth, _videoHeight, Width, Height);
                if (EnsureIntermediate(_videoWidth, _videoHeight) && ConvertYuvToIntermediate())
                {
                    // The intermediate is still bound as a render target here. D3D11 will
                    // not let the same resource be an RTV and an SRV at once and silently
                    // drops the SRV, so it has to be released before the compute pass.
                    _context.OMSetRenderTargets((ID3D11RenderTargetView?)null);

                    var hdrMode = SwapChainIsPq ? NisHdrMode.Pq : NisHdrMode.None;
                    if (Nis!.Process(_context, _rgbIntermediateSrv!, _videoWidth, _videoHeight, fitW, fitH, hdrMode))
                        nisResult = Nis.OutputView;
                }
                // Restore the back buffer: the conversion pass retargeted the pipeline.
                _context.OMSetRenderTargets(_backBufferView);
                _context.RSSetViewport(0, 0, Width, Height);
            }

            if (nisResult is not null)
            {
                // NIS output is SDR RGB regardless of mode, so it takes the encoding path.
                SetConstants(fx, fy, fw, fh, mode: 1);
                _context.PSSetShaderResource(2, nisResult);
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
                try { WritePng(map, (int)desc.Width, (int)desc.Height, SwapChainIsPq, path); }
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

    private static unsafe void WritePng(MappedSubresource map, int width, int height, bool pq, string path)
    {
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, width, height);
        var dst = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < height; y++)
            {
                var src = (uint*)((byte*)map.DataPointer + y * (long)map.RowPitch);
                var row = (byte*)dst.Scan0 + y * (long)dst.Stride;
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
                        // Undo what the shaders did: PQ back to luminance, back off the
                        // 200-nit level the UI was encoded at, then re-apply sRGB gamma.
                        r = PqToSrgb(r); g = PqToSrgb(g); b = PqToSrgb(b);
                    }

                    row[x * 4 + 0] = ToByte(b);
                    row[x * 4 + 1] = ToByte(g);
                    row[x * 4 + 2] = ToByte(r);
                    row[x * 4 + 3] = 255;
                }
            }
        }
        finally { bmp.UnlockBits(dst); }

        bmp.Save(path, ImageFormat.Png);

        static byte ToByte(float f) => (byte)Math.Clamp(f * 255f + 0.5f, 0, 255);

        static float PqToSrgb(float e)
        {
            const float m1 = 0.1593017578125f, m2 = 78.84375f;
            const float c1 = 0.8359375f, c2 = 18.8515625f, c3 = 18.6875f;
            float p = MathF.Pow(MathF.Max(e, 0f), 1f / m2);
            float num = MathF.Max(p - c1, 0f);
            float lum = MathF.Pow(num / (c2 - c3 * p), 1f / m1);   // 0..1 of 10000 nits
            // The reference level everything SDR was encoded at, so it comes back as 1.0.
            return MathF.Pow(Math.Clamp(lum * 10000f / 200f, 0f, 1f), 1f / 2.2f);
        }
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
    /// video-processor output arrives already in PQ, and only while its HDR filter is on.
    /// </summary>
    private uint NeedsPqEncode(uint mode) =>
        SwapChainIsPq && !(mode == 2 && Processor?.HdrEnabled == true) ? 1u : 0u;

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
        Log.Debug($"NIS intermediate surface {width}x{height}");
        return true;
    }

    /// <summary>Runs the YUV conversion shader full-screen into the intermediate surface.</summary>
    private bool ConvertYuvToIntermediate()
    {
        if (_rgbIntermediateRtv is null) return false;

        _context.OMSetRenderTargets(_rgbIntermediateRtv);
        _context.RSSetViewport(0, 0, _videoWidth, _videoHeight);
        // NIS applies its own colour handling, so feed it plain SDR: mode 0 with no
        // PQ encoding, which is what SetConstants produces while SwapChainIsPq is false
        // and what the explicit mode-0 path does anyway.
        SetConstants(-1f, 1f, 2f, -2f, mode: 0, forceNoPq: true);
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
    }

    public void Dispose()
    {
        Ui?.Dispose();
        Processor?.Dispose();
        Nis?.Dispose();
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
            uint   _pad;
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
        float3 SdrToPQ(float3 srgb, float nits)
        {
            return LinearToPQ(pow(srgb, 2.2) * (nits / 10000.0));
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

            y = (y - 0.0625) * 1.1643836;
            float r = y + 1.7927410 * cb.y;
            float g = y - 0.2132486 * cb.x - 0.5329093 * cb.y;
            float b = y + 2.1124018 * cb.x;

            float3 rgb = saturate(float3(r, g, b));
            // Our own shader only ever produces SDR; map it into PQ so the picture stays
            // correct when the swapchain is in HDR and VSR/HDR are switched off.
            if (IsHdr != 0) rgb = SdrToPQ(rgb, Nits);
            return float4(rgb, 1.0);
        }
        """;
}
