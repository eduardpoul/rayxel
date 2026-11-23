using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Rayxel.Diagnostics;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Rayxel.Render;

/// <summary>
/// The D3D11 video processor path. RTX Video HDR is a driver feature exposed only here —
/// it cannot be applied to an arbitrary texture, it has to be switched on for a video
/// stream inside a VideoProcessorBlt. This is the same call browsers make; nothing about
/// it is private to NVIDIA's own software.
/// <para>
/// The GUID and the payload's layout are taken from mpv's vf_d3d11vpp, which is where
/// this interface is documented in practice.
/// </para>
/// <para>
/// The same path carries the drivers' own upscalers, RTX Video Super Resolution among
/// them. That used to be offered here; with DLSS and FSR it no longer is.
/// </para>
/// </summary>
public sealed class VideoProcessor : IDisposable
{
    /// <summary>NVIDIA_TRUE_HDR_INTERFACE_GUID — RTX Video HDR (SDR to HDR inverse tone map).</summary>
    private static readonly Guid NvidiaTrueHdr =
        new(0xfdd62bb4, 0x620b, 0x4fd7, 0x9a, 0xb3, 0x1e, 0x59, 0xd0, 0xd5, 0x44, 0xb3);

    /// <summary>The payload NVIDIA's extension takes: a version, a method selector and an enable flag.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NvidiaExtension
    {
        public uint Version;
        public uint Method;
        public uint Enable;
    }

    public enum GpuVendor { Unknown, Nvidia, Intel, Amd }

    public GpuVendor Vendor { get; private set; } = GpuVendor.Unknown;

    /// <summary>RTX Video HDR has no Intel or AMD equivalent — nothing to offer there.</summary>
    public bool HdrFilterSupported => Vendor == GpuVendor.Nvidia;

    private readonly ID3D11Device _device;
    private ID3D11VideoDevice? _videoDevice;
    private ID3D11VideoContext? _videoContext;
    private ID3D11VideoProcessor? _processor;
    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessorOutputView? _outputView;

    private ID3D11Texture2D? _output;
    private ID3D11ShaderResourceView? _outputSrv;

    private int _inWidth, _inHeight, _outWidth, _outHeight;
    private Format _inFormat = Format.Unknown;

    public bool Available => _videoDevice is not null && _videoContext is not null;
    public ID3D11ShaderResourceView? OutputView => _outputSrv;
    public int OutputWidth => _outWidth;
    public int OutputHeight => _outHeight;

    public bool HdrEnabled { get; private set; }

    /// <summary>Set when the driver rejected an extension, so the UI can stop claiming it is on.</summary>
    public string? LastError { get; private set; }

    public VideoProcessor(ID3D11Device device)
    {
        _device = device;
        _videoDevice = device.QueryInterfaceOrNull<ID3D11VideoDevice>();
        _videoContext = device.ImmediateContext.QueryInterfaceOrNull<ID3D11VideoContext>();

        if (!Available) { Log.Warn("ID3D11VideoDevice/VideoContext unavailable - RTX Video HDR cannot be used"); return; }

        Vendor = DetectVendor(device);
        Log.Info($"D3D11 video processor available, GPU vendor: {Vendor} " +
                 $"(HDR filter: {(HdrFilterSupported ? "supported" : "NVIDIA only")})");
    }

    private static GpuVendor DetectVendor(ID3D11Device device)
    {
        try
        {
            using var dxgiDevice = device.QueryInterfaceOrNull<IDXGIDevice>();
            using var adapter = dxgiDevice?.GetAdapter();
            if (adapter is null) return GpuVendor.Unknown;
            return adapter.Description.VendorId switch
            {
                0x10DE => GpuVendor.Nvidia,
                0x8086 => GpuVendor.Intel,
                0x1002 or 0x1022 => GpuVendor.Amd,
                _ => GpuVendor.Unknown
            };
        }
        catch (Exception ex) { Log.Warn($"vendor detection failed: {ex.Message}"); return GpuVendor.Unknown; }
    }

    /// <summary>
    /// (Re)builds the processor for a given input/output size. The output is the size the
    /// picture is shown at: the driver's own scaler takes it there.
    /// </summary>
    public void Configure(int inWidth, int inHeight, Format inFormat, int outWidth, int outHeight)
    {
        if (!Available) return;
        if (_processor is not null && _inWidth == inWidth && _inHeight == inHeight
            && _outWidth == outWidth && _outHeight == outHeight && _inFormat == inFormat
            && _builtForHdr == _outputHdr)
            return;

        ReleaseProcessor();

        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)inWidth,
            InputHeight = (uint)inHeight,
            OutputWidth = (uint)outWidth,
            OutputHeight = (uint)outHeight,
            Usage = VideoUsage.PlaybackNormal
        };

        _enumerator = _videoDevice!.CreateVideoProcessorEnumerator(content);
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);

        _output = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)outWidth,
            Height = (uint)outHeight,
            MipLevels = 1,
            ArraySize = 1,
            // The format is what tells the driver what kind of picture to write, more
            // than the colour space set below does. Into a 10-bit surface, on a desktop
            // that is in HDR, NVIDIA's driver writes PQ / BT.2020 whatever it was asked
            // for; it writes plain sRGB only into an 8-bit one. So 10 bits when RTX Video
            // HDR is to produce the picture - it needs the room - and 8 otherwise. Getting
            // this wrong is not subtle: an SDR picture that is already PQ gets encoded to
            // PQ a second time, and comes out at a third of its brightness and grey.
            Format = _outputHdr ? Format.R10G10B10A2_UNorm : Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget
        });
        _outputSrv = _device.CreateShaderResourceView(_output);
        _outputView = _videoDevice.CreateVideoProcessorOutputView(_output, _enumerator,
            new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D });

        _inWidth = inWidth; _inHeight = inHeight; _inFormat = inFormat;
        _outWidth = outWidth; _outHeight = outHeight;
        _builtForHdr = _outputHdr;

        Log.Info($"video processor: {inWidth}x{inHeight} {inFormat} -> {outWidth}x{outHeight} " +
                 $"(scale {outWidth / (double)inWidth:0.00}x), {(_outputHdr ? "HDR" : "SDR")} out");

        // Without an explicit colour space the driver has to guess how to interpret the
        // YUV planes, and guesses wrong — the symptom is a completely black output.
        // The capture reports BT.709 with a 16-235 nominal range.
        try
        {
            var input = new VideoProcessorColorSpace
            {
                Usage = 0,          // 0 = playback (quality), 1 = video processing
                RGB_Range = 0,       // full range, only meaningful for RGB inputs
                YCbCr_Matrix = 1,    // 1 = BT.709
                YCbCr_xvYCC = 0,
                Nominal_Range = 1    // 1 = 16-235 studio range
            };
            _videoContext!.VideoProcessorSetStreamColorSpace(_processor, 0, input);
            // The same again in the newer form. The output is described in that form
            // (ApplyOutputColorSpace), and the two halves of a conversion are better
            // stated in one vocabulary than in two.
            using (var vc1 = _videoContext!.QueryInterfaceOrNull<ID3D11VideoContext1>())
                vc1?.VideoProcessorSetStreamColorSpace1(_processor, 0, ColorSpaceType.YcbcrStudioG22LeftP709);

            ApplyOutputColorSpace();

            // Let the driver pick its best scaler rather than a fixed one.
            _videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, true);
            Log.Debug("video processor colour space set: BT.709 limited in, full-range RGB out");
        }
        catch (SharpGenException e)
        {
            Log.Warn($"colour space setup failed: {e.ResultCode}");
        }

        // The extension lives on the processor, so it is applied again to a new one.
        if (HdrEnabled) ApplyHdr(true);
    }

    private bool _outputHdr, _builtForHdr;

    /// <summary>
    /// Whether the processor is to produce an HDR picture (PQ / BT.2020), which is what
    /// RTX Video HDR makes, or an SDR one. It is rebuilt on the spot when this changes,
    /// because the surface it writes to has to change with it: see <see cref="Configure"/>.
    /// </summary>
    public void SetOutputHdr(bool hdr)
    {
        if (_outputHdr == hdr) return;
        _outputHdr = hdr;
        if (_processor is not null) Configure(_inWidth, _inHeight, _inFormat, _outWidth, _outHeight);
    }

    /// <summary>True when what <see cref="OutputView"/> holds is PQ / BT.2020 rather than sRGB.</summary>
    public bool OutputIsHdr => _builtForHdr;

    private void ApplyOutputColorSpace()
    {
        if (_processor is null || _videoContext is null) return;
        try
        {
            // D3D11_VIDEO_PROCESSOR_COLOR_SPACE cannot express BT.2020/PQ on its own, so
            // the HDR case goes through the DXGI colour-space entry point on VideoContext1.
            using var vc1 = _videoContext.QueryInterfaceOrNull<ID3D11VideoContext1>();
            if (vc1 is not null)
            {
                vc1.VideoProcessorSetOutputColorSpace1(_processor,
                    _outputHdr ? ColorSpaceType.RgbFullG2084NoneP2020 : ColorSpaceType.RgbFullG22NoneP709);
                Log.Debug($"video processor output colour space: {(_outputHdr ? "PQ / BT.2020" : "sRGB / BT.709")}");
                return;
            }

            _videoContext.VideoProcessorSetOutputColorSpace(_processor, new VideoProcessorColorSpace
            {
                Usage = 0, RGB_Range = 0, YCbCr_Matrix = 1, YCbCr_xvYCC = 0, Nominal_Range = 2
            });
            if (_outputHdr) Log.Warn("ID3D11VideoContext1 unavailable — HDR output colour space cannot be set");
        }
        catch (Exception ex) { Log.Warn($"output colour space failed: {ex.Message}"); }
    }

    public bool ToggleHdr() => ApplyHdr(!HdrEnabled);

    private bool ApplyHdr(bool enable)
    {
        if (!HdrFilterSupported)
        {
            LastError = $"no SDR-to-HDR video filter on {Vendor} hardware";
            Log.Warn(LastError);
            return false;
        }
        var ext = new NvidiaExtension { Version = 4, Method = 3, Enable = enable ? 1u : 0u };
        if (!SetExtension(NvidiaTrueHdr, ext, "RTX Video HDR")) return false;
        HdrEnabled = enable;
        return true;
    }

    private unsafe bool SetExtension<T>(Guid guid, T ext, string label) where T : unmanaged
    {
        if (_processor is null || _videoContext is null)
        {
            LastError = $"{label}: no video processor configured";
            Log.Warn(LastError);
            return false;
        }

        try
        {
            _videoContext.VideoProcessorSetStreamExtension(_processor, 0, guid,
                (uint)sizeof(T), new IntPtr(&ext));
            LastError = null;
            Log.Info($"{label}: extension accepted");
            return true;
        }
        catch (Exception e)
        {
            // A non-NVIDIA GPU, or a driver that does not expose this feature, fails here.
            // Catching everything is deliberate: this runs on the render thread and an
            // escaping exception would take the process down rather than disable a toggle.
            LastError = $"{label} rejected: {e.GetType().Name}: {e.Message}";
            Log.Warn(LastError);
            return false;
        }
    }

    private ID3D11VideoProcessorInputView? _inputView;
    private ID3D11Texture2D? _inputViewSource;

    /// <summary>Runs one frame through the processor. Returns false if the path is not usable.</summary>
    public bool Process(ID3D11Texture2D input)
    {
        if (!Available || _processor is null || _outputView is null) return false;

        // Input views are expensive to build — one per texture, not one per frame. The
        // capture texture is stable between mode changes, so this is created once.
        if (_inputView is null || !ReferenceEquals(_inputViewSource, input))
        {
            _inputView?.Dispose();
            _inputView = _videoDevice!.CreateVideoProcessorInputView(input, _enumerator!,
                new VideoProcessorInputViewDescription { ViewDimension = VideoProcessorInputViewDimension.Texture2D });
            _inputViewSource = input;
            Log.Debug("video processor input view created");
        }
        var inputView = _inputView;

        var stream = new VideoProcessorStream
        {
            Enable = true,
            InputSurface = inputView
        };

        _videoContext!.VideoProcessorBlt(_processor, _outputView, 0, [stream]);
        return true;
    }

    private void ReleaseProcessor()
    {
        _inputView?.Dispose(); _inputView = null; _inputViewSource = null;
        _outputSrv?.Dispose(); _outputSrv = null;
        _output?.Dispose(); _output = null;
        _outputView?.Dispose(); _outputView = null;
        _processor?.Dispose(); _processor = null;
        _enumerator?.Dispose(); _enumerator = null;
    }

    public void Dispose()
    {
        ReleaseProcessor();
        _videoContext?.Dispose(); _videoContext = null;
        _videoDevice?.Dispose(); _videoDevice = null;
    }
}
