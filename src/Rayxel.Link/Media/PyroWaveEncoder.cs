using System.Runtime.InteropServices;

namespace Rayxel.Link.Media;

/// <summary>
/// PyroWave, through the codec's own library: a wavelet codec in which every frame stands
/// alone, made for links fast enough to spend 100 Mbit/s and more on a picture in exchange
/// for the shortest encode and decode there are. It runs on the GPU's shaders through Vulkan,
/// on a compute queue so that it overlaps a game's drawing rather than queueing behind it.
/// <para>
/// The library is optional and loaded the first time it is asked for, from beside this
/// program first: libpyrowave-shared.so.1 on Linux, libpyrowave-shared-1.dll on Windows. A
/// sender without it simply does not offer the codec. It is the same on every platform, so
/// it lives here and not in a platform's library.
/// </para>
/// </summary>
public sealed unsafe class PyroWaveEncoder : IVideoEncoder
{
    private const uint ApiMajor = 1;
    private const int Success = 0, Chroma420 = 0, FormatNv12 = 0, QueueCompute = 2;

    public static string LibraryName => OperatingSystem.IsWindows() ? "libpyrowave-shared-1.dll" : "libpyrowave-shared.so.1";

    [StructLayout(LayoutKind.Sequential)]
    private struct CreateInfo
    {
        public IntPtr Device;
        public int Width, Height;
        public int Chroma;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct RateControl
    {
        public nuint MaximumBitstreamSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Packet
    {
        public nuint Offset, Size;
    }

    private static readonly object Gate = new();
    private static bool _tried, _loaded;
    private static string _problem = "";
    private static IntPtr _library, _device;

    private static delegate* unmanaged[Cdecl]<uint*, uint*, uint*, void> _apiVersion;
    private static delegate* unmanaged[Cdecl]<IntPtr*, int> _createDevice;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, int> _setQueue;
    private static delegate* unmanaged[Cdecl]<CreateInfo*, IntPtr*, int> _create;
    private static delegate* unmanaged[Cdecl]<IntPtr, CpuBuffer*, RateControl*, int> _encodeCpu;
    private static delegate* unmanaged[Cdecl]<IntPtr, nuint, nuint*, int> _countPackets;
    private static delegate* unmanaged[Cdecl]<IntPtr, Packet*, nuint, nuint*, void*, nuint, int> _packetize;
    private static delegate* unmanaged[Cdecl]<IntPtr, void> _destroy;

    /// <summary>
    /// Loads the library and makes the one Vulkan device every encoder shares: making one
    /// takes long enough to be felt when the bitrate or the size changes, and there is one GPU.
    /// </summary>
    private static bool Load()
    {
        lock (Gate)
        {
            if (_tried) return _loaded;
            _tried = true;
            try
            {
                string beside = Path.Combine(AppContext.BaseDirectory, LibraryName);
                if (!(File.Exists(beside) && NativeLibrary.TryLoad(beside, out _library)) &&
                    !NativeLibrary.TryLoad(LibraryName, typeof(PyroWaveEncoder).Assembly, null, out _library))
                {
                    _problem = $"{LibraryName} is not beside this program or anywhere the loader looks";
                    return false;
                }

                _apiVersion = (delegate* unmanaged[Cdecl]<uint*, uint*, uint*, void>)Export("pyrowave_get_api_version");
                _createDevice = (delegate* unmanaged[Cdecl]<IntPtr*, int>)Export("pyrowave_create_default_device");
                _setQueue = (delegate* unmanaged[Cdecl]<IntPtr, int, int>)Export("pyrowave_device_set_queue_type");
                _create = (delegate* unmanaged[Cdecl]<CreateInfo*, IntPtr*, int>)Export("pyrowave_encoder_create");
                _encodeCpu = (delegate* unmanaged[Cdecl]<IntPtr, CpuBuffer*, RateControl*, int>)Export("pyrowave_encoder_encode_cpu");
                _countPackets = (delegate* unmanaged[Cdecl]<IntPtr, nuint, nuint*, int>)Export("pyrowave_encoder_compute_num_packets");
                _packetize = (delegate* unmanaged[Cdecl]<IntPtr, Packet*, nuint, nuint*, void*, nuint, int>)
                    Export("pyrowave_encoder_packetize");
                _destroy = (delegate* unmanaged[Cdecl]<IntPtr, void>)Export("pyrowave_encoder_destroy");

                uint major, minor, patch;
                _apiVersion(&major, &minor, &patch);
                if (major != ApiMajor)
                {
                    _problem = $"{LibraryName} speaks version {major} of its interface, this program {ApiMajor}";
                    return false;
                }

                IntPtr device;
                if (_createDevice(&device) != Success || device == IntPtr.Zero)
                {
                    _problem = "the codec found no Vulkan device it can use";
                    return false;
                }
                if (_setQueue(device, QueueCompute) != Success)
                    LinkLog.Info("pyrowave: no separate compute queue; encoding on the graphics one, behind whatever draws");
                _device = device;
                _loaded = true;
                return true;
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or BadImageFormatException)
            {
                _problem = $"{LibraryName}: {ex.Message}";
                return false;
            }
        }
    }

    private static IntPtr Export(string name) =>
        NativeLibrary.TryGetExport(_library, name, out IntPtr address)
            ? address
            : throw new EntryPointNotFoundException($"{LibraryName} has no {name} - another version?");

    public static bool Available(out string why)
    {
        bool ok = Load();
        why = ok ? "" : _problem;
        return ok;
    }

    public static PyroWaveEncoder? Open(EncoderConfig config, out string why)
    {
        if (!Load())
        {
            why = _problem;
            return null;
        }
        if ((config.Width & 1) != 0 || (config.Height & 1) != 0)
        {
            why = $"the codec's 4:2:0 needs an even size, not {config.Width}x{config.Height}";
            return null;
        }

        var info = new CreateInfo { Device = _device, Width = config.Width, Height = config.Height, Chroma = Chroma420 };
        IntPtr encoder;
        if (_create(&info, &encoder) != Success)
        {
            why = $"the codec will not encode {config.Width}x{config.Height}";
            return null;
        }
        why = "";
        return new PyroWaveEncoder(config, encoder);
    }

    private readonly EncoderConfig _config;
    private IntPtr _encoder;
    private readonly nuint _capBytes;
    private readonly byte[] _bitstream, _scratch;

    private PyroWaveEncoder(EncoderConfig config, IntPtr encoder)
    {
        _config = config;
        _encoder = encoder;
        int fps = config.Fps > 0 ? config.Fps : 60;
        _capBytes = (nuint)((long)config.BitrateKbps * 1000 / 8 / fps);

        // Room for a frame at the cap and the packets' own headers; the codec never goes
        // over the cap, so this is never grown.
        _bitstream = GC.AllocateUninitializedArray<byte>((int)_capBytes + (64 << 10), pinned: true);
        _scratch = GC.AllocateUninitializedArray<byte>(config.Width * config.Height * 3 / 2, pinned: true);

        Description = $"PyroWave {config.Width}x{config.Height} 4:2:0, at most {_capBytes / 1024.0:0} kB a frame " +
                      $"({config.BitrateKbps / 1000.0:0.#} Mbit/s at {fps} fps), every frame a keyframe, GPU compute queue";
    }

    public Wire.Codec Codec => Wire.Codec.PyroWave;
    public string Description { get; }
    public string Error { get; private set; } = "";

    public bool Encode(in CapturedFrame frame, bool forceKeyframe, CursorOverlay? cursor, out EncodedFrame encoded)
    {
        encoded = default;
        long start = Clock.NowNs;
        int width = _config.Width, height = _config.Height;

        fixed (byte* bitstream = _bitstream, scratch = _scratch)
        {
            // With a pointer to draw, the picture is copied first: the codec reads the frame
            // as it lies, and the frame belongs to the capture.
            byte* picture = frame.Data;
            int pitch = frame.Stride;
            if (cursor is not null)
            {
                CursorOverlay.CopyNv12(frame, scratch, width, scratch + width * height, width, cursor);
                picture = scratch;
                pitch = width;
            }

            var buffer = new CpuBuffer
            {
                Data0 = picture,
                Data1 = picture + (long)pitch * height,
                Data2 = picture + (long)pitch * height,
                Stride0 = (nuint)pitch, Stride1 = (nuint)pitch,
                Size0 = (nuint)((long)pitch * height), Size1 = (nuint)((long)pitch * (height / 2)),
                Width = width, Height = height, Format = FormatNv12,
            };
            var rate = new RateControl { MaximumBitstreamSize = _capBytes };
            if (_encodeCpu(_encoder, &buffer, &rate) != Success)
            {
                Error = "the codec refused the picture";
                return false;
            }
            long handed = Clock.NowNs;

            // Counting the packets is what waits for the GPU. They are asked for as one: the
            // frame is cut into datagrams like any other, and the decoder takes the codec's
            // packets back all at once. (Packets of the codec's own size, one per datagram,
            // would let a frame be shown with a piece missing - but a packet is at least one
            // 32x32 block, and one of fine detail at a high rate is larger than a datagram;
            // IP splitting those lost more frames than whole-or-nothing ever does.)
            nuint count, made;
            Packet packet;
            var cap = (nuint)_bitstream.Length;
            if (_countPackets(_encoder, cap, &count) != Success || count != 1 ||
                _packetize(_encoder, &packet, cap, &made, bitstream, cap) != Success || made != 1)
            {
                Error = "the codec could not hand over the frame";
                return false;
            }

            encoded = new EncodedFrame(bitstream + (long)packet.Offset, (int)packet.Size, keyframe: true,
                                       uploadNs: handed - start, encodeNs: Clock.NowNs - handed);
            return packet.Size > 0;
        }
    }

    public void Dispose()
    {
        if (_encoder != IntPtr.Zero) _destroy(_encoder);
        _encoder = IntPtr.Zero;
    }
}
