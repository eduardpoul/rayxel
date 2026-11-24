using System.Diagnostics;
using System.Runtime.InteropServices;
using Rayxel.Diagnostics;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Rayxel.Render;

/// <summary>
/// DLSS Frame Generation, called through NGX the way <see cref="DlssScaler"/> calls DLSS
/// Super Resolution: the driver's own NGX, no NVIDIA code compiled in.
/// <para>
/// It draws the frames between two real ones. A game hands it the motion and the depth
/// of every pixel; a video has neither, so it gets what the upscalers get: the motion
/// <see cref="OpticalFlow"/> estimated back to the frame before, a flat depth, and a camera
/// that never moved, because all of the motion is in the vectors. Measured against a
/// picture moving by a known amount, the frames come out where they belong: half way
/// for one generated frame, a quarter, a half and three quarters for three.
/// </para>
/// <para>
/// Called through NGX rather than Streamline, so nothing takes over the swapchain: this
/// program decides when each frame is shown, and can say what that costs.
/// </para>
/// <para>
/// It runs on Direct3D 12 only. On Direct3D 11 NGX builds it and then refuses every frame
/// with "platform error", so it gets a Direct3D 12 device of its own on the same card, as
/// <see cref="FsrScaler"/> does, and the pictures cross in textures both devices can see.
/// A shared fence keeps the two in step on the GPU: Direct3D 11 raises it when the
/// picture is ready, Direct3D 12 waits for that, generates and raises it again, and
/// Direct3D 11 waits for that before it draws a generated frame. No thread waits.
/// </para>
/// <para>
/// It needs its own copy of the network, nvngx_dlssg.dll, 310.7 or newer, beside this
/// program: from the DLSS SDK, a game with DLSS 4, or DLSS Swapper's library. The driver
/// carries an older one (310.2) that it falls back on, and that copy refuses every frame
/// given to it this way. 310.7.129, 310.9.1 and 310.9.16 have been seen to work.
/// </para>
/// </summary>
public sealed unsafe class DlssFrameGen : IDisposable
{
    private const int FeatureFrameGeneration = 11;  // NVSDK_NGX_Feature_FrameGeneration
    private const uint DxgiRgba8 = 28;              // DXGI_FORMAT_R8G8B8A8_UNORM

    /// <summary>Most frames generated between two real ones: four times the rate.</summary>
    public const int MaxGenerated = 3;

    /// <summary>The oldest copy of the network that takes frames this way.</summary>
    private static readonly Version LeastDll = new(310, 7, 0, 0);

    private readonly ID3D11Device _device;

    private IntPtr _core;
    private DlssScaler.CommonInfo* _info;
    private float* _identity;
    private volatile bool _started, _failed;
    private bool _ngx;
    private Task? _starting;

    private IntPtr _device12, _queue, _list, _fence12;
    private readonly IntPtr[] _allocators = new IntPtr[2];
    private readonly ulong[] _allocatorDone = new ulong[2];
    private int _allocator;
    private ID3D11Fence? _fence11;
    private ID3D11DeviceContext4? _context4;
    private ulong _fenceValue;

    private IntPtr _params, _feature;
    private delegate* unmanaged[Cdecl]<IntPtr*, uint> _caps;
    private delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, IntPtr*, uint> _create;
    private delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, uint> _evaluate;
    private delegate* unmanaged[Cdecl]<IntPtr, uint> _release;
    private delegate* unmanaged[Cdecl]<IntPtr, uint> _destroy;
    private delegate* unmanaged[Cdecl]<IntPtr, uint*, uint> _shutdown;

    // What it is fed and what it writes, each as both devices hold it.
    private Shared? _color, _motion, _depth;
    private readonly Shared?[] _outputs = new Shared?[MaxGenerated];
    private readonly ID3D11ShaderResourceView?[] _outputViews = new ID3D11ShaderResourceView?[MaxGenerated];
    private int _width, _height, _motionWidth, _motionHeight;
    private bool _reset = true;

    /// <summary>Why it cannot run, when it cannot; null while it can or has not been tried.</summary>
    public string? LastError { get; private set; }

    /// <summary>True until it has been tried and failed: an NVIDIA card, and the network beside this program.</summary>
    public bool Possible => !_failed && Unsupported is null;

    /// <summary>True while NGX is being started on its thread: nothing is generated until it has.</summary>
    public bool Starting => _starting is { IsCompleted: false };

    /// <summary>
    /// Starts NGX on a thread of its own, if it has not been. It takes seconds on Direct3D
    /// 12 - two on an RTX 5080 - and the picture goes on being shown meanwhile.
    /// </summary>
    public void StartInBackground()
    {
        if (_started || _failed || _starting is not null) return;
        _starting = Task.Run(() => Start());
    }

    /// <summary>The size it is built for, as the picture going in; 0 before it is built.</summary>
    public int Width => _width;
    public int Height => _height;

    /// <summary>
    /// Why it cannot be offered on this machine, before anything is started: no NVIDIA card,
    /// or no copy of the network that works this way. Null when it can be tried.
    /// </summary>
    public string? Unsupported { get; }

    public DlssFrameGen(ID3D11Device device)
    {
        _device = device;
        bool nvidia = false;
        try
        {
            using var dxgiDevice = device.QueryInterfaceOrNull<IDXGIDevice>();
            using var adapter = dxgiDevice?.GetAdapter();
            nvidia = adapter?.Description.VendorId == 0x10DE;
        }
        catch (Exception ex) { Log.Debug($"DLSS FG: adapter not identified: {ex.Message}"); }
        Unsupported = !nvidia ? "needs an NVIDIA RTX card"
            : DlssScaler.CorePath is null ? "the graphics driver has no NGX"
            : DllProblem();
    }

    /// <summary>What is wrong with the nvngx_dlssg.dll beside this program, or null when it will do.</summary>
    private static string? DllProblem()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "nvngx_dlssg.dll");
        if (!File.Exists(path)) return "no nvngx_dlssg.dll beside Rayxel.exe";
        var info = FileVersionInfo.GetVersionInfo(path);
        var version = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
        return version < LeastDll ? $"nvngx_dlssg.dll beside Rayxel.exe is {version.ToString(3)}; it needs 310.7 or newer" : null;
    }

    /// <summary>
    /// Loads NGX on a Direct3D 12 device of its own. Cheap to call again: the work is done
    /// once, and a failure is remembered.
    /// </summary>
    public bool Start()
    {
        if (_started) return true;
        if (_failed) return false;
        if (Unsupported is { } why) return Fail(why);

        long began = Stopwatch.GetTimestamp();
        try
        {
            _core = NativeLibrary.Load(DlssScaler.CorePath!);
            var init = (void*)Export("NVSDK_NGX_D3D12_Init_ProjectID");
            _caps = (delegate* unmanaged[Cdecl]<IntPtr*, uint>)Export("NVSDK_NGX_D3D12_GetCapabilityParameters");
            _create = (delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, IntPtr*, uint>)Export("NVSDK_NGX_D3D12_CreateFeature");
            _evaluate = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, uint>)Export("NVSDK_NGX_D3D12_EvaluateFeature");
            _release = (delegate* unmanaged[Cdecl]<IntPtr, uint>)Export("NVSDK_NGX_D3D12_ReleaseFeature");
            _destroy = (delegate* unmanaged[Cdecl]<IntPtr, uint>)Export("NVSDK_NGX_D3D12_DestroyParameters");
            _shutdown = (delegate* unmanaged[Cdecl]<IntPtr, uint*, uint>)Export("NVSDK_NGX_D3D12_Shutdown1");

            using (var dxgiDevice = _device.QueryInterface<IDXGIDevice>())
            using (var adapter = dxgiDevice.GetAdapter())
                _device12 = D3D12.CreateDevice(adapter.NativePointer);
            _queue = D3D12.CreateQueue(_device12);
            _allocators[0] = D3D12.CreateAllocator(_device12);
            _allocators[1] = D3D12.CreateAllocator(_device12);
            _list = D3D12.CreateList(_device12, _allocators[0]);
            D3D12.Check(D3D12.Close(_list), "closing the first command list");

            _fence12 = D3D12.CreateSharedFence(_device12);
            IntPtr handle = D3D12.CreateSharedHandle(_device12, _fence12);
            try
            {
                using var device5 = _device.QueryInterface<ID3D11Device5>();
                _fence11 = device5.OpenSharedFence<ID3D11Fence>(handle);
            }
            finally { CloseHandle(handle); }

            Directory.CreateDirectory(DlssScaler.DataPath);
            _info = (DlssScaler.CommonInfo*)NativeMemory.AllocZeroed((nuint)sizeof(DlssScaler.CommonInfo));
            _info->Paths = (char**)NativeMemory.Alloc((nuint)sizeof(char*));
            _info->Paths[0] = (char*)Marshal.StringToHGlobalUni(AppContext.BaseDirectory.TrimEnd('\\', '/'));
            _info->PathCount = 1;
            _info->LogCallback = &DlssScaler.OnLog;
            _info->MinimumLogLevel = 1;
            _info->NoOtherLogSinks = 1;

            // A camera that never moved: every matrix the same, the one that changes nothing.
            _identity = (float*)NativeMemory.AllocZeroed(16 * sizeof(float));
            _identity[0] = _identity[5] = _identity[10] = _identity[15] = 1f;

            uint result;
            fixed (byte* project = DlssScaler.ProjectId)
            fixed (byte* version = DlssScaler.EngineVersion)
            fixed (char* data = DlssScaler.DataPath)
                result = DlssScaler.CallFromModule(init,
                [
                    (nint)project, DlssScaler.EngineCustom, (nint)version, (nint)data, _device12, DlssScaler.SdkVersion, (nint)_info
                ]);
            if (result != DlssScaler.Success) return Fail($"NGX did not start on Direct3D 12: {DlssScaler.Describe(result)}");
            _ngx = true;

            IntPtr parameters;
            result = _caps(&parameters);
            if (result != DlssScaler.Success || parameters == IntPtr.Zero)
                return Fail($"NGX would not say what it can do: {DlssScaler.Describe(result)}");
            _params = parameters;

            var p = new DlssScaler.Parameters(_params);
            p.Get("FrameGeneration.Available"u8, out int available);
            if (available == 0)
            {
                p.Get("FrameGeneration.NeedsUpdatedDriver"u8, out int needsDriver);
                return Fail(needsDriver != 0 ? "it needs a newer graphics driver" : "NGX says it is not available on this card");
            }

            _started = true;
            Log.Info($"DLSS FG: NGX ready on Direct3D 12 in {Stopwatch.GetElapsedTime(began).TotalMilliseconds:0} ms");
            return true;
        }
        catch (Exception ex)
        {
            return Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Starts generating the frames between the one given last time and this one, into
    /// <paramref name="count"/> textures that <see cref="Output"/> then gives out in order.
    /// The colour is RGBA8; the motion is <see cref="OpticalFlow.PerPixel"/>, in pixels of
    /// a picture of the motion's own size, pointing back to where each pixel was.
    /// <para>
    /// Nothing waits here. The context is left waiting, on the GPU, for the frames to be
    /// done, so whatever it draws next can read them.
    /// </para>
    /// </summary>
    /// <returns>False when nothing was generated; then nothing is to be shown from it.</returns>
    public bool Generate(ID3D11DeviceContext context, ID3D11Texture2D color, ID3D11Texture2D motion,
                         int motionWidth, int motionHeight, int count, bool reset)
    {
        if (!_started)
        {
            StartInBackground();
            return false;
        }
        count = Math.Clamp(count, 1, MaxGenerated);

        try
        {
            var desc = color.Description;
            if (!Fit(context, (int)desc.Width, (int)desc.Height, motionWidth, motionHeight)) return false;

            // Into the textures both devices see, then a mark on the fence for Direct3D 12 to
            // wait for. Flushed at once, so that Direct3D 12 can start as soon as it is there.
            context.CopyResource(_color!.Texture, color);
            context.CopyResource(_motion!.Texture, motion);
            _context4 ??= context.QueryInterface<ID3D11DeviceContext4>();
            ulong inputsReady = ++_fenceValue;
            _context4.Signal(_fence11!, inputsReady);
            context.Flush();

            // A list's allocator may only be reset once the GPU is done with what it
            // recorded. Two of them take turns, so this is a wait in name only.
            _allocator ^= 1;
            IntPtr allocator = _allocators[_allocator];
            if (_allocatorDone[_allocator] != 0) D3D12.WaitFor(_fence12, _allocatorDone[_allocator]);
            D3D12.Check(D3D12.Reset(allocator), "resetting the allocator");
            D3D12.Check(D3D12.Reset(_list, allocator), "opening the command list");

            // A texture shared with Direct3D 11 is "common" whenever Direct3D 11 has it.
            int barriers = 3 + count;
            D3D12.Barrier* into = stackalloc D3D12.Barrier[barriers];
            into[0] = D3D12.Transition(_color.Resource, D3D12.Common, D3D12.NonPixelShaderResource);
            into[1] = D3D12.Transition(_motion.Resource, D3D12.Common, D3D12.NonPixelShaderResource);
            into[2] = D3D12.Transition(_depth!.Resource, D3D12.Common, D3D12.NonPixelShaderResource);
            for (int i = 0; i < count; i++)
                into[3 + i] = D3D12.Transition(_outputs[i]!.Resource, D3D12.Common, D3D12.UnorderedAccess);
            D3D12.Barriers(_list, into, (uint)barriers);

            var p = new DlssScaler.Parameters(_params);
            p.Set("DLSSG.Backbuffer"u8, _color.Resource, d3d12Resource: true);
            p.Set("DLSSG.MVecs"u8, _motion.Resource, d3d12Resource: true);
            p.Set("DLSSG.Depth"u8, _depth.Resource, d3d12Resource: true);
            uint result = DlssScaler.Success;
            for (int i = 0; i < count && result == DlssScaler.Success; i++)
            {
                p.Set("DLSSG.OutputInterpolated"u8, _outputs[i]!.Resource, d3d12Resource: true);
                SetEval(p, count, i + 1, reset || _reset);
                result = _evaluate(_list, _feature, _params, IntPtr.Zero);
            }

            D3D12.Barrier* back = stackalloc D3D12.Barrier[barriers];
            for (int i = 0; i < barriers; i++) back[i] = D3D12.Transition(into[i].Resource, into[i].After, D3D12.Common);
            D3D12.Barriers(_list, back, (uint)barriers);
            D3D12.Check(D3D12.Close(_list), "closing the command list");

            // Even a refused frame leaves a list to run, for the barriers in it; and
            // Direct3D 11 is already waiting at the mark it set.
            D3D12.Check(D3D12.Wait(_queue, _fence12, inputsReady), "waiting for the picture");
            D3D12.Execute(_queue, _list);
            ulong done = ++_fenceValue;
            D3D12.Check(D3D12.Signal(_queue, _fence12, done), "raising the fence");
            _allocatorDone[_allocator] = done;
            _context4.Wait(_fence11!, done);

            if (result != DlssScaler.Success)
            {
                _reset = true;
                Log.Warn($"DLSS FG refused a frame: {DlssScaler.Describe(result)}");
                return false;
            }
            _reset = false;
            return true;
        }
        catch (Exception ex)
        {
            Release();
            Fail($"{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>The generated frame <paramref name="index"/> (from 0), after <see cref="Generate"/>.</summary>
    public ID3D11ShaderResourceView? Output(int index) =>
        index is >= 0 and < MaxGenerated ? _outputViews[index] : null;

    /// <summary>The frames before and after a gap have nothing in common: the next pair starts afresh.</summary>
    public void Reset() => _reset = true;

    /// <summary>Makes sure there is a feature built for these sizes.</summary>
    private bool Fit(ID3D11DeviceContext context, int width, int height, int motionWidth, int motionHeight)
    {
        if (_feature != IntPtr.Zero && width == _width && height == _height
            && motionWidth == _motionWidth && motionHeight == _motionHeight)
            return true;

        Release();
        long began = Stopwatch.GetTimestamp();
        _width = width; _height = height;
        _motionWidth = motionWidth; _motionHeight = motionHeight;

        _color = Share(context, width, height, Format.R8G8B8A8_UNorm, BindFlags.ShaderResource);
        _motion = Share(context, motionWidth, motionHeight, Format.R16G16B16A16_Float, BindFlags.ShaderResource);
        _depth = Share(context, motionWidth, motionHeight, Format.R32_Float, BindFlags.RenderTarget | BindFlags.ShaderResource);
        for (int i = 0; i < MaxGenerated; i++)
        {
            _outputs[i] = Share(context, width, height, Format.R8G8B8A8_UNorm, BindFlags.UnorderedAccess | BindFlags.ShaderResource);
            _outputViews[i] = _device.CreateShaderResourceView(_outputs[i]!.Texture);
        }

        var p = new DlssScaler.Parameters(_params);
        p.Set("CreationNodeMask"u8, 1u);
        p.Set("VisibilityNodeMask"u8, 1u);
        p.Set("Width"u8, (uint)width);
        p.Set("Height"u8, (uint)height);
        p.Set("DLSSG.Width"u8, (uint)width);
        p.Set("DLSSG.Height"u8, (uint)height);
        p.Set("DLSSG.BackbufferFormat"u8, DxgiRgba8);
        p.Set("DLSSG.InternalWidth"u8, (uint)motionWidth);
        p.Set("DLSSG.InternalHeight"u8, (uint)motionHeight);
        p.Set("DLSSG.DynamicResolution"u8, 0u);
        p.Set("DLSSG.MultiFrameCountMax"u8, (uint)MaxGenerated);
        SetAreas(p);

        D3D12.Check(D3D12.Reset(_allocators[_allocator]), "resetting the allocator");
        D3D12.Check(D3D12.Reset(_list, _allocators[_allocator]), "opening the command list");
        IntPtr feature;
        uint result = _create(_list, FeatureFrameGeneration, _params, &feature);
        D3D12.Check(D3D12.Close(_list), "closing the command list");
        if (result != DlssScaler.Success || feature == IntPtr.Zero)
        {
            Release();
            return Fail($"could not be built for {width}x{height}: {DlssScaler.Describe(result)}");
        }
        _feature = feature;
        D3D12.Execute(_queue, _list);
        ulong done = ++_fenceValue;
        D3D12.Check(D3D12.Signal(_queue, _fence12, done), "raising the fence");
        D3D12.WaitFor(_fence12, done);
        _reset = true;

        Log.Info($"DLSS FG: built for {width}x{height}, motion at {motionWidth}x{motionHeight}, " +
                 $"in {Stopwatch.GetElapsedTime(began).TotalMilliseconds:0} ms");
        return true;
    }

    private void SetEval(DlssScaler.Parameters p, int count, int index, bool reset)
    {
        p.Set("DLSSG.MultiFrameCount"u8, (uint)count);
        p.Set("DLSSG.MultiFrameIndex"u8, (uint)index);

        p.Set("DLSSG.CameraViewToClip"u8, (IntPtr)_identity, d3d12Resource: false);
        p.Set("DLSSG.ClipToCameraView"u8, (IntPtr)_identity, d3d12Resource: false);
        p.Set("DLSSG.ClipToLensClip"u8, (IntPtr)_identity, d3d12Resource: false);
        p.Set("DLSSG.ClipToPrevClip"u8, (IntPtr)_identity, d3d12Resource: false);
        p.Set("DLSSG.PrevClipToClip"u8, (IntPtr)_identity, d3d12Resource: false);

        p.Set("DLSSG.JitterOffsetX"u8, 0f);
        p.Set("DLSSG.JitterOffsetY"u8, 0f);
        // The vectors are in pixels of their own picture; NGX wants them as a fraction of it.
        p.Set("DLSSG.MvecScaleX"u8, 1f / _motionWidth);
        p.Set("DLSSG.MvecScaleY"u8, 1f / _motionHeight);
        p.Set("DLSSG.CameraPinholeOffsetX"u8, 0f);
        p.Set("DLSSG.CameraPinholeOffsetY"u8, 0f);
        p.Set("DLSSG.CameraPosX"u8, 0f);
        p.Set("DLSSG.CameraPosY"u8, 0f);
        p.Set("DLSSG.CameraPosZ"u8, 0f);
        p.Set("DLSSG.CameraUpX"u8, 0f);
        p.Set("DLSSG.CameraUpY"u8, 1f);
        p.Set("DLSSG.CameraUpZ"u8, 0f);
        p.Set("DLSSG.CameraRightX"u8, 1f);
        p.Set("DLSSG.CameraRightY"u8, 0f);
        p.Set("DLSSG.CameraRightZ"u8, 0f);
        p.Set("DLSSG.CameraFwdX"u8, 0f);
        p.Set("DLSSG.CameraFwdY"u8, 0f);
        p.Set("DLSSG.CameraFwdZ"u8, 1f);
        p.Set("DLSSG.CameraNear"u8, 0.1f);
        p.Set("DLSSG.CameraFar"u8, 100f);
        p.Set("DLSSG.CameraFOV"u8, 1f);
        p.Set("DLSSG.CameraAspectRatio"u8, _width / (float)_height);
        p.Set("DLSSG.ColorBuffersHDR"u8, 0u);
        p.Set("DLSSG.DepthInverted"u8, 0u);
        // All of the motion is in the vectors: there is no camera to account for apart from them.
        p.Set("DLSSG.CameraMotionIncluded"u8, 1u);
        p.Set("DLSSG.Reset"u8, reset ? 1u : 0u);
        p.Set("DLSSG.AutomodeOverrideReset"u8, 0u);
        p.Set("DLSSG.NotRenderingGameFrames"u8, 0u);
        // With every matrix the identity, nothing is in perspective.
        p.Set("DLSSG.OrthoProjection"u8, 1u);
        p.Set("DLSSG.MvecDilated"u8, 0u);
        p.Set("DLSSG.MvecJittered"u8, 0u);
        p.Set("DLSSG.MenuDetectionEnabled"u8, 0u);
        SetAreas(p);
    }

    /// <summary>
    /// Every area given as the whole of its picture, under the names the SDK uses now and
    /// under the older one for the back buffer. They also go in when it is built: it takes
    /// the area it will work on from them then, and refuses every frame whose area differs.
    /// </summary>
    private void SetAreas(DlssScaler.Parameters p)
    {
        foreach (var (area, w, h) in (ReadOnlySpan<(string, int, int)>)
                 [("DLSSG.MVecsSubrect", _motionWidth, _motionHeight), ("DLSSG.DepthSubrect", _motionWidth, _motionHeight),
                  ("DLSSG.InputBackbufferSubrect", _width, _height), ("DLSSG.OutputInterpolatedSubrect", _width, _height),
                  ("DLSSG.BackbufferSubrect", _width, _height)])
        {
            p.Set(System.Text.Encoding.ASCII.GetBytes(area + "BaseX\0"), 0u);
            p.Set(System.Text.Encoding.ASCII.GetBytes(area + "BaseY\0"), 0u);
            p.Set(System.Text.Encoding.ASCII.GetBytes(area + "Width\0"), (uint)w);
            p.Set(System.Text.Encoding.ASCII.GetBytes(area + "Height\0"), (uint)h);
        }
    }

    /// <summary>A texture made by Direct3D 11 and opened by Direct3D 12.</summary>
    private Shared Share(ID3D11DeviceContext context, int width, int height, Format format, BindFlags bind)
    {
        var texture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = bind,
            MiscFlags = ResourceOptionFlags.Shared | ResourceOptionFlags.SharedNTHandle
        });
        try
        {
            // Depth, which a video does not have: everything at the same distance, half way
            // between near and far, so that no part of the picture is in front of another.
            if (format == Format.R32_Float)
            {
                using var target = _device.CreateRenderTargetView(texture);
                context.ClearRenderTargetView(target, new Vortice.Mathematics.Color4(0.5f, 0.5f, 0.5f, 0.5f));
            }

            using var resource = texture.QueryInterface<IDXGIResource1>();
            IntPtr handle = resource.CreateSharedHandle(null,
                Vortice.DXGI.SharedResourceFlags.Read | Vortice.DXGI.SharedResourceFlags.Write, null);
            try { return new Shared(texture, D3D12.OpenShared(_device12, handle)); }
            finally { CloseHandle(handle); }
        }
        catch
        {
            texture.Dispose();
            throw;
        }
    }

    private IntPtr Export(string name) =>
        NativeLibrary.TryGetExport(_core, name, out IntPtr address)
            ? address
            : throw new EntryPointNotFoundException($"the driver's NGX has no {name}");

    private bool Fail(string why)
    {
        _failed = true;
        LastError = why;
        Log.Warn($"DLSS FG unavailable: {why}");
        return false;
    }

    /// <summary>Lets go of what was built for one size, once the GPU is done with it.</summary>
    private void Release()
    {
        try
        {
            if (_fence12 != IntPtr.Zero && _fenceValue != 0) D3D12.WaitFor(_fence12, _fenceValue);
        }
        catch (Exception ex) { Log.Debug($"DLSS FG: waiting for the GPU: {ex.Message}"); }

        if (_feature != IntPtr.Zero) { _release(_feature); _feature = IntPtr.Zero; }
        _color?.Dispose(); _color = null;
        _motion?.Dispose(); _motion = null;
        _depth?.Dispose(); _depth = null;
        for (int i = 0; i < MaxGenerated; i++)
        {
            _outputViews[i]?.Dispose(); _outputViews[i] = null;
            _outputs[i]?.Dispose(); _outputs[i] = null;
        }
        _width = _height = _motionWidth = _motionHeight = 0;
    }

    public void Dispose()
    {
        try { _starting?.Wait(10_000); } catch { }
        Release();
        if (_params != IntPtr.Zero) { _destroy(_params); _params = IntPtr.Zero; }
        if (_ngx) { uint left = 0; _shutdown(_device12, &left); _ngx = false; }

        _fence11?.Dispose(); _fence11 = null;
        _context4?.Dispose(); _context4 = null;
        foreach (var o in new[] { _list, _allocators[0], _allocators[1], _queue, _fence12, _device12 })
            if (o != IntPtr.Zero) D3D12.Release(o);
        _list = _allocators[0] = _allocators[1] = _queue = _fence12 = _device12 = IntPtr.Zero;

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
        if (_identity is not null) { NativeMemory.Free(_identity); _identity = null; }
        _started = false;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int CloseHandle(IntPtr handle);

    /// <summary>One texture, as each of the two devices holds it.</summary>
    private sealed class Shared(ID3D11Texture2D texture, IntPtr resource) : IDisposable
    {
        public ID3D11Texture2D Texture { get; } = texture;
        public IntPtr Resource { get; private set; } = resource;

        public void Dispose()
        {
            if (Resource != IntPtr.Zero) { D3D12.Release(Resource); Resource = IntPtr.Zero; }
            Texture.Dispose();
        }
    }

    /// <summary>
    /// The calls of Direct3D 12 this needs, through the interfaces' tables of methods - the
    /// same slots <see cref="FsrScaler"/> uses, counted off d3d12.h.
    /// </summary>
    private static class D3D12
    {
        public const int Common = 0, UnorderedAccess = 0x8, NonPixelShaderResource = 0x40;

        private const int FeatureLevel12 = 0xc000;
        private const int FenceShared = 0x1;
        private const uint GenericAll = 0x10000000;

        private static readonly Guid IidDevice = new("189819f1-1db6-4b57-be54-1821339b85f7");
        private static readonly Guid IidQueue = new("0ec870a6-5d7e-4c22-8cfc-5baae07616ed");
        private static readonly Guid IidAllocator = new("6102dee4-af59-4b09-b999-b44d73f09b24");
        private static readonly Guid IidList = new("5b160d0f-ac1b-4185-8ba8-b3ae42a5a455");
        private static readonly Guid IidFence = new("0a753dcf-c4d8-4b91-adf6-be5a60d95a76");
        private static readonly Guid IidResource = new("696442be-a72e-4059-bc79-5b5c98040fad");

        [StructLayout(LayoutKind.Sequential)]
        private struct QueueDescription { public int Type, Priority, Flags; public uint NodeMask; }

        /// <summary>D3D12_RESOURCE_BARRIER, as far as a change of state needs it.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct Barrier
        {
            public int Type, Flags;
            public IntPtr Resource;
            public uint Subresource;
            public int Before, After;
        }

        public static Barrier Transition(IntPtr resource, int before, int after) => new()
        {
            Resource = resource,
            Subresource = 0xFFFFFFFF,   // all of it
            Before = before,
            After = after
        };

        [DllImport("d3d12.dll", ExactSpelling = true)]
        private static extern int D3D12CreateDevice(IntPtr adapter, int minimumFeatureLevel, Guid* iid, IntPtr* device);

        private static void* Slot(IntPtr o, int index) => (*(void***)o)[index];

        public static void Check(int result, string doing)
        {
            if (result < 0) throw new InvalidOperationException($"Direct3D 12, {doing}: 0x{result:X8}");
        }

        public static IntPtr CreateDevice(IntPtr adapter)
        {
            Guid iid = IidDevice;
            IntPtr made;
            Check(D3D12CreateDevice(adapter, FeatureLevel12, &iid, &made), "starting on this card");
            return made;
        }

        public static IntPtr CreateQueue(IntPtr device)
        {
            var description = new QueueDescription();
            Guid iid = IidQueue;
            IntPtr made;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, QueueDescription*, Guid*, IntPtr*, int>)Slot(device, 8))(
                device, &description, &iid, &made), "making a command queue");
            return made;
        }

        public static IntPtr CreateAllocator(IntPtr device)
        {
            Guid iid = IidAllocator;
            IntPtr made;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, int, Guid*, IntPtr*, int>)Slot(device, 9))(
                device, 0, &iid, &made), "making a command allocator");
            return made;
        }

        public static IntPtr CreateList(IntPtr device, IntPtr allocator)
        {
            Guid iid = IidList;
            IntPtr made;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int, IntPtr, IntPtr, Guid*, IntPtr*, int>)Slot(device, 12))(
                device, 0, 0, allocator, IntPtr.Zero, &iid, &made), "making a command list");
            return made;
        }

        public static IntPtr CreateSharedFence(IntPtr device)
        {
            Guid iid = IidFence;
            IntPtr made;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, ulong, int, Guid*, IntPtr*, int>)Slot(device, 36))(
                device, 0, FenceShared, &iid, &made), "making a fence");
            return made;
        }

        public static IntPtr CreateSharedHandle(IntPtr device, IntPtr shared)
        {
            IntPtr handle;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void*, uint, char*, IntPtr*, int>)Slot(device, 31))(
                device, shared, null, GenericAll, null, &handle), "sharing the fence");
            return handle;
        }

        public static IntPtr OpenShared(IntPtr device, IntPtr handle)
        {
            Guid iid = IidResource;
            IntPtr opened;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)Slot(device, 32))(
                device, handle, &iid, &opened), "opening a texture of Direct3D 11's");
            return opened;
        }

        public static void Execute(IntPtr queue, IntPtr list) =>
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, void>)Slot(queue, 10))(queue, 1, &list);

        public static int Signal(IntPtr queue, IntPtr fence, ulong value) =>
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, ulong, int>)Slot(queue, 14))(queue, fence, value);

        public static int Wait(IntPtr queue, IntPtr fence, ulong value) =>
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, ulong, int>)Slot(queue, 15))(queue, fence, value);

        public static int Reset(IntPtr allocator) =>
            ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(allocator, 8))(allocator);

        public static int Close(IntPtr list) =>
            ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(list, 9))(list);

        public static int Reset(IntPtr list, IntPtr allocator) =>
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)Slot(list, 10))(list, allocator, IntPtr.Zero);

        public static void Barriers(IntPtr list, Barrier* barriers, uint count) =>
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, Barrier*, void>)Slot(list, 26))(list, count, barriers);

        public static ulong Completed(IntPtr fence) =>
            ((delegate* unmanaged[Stdcall]<IntPtr, ulong>)Slot(fence, 8))(fence);

        /// <summary>Blocks until the fence has reached the value.</summary>
        public static void WaitFor(IntPtr fence, ulong value)
        {
            if (Completed(fence) >= value) return;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, ulong, IntPtr, int>)Slot(fence, 9))(fence, value, IntPtr.Zero),
                  "waiting for the GPU");
        }

        public static uint Release(IntPtr o) => ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(o, 2))(o);
    }
}
