using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Rayxel.Diagnostics;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Rayxel.Capture;

/// <summary>
/// The PyroWave codec's decoder, through the codec's own library: a wavelet codec in which
/// every frame stands alone, made for links fast enough to spend 100 Mbit/s and more on a
/// Steam Deck's picture in exchange for the shortest encode and decode there are.
/// <para>
/// The library is optional and loaded the first time it is wanted, from beside this program
/// or from <see cref="DataFolder"/>. It decodes with Vulkan compute shaders on the same GPU
/// the picture is drawn on, picked by the adapter's LUID, and hands the picture back in CPU
/// memory. From there it goes into a staging texture, and on into the renderer's texture by
/// the same copy every other decoded picture takes. That is the codec's simple path, and at a
/// Deck's size it costs well under a millisecond; sharing the picture between Vulkan and
/// Direct3D 11 directly would save that, at the price of a great deal more machinery.
/// </para>
/// </summary>
internal sealed unsafe class PyroWaveDecoder : IDisposable
{
    public const string LibraryName = "libpyrowave-shared-1.dll";

    /// <summary>The one major version of the codec's interface this code speaks.</summary>
    private const uint ApiMajor = 1;

    private const int Success = 0;
    private const int Chroma420 = 0;
    private const int FormatYuv420P = 1;
    private const int QueueCompute = 2;          // VK_QUEUE_COMPUTE_BIT
    private const int GlobalPriorityMedium = 256; // VK_QUEUE_GLOBAL_PRIORITY_MEDIUM

    /// <summary>Where the library may be kept when it is not beside the program.</summary>
    public static string DataFolder => Path.Combine(Settings.AppSettings.Directory, "pyrowave");

    [StructLayout(LayoutKind.Sequential)]
    private struct CreateInfo
    {
        public IntPtr Device;
        public int Width, Height;
        public int Chroma;
        public byte FragmentPath; // a C bool
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CpuBuffer
    {
        public void* Data0, Data1, Data2;
        public nuint Stride0, Stride1, Stride2;
        public nuint Size0, Size1, Size2;
        public int Width, Height;
        public int Format;
    }

    // The library, once for the process. The Vulkan device is made once too: making one
    // takes long enough to be felt, and every decoder may share it.
    private static readonly object Gate = new();
    private static bool _looked;
    private static string? _problem;
    private static IntPtr _library, _device;
    private static string _where = "";

    private static delegate* unmanaged[Cdecl]<uint*, uint*, uint*, void> _apiVersion;
    private static delegate* unmanaged[Cdecl]<uint, uint, void*, void*, void*, int, IntPtr*, int> _createDevice;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, int> _setQueue;
    private static delegate* unmanaged[Cdecl]<CreateInfo*, IntPtr*, int> _create;
    private static delegate* unmanaged[Cdecl]<IntPtr, void*, nuint, int> _push;
    private static delegate* unmanaged[Cdecl]<IntPtr, byte, byte> _isReady;
    private static delegate* unmanaged[Cdecl]<IntPtr, CpuBuffer*, int> _decodeCpu;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _destroy;

    /// <summary>
    /// Why the codec cannot be used here, or null when it can. Only finds and loads the
    /// library, which is quick; whether the GPU takes to it is learnt by the first decoder.
    /// </summary>
    public static string? Problem
    {
        get
        {
            lock (Gate)
            {
                if (!_looked) Load();
                return _problem;
            }
        }
    }

    private static void Load()
    {
        _looked = true;
        try
        {
            IntPtr handle = IntPtr.Zero;
            foreach (string place in (string[])[AppContext.BaseDirectory, DataFolder])
            {
                string path = Path.Combine(place, LibraryName);
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out handle))
                {
                    _where = place;
                    break;
                }
            }
            // A single-file build keeps the libraries it carries in a folder of its own,
            // which the loader is told about and this search is not.
            if (handle == IntPtr.Zero &&
                NativeLibrary.TryLoad(LibraryName, typeof(PyroWaveDecoder).Assembly, null, out handle))
                _where = "the program";
            if (handle == IntPtr.Zero)
            {
                _problem = $"{LibraryName} is neither beside this program nor in {DataFolder}";
                return;
            }
            _library = handle;

            _apiVersion = (delegate* unmanaged[Cdecl]<uint*, uint*, uint*, void>)Export("pyrowave_get_api_version");
            _createDevice = (delegate* unmanaged[Cdecl]<uint, uint, void*, void*, void*, int, IntPtr*, int>)
                Export("pyrowave_create_device_by_compat");
            _setQueue = (delegate* unmanaged[Cdecl]<IntPtr, int, int>)Export("pyrowave_device_set_queue_type");
            _create = (delegate* unmanaged[Cdecl]<CreateInfo*, IntPtr*, int>)Export("pyrowave_decoder_create");
            _push = (delegate* unmanaged[Cdecl]<IntPtr, void*, nuint, int>)Export("pyrowave_decoder_push_packet");
            _isReady = (delegate* unmanaged[Cdecl]<IntPtr, byte, byte>)Export("pyrowave_decoder_decode_is_ready");
            _decodeCpu = (delegate* unmanaged[Cdecl]<IntPtr, CpuBuffer*, int>)
                Export("pyrowave_decoder_decode_cpu_buffer_synchronous");
            _destroy = (delegate* unmanaged[Cdecl]<IntPtr, void>)Export("pyrowave_decoder_destroy");

            uint major, minor, patch;
            _apiVersion(&major, &minor, &patch);
            if (major != ApiMajor)
            {
                _problem = $"{LibraryName} speaks version {major} of its interface, this program {ApiMajor}";
                return;
            }
            Log.Info($"PyroWave: {LibraryName} {major}.{minor}.{patch} from {_where}");
        }
        catch (Exception ex)
        {
            _problem = $"{LibraryName} could not be loaded: {ex.Message}";
        }
    }

    private static IntPtr Export(string name) =>
        NativeLibrary.TryGetExport(_library, name, out IntPtr address)
            ? address
            : throw new EntryPointNotFoundException($"{LibraryName} has no {name} - another version?");

    /// <summary>The codec's Vulkan device, on the GPU <paramref name="d3d"/> is on.</summary>
    private static IntPtr Device(ID3D11Device d3d)
    {
        lock (Gate)
        {
            if (_device != IntPtr.Zero) return _device;
            if (Problem is { } problem) throw new InvalidOperationException(problem);

            // The adapter's LUID is the one thing Direct3D and Vulkan both call a GPU by.
            using var dxgi = d3d.QueryInterface<IDXGIDevice>();
            using var adapter = dxgi.GetAdapter();
            var luid = adapter.Description.Luid;
            byte* bytes = stackalloc byte[8];
            *(uint*)bytes = luid.LowPart;
            *(int*)(bytes + 4) = luid.HighPart;

            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            IntPtr device;
            int result = _createDevice(0, 0, null, null, bytes, GlobalPriorityMedium, &device);
            if (result != Success || device == IntPtr.Zero)
                throw new InvalidOperationException(
                    $"PyroWave found no Vulkan device on {adapter.Description.Description} (error {result})");

            // Its own queue, so a frame's decode does not wait behind whatever Vulkan work
            // there is; not that there is any but ours.
            if (_setQueue(device, QueueCompute) != Success)
                Log.Info("PyroWave: no separate compute queue, decoding on the graphics one");

            _device = device;
            Log.Info($"PyroWave: Vulkan device on {adapter.Description.Description} in " +
                     $"{System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds:0} ms");
            return _device;
        }
    }

    private readonly ID3D11Device _d3d;
    // The device's own object for its immediate context, which the renderer holds as well:
    // used here, never disposed here.
    private readonly ID3D11DeviceContext _context;
    private IntPtr _decoder;
    private readonly int _width, _height;
    private readonly byte[] _u, _v;

    // Several, so that filling one never waits for the GPU to finish copying out of the
    // last: mapping a texture that is still being read stalls until it is not.
    private readonly ID3D11Texture2D[] _staging = new ID3D11Texture2D[3];
    private int _next;

    public string Name => "PyroWave (Vulkan)";

    public PyroWaveDecoder(ID3D11Device d3d, int width, int height)
    {
        if ((width & 1) != 0 || (height & 1) != 0)
            throw new ArgumentException($"PyroWave's 4:2:0 needs an even size, not {width}x{height}");
        _d3d = d3d;
        _width = width;
        _height = height;

        var info = new CreateInfo { Device = Device(d3d), Width = width, Height = height, Chroma = Chroma420 };
        IntPtr decoder;
        int result = _create(&info, &decoder);
        if (result != Success)
            throw new InvalidOperationException($"PyroWave will not decode {width}x{height} (error {result})");
        _decoder = decoder;

        _u = GC.AllocateUninitializedArray<byte>(width / 2 * (height / 2), pinned: true);
        _v = GC.AllocateUninitializedArray<byte>(width / 2 * (height / 2), pinned: true);

        _context = d3d.ImmediateContext;
        var description = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Write,
        };
        for (int i = 0; i < _staging.Length; i++) _staging[i] = d3d.CreateTexture2D(description);
    }

    /// <summary>The codec's packets, one after another: a whole frame. False when the codec could not read them.</summary>
    public bool Push(ReadOnlySpan<byte> packet)
    {
        fixed (byte* p = packet) return _push(_decoder, p, (nuint)packet.Length) == Success;
    }

    /// <summary>Whether every packet of the frame pushed is in.</summary>
    public bool Ready => _isReady(_decoder, 0) != 0;

    /// <summary>
    /// Decodes the frame whose packets have been pushed and passes the picture on. Blocks
    /// while the GPU decodes, which is the point: the picture is wanted now.
    /// </summary>
    public void Decode(long sampleTime, IPictureSink sink)
    {
        var staging = _staging[_next];
        _next = (_next + 1) % _staging.Length;

        // The luma goes straight into the texture; the two chroma planes come out apart and
        // are woven together into NV12's one plane of pairs.
        var map = _context.Map(staging, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None);
        try
        {
            byte* luma = (byte*)map.DataPointer;
            nuint pitch = map.RowPitch;
            fixed (byte* u = _u, v = _v)
            {
                var buffer = new CpuBuffer
                {
                    Data0 = luma, Data1 = u, Data2 = v,
                    Stride0 = pitch, Stride1 = (nuint)(_width / 2), Stride2 = (nuint)(_width / 2),
                    Size0 = pitch * (nuint)_height,
                    Size1 = (nuint)_u.Length, Size2 = (nuint)_v.Length,
                    Width = _width, Height = _height, Format = FormatYuv420P,
                };
                int result = _decodeCpu(_decoder, &buffer);
                if (result != Success) throw new IOException($"PyroWave could not decode the frame (error {result})");

                Interleave(u, v, luma + pitch * (nuint)_height, pitch, _width / 2, _height / 2);
            }
        }
        finally
        {
            _context.Unmap(staging, 0);
        }

        sink.OnPicture(staging, 0, sampleTime);
    }

    /// <summary>U and V planes into one plane of UV pairs, sixteen pairs at a time.</summary>
    private static void Interleave(byte* u, byte* v, byte* uv, nuint pitch, int width, int height)
    {
        for (int y = 0; y < height; y++)
        {
            byte* us = u + y * width, vs = v + y * width, d = uv + (nuint)y * pitch;
            int x = 0;
            if (Sse2.IsSupported)
            {
                for (; x + 16 <= width; x += 16)
                {
                    var a = Sse2.LoadVector128(us + x);
                    var b = Sse2.LoadVector128(vs + x);
                    Sse2.Store(d + 2 * x, Sse2.UnpackLow(a, b));
                    Sse2.Store(d + 2 * x + 16, Sse2.UnpackHigh(a, b));
                }
            }
            for (; x < width; x++)
            {
                d[2 * x] = us[x];
                d[2 * x + 1] = vs[x];
            }
        }
    }

    public void Dispose()
    {
        if (_decoder != IntPtr.Zero) _destroy(_decoder);
        _decoder = IntPtr.Zero;
        foreach (var texture in _staging) texture?.Dispose();
    }
}
