using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Rayxel.Diagnostics;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Rayxel.Render;

/// <summary>One of the upscalers AMD's library offers on this card: its id for asking, its name for showing.</summary>
public readonly record struct FsrVersion(ulong Id, string Name);

/// <summary>
/// AMD's FSR upscaling, run on a video instead of on a game.
/// <para>
/// Like DLSS it is a temporal upscaler, and it is fed the same way: the picture, the
/// movement <see cref="OpticalFlow"/> estimated and a flat constant for depth. What that
/// can and cannot add is said on <see cref="DlssScaler"/>. One thing differs. DLSS makes
/// a good picture of frames that all lie on the same grid; FSR 3 does not - with nothing
/// between the pixels to go by, slanted edges come out in steps - so the picture is
/// moved by part of a pixel before each frame, as a game moves its camera, and FSR is
/// told by how much.
/// </para>
/// <para>
/// "FSR" is several upscalers behind one interface, and which of them runs is decided by
/// AMD's library from the card it finds. FSR 4, the one built on a neural network, is
/// offered on Radeon RX 6000, 7000 and 9000 cards and some Ryzen processors, in a full or
/// a lighter model that the library picks by itself. On anything else - a GeForce - the
/// same call gets FSR 3.1, which is worked out by formula. <see cref="Versions"/> is what
/// the library offered here and <see cref="VersionInUse"/> what it is running.
/// </para>
/// <para>
/// It only exists for Direct3D 12, and this program draws with Direct3D 11. So it gets a
/// Direct3D 12 device of its own on the same card, and the pictures going in and coming
/// out are textures both devices can see: nothing is copied between them. What keeps the
/// two in step is a fence, a counter they share - Direct3D 11 raises it when the picture
/// is ready, Direct3D 12 waits for that, upscales, raises it again, and Direct3D 11 waits
/// for that before it draws the result. The waits are the GPU's, not this thread's.
/// </para>
/// <para>
/// What it costs depends on more than the work. On an RTX 5080 a frame of FSR 3 taken
/// from 1280x800 to 2304x1440 is 0.6 ms - so little that after a few seconds of nothing
/// else to do the driver drops the card into a lower power state, where its memory runs
/// at an eighth of the speed and the same frame takes 3 ms. That is the figure it settles
/// at, and it is the card's doing: waiting for the result on this thread instead, asleep
/// or spinning, was tried and changes nothing but the time this thread loses.
/// </para>
/// <para>
/// The library is two DLLs of AMD's, signed by AMD, that it publishes with its FSR SDK.
/// None of it is compiled into this program or kept with its source: they are looked for
/// beside the program and in <see cref="DataFolder"/>, and without them there is no FSR.
/// </para>
/// </summary>
public sealed unsafe class FsrScaler : IDisposable
{
    /// <summary>The entry point: it finds the effects and passes calls on to them.</summary>
    public const string LoaderName = "amd_fidelityfx_loader_dx12.dll";

    /// <summary>The upscalers themselves.</summary>
    public const string UpscalerName = "amd_fidelityfx_upscaler_dx12.dll";

    // The same wait as DLSS's before a new size is taken up, for the same reason.
    private const int SettleFrames = 20;

    // From ffx_api.h, ffx_upscale.h and ffx_api_dx12.h of the FSR SDK.
    private const ulong TypeCreateUpscale = 0x00010000, TypeDispatchUpscale = 0x00010001, TypeUpscaleVersion = 0x0001000b;
    private const ulong TypeBackendDx12 = 0x2, TypeQueryVersions = 4, TypeOverrideVersion = 5, TypeProviderVersion = 6;

    // The version of those headers: 4.1.2. The structures are the same as 4.1.1's.
    private const uint HeaderVersion = (4u << 22) | (1u << 12) | 2u;

    private const uint CreateAutoExposure = 1 << 5, CreateDebugChecking = 1 << 7, CreateNonLinearColour = 1 << 8;
    private const uint DispatchColourIsSrgb = 1 << 1;

    // FfxApiSurfaceFormat, FfxApiResourceType, FfxApiResourceUsage, FfxApiResourceState.
    private const uint FormatRgba8 = 10, FormatRg16Float = 18, FormatR32Float = 28;
    private const uint TypeTexture2D = 2, UsageReadOnly = 0, UsageUnorderedAccess = 1 << 1;
    private const uint StateUnorderedAccess = 1 << 1, StateComputeRead = 1 << 2;

    // D3D12_RESOURCE_STATES. A texture shared with Direct3D 11 is "common" whenever
    // Direct3D 11 has it.
    private const int Common = 0, UnorderedAccess = 0x8, ShaderResource = 0x40;

    private static readonly Guid IidDevice = new("189819f1-1db6-4b57-be54-1821339b85f7");
    private static readonly Guid IidQueue = new("0ec870a6-5d7e-4c22-8cfc-5baae07616ed");
    private static readonly Guid IidAllocator = new("6102dee4-af59-4b09-b999-b44d73f09b24");
    private static readonly Guid IidList = new("5b160d0f-ac1b-4185-8ba8-b3ae42a5a455");
    private static readonly Guid IidFence = new("0a753dcf-c4d8-4b91-adf6-be5a60d95a76");
    private static readonly Guid IidResource = new("696442be-a72e-4059-bc79-5b5c98040fad");

    [StructLayout(LayoutKind.Sequential)]
    private struct Header { public ulong Type; public Header* Next; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Size2 { public uint Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Float2 { public float X, Y; }

    /// <summary>FfxApiResource: the texture, what it is, and the state it is handed over in.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Resource
    {
        public IntPtr Pointer;
        public uint Type, Format, Width, Height, Depth, MipCount, Flags, Usage;
        public uint State;
    }

    /// <summary>ffxCreateContextDescUpscale.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CreateUpscale
    {
        public Header Header;
        public uint Flags;
        public Size2 MaxRenderSize, MaxUpscaleSize;
        public delegate* unmanaged[Cdecl]<uint, char*, void> Message;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UpscaleVersion { public Header Header; public uint Version; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Backend { public Header Header; public IntPtr Device; }

    [StructLayout(LayoutKind.Sequential)]
    private struct OverrideVersion { public Header Header; public ulong VersionId; }

    /// <summary>
    /// Everything a context is created from. The library keeps the pointers it is given
    /// for as long as the context lives, so this is kept, in memory that does not move.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CreateChain
    {
        public CreateUpscale Upscale;
        public UpscaleVersion Version;
        public Backend Backend;
        public OverrideVersion Override;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryVersions
    {
        public Header Header;
        public ulong CreateType;
        public IntPtr Device;
        public ulong* Count;
        public ulong* Ids;
        public byte** Names;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderVersion { public Header Header; public ulong VersionId; public byte* Name; }

    /// <summary>ffxDispatchDescUpscale. The two flags are C's one-byte bool.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DispatchUpscale
    {
        public Header Header;
        public IntPtr CommandList;
        public Resource Color, Depth, MotionVectors, Exposure, Reactive, TransparencyAndComposition, Output;
        public Float2 JitterOffset, MotionVectorScale;
        public Size2 RenderSize, UpscaleSize;
        public byte EnableSharpening;
        public float Sharpness, FrameTimeDelta, PreExposure;
        public byte Reset;
        public float CameraNear, CameraFar, CameraFovAngleVertical, ViewSpaceToMetersFactor;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueueDescription { public int Type, Priority, Flags; public uint NodeMask; }

    /// <summary>D3D12_RESOURCE_BARRIER, as far as a change of state needs it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Barrier
    {
        public int Type, Flags;
        public IntPtr Resource;
        public uint Subresource;
        public int Before, After;
    }

    /// <summary>One texture, as each of the two devices holds it.</summary>
    private sealed class Shared(ID3D11Texture2D texture, IntPtr resource) : IDisposable
    {
        public ID3D11Texture2D Texture { get; } = texture;

        /// <summary>The same memory as an ID3D12Resource.</summary>
        public IntPtr Resource { get; private set; } = resource;

        public void Dispose()
        {
            if (Resource != IntPtr.Zero) { Unknown.Release(Resource); Resource = IntPtr.Zero; }
            Texture.Dispose();
        }
    }

    private readonly ID3D11Device _device;
    private ID3D11Device5? _device5;
    private ID3D11DeviceContext4? _context4;
    private ID3D11Fence? _fence11;

    // Direct3D 12's side. Only a dozen of its calls are needed, so they are made through
    // the interfaces' own tables rather than through a library brought in for it: see D3D12.
    private IntPtr _device12, _queue, _list, _fence12;
    private const int Allocators = 3;
    private readonly IntPtr[] _allocators = new IntPtr[Allocators];
    private readonly ulong[] _allocatorBusyUntil = new ulong[Allocators];
    private int _allocator;
    private ulong _fenceValue, _lastDone;

    private IntPtr _entry;
    private delegate* unmanaged[Cdecl]<IntPtr*, Header*, void*, uint> _create;
    private delegate* unmanaged[Cdecl]<IntPtr*, void*, uint> _destroy;
    private delegate* unmanaged[Cdecl]<IntPtr*, Header*, uint> _query;
    private delegate* unmanaged[Cdecl]<IntPtr*, Header*, uint> _dispatch;

    private IntPtr _upscaler;
    private CreateChain* _chain;
    private bool _started, _failed;
    private readonly List<FsrVersion> _versions = [];

    private Shared? _color, _motion, _depth, _output;
    private ID3D11RenderTargetView? _motionTarget;
    private ID3D11ShaderResourceView? _outputView;
    private int _inWidth, _inHeight, _outWidth, _outHeight;
    private int _askedWidth, _askedHeight, _askedFor;
    private bool _reset;
    private long _lastFrame;
    private string? _builtFor;

    /// <summary>The upscaled picture, once a frame has been through.</summary>
    public ID3D11ShaderResourceView? OutputView => _outputView;

    public int OutputWidth => _outWidth;
    public int OutputHeight => _outHeight;

    /// <summary>Why it is not running, when it is not.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// True while there is a reason to offer it: AMD's library is where it is looked for,
    /// and nothing has failed so far. Which upscaler it gives is only known once it is started.
    /// </summary>
    public bool Possible => !_failed && Folder is not null;

    /// <summary>What the library offers on this card, newest first. Empty until it is started.</summary>
    public IReadOnlyList<FsrVersion> Versions => _versions;

    /// <summary>
    /// The upscaler asked for, by the name it is offered under ("3.1.5"), or by the start
    /// of one ("3", "4.1"). Null leaves the choice to the library, which takes the newest
    /// it can run here. Changing it takes effect with the next frame, as the first of a
    /// new history.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>The upscaler in use, by the library's own account; null when none is.</summary>
    public string? VersionInUse { get; private set; }

    /// <summary>
    /// How much of FSR's own sharpening goes on last, from 0 for none to 1. It is part of
    /// how FSR is meant to look: what it accumulates comes out soft, and softer still from
    /// a picture that had to be moved about for it (see <see cref="Process"/>). At a half,
    /// what stands still comes out about as sharp as DLSS leaves it.
    /// </summary>
    public float Sharpness { get; set; } = 0.5f;

    /// <summary>What it is doing, for the log and the readout.</summary>
    public string Description => _upscaler == IntPtr.Zero
        ? "not running"
        : $"{_inWidth}x{_inHeight} -> {_outWidth}x{_outHeight}, FSR {VersionInUse ?? "of a version not reported"}";

    public FsrScaler(ID3D11Device device) => _device = device;

    /// <summary>A folder to look in before the others. For trying a copy of the library that lies somewhere else.</summary>
    public static string? FolderOverride
    {
        get => _folderOverride;
        set { _folderOverride = value; _folderLooked = false; }
    }

    private static string? _folderOverride, _folder;
    private static bool _folderLooked;

    /// <summary>Where the library may be kept when it is not beside the program.</summary>
    public static string DataFolder => Path.Combine(Settings.AppSettings.Directory, "fsr");

    /// <summary>The folder AMD's library was found in, or null when it is in none of the places looked.</summary>
    public static string? Folder
    {
        get
        {
            if (_folderLooked) return _folder;
            _folderLooked = true;
            _folder = null;
            foreach (string? place in (string?[])[FolderOverride, AppContext.BaseDirectory, DataFolder])
            {
                if (string.IsNullOrWhiteSpace(place)) continue;
                try
                {
                    string full = Path.GetFullPath(place);
                    if (File.Exists(Path.Combine(full, UpscalerName))) return _folder = full;
                }
                catch (Exception ex) { Log.Debug($"FSR: {place} cannot be looked in: {ex.Message}"); }
            }
            return null;
        }
    }

    /// <summary>
    /// Loads the library, gives it a Direct3D 12 device on this card and asks what it can
    /// run there. Cheap to call again: it does the work once and remembers the answer.
    /// </summary>
    public bool Start()
    {
        if (_started) return true;
        if (_failed) return false;

        try
        {
            if (Folder is not { } folder)
                return Fail($"AMD's {UpscalerName} is neither beside this program nor in {DataFolder}");
            // The other side reads these byte for byte. Wrong here means wrong everywhere.
            if (sizeof(DispatchUpscale) != 432 || sizeof(CreateUpscale) != 48 || sizeof(Resource) != 48)
                return Fail("the structures FSR is called with are not the size its headers give");

            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            StartDirect3D12();
            Load(folder);
            ReadVersions();

            _started = true;
            Log.Info($"FSR: ready in {System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds:0} ms " +
                     $"({LibraryVersion(folder)} in {folder}), offering {string.Join(", ", _versions.Select(v => v.Name))}");
            return true;
        }
        catch (Exception ex)
        {
            return Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string LibraryVersion(string folder)
    {
        try
        {
            return "library " + System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(folder, UpscalerName)).FileVersion;
        }
        catch { return "library of unknown version"; }
    }

    /// <summary>
    /// A Direct3D 12 device on the card the Direct3D 11 one is on, something to give it
    /// work through, and the fence the two keep in step by.
    /// </summary>
    private void StartDirect3D12()
    {
        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();

        _device12 = D3D12.CreateDevice(adapter.NativePointer);
        _queue = D3D12.CreateQueue(_device12);
        for (int i = 0; i < Allocators; i++) _allocators[i] = D3D12.CreateAllocator(_device12);
        _list = D3D12.CreateList(_device12, _allocators[0]);
        // A list is made open, and has to be closed before it can be opened for a frame.
        D3D12.Check(D3D12.Close(_list), "closing the new command list");

        _fence12 = D3D12.CreateSharedFence(_device12);
        IntPtr handle = D3D12.CreateSharedHandle(_device12, _fence12);
        try
        {
            _device5 = _device.QueryInterface<ID3D11Device5>();
            _fence11 = _device5.OpenSharedFence<ID3D11Fence>(handle);
        }
        finally { CloseHandle(handle); }
    }

    private void Load(string folder)
    {
        // The effects first, and by their full path. The loader looks for them beside the
        // program, and takes a copy that is already loaded wherever that came from.
        IntPtr effects = NativeLibrary.Load(Path.Combine(folder, UpscalerName));
        string loader = Path.Combine(folder, LoaderName);
        // The effects answer the same five calls themselves, which is all a lone copy needs.
        _entry = File.Exists(loader) ? NativeLibrary.Load(loader) : effects;

        _create = (delegate* unmanaged[Cdecl]<IntPtr*, Header*, void*, uint>)Export("ffxCreateContext");
        _destroy = (delegate* unmanaged[Cdecl]<IntPtr*, void*, uint>)Export("ffxDestroyContext");
        _query = (delegate* unmanaged[Cdecl]<IntPtr*, Header*, uint>)Export("ffxQuery");
        _dispatch = (delegate* unmanaged[Cdecl]<IntPtr*, Header*, uint>)Export("ffxDispatch");
    }

    private IntPtr Export(string name) =>
        NativeLibrary.TryGetExport(_entry, name, out IntPtr address)
            ? address
            : throw new EntryPointNotFoundException($"AMD's library has no {name}");

    /// <summary>
    /// Asks which upscalers there are for this device. The answer depends on the card:
    /// the library leaves out the ones it will not run there.
    /// </summary>
    private void ReadVersions()
    {
        _versions.Clear();
        ulong count = 0;
        var query = new QueryVersions
        {
            Header = { Type = TypeQueryVersions }, CreateType = TypeCreateUpscale, Device = _device12, Count = &count
        };
        uint result = _query(null, &query.Header);
        if (result != 0 || count == 0) throw new InvalidOperationException($"FSR offers no upscaler here: {Describe(result)}");

        ulong* ids = stackalloc ulong[(int)count];
        byte** names = stackalloc byte*[(int)count];
        query.Ids = ids;
        query.Names = names;
        result = _query(null, &query.Header);
        if (result != 0) throw new InvalidOperationException($"FSR would not list its upscalers: {Describe(result)}");

        // The names are the library's own memory, and it may write over them: copied at once.
        for (int i = 0; i < (int)count; i++)
            _versions.Add(new FsrVersion(ids[i], Marshal.PtrToStringUTF8((IntPtr)names[i]) ?? $"0x{ids[i]:X}"));
    }

    /// <summary>
    /// The offered upscaler a name stands for: that name, else the newest whose name begins
    /// the same way down to the minor version, else down to the major. A name remembered
    /// from one copy of the library still finds its successor in the next ("3.1.5" finds
    /// "3.1.6"). Null when nothing fits, which leaves the choice to the library.
    /// </summary>
    private FsrVersion? Resolve(string? asked)
    {
        if (string.IsNullOrWhiteSpace(asked)) return null;
        // A version the driver supplies is marked with a trailing " *".
        static string Plain(string name) => name.TrimEnd('*', ' ');

        string want = Plain(asked.Trim());
        foreach (var v in _versions) if (Plain(v.Name).Equals(want, StringComparison.OrdinalIgnoreCase)) return v;

        string[] parts = want.Split('.');
        for (int keep = Math.Min(2, parts.Length); keep >= 1; keep--)
        {
            string prefix = string.Join('.', parts.Take(keep));
            foreach (var v in _versions)
            {
                string name = Plain(v.Name);
                if (name.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase)) return v;
            }
        }
        return null;
    }

    /// <summary>Whether this one of <see cref="Versions"/> is what <see cref="Version"/> asks for.</summary>
    public bool Asks(FsrVersion offered) => Resolve(Version) is { } asked && asked.Id == offered.Id;

    private bool Fail(string why)
    {
        _failed = true;
        LastError = why;
        Log.Warn($"FSR unavailable: {why}");
        return false;
    }

    /// <summary>Makes the next frame the first of a new history: what was accumulated is of another picture.</summary>
    public void ResetHistory() => _reset = true;

    /// <summary>
    /// Upscales one frame into <see cref="OutputView"/>. The picture is plain SDR colour
    /// at its own size; the vectors are one per pixel of it, in its pixels, each saying
    /// where that pixel was in the frame before relative to where it is now - which is
    /// the direction and the unit FSR takes them in, as it is DLSS's. No vectors means
    /// nothing moved.
    /// <para>
    /// The shift is how far, in its own pixels, the picture has been moved right and down
    /// from where it belongs. FSR is built to be given a different one every frame.
    /// </para>
    /// <para>
    /// False when there is no picture to show for it: FSR could not be started or built,
    /// or refused the frame.
    /// </para>
    /// </summary>
    public bool Process(ID3D11DeviceContext context, ID3D11Texture2D color, ID3D11Texture2D? motion,
                        float shiftX, float shiftY, int inWidth, int inHeight, int outWidth, int outHeight)
    {
        if (!Start()) return false;
        if (!Fit(context, inWidth, inHeight, outWidth, outHeight)) return false;

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float sinceLast = _lastFrame == 0
            ? 16.7f
            : (float)Math.Clamp(System.Diagnostics.Stopwatch.GetElapsedTime(_lastFrame, now).TotalMilliseconds, 1, 100);
        _lastFrame = now;

        try
        {
            _context4 ??= context.QueryInterface<ID3D11DeviceContext4>();

            // Direct3D 11's part: the picture and its vectors into the textures both
            // devices see, and the fence raised behind them. Flushed, because Direct3D 12
            // is about to wait for exactly that and would otherwise wait for nothing sent.
            context.CopyResource(_color!.Texture, color);
            if (motion is not null) context.CopyResource(_motion!.Texture, motion);
            else context.ClearRenderTargetView(_motionTarget!, new Vortice.Mathematics.Color4(0f, 0f, 0f, 0f));
            ulong handedOver = ++_fenceValue;
            _context4.Signal(_fence11!, handedOver);
            context.Flush();

            // Direct3D 12's part.
            D3D12.Check(D3D12.Wait(_queue, _fence12, handedOver), "waiting for the picture");
            IntPtr allocator = NextAllocator();
            D3D12.Check(D3D12.Reset(allocator), "resetting the allocator");
            D3D12.Check(D3D12.Reset(_list, allocator), "opening the command list");

            Barrier* states = stackalloc Barrier[4];
            states[0] = Change(_color.Resource, Common, ShaderResource);
            states[1] = Change(_motion!.Resource, Common, ShaderResource);
            states[2] = Change(_depth!.Resource, Common, ShaderResource);
            states[3] = Change(_output!.Resource, Common, UnorderedAccess);
            D3D12.Barriers(_list, states, 4);

            var frame = new DispatchUpscale
            {
                Header = { Type = TypeDispatchUpscale },
                CommandList = _list,
                Color = Describe(_color, FormatRgba8, _inWidth, _inHeight, UsageReadOnly, StateComputeRead),
                Depth = Describe(_depth, FormatR32Float, _inWidth, _inHeight, UsageReadOnly, StateComputeRead),
                MotionVectors = Describe(_motion, FormatRg16Float, _inWidth, _inHeight, UsageReadOnly, StateComputeRead),
                Output = Describe(_output, FormatRgba8, _outWidth, _outHeight, UsageUnorderedAccess, StateUnorderedAccess),
                // How far, in its own pixels, the picture was moved right and down before
                // it was handed over: FSR takes what it finds at a pixel to belong that
                // much up and to the left of it.
                JitterOffset = { X = shiftX, Y = shiftY },
                // The vectors are already in pixels of the picture going in.
                MotionVectorScale = { X = 1f, Y = 1f },
                RenderSize = { Width = (uint)_inWidth, Height = (uint)_inHeight },
                UpscaleSize = { Width = (uint)_outWidth, Height = (uint)_outHeight },
                EnableSharpening = (byte)(Sharpness > 0f ? 1 : 0),
                Sharpness = Math.Clamp(Sharpness, 0f, 1f),
                FrameTimeDelta = sinceLast,
                PreExposure = 1f,
                Reset = (byte)(_reset ? 1 : 0),
                // A camera for a picture that has none. With the depth flat, all these
                // decide is that it is a valid camera: near is nearer than far.
                CameraNear = 0.1f,
                CameraFar = 1000f,
                CameraFovAngleVertical = 1f,
                ViewSpaceToMetersFactor = 1f,
                Flags = DispatchColourIsSrgb
            };
            IntPtr upscaler = _upscaler;
            uint result = _dispatch(&upscaler, &frame.Header);

            // FSR leaves each texture in the state it was handed over in.
            states[0] = Change(_color.Resource, ShaderResource, Common);
            states[1] = Change(_motion.Resource, ShaderResource, Common);
            states[2] = Change(_depth.Resource, ShaderResource, Common);
            states[3] = Change(_output.Resource, UnorderedAccess, Common);
            D3D12.Barriers(_list, states, 4);
            D3D12.Check(D3D12.Close(_list), "closing the command list");

            if (result != 0)
            {
                Release();
                return Fail($"FSR refused a frame: {Describe(result)}");
            }

            D3D12.Execute(_queue, _list);
            ulong done = ++_fenceValue;
            D3D12.Check(D3D12.Signal(_queue, _fence12, done), "raising the fence");
            _allocatorBusyUntil[_allocator] = done;
            _lastDone = done;

            // And Direct3D 11 again: nothing after this is carried out before the result is there.
            _context4.Wait(_fence11!, done);
        }
        catch (Exception ex)
        {
            string removed = _device12 != IntPtr.Zero && D3D12.RemovedReason(_device12) is var reason && reason < 0
                ? $" (Direct3D 12 device lost: 0x{reason:X8})" : "";
            Release();
            return Fail($"{ex.GetType().Name}: {ex.Message}{removed}");
        }

        _reset = false;
        return true;
    }

    private static Barrier Change(IntPtr resource, int before, int after) => new()
    {
        Type = 0, Flags = 0, Resource = resource, Subresource = 0xffffffff, Before = before, After = after
    };

    private static Resource Describe(Shared shared, uint format, int width, int height, uint usage, uint state) => new()
    {
        Pointer = shared.Resource,
        Type = TypeTexture2D,
        Format = format,
        Width = (uint)width,
        Height = (uint)height,
        Depth = 1,
        MipCount = 1,
        Flags = 0,
        Usage = usage,
        State = state
    };

    /// <summary>
    /// The next allocator round, once the GPU is done with what was last recorded into it.
    /// Three of them, so that in the ordinary run of things there is nothing to wait for.
    /// </summary>
    private IntPtr NextAllocator()
    {
        _allocator = (_allocator + 1) % Allocators;
        ulong busyUntil = _allocatorBusyUntil[_allocator];
        if (busyUntil != 0 && D3D12.Completed(_fence12) < busyUntil) D3D12.WaitFor(_fence12, busyUntil);
        return _allocators[_allocator];
    }

    /// <summary>Makes sure there is an upscaler built for these sizes, or one still good enough.</summary>
    private bool Fit(ID3D11DeviceContext context, int inWidth, int inHeight, int outWidth, int outHeight)
    {
        if (_upscaler != IntPtr.Zero && !string.Equals(_builtFor, Version, StringComparison.OrdinalIgnoreCase))
            return Create(context, inWidth, inHeight, outWidth, outHeight);

        bool sameSource = _upscaler != IntPtr.Zero && inWidth == _inWidth && inHeight == _inHeight;
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
        Release();
        long began = System.Diagnostics.Stopwatch.GetTimestamp();

        try
        {
            var asked = Resolve(Version);
            if (Version is not null && asked is null)
                Log.Warn($"FSR: no \"{Version}\" among {string.Join(", ", _versions.Select(v => v.Name))}; left to the library");

            _chain = (CreateChain*)NativeMemory.AllocZeroed((nuint)sizeof(CreateChain));
            _chain->Backend = new Backend { Header = { Type = TypeBackendDx12 }, Device = _device12 };
            _chain->Override = new OverrideVersion
            {
                Header = { Type = TypeOverrideVersion, Next = &_chain->Backend.Header }, VersionId = asked?.Id ?? 0
            };
            _chain->Version = new UpscaleVersion
            {
                Header =
                {
                    Type = TypeUpscaleVersion,
                    // Naming a version switches the library's own choosing off, so it is
                    // only named when one was asked for.
                    Next = asked is null ? &_chain->Backend.Header : &_chain->Override.Header
                },
                Version = HeaderVersion
            };
            _chain->Upscale = new CreateUpscale
            {
                Header = { Type = TypeCreateUpscale, Next = &_chain->Version.Header },
                // The picture is ordinary gamma-encoded SDR, not the linear light a game
                // has at this point. FSR 4 is told so; FSR 3 has no such setting and
                // takes it as it comes. The checking is its own, into our log.
                Flags = CreateAutoExposure | CreateNonLinearColour
                        | (Log.MinLevel <= LogLevel.Debug ? CreateDebugChecking : 0),
                MaxRenderSize = { Width = (uint)inWidth, Height = (uint)inHeight },
                MaxUpscaleSize = { Width = (uint)outWidth, Height = (uint)outHeight },
                Message = &OnMessage
            };

            IntPtr made = IntPtr.Zero;
            uint result = _create(&made, &_chain->Upscale.Header, null);
            if (result != 0 || made == IntPtr.Zero)
                return Fail($"FSR could not be built for {inWidth}x{inHeight} -> {outWidth}x{outHeight}: {Describe(result)}");
            _upscaler = made;
            _builtFor = Version;

            var running = new ProviderVersion { Header = { Type = TypeProviderVersion } };
            VersionInUse = _query(&made, &running.Header) == 0 && running.Name is not null
                ? Marshal.PtrToStringUTF8((IntPtr)running.Name)
                : null;

            _color = Share(inWidth, inHeight, Format.R8G8B8A8_UNorm, BindFlags.RenderTarget | BindFlags.ShaderResource);
            _motion = Share(inWidth, inHeight, Format.R16G16_Float, BindFlags.RenderTarget | BindFlags.ShaderResource);
            _motionTarget = _device.CreateRenderTargetView(_motion.Texture);

            // Depth, which a video does not have: the same distance everywhere, as for DLSS.
            _depth = Share(inWidth, inHeight, Format.R32_Float, BindFlags.RenderTarget | BindFlags.ShaderResource);
            using (var target = _device.CreateRenderTargetView(_depth.Texture))
                context.ClearRenderTargetView(target, new Vortice.Mathematics.Color4(0.5f, 0.5f, 0.5f, 0.5f));

            // FSR writes its result from a compute shader, hence unordered access.
            _output = Share(outWidth, outHeight, Format.R8G8B8A8_UNorm, BindFlags.UnorderedAccess | BindFlags.ShaderResource);
            _outputView = _device.CreateShaderResourceView(_output.Texture);

            _inWidth = inWidth; _inHeight = inHeight;
            _outWidth = outWidth; _outHeight = outHeight;
            _askedFor = 0;
            _reset = true;
            _lastFrame = 0;

            Log.Info($"FSR: {Description}, built in " +
                     $"{System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds:0} ms");
            return true;
        }
        catch (Exception ex)
        {
            Release();
            return Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// A texture made by Direct3D 11 and opened by Direct3D 12. Shared by handle, which is
    /// what lets another device - of another API, even - open the same memory.
    /// </summary>
    private Shared Share(int width, int height, Format format, BindFlags bind)
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

    private void Release()
    {
        // Nothing of this may go while the GPU is still working with it.
        try
        {
            if (_fence12 != IntPtr.Zero && _lastDone != 0 && D3D12.Completed(_fence12) < _lastDone)
                D3D12.WaitFor(_fence12, _lastDone);
        }
        catch (Exception ex) { Log.Debug($"FSR: waiting for the GPU before releasing: {ex.Message}"); }

        if (_upscaler != IntPtr.Zero)
        {
            IntPtr upscaler = _upscaler;
            uint result = _destroy(&upscaler, null);
            if (result != 0) Log.Debug($"FSR: destroying the context: {Describe(result)}");
            _upscaler = IntPtr.Zero;
        }
        if (_chain is not null) { NativeMemory.Free(_chain); _chain = null; }

        _outputView?.Dispose(); _outputView = null;
        _motionTarget?.Dispose(); _motionTarget = null;
        _output?.Dispose(); _output = null;
        _depth?.Dispose(); _depth = null;
        _motion?.Dispose(); _motion = null;
        _color?.Dispose(); _color = null;
        _inWidth = _inHeight = _outWidth = _outHeight = 0;
        VersionInUse = null;
    }

    public void Dispose()
    {
        try { Release(); }
        catch (Exception ex) { Log.Debug($"FSR: releasing: {ex.Message}"); }

        _fence11?.Dispose(); _fence11 = null;
        _context4?.Dispose(); _context4 = null;
        _device5?.Dispose(); _device5 = null;

        Unknown.Release(ref _list);
        for (int i = 0; i < Allocators; i++) Unknown.Release(ref _allocators[i]);
        Unknown.Release(ref _fence12);
        Unknown.Release(ref _queue);
        Unknown.Release(ref _device12);
        // The DLLs stay loaded: unloading code that may still have threads is not worth the saving.
        _started = false;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnMessage(uint type, char* message)
    {
        // Called from inside the library. Nothing may escape back into it.
        try
        {
            string text = new string(message).TrimEnd();
            if (text.Length == 0) return;
            if (type == 0) Log.Warn("fsr: " + text);
            else Log.Debug("fsr: " + text);
        }
        catch { }
    }

    /// <summary>FfxApiReturnCodes.</summary>
    private static string Describe(uint result) => result switch
    {
        0 => "success",
        1 => "an error it does not specify",
        2 => "a structure it does not know",
        3 => "an error from Direct3D 12 or from the upscaler itself",
        4 => "no upscaler for this request",
        5 => "out of memory",
        6 => "an invalid parameter",
        7 => "a structure newer than this copy of the library",
        _ => $"code {result}"
    };

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int CloseHandle(IntPtr handle);

    /// <summary>The three calls every COM object answers, of which one is needed.</summary>
    private static class Unknown
    {
        public static void Release(IntPtr o) => ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(void***)o)[2])(o);

        public static void Release(ref IntPtr o)
        {
            if (o == IntPtr.Zero) return;
            Release(o);
            o = IntPtr.Zero;
        }
    }

    /// <summary>
    /// The few calls of Direct3D 12 this needs, made through the interfaces' tables of
    /// methods. The slots are where d3d12.h puts them, counted off the header's own
    /// declarations: a device's methods start at 7, behind the three of IUnknown and the
    /// four of ID3D12Object, and those of the objects a device makes at 8, behind one more.
    /// </summary>
    private static class D3D12
    {
        private const int FeatureLevel12 = 0xc000;      // D3D_FEATURE_LEVEL_12_0
        private const int FenceShared = 0x1;            // D3D12_FENCE_FLAG_SHARED
        private const uint GenericAll = 0x10000000;

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
            var description = new QueueDescription();   // direct, normal priority
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

        public static int RemovedReason(IntPtr device) =>
            ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(device, 37))(device);

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

        /// <summary>Blocks until the fence has reached the value: with no event to set, the call itself waits.</summary>
        public static void WaitFor(IntPtr fence, ulong value) =>
            Check(((delegate* unmanaged[Stdcall]<IntPtr, ulong, IntPtr, int>)Slot(fence, 9))(fence, value, IntPtr.Zero),
                  "waiting for the GPU");
    }
}
