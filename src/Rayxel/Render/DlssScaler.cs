using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Rayxel.Diagnostics;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Rayxel.Render;

/// <summary>
/// Which of DLSS's networks to ask for. They go by letter, in the order they came out: K
/// with DLSS 4, L and M with DLSS 4.5, where L is the heavier of the two and M the quicker.
/// The values are NVSDK_NGX_DLSS_Hint_Render_Preset.
/// </summary>
public enum DlssModel
{
    K = 11,
    L = 12,
    M = 13
}

/// <summary>
/// DLSS Super Resolution, run on a video instead of on a game.
/// <para>
/// DLSS is a temporal upscaler: it builds each output frame from the new picture and from
/// what it has accumulated out of the ones before, and to line those up it has to be told
/// how everything moved. A game hands it that. Here it comes from <see cref="OpticalFlow"/>,
/// which only estimates it, and where the estimate is wrong DLSS smears or trails.
/// </para>
/// <para>
/// Two more things a game supplies cannot be recovered from a video at all. One is depth,
/// which gets a flat constant. The other is the sub-pixel shift a game gives its camera
/// from frame to frame, which is where DLSS finds detail that no single frame holds: a
/// video has every frame on the same grid, so what this can add is steadier edges and
/// less noise, not detail that was never sent.
/// </para>
/// <para>
/// It runs inside the NVIDIA driver. The part of the driver that hosts it, NGX, is a DLL
/// in the driver's own folder; NVIDIA's SDK is a thin layer that finds that DLL and calls
/// it, and the SDK publishes how (nvsdk_ngx_loader.h and its header-only build). The same
/// is done here directly, so nothing of NVIDIA's is compiled into this program.
/// </para>
/// <para>
/// The network itself is a second file, nvngx_dlss.dll, and NGX decides which copy to
/// use: one found beside this program, or one the driver keeps for itself under
/// ProgramData\NVIDIA\NGX - which it only hands to a program it does not know when "DLSS
/// override" is switched on for all programs in the NVIDIA app. With neither there is no
/// DLSS, and <see cref="LastError"/> says so. The driver's settings can also replace the
/// network and the mode asked for here; the log shows what was actually used.
/// </para>
/// </summary>
public sealed unsafe class DlssScaler : IDisposable
{
    // NVSDK_NGX_Result. Anything else is 0xBAD00000 with a reason in the low bits.
    internal const uint Success = 1;

    // From nvsdk_ngx_defs.h.
    private const int FeatureSuperSampling = 1;
    internal const int EngineCustom = 0;
    internal const int SdkVersion = 0x15;

    // "MVLowRes": the vectors are as large as the picture going in, not the one coming out.
    private const int FlagMotionAtInputSize = 1 << 1;

    // DLSS is made for enlarging by 1 to 3 times each way.
    private const int MaxScale = 3;

    // Building the network for a new size takes a noticeable part of a second, and a window
    // being dragged asks for a new size every frame. A new size is taken up once it has
    // been asked for this many frames running; until then the picture already being built
    // is shown, stretched.
    private const int SettleFrames = 20;

    // What NGX knows this program as. NVIDIA issues ids to games; anything else passes one
    // of its own making, which only has to have the shape of a GUID.
    internal static ReadOnlySpan<byte> ProjectId => "dd901ba8-7697-49d9-ad14-59d1a44a7647"u8;
    internal static ReadOnlySpan<byte> EngineVersion => "1.0"u8;

    /// <summary>NVSDK_NGX_FeatureCommonInfo: where else to look for nvngx_dlss.dll, and where to send the log.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CommonInfo
    {
        public char** Paths;
        public uint PathCount;
        public IntPtr Internal;
        public delegate* unmanaged[Cdecl]<byte*, int, int, void> LogCallback;
        public int MinimumLogLevel;
        public byte NoOtherLogSinks;
    }

    private readonly ID3D11Device _device;
    private readonly bool _nvidia;

    // Direct3D's lock around the device, for a device used from several threads: ours is,
    // by the decoder. See Hold.
    private readonly ID3D11Multithread? _threads;

    private IntPtr _core;
    private IntPtr _parameters;
    private IntPtr _feature;
    private CommonInfo* _info;
    private bool _started, _failed;

    // The driver's entry points, as the SDK's own loader names and calls them.
    private void* _init;
    private delegate* unmanaged[Cdecl]<IntPtr, uint*, uint> _shutdown;
    private delegate* unmanaged[Cdecl]<IntPtr*, uint> _capabilityParameters;
    private delegate* unmanaged[Cdecl]<IntPtr, uint> _destroyParameters;
    private delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, IntPtr*, uint> _createFeature;
    private delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, uint> _evaluateFeature;
    private delegate* unmanaged[Cdecl]<IntPtr, uint> _releaseFeature;

    private ID3D11Texture2D? _depth, _still, _output;
    private ID3D11ShaderResourceView? _outputView;
    private int _inWidth, _inHeight, _outWidth, _outHeight;
    private int _askedWidth, _askedHeight, _askedFor;
    private bool _reset;
    private long _lastFrame;

    /// <summary>The upscaled picture, once a frame has been through.</summary>
    public ID3D11ShaderResourceView? OutputView => _outputView;

    public int OutputWidth => _outWidth;
    public int OutputHeight => _outHeight;

    /// <summary>Why it is not running, when it is not.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// True while there is a reason to offer it: an NVIDIA card with NGX in its driver, and
    /// no failure so far. Whether DLSS itself starts is only known once it is tried.
    /// </summary>
    public bool Possible => _nvidia && !_failed && CorePath is not null;

    /// <summary>
    /// The network asked for. Changing it takes effect with the next frame, which is the
    /// first of a new history: what one network accumulated is no use to another.
    /// <para>
    /// M unless told otherwise, and not DLSS's own pick. For the enlargements a video
    /// usually gets, DLSS would take K, and K leaves a compressed picture much as it came:
    /// the codec's blocks and the stair-steps are still there. L and M both clear them; M
    /// smooths fine regular detail away with them, L keeps it. Against clips whose right
    /// answer is known (tools/flow), M comes closest to it, at twice the size and at a
    /// third as large again, and K furthest - which is why it is M and not L, the choice
    /// until then. On an RTX 5080, taking 1280x800 to 2304x1440, they cost 0.85 (K),
    /// 1.10 (M) and 1.30 ms (L) a frame.
    /// </para>
    /// </summary>
    public DlssModel Model { get; set; } = DlssModel.M;

    /// <summary>
    /// The network in use, as a letter, by NGX's own account while it was being built;
    /// null when it did not say. It is not always the one asked for: see <see cref="ModelForced"/>.
    /// </summary>
    public string? ModelInUse { get; private set; }

    /// <summary>
    /// True when the network in use was the driver's choice over ours. "DLSS override" in
    /// the NVIDIA app names a network for every program, and takes precedence.
    /// </summary>
    public bool ModelForced { get; private set; }

    private DlssModel _builtModel;

    /// <summary>Lets go of what the frames so far added up to: the next one starts afresh.</summary>
    public void ResetHistory() => _reset = true;

    /// <summary>What it is doing, for the log and the readout: sizes, the mode they fall under, the network.</summary>
    public string Description => _feature == IntPtr.Zero
        ? "not running"
        : $"{_inWidth}x{_inHeight} -> {_outWidth}x{_outHeight}, {QualityName(Quality(_inWidth, _outWidth))}, " +
          $"model {ModelInUse ?? "not reported"}{(ModelForced ? " (the driver's choice)" : "")}";

    public DlssScaler(ID3D11Device device)
    {
        _device = device;
        _threads = device.QueryInterfaceOrNull<ID3D11Multithread>();
        try
        {
            using var dxgiDevice = device.QueryInterfaceOrNull<IDXGIDevice>();
            using var adapter = dxgiDevice?.GetAdapter();
            _nvidia = adapter?.Description.VendorId == 0x10DE;
        }
        catch (Exception ex) { Log.Debug($"DLSS: adapter not identified: {ex.Message}"); }
    }

    private static string? _corePath;
    private static bool _coreLooked;

    /// <summary>
    /// Where the driver keeps NGX, or null when there is none. The driver's installer
    /// writes the folder to the registry, in a place that has moved twice; all three are
    /// tried, newest first, exactly as the SDK's loader does.
    /// </summary>
    public static string? CorePath
    {
        get
        {
            if (_coreLooked) return _corePath;
            _coreLooked = true;

            (string Key, string Value)[] places =
            [
                (@"SYSTEM\CurrentControlSet\Services\nvlddmkm\Parameters\NGXCore", "NGXPath"),
                (@"SYSTEM\CurrentControlSet\Services\nvlddmkm\NGXCore", "NGXPath"),
                (@"SOFTWARE\NVIDIA Corporation\Global\NGXCore", "FullPath"),
            ];
            try
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                foreach (var (key, value) in places)
                {
                    using var k = machine.OpenSubKey(key);
                    if (k?.GetValue(value) is not string folder || !Directory.Exists(folder)) continue;
                    // "_nvngx.dll" since driver 471.11, "nvngx.dll" before it.
                    foreach (string name in (string[])["_nvngx.dll", "nvngx.dll"])
                    {
                        string file = Path.Combine(folder, name);
                        if (File.Exists(file)) return _corePath = file;
                    }
                }
            }
            catch (Exception ex) { Log.Debug($"DLSS: looking for NGX failed: {ex.Message}"); }
            return null;
        }
    }

    /// <summary>Where NGX may keep what it writes. It refuses to start without a folder it can write to.</summary>
    internal static string DataPath => Path.Combine(Settings.AppSettings.Directory, "ngx");

    /// <summary>
    /// Loads NGX and asks it whether DLSS can run on this card and driver. Cheap to call
    /// again: it does the work once and remembers the answer either way.
    /// </summary>
    public bool Start()
    {
        if (_started) return true;
        if (_failed) return false;
        if (!_nvidia) return Fail("DLSS needs an NVIDIA RTX card");

        try
        {
            if (CorePath is not { } path) return Fail("the graphics driver has no NGX, which is what runs DLSS");
            using var held = Hold();

            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            _core = NativeLibrary.Load(path);
            // (project id, engine type, engine version, data folder, device, SDK version, common info)
            _init = (void*)Export("NVSDK_NGX_D3D11_Init_ProjectID");
            _shutdown = (delegate* unmanaged[Cdecl]<IntPtr, uint*, uint>)Export("NVSDK_NGX_D3D11_Shutdown1");
            _capabilityParameters = (delegate* unmanaged[Cdecl]<IntPtr*, uint>)Export("NVSDK_NGX_D3D11_GetCapabilityParameters");
            _destroyParameters = (delegate* unmanaged[Cdecl]<IntPtr, uint>)Export("NVSDK_NGX_D3D11_DestroyParameters");
            _createFeature = (delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, IntPtr*, uint>)Export("NVSDK_NGX_D3D11_CreateFeature");
            _evaluateFeature = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, uint>)Export("NVSDK_NGX_D3D11_EvaluateFeature");
            _releaseFeature = (delegate* unmanaged[Cdecl]<IntPtr, uint>)Export("NVSDK_NGX_D3D11_ReleaseFeature");

            Directory.CreateDirectory(DataPath);

            _info = (CommonInfo*)NativeMemory.AllocZeroed((nuint)sizeof(CommonInfo));

            // Where this program is. NGX looks beside "the application" by itself, but
            // what it takes for the application is not this one: see CallFromModule.
            _info->Paths = (char**)NativeMemory.Alloc((nuint)sizeof(char*));
            _info->Paths[0] = (char*)Marshal.StringToHGlobalUni(AppContext.BaseDirectory.TrimEnd('\\', '/'));
            _info->PathCount = 1;

            // NGX's own account of what it does goes into our log and nowhere else. It is
            // the only place a refusal is explained: the result codes say little more than
            // "no".
            _info->LogCallback = &OnLog;
            _info->MinimumLogLevel = 1;
            _info->NoOtherLogSinks = 1;

            uint result;
            fixed (byte* project = ProjectId)
            fixed (byte* version = EngineVersion)
            fixed (char* data = DataPath)
                result = CallFromModule(_init,
                [
                    (nint)project, EngineCustom, (nint)version, (nint)data, _device.NativePointer, SdkVersion, (nint)_info
                ]);
            if (result != Success) return Fail($"NGX did not start: {Describe(result)}");

            IntPtr parameters;
            result = _capabilityParameters(&parameters);
            if (result != Success || parameters == IntPtr.Zero)
                return Fail($"NGX would not say what it can do: {Describe(result)}");
            _parameters = parameters;

            var p = new Parameters(_parameters);
            p.Get("SuperSampling.Available"u8, out int available);
            if (available == 0)
            {
                p.Get("SuperSampling.NeedsUpdatedDriver"u8, out int needsDriver);
                p.Get("SuperSampling.FeatureInitResult"u8, out int why);
                return Fail(needsDriver != 0
                    ? "DLSS needs a newer graphics driver"
                    : $"DLSS is not available here: {Describe((uint)why)}");
            }

            _started = true;
            Log.Info($"DLSS: NGX ready in {System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds:0} ms ({path})");
            return true;
        }
        catch (Exception ex)
        {
            return Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Keeps every other thread out of the device until it is disposed.
    /// <para>
    /// Direct3D serialises the calls of a device that is used from several threads, and
    /// NGX works on the device without going through that: with the decoder busy on its
    /// own thread, a frame put through DLSS hangs or crashes inside the driver. Taking the
    /// same lock here first makes the decoder wait out the call instead. NGX's own calls
    /// into Direct3D are made on this thread, which already holds the lock, so they pass.
    /// </para>
    /// </summary>
    private DeviceHold Hold() => new(_threads);

    private readonly struct DeviceHold : IDisposable
    {
        private readonly ID3D11Multithread? _threads;

        public DeviceHold(ID3D11Multithread? threads)
        {
            _threads = threads;
            _threads?.Enter();
        }

        public void Dispose() => _threads?.Leave();
    }

    /// <summary>
    /// Calls a function of NGX's from inside a system DLL rather than from here.
    /// <para>
    /// NGX takes "the application's folder" to be the folder of whatever module the call
    /// came from, which it finds from the address the call will return to. Code the .NET
    /// runtime compiled belongs to no module, the lookup comes back empty, and NGX goes on
    /// to read the name it did not get: a call made directly from here crashes inside the
    /// driver. DispCallFunc is Windows' own way of calling a function by address with a
    /// list of arguments, and a call it makes returns into oleaut32.dll.
    /// </para>
    /// <para>
    /// Every argument is passed as a 64-bit integer, which on x64 is how pointers and
    /// 32-bit integers both travel.
    /// </para>
    /// </summary>
    internal static uint CallFromModule(void* function, ReadOnlySpan<nint> arguments)
    {
        const ushort Int64 = 20, UInt32 = 19;   // VT_I8, VT_UI4
        const int Cdecl = 1;                    // CC_CDECL

        int count = arguments.Length;
        Variant* values = stackalloc Variant[count];
        Variant** pointers = stackalloc Variant*[count];
        ushort* types = stackalloc ushort[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = new Variant { Type = Int64, Value = arguments[i] };
            pointers[i] = &values[i];
            types[i] = Int64;
        }

        Variant returned = default;
        int hr = DispCallFunc(IntPtr.Zero, (nuint)function, Cdecl, UInt32, (uint)count, types, pointers, &returned);
        if (hr < 0) throw new InvalidOperationException($"DispCallFunc failed: 0x{hr:X8}");
        return (uint)returned.Value;
    }

    /// <summary>A VARIANT, as far as DispCallFunc needs one: a type and eight bytes of value.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public long Value;
    }

    [DllImport("oleaut32.dll", ExactSpelling = true)]
    private static extern int DispCallFunc(IntPtr instance, nuint function, int convention, ushort returnType,
                                           uint count, ushort* types, Variant** arguments, Variant* returned);

    private IntPtr Export(string name) =>
        NativeLibrary.TryGetExport(_core, name, out IntPtr address)
            ? address
            : throw new EntryPointNotFoundException($"the driver's NGX has no {name}");

    private bool Fail(string why)
    {
        _failed = true;
        LastError = why;
        Log.Warn($"DLSS unavailable: {why}");
        return false;
    }

    /// <summary>
    /// The size to build for a picture that will be shown in a rectangle of the given size:
    /// that rectangle, kept within what DLSS is made for. Shown smaller than it is, the
    /// picture is still put through at its own size, for the steadiness alone.
    /// </summary>
    public static (int Width, int Height) OutputSize(int inWidth, int inHeight, int fitWidth, int fitHeight)
    {
        if (fitWidth <= inWidth || fitHeight <= inHeight) return (inWidth, inHeight);
        if (fitWidth > inWidth * MaxScale || fitHeight > inHeight * MaxScale)
            return (inWidth * MaxScale, inHeight * MaxScale);
        return (fitWidth, fitHeight);
    }

    /// <summary>
    /// Diagnostic: the mode DLSS is told it is in, as an NVSDK_NGX_PerfQuality_Value,
    /// whatever the sizes say; null leaves it to them. For seeing what the mode by itself
    /// changes when the sizes stay as they are - which, with one network asked for under
    /// every mode, is nothing. A clip of 1920x1080 came out at 2560x1440 the same to the
    /// last bit as quality, as ultra quality and as DLAA, and one of 1280x800 at 2304x1440
    /// the same as balanced, as quality and as performance (DLSS 310, the driver choosing
    /// network M). Ultra quality is a name NVIDIA has never filled in, and DLAA is no more
    /// than DLSS with nothing to enlarge.
    /// </summary>
    public static int? ForcedQuality { get; set; }

    /// <summary>
    /// DLSS's name for how far it is enlarging, which also picks the network it uses.
    /// The values are NVSDK_NGX_PerfQuality_Value; the steps are 1.5, 1.72, 2 and 3 times.
    /// </summary>
    private static int Quality(int inWidth, int outWidth)
    {
        if (ForcedQuality is { } forced) return forced;
        double scale = outWidth / (double)Math.Max(1, inWidth);
        return scale switch
        {
            <= 1.01 => 5,
            <= 1.6 => 2,
            <= 1.85 => 1,
            <= 2.2 => 0,
            _ => 3
        };
    }

    private static string QualityName(int quality) => quality switch
    {
        5 => "DLAA",
        4 => "ultra quality",
        2 => "quality",
        1 => "balanced",
        0 => "performance",
        _ => "ultra performance"
    };

    // What NGX calls the same modes in its log.
    private static string QualityInLog(int quality) => quality switch
    {
        5 => "DLAA",
        4 => "UltraQuality",
        2 => "Quality",
        1 => "Balanced",
        0 => "Perf",
        _ => "UltraPerf"
    };

    // What NGX said about networks while one was being built. Its log is the only account
    // there is of which it took: nothing in the interface reports it.
    private static readonly List<string> Heard = [];
    private static volatile bool _listening;

    /// <summary>
    /// Picks out of <see cref="Heard"/> the network taken for a mode. The line reads like
    /// "(Balanced) Using DRS Overridden Preset M": the mode, how it was arrived at, the letter.
    /// </summary>
    private static (string? Letter, bool Forced) ModelHeard(int quality)
    {
        string mode = "(" + QualityInLog(quality) + ")";
        lock (Heard)
        {
            for (int i = Heard.Count - 1; i >= 0; i--)
            {
                string line = Heard[i];
                // DLSS Super Resolution only: Ray Reconstruction reports its own beside it.
                if (!line.Contains("NgxDltss", StringComparison.Ordinal) || !line.Contains(mode, StringComparison.Ordinal)) continue;
                var match = System.Text.RegularExpressions.Regex.Match(line, @"Preset[ _]?([A-Z])\b");
                if (!match.Success) continue;
                return (match.Groups[1].Value, line.Contains("Overrid", StringComparison.OrdinalIgnoreCase));
            }
        }
        return (null, false);
    }

    /// <summary>
    /// Upscales one frame into <see cref="OutputView"/>. The picture is plain SDR colour
    /// at its own size; the vectors are one per pixel of it, in its pixels, each saying
    /// where that pixel was in the frame before relative to where it is now. No vectors
    /// means nothing moved.
    /// <para>
    /// False when there is no picture to show for it: DLSS could not be started or built,
    /// or refused the frame.
    /// </para>
    /// </summary>
    public bool Process(ID3D11DeviceContext context, ID3D11Texture2D color, ID3D11Texture2D? motion,
                        int inWidth, int inHeight, int outWidth, int outHeight)
    {
        if (!Start()) return false;
        if (!Fit(context, inWidth, inHeight, outWidth, outHeight)) return false;

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float sinceLast = _lastFrame == 0
            ? 16.7f
            : (float)Math.Clamp(System.Diagnostics.Stopwatch.GetElapsedTime(_lastFrame, now).TotalMilliseconds, 1, 100);
        _lastFrame = now;

        using var held = Hold();
        var p = new Parameters(_parameters);
        p.Set("Color"u8, color);
        p.Set("Output"u8, _output);
        p.Set("Depth"u8, _depth);
        p.Set("MotionVectors"u8, motion ?? _still);
        p.Set("Jitter.Offset.X"u8, 0f);
        p.Set("Jitter.Offset.Y"u8, 0f);
        p.Set("Sharpness"u8, 0f);
        p.Set("Reset"u8, _reset ? 1 : 0);
        p.Set("MV.Scale.X"u8, 1f);
        p.Set("MV.Scale.Y"u8, 1f);
        p.Set("DLSS.Render.Subrect.Dimensions.Width"u8, (uint)_inWidth);
        p.Set("DLSS.Render.Subrect.Dimensions.Height"u8, (uint)_inHeight);
        p.Set("DLSS.Pre.Exposure"u8, 1f);
        p.Set("DLSS.Exposure.Scale"u8, 1f);
        p.Set("FrameTimeDeltaInMsec"u8, sinceLast);

        uint result = _evaluateFeature(context.NativePointer, _feature, _parameters, IntPtr.Zero);
        if (result != Success)
        {
            Release();
            return Fail($"DLSS refused a frame: {Describe(result)}");
        }

        _reset = false;
        return true;
    }

    /// <summary>Makes sure there is a network built for these sizes, or one still good enough.</summary>
    private bool Fit(ID3D11DeviceContext context, int inWidth, int inHeight, int outWidth, int outHeight)
    {
        if (_feature != IntPtr.Zero && _builtModel != Model)
            return Create(context, inWidth, inHeight, outWidth, outHeight);

        bool sameSource = _feature != IntPtr.Zero && inWidth == _inWidth && inHeight == _inHeight;
        if (sameSource && outWidth == _outWidth && outHeight == _outHeight)
        {
            _askedFor = 0;
            return true;
        }

        if (sameSource)
        {
            if (outWidth != _askedWidth || outHeight != _askedHeight)
            {
                _askedWidth = outWidth; _askedHeight = outHeight;
                _askedFor = 0;
            }
            if (++_askedFor < SettleFrames) return true;
        }

        return Create(context, inWidth, inHeight, outWidth, outHeight);
    }

    private bool Create(ID3D11DeviceContext context, int inWidth, int inHeight, int outWidth, int outHeight)
    {
        using var held = Hold();
        Release();
        long began = System.Diagnostics.Stopwatch.GetTimestamp();

        try
        {
            int quality = Quality(inWidth, outWidth);
            var p = new Parameters(_parameters);
            p.Set("Width"u8, (uint)inWidth);
            p.Set("Height"u8, (uint)inHeight);
            p.Set("OutWidth"u8, (uint)outWidth);
            p.Set("OutHeight"u8, (uint)outHeight);
            p.Set("PerfQualityValue"u8, quality);
            p.Set("DLSS.Feature.Create.Flags"u8, FlagMotionAtInputSize);
            p.Set("DLSS.Enable.Output.Subrects"u8, 0);

            // The network is asked for mode by mode. The same one is asked for under every
            // mode, so that it does not depend on which of them the sizes fall under.
            uint model = (uint)Model;
            p.Set("DLSS.Hint.Render.Preset.DLAA"u8, model);
            p.Set("DLSS.Hint.Render.Preset.Quality"u8, model);
            p.Set("DLSS.Hint.Render.Preset.Balanced"u8, model);
            p.Set("DLSS.Hint.Render.Preset.Performance"u8, model);
            p.Set("DLSS.Hint.Render.Preset.UltraPerformance"u8, model);
            p.Set("DLSS.Hint.Render.Preset.UltraQuality"u8, model);

            lock (Heard) Heard.Clear();
            _listening = true;
            IntPtr feature;
            uint result = _createFeature(context.NativePointer, FeatureSuperSampling, _parameters, &feature);
            _listening = false;
            if (result != Success || feature == IntPtr.Zero)
                return Fail($"DLSS could not be built for {inWidth}x{inHeight} -> {outWidth}x{outHeight}: {Describe(result)}");
            _feature = feature;
            _builtModel = Model;
            (ModelInUse, ModelForced) = ModelHeard(quality);

            // Depth, which a video does not have. Everything is said to be at the same
            // distance, half way between near and far: no part of the picture is then
            // taken to be in front of another.
            _depth = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)inWidth,
                Height = (uint)inHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R32_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
            });
            using (var target = _device.CreateRenderTargetView(_depth))
                context.ClearRenderTargetView(target, new Vortice.Mathematics.Color4(0.5f, 0.5f, 0.5f, 0.5f));

            // The vectors of a picture in which nothing moved.
            _still = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)inWidth,
                Height = (uint)inHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R16G16_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
            });
            using (var target = _device.CreateRenderTargetView(_still))
                context.ClearRenderTargetView(target, new Vortice.Mathematics.Color4(0f, 0f, 0f, 0f));

            // DLSS writes its result from a compute shader, hence unordered access.
            _output = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)outWidth,
                Height = (uint)outHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource
            });
            _outputView = _device.CreateShaderResourceView(_output);

            _inWidth = inWidth; _inHeight = inHeight;
            _outWidth = outWidth; _outHeight = outHeight;
            _askedFor = 0;
            // Whatever it remembered belongs to another size, or there is nothing yet.
            _reset = true;
            _lastFrame = 0;

            Log.Info($"DLSS: {Description}, built in " +
                     $"{System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds:0} ms");
            return true;
        }
        catch (Exception ex)
        {
            Release();
            return Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Release()
    {
        using var held = Hold();
        if (_feature != IntPtr.Zero)
        {
            uint result = _releaseFeature(_feature);
            if (result != Success) Log.Debug($"DLSS: releasing the feature: {Describe(result)}");
            _feature = IntPtr.Zero;
        }
        _outputView?.Dispose(); _outputView = null;
        _output?.Dispose(); _output = null;
        _depth?.Dispose(); _depth = null;
        _still?.Dispose(); _still = null;
        _inWidth = _inHeight = _outWidth = _outHeight = 0;
    }

    public void Dispose()
    {
        Release();
        if (_core != IntPtr.Zero) Shutdown();
        _threads?.Dispose();
    }

    private void Shutdown()
    {
        using var held = Hold();

        if (_parameters != IntPtr.Zero) { _destroyParameters(_parameters); _parameters = IntPtr.Zero; }
        if (_shutdown is not null)
        {
            // The driver's side of this call also reports how many devices still use NGX;
            // it is given somewhere to write that even though nothing here reads it.
            uint left = 0;
            _shutdown(_device.NativePointer, &left);
        }
        if (_info is not null)
        {
            if (_info->Paths is not null)
            {
                Marshal.FreeHGlobal((IntPtr)_info->Paths[0]);
                NativeMemory.Free(_info->Paths);
            }
            NativeMemory.Free(_info);
            _info = null;
        }
        // The DLL itself stays loaded: it is the driver's, and it may have threads of its own.
        _started = false;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void OnLog(byte* message, int level, int source)
    {
        // Called from any of NGX's threads. Nothing may escape back into it.
        try
        {
            string? text = Marshal.PtrToStringUTF8((IntPtr)message)?.TrimEnd();
            if (string.IsNullOrEmpty(text)) return;
            Log.Debug("ngx: " + text);
            if (_listening && text.Contains("Preset", StringComparison.Ordinal))
                lock (Heard) Heard.Add(text);
        }
        catch { }
    }

    internal static string Describe(uint result) => result switch
    {
        Success => "success",
        0xBAD00001 => "not supported on this system",
        0xBAD00002 => "platform error",
        0xBAD00003 => "feature already exists",
        0xBAD00004 => "no nvngx_dlss.dll beside this program, and the driver offers none of its own " +
                      "(it does once DLSS override is on for all programs in the NVIDIA app)",
        0xBAD00005 => "invalid parameter",
        0xBAD00006 => "scratch buffer too small",
        0xBAD00007 => "not initialised",
        0xBAD00008 => "unsupported input format",
        0xBAD00009 => "the output cannot be written to",
        0xBAD0000A => "missing input",
        0xBAD0000B => "unable to initialise the feature",
        0xBAD0000C => "out of date",
        0xBAD0000D => "out of GPU memory",
        0xBAD0000E => "unsupported format",
        0xBAD0000F => "unable to write to the data folder",
        0xBAD00010 => "unsupported parameter",
        0xBAD00011 => "denied",
        0xBAD00012 => "not implemented",
        _ => $"0x{result:X8}"
    };

    /// <summary>
    /// NVSDK_NGX_Parameter: a bag of named values, and a C++ interface that has to be
    /// called through its table of methods. The slots are where MSVC puts them - the
    /// overloads of one name in the reverse of the order nvsdk_ngx_params.h declares them
    /// - which was found by compiling that declaration, not assumed.
    /// <para>
    /// Names are passed as UTF-8 literals, which the compiler ends with the zero byte the
    /// other side reads up to.
    /// </para>
    /// </summary>
    internal readonly struct Parameters(IntPtr self)
    {
        private const int SetPointerSlot = 0, SetResource12Slot = 1, SetResourceSlot = 2, SetIntSlot = 3, SetUIntSlot = 4,
                          SetFloatSlot = 6, GetIntSlot = 11, GetUIntSlot = 12;

        private void* Slot(int index) => (*(void***)self)[index];

        public void Set(ReadOnlySpan<byte> name, int value)
        {
            fixed (byte* n = name)
                ((delegate* unmanaged[Cdecl]<IntPtr, byte*, int, void>)Slot(SetIntSlot))(self, n, value);
        }

        public void Set(ReadOnlySpan<byte> name, uint value)
        {
            fixed (byte* n = name)
                ((delegate* unmanaged[Cdecl]<IntPtr, byte*, uint, void>)Slot(SetUIntSlot))(self, n, value);
        }

        public void Set(ReadOnlySpan<byte> name, float value)
        {
            fixed (byte* n = name)
                ((delegate* unmanaged[Cdecl]<IntPtr, byte*, float, void>)Slot(SetFloatSlot))(self, n, value);
        }

        public void Set(ReadOnlySpan<byte> name, ID3D11Resource? value)
        {
            fixed (byte* n = name)
                ((delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr, void>)Slot(SetResourceSlot))(
                    self, n, value?.NativePointer ?? IntPtr.Zero);
        }

        /// <summary>An ID3D12Resource, or a pointer to plain data such as a matrix, given by address.</summary>
        public void Set(ReadOnlySpan<byte> name, IntPtr value, bool d3d12Resource)
        {
            fixed (byte* n = name)
                ((delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr, void>)Slot(d3d12Resource ? SetResource12Slot : SetPointerSlot))(
                    self, n, value);
        }

        public bool Get(ReadOnlySpan<byte> name, out uint value)
        {
            uint read = 0;
            uint result;
            fixed (byte* n = name)
                result = ((delegate* unmanaged[Cdecl]<IntPtr, byte*, uint*, uint>)Slot(GetUIntSlot))(self, n, &read);
            value = read;
            return result == Success;
        }

        public bool Get(ReadOnlySpan<byte> name, out int value)
        {
            int read = 0;
            uint result;
            fixed (byte* n = name)
                result = ((delegate* unmanaged[Cdecl]<IntPtr, byte*, int*, uint>)Slot(GetIntSlot))(self, n, &read);
            value = read;
            return result == Success;
        }
    }
}
