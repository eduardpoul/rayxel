using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Rayxel.Diagnostics;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Rayxel.Render;

/// <summary>
/// The D3D11 video processor path. RTX VSR and RTX Video HDR are driver features
/// exposed only here — they cannot be applied to an arbitrary texture, they have to be
/// switched on for a video stream inside a VideoProcessorBlt. This is the same call
/// browsers make; nothing about it is private to NVIDIA's own software.
/// <para>
/// GUIDs and payload layouts are taken from mpv's vf_d3d11vpp, which is where these
/// interfaces are documented in practice.
/// </para>
/// </summary>
public sealed class VideoProcessor : IDisposable
{
    /// <summary>NVIDIA_PPE_INTERFACE_GUID — RTX Video Super Resolution.</summary>
    private static readonly Guid NvidiaSuperResolution =
        new(0xd43ce1b3, 0x1f4b, 0x48ac, 0xba, 0xee, 0xc3, 0xc2, 0x53, 0x75, 0xe6, 0xf7);

    /// <summary>NVIDIA_TRUE_HDR_INTERFACE_GUID — RTX Video HDR (SDR to HDR inverse tone map).</summary>
    private static readonly Guid NvidiaTrueHdr =
        new(0xfdd62bb4, 0x620b, 0x4fd7, 0x9a, 0xb3, 0x1e, 0x59, 0xd0, 0xd5, 0x44, 0xb3);

    /// <summary>INTEL_VPE_INTERFACE_GUID — Intel's video processing extension entry point.</summary>
    private static readonly Guid IntelVpe =
        new(0xedd1d4b9, 0x8659, 0x4cbc, 0xa4, 0xd6, 0x98, 0x31, 0xa2, 0x16, 0x3a, 0xc3);

    private const uint IntelVpeFunctionScaling = 0x37;
    private const uint IntelVpeScalingDefault = 0x0;
    private const uint IntelVpeScalingSuperResolution = 0x2;

    /// <summary>Payload both NVIDIA extensions take: a version, a method selector and an enable flag.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NvidiaExtension
    {
        public uint Version;
        public uint Method;
        public uint Enable;
    }

    /// <summary>
    /// Intel's payload is a function selector plus a pointer to that function's argument,
    /// rather than a flat struct like NVIDIA's.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct IntelExtension
    {
        public uint Function;
        public IntPtr Param;
    }

    public enum GpuVendor { Unknown, Nvidia, Intel, Amd }

    public GpuVendor Vendor { get; private set; } = GpuVendor.Unknown;

    /// <summary>True when this GPU exposes a driver super-resolution filter we know how to drive.</summary>
    public bool SuperResolutionSupported => Vendor is GpuVendor.Nvidia or GpuVendor.Intel;

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

    public bool SuperResolutionEnabled { get; private set; }
    public bool HdrEnabled { get; private set; }

    /// <summary>Set when the driver rejected an extension, so the UI can stop claiming it is on.</summary>
    public string? LastError { get; private set; }

    public VideoProcessor(ID3D11Device device)
    {
        _device = device;
        _videoDevice = device.QueryInterfaceOrNull<ID3D11VideoDevice>();
        _videoContext = device.ImmediateContext.QueryInterfaceOrNull<ID3D11VideoContext>();

        if (!Available) { Log.Warn("ID3D11VideoDevice/VideoContext unavailable - VSR and RTX HDR cannot be used"); return; }

        Vendor = DetectVendor(device);
        Log.Info($"D3D11 video processor available, GPU vendor: {Vendor} " +
                 $"(super resolution: {(SuperResolutionSupported ? "supported" : "no driver filter")}, " +
                 $"HDR filter: {(HdrFilterSupported ? "supported" : "NVIDIA only")})");
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
    /// (Re)builds the processor for a given input/output size. Output is the upscale
    /// target: VSR only engages when the output is larger than the input.
    /// </summary>
    public void Configure(int inWidth, int inHeight, Format inFormat, int outWidth, int outHeight)
    {
        if (!Available) return;
        if (_processor is not null && _inWidth == inWidth && _inHeight == inHeight
            && _outWidth == outWidth && _outHeight == outHeight && _inFormat == inFormat)
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
            // 10-bit so RTX Video HDR has somewhere to put the range it generates.
            Format = Format.R10G10B10A2_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget
        });
        _outputSrv = _device.CreateShaderResourceView(_output);
        _outputView = _videoDevice.CreateVideoProcessorOutputView(_output, _enumerator,
            new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D });

        _inWidth = inWidth; _inHeight = inHeight; _inFormat = inFormat;
        _outWidth = outWidth; _outHeight = outHeight;

        Log.Info($"video processor: {inWidth}x{inHeight} {inFormat} -> {outWidth}x{outHeight} " +
                 $"(scale {outWidth / (double)inWidth:0.00}x)");

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

            ApplyOutputColorSpace();

            // Let the driver pick its best scaler rather than a fixed one; VSR replaces it
            // when the extension is enabled.
            _videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, true);
            Log.Debug("video processor colour space set: BT.709 limited in, full-range RGB out");
        }
        catch (SharpGenException e)
        {
            Log.Warn($"colour space setup failed: {e.ResultCode}");
        }

        // Extensions live on the processor, so re-apply whatever the user had toggled on.
        if (SuperResolutionEnabled) ApplySuperResolution(true);
        if (HdrEnabled) ApplyHdr(true);
    }

    private bool _outputHdr;

    /// <summary>
    /// Tells the processor whether its output goes to a PQ / BT.2020 swapchain. Without
    /// this the driver keeps converting to BT.709 and RTX Video HDR has no visible effect.
    /// </summary>
    public void SetOutputHdr(bool hdr)
    {
        _outputHdr = hdr;
        ApplyOutputColorSpace();
    }

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

    public bool ToggleSuperResolution() => ApplySuperResolution(!SuperResolutionEnabled);
    public bool ToggleHdr() => ApplyHdr(!HdrEnabled);

    private bool ApplySuperResolution(bool enable)
    {
        switch (Vendor)
        {
            case GpuVendor.Nvidia:
            {
                var ext = new NvidiaExtension { Version = 1, Method = 2, Enable = enable ? 1u : 0u };
                if (!SetExtension(NvidiaSuperResolution, ext, "RTX VSR")) return false;
                break;
            }
            case GpuVendor.Intel:
                if (!ApplyIntelSuperResolution(enable)) return false;
                break;
            default:
                LastError = $"no driver super resolution on {Vendor} hardware";
                Log.Warn(LastError);
                return false;
        }

        SuperResolutionEnabled = enable;
        return true;
    }

    /// <summary>
    /// Intel's scaling mode is selected by pointing the extension at a mode value rather
    /// than setting an enable bit, so switching off means selecting the default scaler.
    /// Written from the documented layout but never run on Intel hardware - unverified.
    /// </summary>
    private unsafe bool ApplyIntelSuperResolution(bool enable)
    {
        uint mode = enable ? IntelVpeScalingSuperResolution : IntelVpeScalingDefault;
        var ext = new IntelExtension { Function = IntelVpeFunctionScaling, Param = new IntPtr(&mode) };
        return SetExtension(IntelVpe, ext, "Intel VSR");
    }

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
