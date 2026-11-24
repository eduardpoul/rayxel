using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Rayxel.Link;
using Rayxel.Link.Media;
using Rayxel.Send.Linux.Va;
using static Rayxel.Send.Linux.Va.VaApi;

namespace Rayxel.Send.Linux;

/// <summary>
/// HEVC and AV1 on the GPU's own video encoder, through VA-API. Everything but the
/// parameters and headers of a frame is the same for the two: the device, the surfaces, how
/// the picture gets in, the rate control and how the bitstream comes out.
/// <para>
/// The port of src/rayxel-send/src/va_encoder.c, call for call; the headers the driver works
/// from are <see cref="HevcHeaders"/> and <see cref="Av1Headers"/>.
/// </para>
/// </summary>
public sealed unsafe class VaEncoder : IVideoEncoder
{
    /// <summary>The Deck's encoder offers exactly one coding tree block size (see --probe).</summary>
    private const uint CtbSize = 64;

    // HEVC nal_unit_type and slice_type values used here.
    private const byte NalTrailR = 1;
    private const byte NalIdrWRadl = 19;
    private const byte SliceP = 1;
    private const byte SliceI = 2;

    /// <summary>AV1 reference frame names, as VA's search_idx fields number them.</summary>
    private const uint Av1RefLast = 1;

    private readonly EncoderConfig _config;
    private int _fd = -1;
    private IntPtr _dpy;
    private bool _initialized;
    private readonly int _profile;
    private int _entrypoint;
    private uint _vaConfig = VA_INVALID_ID;
    private uint _context = VA_INVALID_ID;
    private uint _input = VA_INVALID_SURFACE;
    private readonly uint[] _recon = [VA_INVALID_SURFACE, VA_INVALID_SURFACE];
    private uint _coded = VA_INVALID_ID;

    /// <summary>
    /// Set when the input surface cannot be mapped directly and the picture has to go through
    /// an image of our own and a second copy by the driver.
    /// </summary>
    private bool _putImage;
    private VAImage _image;

    // What the stream is, as written into its headers.
    private HevcStream _stream;
    private Av1Stream _av1;
    private readonly byte[] _av1Sequence = new byte[Av1Headers.HeadersMax];
    private int _av1SequenceSize;

    private ulong _frames;       // encoded so far
    private uint _sinceKeyframe; // also the picture order count
    private int _current;        // which reconstruction surface this frame writes

    private byte* _out;
    private long _outCap;

    /// <summary>The buffers of the frame being put together, destroyed once it is encoded.</summary>
    private readonly uint[] _ids = new uint[16];
    private int _count;

    private VaEncoder(EncoderConfig config)
    {
        _config = config;
        _image.image_id = VA_INVALID_ID;
        _profile = ProfileFor(config.Codec);
    }

    public Wire.Codec Codec => _config.Codec;
    public string Description { get; private set; } = "";
    public string Error { get; private set; } = "";

    private bool IsAv1 => _config.Codec == Wire.Codec.Av1;

    private static int ProfileFor(Wire.Codec codec) => codec == Wire.Codec.Av1 ? VAProfileAV1Profile0 : VAProfileHEVCMain;

    /// <summary>
    /// Whether this machine can encode <paramref name="codec"/> at all; when not, why. Opens
    /// and closes what it needs to find out, so it is for a probe, not for every frame.
    /// </summary>
    public static bool Available(Wire.Codec codec, out string why)
    {
        if (!Ours(codec, out why)) return false;
        try
        {
            VaLayout.Check();
            if (!OpenDisplay(codec, ProfileFor(codec), out int fd, out IntPtr dpy, out _, out why)) return false;
            vaTerminate(dpy);
            close(fd);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            why = Unloadable(ex);
            return false;
        }
    }

    /// <summary>Null on failure, with the reason in <paramref name="why"/>.</summary>
    public static VaEncoder? Open(EncoderConfig config, out string why)
    {
        if (!Ours(config.Codec, out why)) return null;
        var e = new VaEncoder(config);
        try
        {
            VaLayout.Check();
            if (e.Start())
            {
                why = "";
                return e;
            }
            why = e.Error;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            why = Unloadable(ex);
        }
        e.Dispose();
        return null;
    }

    /// <summary>
    /// The codecs this encoder makes. The C program's dispatcher (encoder.c) turned the rest
    /// away before it got here; this is the only door now.
    /// </summary>
    private static bool Ours(Wire.Codec codec, out string why)
    {
        why = codec switch
        {
            Wire.Codec.Hevc or Wire.Codec.Av1 => "",
            Wire.Codec.PyroWave => "PyroWave is not a VA-API codec",
            _ => $"codec {(byte)codec} is not one this program knows",
        };
        return why.Length == 0;
    }

    /// <summary>The C program was linked against libva; this one finds out when it first calls it.</summary>
    private static string Unloadable(Exception ex) => $"VA-API is not installed here ({ex.Message})";

    private bool Check(int status, string what)
    {
        if (status == VA_STATUS_SUCCESS) return true;
        Error = $"{what}: {ErrorString(status)}";
        return false;
    }

    /// <summary>libva's info messages, which would otherwise go to standard error, go nowhere.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void Quiet(IntPtr context, byte* message)
    {
    }

    /// <summary>
    /// The lowest level, and tier, whose limits hold this picture size, sample rate and
    /// bitrate. general_level_idc is thirty times the level number. Hardware decoders do not
    /// enforce any of it, but a stream that states less than it uses is a wrong stream.
    /// </summary>
    private static void PickLevel(uint width, uint height, uint fps, uint bitrateKbps, out byte level, out byte tier)
    {
        ReadOnlySpan<(byte Idc, uint Samples, ulong Rate, uint MainKbps, uint HighKbps)> levels =
        [
            // HighKbps 0: the level has no high tier.
            (90, 552960, 16588800, 6000, 0),             // 3
            (93, 983040, 33177600, 10000, 0),            // 3.1
            (120, 2228224, 66846720, 12000, 30000),      // 4
            (123, 2228224, 133693440, 20000, 50000),     // 4.1
            (150, 8912896, 267386880, 25000, 100000),    // 5
            (153, 8912896, 534773760, 40000, 160000),    // 5.1
            (156, 8912896, 1069547520, 60000, 240000),   // 5.2
            (180, 35651584, 1069547520, 60000, 240000),  // 6
            (183, 35651584, 2139095040, 120000, 480000), // 6.1
            (186, 35651584, 4278190080, 240000, 800000), // 6.2
        ];
        ulong samples = (ulong)width * height, rate = samples * fps;

        foreach (var l in levels)
        {
            if (samples > l.Samples || rate > l.Rate) continue;
            if (bitrateKbps <= l.MainKbps)
            {
                level = l.Idc;
                tier = 0;
                return;
            }
            if (bitrateKbps <= l.HighKbps)
            {
                level = l.Idc;
                tier = 1;
                return;
            }
        }
        level = 186;
        tier = 1;
    }

    /// <summary>
    /// The encode entry point this display offers for <paramref name="profile"/>, or 0 when it
    /// has none. The low-power one is a separate engine on some GPUs and the only one on others.
    /// </summary>
    private static int EncodeEntrypoint(IntPtr dpy, int profile)
    {
        int count = vaMaxNumEntrypoints(dpy);
        var entrypoints = new int[count > 0 ? count : 1];
        int found = 0;

        fixed (int* list = entrypoints)
        {
            if (vaQueryConfigEntrypoints(dpy, profile, list, &count) == VA_STATUS_SUCCESS)
            {
                for (int i = 0; i < count; i++)
                    if (list[i] == VAEntrypointEncSlice) found = VAEntrypointEncSlice;
                for (int i = 0; i < count && found == 0; i++)
                    if (list[i] == VAEntrypointEncSliceLP) found = VAEntrypointEncSliceLP;
            }
        }
        return found;
    }

    private static bool OpenDisplay(Wire.Codec codec, int profile, out int fd, out IntPtr dpy, out int entrypoint,
                                    out string error)
    {
        for (int minor = 128; minor < 136; minor++)
        {
            int handle = open($"/dev/dri/renderD{minor}", O_RDWR | O_CLOEXEC);
            if (handle < 0) continue;

            try
            {
                IntPtr display = vaGetDisplayDRM(handle);
                int major, min;
                if (display != IntPtr.Zero)
                {
                    vaSetInfoCallback(display, (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte*, void>)&Quiet, IntPtr.Zero);
                    if (vaInitialize(display, &major, &min) == VA_STATUS_SUCCESS)
                    {
                        int found = EncodeEntrypoint(display, profile);
                        if (found != 0)
                        {
                            fd = handle;
                            dpy = display;
                            entrypoint = found;
                            error = "";
                            return true;
                        }
                        vaTerminate(display);
                    }
                }
            }
            catch
            {
                // libva missing: the render node is not to stay open behind the exception.
                close(handle);
                throw;
            }
            close(handle);
        }
        fd = -1;
        dpy = IntPtr.Zero;
        entrypoint = 0;
        error = $"no GPU here has a hardware {Wire.CodecName(codec)} encoder";
        return false;
    }

    /// <summary>
    /// How the picture gets into the input surface: mapped directly if the driver allows it,
    /// otherwise through an image of our own that the driver then copies in.
    /// </summary>
    private bool ChooseUpload()
    {
        VAImage derived;
        if (vaDeriveImage(_dpy, _input, &derived) == VA_STATUS_SUCCESS)
        {
            bool usable = derived.format.fourcc == VA_FOURCC_NV12 && derived.num_planes >= 2;
            vaDestroyImage(_dpy, derived.image_id);
            if (usable) return true;
        }

        int count = vaMaxNumImageFormats(_dpy);
        var formats = new VAImageFormat[count > 0 ? count : 1];
        bool created = false;
        fixed (VAImageFormat* list = formats)
        fixed (VAImage* image = &_image)
        {
            if (vaQueryImageFormats(_dpy, list, &count) == VA_STATUS_SUCCESS)
            {
                for (int i = 0; i < count && !created; i++)
                {
                    if (list[i].fourcc != VA_FOURCC_NV12) continue;
                    created = vaCreateImage(_dpy, &list[i], _config.Width, _config.Height, image) == VA_STATUS_SUCCESS;
                }
            }
        }
        if (!created)
        {
            Error = "the driver offers no way to upload an NV12 picture";
            return false;
        }
        _putImage = true;
        return true;
    }

    private bool GetAttribute(int type, out uint value)
    {
        var attrib = new VAConfigAttrib { type = type };
        value = 0;
        if (vaGetConfigAttributes(_dpy, _profile, _entrypoint, &attrib, 1) != VA_STATUS_SUCCESS) return false;
        if (attrib.value == VA_ATTRIB_NOT_SUPPORTED) return false;
        value = attrib.value;
        return true;
    }

    /// <summary>
    /// AV1's headers come in two packed buffers, the sequence header and the frame header, and
    /// a driver that does not take both cannot be told what each frame is.
    /// </summary>
    private bool ConfigureAv1(out uint packed, out bool txModeSelect)
    {
        const uint both = VA_ENC_PACKED_HEADER_SEQUENCE | VA_ENC_PACKED_HEADER_PICTURE;
        packed = 0;
        txModeSelect = true;
        if (!GetAttribute(VAConfigAttribEncPackedHeaders, out uint offered) || (offered & both) != both)
        {
            Error = "the AV1 encoder does not take sequence and frame headers from us";
            return false;
        }
        packed = both;

        // The transform-size modes it can signal: TX_MODE_SELECT when it can, the largest
        // transform everywhere otherwise. Without the attribute, SELECT is what drivers do.
        // (The C code asks only when built against VA-API 1.16 or later; here it is always
        // asked, and a libva that predates the attribute answers that there is none.)
        if (GetAttribute(VAConfigAttribEncAV1Ext2, out uint ext2))
        {
            uint modes = VAConfigAttribValEncAV1Ext2.TxModeSupport(ext2);
            if (modes != 0 && (modes & 0x04) == 0) txModeSelect = false;
        }
        return true;
    }

    /// <summary>Everything va_encoder_open does once it has its struct: false, with <see cref="Error"/> set, on failure.</summary>
    private bool Start()
    {
        EncoderConfig c = _config;
        uint w = (uint)c.Width, h = (uint)c.Height;
        if (c.Width < 130 || c.Height < 128 || (w & 1) != 0 || (h & 1) != 0)
        {
            Error = $"{c.Width}x{c.Height} cannot be encoded: both sides must be even and at least 130x128";
            return false;
        }
        if (!OpenDisplay(c.Codec, _profile, out _fd, out _dpy, out _entrypoint, out string error))
        {
            Error = error;
            return false;
        }
        _initialized = true;

        // The driver writes no parameter set and no usable slice or frame header unless it is
        // given one to work from; see HevcHeaders and Av1Headers.
        uint packed = VA_ENC_PACKED_HEADER_SEQUENCE | VA_ENC_PACKED_HEADER_PICTURE | VA_ENC_PACKED_HEADER_SLICE;
        bool txModeSelect = true;
        if (IsAv1 && !ConfigureAv1(out packed, out txModeSelect)) return false;

        VAConfigAttrib* attribs = stackalloc VAConfigAttrib[3];
        attribs[0] = new VAConfigAttrib { type = VAConfigAttribRTFormat, value = VA_RT_FORMAT_YUV420 };
        attribs[1] = new VAConfigAttrib { type = VAConfigAttribRateControl, value = VA_RC_CBR };
        attribs[2] = new VAConfigAttrib { type = VAConfigAttribEncPackedHeaders, value = packed };
        uint vaConfig;
        if (!Check(vaCreateConfig(_dpy, _profile, _entrypoint, attribs, 3, &vaConfig), "vaCreateConfig")) return false;
        _vaConfig = vaConfig;

        var format = new VASurfaceAttrib
        {
            type = VASurfaceAttribPixelFormat,
            flags = VA_SURFACE_ATTRIB_SETTABLE,
            value_type = VAGenericValueTypeInteger,
            value_i = (int)VA_FOURCC_NV12,
        };
        uint* surfaces = stackalloc uint[3];
        if (!Check(vaCreateSurfaces(_dpy, VA_RT_FORMAT_YUV420, w, h, surfaces, 3, &format, 1), "vaCreateSurfaces"))
            return false;
        _input = surfaces[0];
        _recon[0] = surfaces[1];
        _recon[1] = surfaces[2];

        uint context;
        if (!Check(vaCreateContext(_dpy, _vaConfig, (int)w, (int)h, VA_PROGRESSIVE, surfaces, 3, &context),
                   "vaCreateContext"))
            return false;
        _context = context;

        // Generous on purpose: a keyframe of noise can exceed the raw picture.
        uint codedSize = 3 * w * h + (1u << 16);
        uint coded;
        if (!Check(vaCreateBuffer(_dpy, _context, VAEncCodedBufferType, codedSize, 1, null, &coded),
                   "vaCreateBuffer(coded)"))
            return false;
        _coded = coded;

        if (!ChooseUpload()) return false;

        string rate = $"CBR {c.BitrateKbps / 1000.0:F1} Mbit/s";
        string upload = _putImage ? "two-copy" : "direct";
        byte colour = (byte)(c.Bt601 ? 6 : 1);

        if (IsAv1)
        {
            Av1Headers.Init(ref _av1, w, h, (uint)c.Fps, (uint)c.BitrateKbps);
            _av1.TxModeSelect = txModeSelect;
            _av1.FullRange = c.FullRange;
            _av1.ColourPrimaries = _av1.TransferCharacteristics = _av1.MatrixCoefficients = colour;
            // AMD's encoders before VCN 5 code whole 64-pixel columns and 16-pixel rows. Saying
            // the picture's own size beside it costs four bytes when it is not needed.
            _av1.RenderSize = w % 64 != 0 || h % 16 != 0;
            _av1SequenceSize = Av1Headers.WriteSequenceHeader(_av1, _av1Sequence);

            Description = $"AV1 Main {w}x{h}, level {2 + _av1.LevelIdx / 4}.{_av1.LevelIdx % 4} " +
                          $"{(_av1.Tier != 0 ? "high" : "main")} tier, {rate}, {upload} upload, " +
                          $"{(_av1.TxModeSelect ? "selected" : "largest")} transforms";
            return true;
        }

        _stream = new HevcStream
        {
            Width = w,
            Height = h,
            // The encoder codes whole 64-pixel columns and 16-pixel rows.
            CodedWidth = (w + CtbSize - 1) / CtbSize * CtbSize,
            CodedHeight = (h + 15) / 16 * 16,
            PocBits = 8,
            FullRange = c.FullRange,
            ColourPrimaries = colour,
            TransferCharacteristics = colour,
            MatrixCoefficients = colour,
        };
        PickLevel(w, h, (uint)c.Fps, (uint)c.BitrateKbps, out _stream.LevelIdc, out _stream.Tier);

        Description = $"HEVC Main {w}x{h} (coded {_stream.CodedWidth}x{_stream.CodedHeight}), " +
                      $"level {_stream.LevelIdc / 30}.{_stream.LevelIdc % 30 / 3} " +
                      $"{(_stream.Tier != 0 ? "high" : "main")} tier, {rate}, {upload} upload";
        return true;
    }

    private bool Upload(in CapturedFrame frame, CursorOverlay? cursor)
    {
        uint w = (uint)_config.Width, h = (uint)_config.Height;
        VAImage image = _image;
        void* mapped = null;

        if (!_putImage && !Check(vaDeriveImage(_dpy, _input, &image), "vaDeriveImage")) return false;
        if (!Check(vaMapBuffer(_dpy, image.buf, &mapped), "vaMapBuffer(image)"))
        {
            if (!_putImage) vaDestroyImage(_dpy, image.image_id);
            return false;
        }

        // The rows one by one, as the surface's pitch is its own; the pointer, when there is
        // one, is laid over the copy without touching the frame (it belongs to the capture).
        byte* luma = (byte*)mapped + image.offsets[0], chroma = (byte*)mapped + image.offsets[1];
        CursorOverlay.CopyNv12(frame, luma, (int)image.pitches[0], chroma, (int)image.pitches[1], cursor);

        vaUnmapBuffer(_dpy, image.buf);

        if (_putImage)
            return Check(vaPutImage(_dpy, _input, image.image_id, 0, 0, w, h, 0, 0, w, h), "vaPutImage");
        vaDestroyImage(_dpy, image.image_id);
        return true;
    }

    private bool AddBuffer(int type, void* data, uint size)
    {
        uint id;
        if (!Check(vaCreateBuffer(_dpy, _context, type, size, 1, data, &id), "vaCreateBuffer")) return false;
        _ids[_count++] = id;
        return true;
    }

    private bool AddBuffer<T>(int type, T data) where T : unmanaged => AddBuffer(type, &data, (uint)sizeof(T));

    private bool AddMisc<T>(uint type, T payload) where T : unmanaged
    {
        uint id;
        void* mapped;

        if (!Check(vaCreateBuffer(_dpy, _context, VAEncMiscParameterBufferType,
                                  (uint)(sizeof(VAEncMiscParameterBuffer) + sizeof(T)), 1, null, &id),
                   "vaCreateBuffer(misc)"))
            return false;
        if (!Check(vaMapBuffer(_dpy, id, &mapped), "vaMapBuffer(misc)"))
        {
            vaDestroyBuffer(_dpy, id);
            return false;
        }
        ((VAEncMiscParameterBuffer*)mapped)->type = type;
        Buffer.MemoryCopy(&payload, (byte*)mapped + sizeof(VAEncMiscParameterBuffer), sizeof(T), sizeof(T));
        vaUnmapBuffer(_dpy, id);
        _ids[_count++] = id;
        return true;
    }

    private bool AddPacked(uint type, ReadOnlySpan<byte> data)
    {
        var header = new VAEncPackedHeaderParameterBuffer
        {
            type = type,
            bit_length = (uint)(data.Length * 8),
            // HEVC's headers come with their emulation prevention bytes in; AV1 has none.
            has_emulation_bytes = (byte)(IsAv1 ? 0 : 1),
        };
        fixed (byte* bytes = data)
            return AddBuffer(VAEncPackedHeaderParameterBufferType, header) &&
                   AddBuffer(VAEncPackedHeaderDataBufferType, bytes, (uint)data.Length);
    }

    /// <summary>How many bits the stream may use, for either codec.</summary>
    private bool AddRateControl()
    {
        EncoderConfig c = _config;
        uint bitsPerSecond = (uint)c.BitrateKbps * 1000;
        double bufferFrames = c.BufferFrames > 0 ? c.BufferFrames : 2.0;
        uint bufferBits = (uint)(bitsPerSecond / (double)c.Fps * bufferFrames);

        var rc = new VAEncMiscParameterRateControl
        {
            bits_per_second = bitsPerSecond,
            target_percentage = 100,
            window_size = (uint)(1000.0 * bufferFrames / c.Fps) + 1,
            // Never pad a quiet frame up to the bitrate, and never skip a frame to stay under it.
            rc_flags = VAEncMiscParameterRateControl.RcFlags(disableFrameSkip: true, disableBitStuffing: true),
        };
        var hrd = new VAEncMiscParameterHRD
        {
            initial_buffer_fullness = bufferBits / 2,
            buffer_size = bufferBits,
        };
        var rate = new VAEncMiscParameterFrameRate { framerate = (uint)c.Fps };

        if (!AddMisc(VAEncMiscParameterTypeRateControl, rc) ||
            !AddMisc(VAEncMiscParameterTypeHRD, hrd) ||
            !AddMisc(VAEncMiscParameterTypeFrameRate, rate))
            return false;

        if (c.MaxFrameBytes != 0)
        {
            // The struct repeats the type at its start: see VAEncMiscParameterBufferMaxFrameSize.
            var cap = new VAEncMiscParameterBufferMaxFrameSize
            {
                type = VAEncMiscParameterTypeMaxFrameSize,
                max_frame_size = (uint)c.MaxFrameBytes * 8, // in bits
            };
            if (!AddMisc(VAEncMiscParameterTypeMaxFrameSize, cap)) return false;
        }
        return true;
    }

    /// <summary>Sent with every HEVC keyframe: what the stream is, and how many bits it may use.</summary>
    private bool AddSequenceHevc()
    {
        EncoderConfig c = _config;
        uint gop = c.Gop != 0 ? (uint)c.Gop : 0x7fffffff;

        var seq = new VAEncSequenceParameterBufferHEVC
        {
            general_profile_idc = 1, // Main
            general_level_idc = _stream.LevelIdc,
            general_tier_flag = _stream.Tier,
            intra_period = gop,
            intra_idr_period = gop,
            ip_period = 1, // no B frames: nothing is ever held back for reordering
            bits_per_second = (uint)c.BitrateKbps * 1000,
            pic_width_in_luma_samples = (ushort)_stream.CodedWidth,
            pic_height_in_luma_samples = (ushort)_stream.CodedHeight,
            seq_fields = VAEncSequenceParameterBufferHEVC.SeqFields(
                chromaFormatIdc: 1, strongIntraSmoothing: true, amp: true, sampleAdaptiveOffset: true),
            // 8 to 64 for coding blocks, 4 to 32 for transforms: the one set this encoder has.
            log2_min_luma_coding_block_size_minus3 = 0,
            log2_diff_max_min_luma_coding_block_size = 3,
            log2_min_transform_block_size_minus2 = 0,
            log2_diff_max_min_transform_block_size = 3,
        };
        return AddBuffer(VAEncSequenceParameterBufferType, seq) && AddRateControl();
    }

    /// <summary>
    /// The HEVC part of a frame: picture parameters, the slice header the driver works from,
    /// and the slice.
    /// </summary>
    private bool AddPictureHevc(bool keyframe, int previous)
    {
        EncoderConfig c = _config;
        Span<byte> headers = stackalloc byte[HevcHeaders.MaxBytes];

        if (keyframe)
        {
            if (!AddSequenceHevc()) return false;
            int size = HevcHeaders.WriteParameterSets(_stream, headers);
            if (!AddPacked(VAEncPackedHeaderSequence, headers[..size])) return false;
        }

        var pic = new VAEncPictureParameterBufferHEVC
        {
            decoded_curr_pic = new VAPictureHEVC { picture_id = _recon[_current], pic_order_cnt = (int)_sinceKeyframe },
            coded_buf = _coded,
            collocated_ref_pic_index = 0xff,
            pic_init_qp = 26,
            nal_unit_type = keyframe ? NalIdrWRadl : NalTrailR,
            pic_fields = VAEncPictureParameterBufferHEVC.PicFields(
                idrPic: keyframe,
                codingType: keyframe ? 1u : 2u,
                referencePic: true,
                transformSkip: true,
                cuQpDelta: true, // rate control moves the QP inside a picture
                loopFilterAcrossSlices: true),
        };
        VAPictureHEVC* references = VAEncPictureParameterBufferHEVC.References(&pic);
        for (int i = 0; i < VAEncPictureParameterBufferHEVC.ReferenceCount; i++) references[i] = VAPictureHEVC.Invalid;
        if (!keyframe)
            references[0] = new VAPictureHEVC
            {
                picture_id = _recon[previous],
                pic_order_cnt = (int)_sinceKeyframe - 1,
                flags = VA_PICTURE_HEVC_RPS_ST_CURR_BEFORE,
            };
        if (!AddBuffer(VAEncPictureParameterBufferType, pic)) return false;

        int headerSize = HevcHeaders.WriteSliceHeader(_stream, keyframe, _sinceKeyframe, headers);
        if (!AddPacked(VAEncPackedHeaderSlice, headers[..headerSize])) return false;

        uint ctbs = ((uint)c.Width + CtbSize - 1) / CtbSize * (((uint)c.Height + CtbSize - 1) / CtbSize);
        var slice = new VAEncSliceParameterBufferHEVC
        {
            slice_segment_address = 0,
            num_ctu_in_slice = ctbs,
            slice_type = keyframe ? SliceI : SliceP,
            max_num_merge_cand = 5,
            slice_fields = VAEncSliceParameterBufferHEVC.SliceFields(
                lastSliceOfPic: true, saoLuma: true, saoChroma: true, loopFilterAcrossSlices: true,
                collocatedFromL0: true),
        };
        VAPictureHEVC* list0 = VAEncSliceParameterBufferHEVC.List0(&slice), list1 = VAEncSliceParameterBufferHEVC.List1(&slice);
        for (int i = 0; i < VAEncSliceParameterBufferHEVC.ReferenceCount; i++)
        {
            list0[i] = VAPictureHEVC.Invalid;
            list1[i] = list0[i];
        }
        if (!keyframe) list0[0] = references[0];
        return AddBuffer(VAEncSliceParameterBufferType, slice);
    }

    /// <summary>
    /// The AV1 part of a frame. Two reconstruction surfaces take turns, and each frame's
    /// reference slot is its surface's number: a key frame fills every slot, and each inter
    /// frame reads the slot the frame before it wrote and writes the other one. That is all
    /// the reference structure a stream that never looks ahead needs.
    /// </summary>
    private bool AddPictureAv1(bool keyframe, int previous)
    {
        EncoderConfig c = _config;
        Span<byte> header = stackalloc byte[Av1Headers.HeadersMax];

        if (keyframe)
        {
            uint gop = c.Gop != 0 ? (uint)c.Gop : 0x7fffffff;
            var seq = new VAEncSequenceParameterBufferAV1
            {
                seq_profile = 0,
                seq_level_idx = _av1.LevelIdx,
                seq_tier = _av1.Tier,
                intra_period = gop,
                ip_period = 1,
                bits_per_second = (uint)c.BitrateKbps * 1000,
                seq_fields = VAEncSequenceParameterBufferAV1.SeqFields(
                    enableOrderHint: true, enableCdef: true, subsamplingX: true, subsamplingY: true),
                order_hint_bits_minus_1 = Av1Headers.OrderHintBits - 1,
            };
            if (!AddBuffer(VAEncSequenceParameterBufferType, seq) ||
                !AddRateControl() ||
                !AddPacked(VAEncPackedHeaderSequence, _av1Sequence.AsSpan(0, _av1SequenceSize)))
                return false;
        }

        var f = new Av1Frame
        {
            Keyframe = keyframe,
            OrderHint = (byte)(_sinceKeyframe & ((1u << Av1Headers.OrderHintBits) - 1)),
            RefreshFrameFlags = keyframe ? (byte)0xff : (byte)(1u << _current),
            RefSlot = (byte)previous,
            BaseQIdx = 128,
        };
        int headerSize = Av1Headers.WriteFrameHeader(_av1, ref f, header);

        var pic = new VAEncPictureParameterBufferAV1
        {
            frame_width_minus_1 = (ushort)(c.Width - 1),
            frame_height_minus_1 = (ushort)(c.Height - 1),
            reconstructed_frame = _recon[_current],
            coded_buf = _coded,
            primary_ref_frame = keyframe ? Av1Headers.PrimaryRefNone : (byte)0,
            order_hint = f.OrderHint,
            refresh_frame_flags = f.RefreshFrameFlags,
            picture_flags = VAEncPictureParameterBufferAV1.PictureFlags(
                frameType: keyframe ? 0u : 1u, // KEY_FRAME, INTER_FRAME
                errorResilientMode: keyframe,
                enableFrameObu: false), // a frame header and a tile group, as the header says
            filter_level_u = f.LoopFilterLevel[2],
            filter_level_v = f.LoopFilterLevel[3],
            interpolation_filter = 0,
            base_qindex = f.BaseQIdx,
            min_base_qindex = 1,
            max_base_qindex = 255,
            mode_control_flags = VAEncPictureParameterBufferAV1.ModeControlFlags(
                txMode: _av1.TxModeSelect ? 2u : 1u, // TX_MODE_SELECT, TX_MODE_LARGEST
                referenceMode: 0),                   // single reference
            tile_cols = 1,
            tile_rows = 1,
            cdef_damping_minus_3 = f.CdefDampingMinus3,
            cdef_bits = f.CdefBits,
            bit_offset_qindex = f.BitOffsetQindex,
            bit_offset_loopfilter_params = f.BitOffsetLoopfilter,
            bit_offset_cdef_params = f.BitOffsetCdef,
            size_in_bits_cdef_params = f.SizeInBitsCdef,
            size_in_bits_frame_hdr_obu = f.SizeInBits,
            // Counted as drivers that patch the header count it: past the sequence header
            // when one goes with the frame, then past the frame header's one header byte.
            byte_offset_frame_hdr_obu_size = (uint)((keyframe ? _av1SequenceSize : 0) + 1),
            tile_group_obu_hdr_info = VAEncPictureParameterBufferAV1.TileGroupObuHdrInfo(obuHasSizeField: true),
        };
        pic.filter_level[0] = f.LoopFilterLevel[0];
        pic.filter_level[1] = f.LoopFilterLevel[1];
        pic.width_in_sbs_minus_1[0] = (ushort)(_av1.SbCols - 1);
        pic.height_in_sbs_minus_1[0] = (ushort)(_av1.SbRows - 1);
        for (int i = 0; i < 8; i++)
        {
            pic.cdef_y_strengths[i] = f.CdefYStrengths[i];
            pic.cdef_uv_strengths[i] = f.CdefUvStrengths[i];
        }
        for (int i = 0; i < 8; i++) pic.reference_frames[i] = VA_INVALID_SURFACE;
        for (int i = 0; i < 7; i++) pic.ref_frame_idx[i] = keyframe ? (byte)0 : (byte)previous;
        if (!keyframe)
        {
            pic.reference_frames[previous] = _recon[previous];
            pic.ref_frame_ctrl_l0 = VAEncPictureParameterBufferAV1.RefFrameCtrl(searchIdx0: Av1RefLast);
        }

        var tileGroup = new VAEncTileGroupBufferAV1 { tg_start = 0, tg_end = 0 };
        return AddBuffer(VAEncPictureParameterBufferType, pic) &&
               AddPacked(VAEncPackedHeaderPicture, header[..headerSize]) &&
               AddBuffer(VAEncSliceParameterBufferType, tileGroup);
    }

    public bool Encode(in CapturedFrame frame, bool forceKeyframe, CursorOverlay? cursor, out EncodedFrame encoded)
    {
        EncoderConfig c = _config;
        encoded = default;
        if (!_initialized)
        {
            Error = "the encoder is closed";
            return false;
        }
        // The C code took the frame's size on trust from its one caller. A picture of
        // another size would be copied past the surface's end, so it is refused instead.
        if (frame.Width != c.Width || frame.Height != c.Height)
        {
            Error = $"a {frame.Width}x{frame.Height} picture cannot go into a {c.Width}x{c.Height} encoder";
            return false;
        }

        bool keyframe = forceKeyframe || _frames == 0 || (c.Gop != 0 && _sinceKeyframe >= (uint)c.Gop);
        Span<byte> delimiter = stackalloc byte[2];
        bool ok = false;
        _count = 0;

        long start = Clock.NowNs;
        if (!Upload(frame, cursor)) return false;
        long uploaded = Clock.NowNs;

        if (keyframe) _sinceKeyframe = 0;
        int previous = _current ^ 1;

        try
        {
            if (!(IsAv1 ? AddPictureAv1(keyframe, previous) : AddPictureHevc(keyframe, previous))) return false;

            if (!Check(vaBeginPicture(_dpy, _context, _input), "vaBeginPicture")) return false;
            fixed (uint* ids = _ids)
            {
                if (!Check(vaRenderPicture(_dpy, _context, ids, _count), "vaRenderPicture"))
                {
                    vaEndPicture(_dpy, _context);
                    return false;
                }
            }
            if (!Check(vaEndPicture(_dpy, _context), "vaEndPicture")) return false;
            if (!Check(vaSyncSurface(_dpy, _input), "vaSyncSurface")) return false;

            void* mapped;
            if (!Check(vaMapBuffer(_dpy, _coded, &mapped), "vaMapBuffer(coded)")) return false;
            var segment = (VACodedBufferSegment*)mapped;

            // An AV1 temporal unit begins with a temporal delimiter, which no encoder writes.
            int lead = IsAv1 ? Av1Headers.WriteTemporalDelimiter(delimiter) : 0;

            long total = lead;
            for (VACodedBufferSegment* s = segment; s != null; s = s->next) total += s->size;
            if (total > _outCap)
            {
                try
                {
                    _out = (byte*)NativeMemory.Realloc(_out, (nuint)(total + total / 2));
                    _outCap = total + total / 2;
                }
                catch (OutOfMemoryException)
                {
                    // The old buffer is still there; the frame does not fit in it, as below says.
                }
            }
            if (total <= _outCap && total > lead)
            {
                delimiter[..lead].CopyTo(new Span<byte>(_out, lead));
                long at = lead;
                for (VACodedBufferSegment* s = segment; s != null; s = s->next)
                {
                    Buffer.MemoryCopy(s->buf, _out + at, _outCap - at, s->size);
                    at += s->size;
                }
                ok = true;
            }
            else
            {
                Error = total > lead ? $"out of memory for a {total}-byte frame" : "the encoder returned no data";
            }
            vaUnmapBuffer(_dpy, _coded);

            if (ok)
            {
                encoded = new EncodedFrame(_out, (int)total, keyframe, uploaded - start, Clock.NowNs - uploaded);
                _frames++;
                _sinceKeyframe++;
                _current = previous;
            }
            return ok;
        }
        finally
        {
            for (int i = 0; i < _count; i++) vaDestroyBuffer(_dpy, _ids[i]);
            _count = 0;
        }
    }

    public void Dispose()
    {
        if (_initialized)
        {
            if (_image.image_id != VA_INVALID_ID) vaDestroyImage(_dpy, _image.image_id);
            if (_coded != VA_INVALID_ID) vaDestroyBuffer(_dpy, _coded);
            if (_context != VA_INVALID_ID) vaDestroyContext(_dpy, _context);
            if (_input != VA_INVALID_SURFACE)
            {
                uint* surfaces = stackalloc uint[3] { _input, _recon[0], _recon[1] };
                vaDestroySurfaces(_dpy, surfaces, 3);
            }
            if (_vaConfig != VA_INVALID_ID) vaDestroyConfig(_dpy, _vaConfig);
            vaTerminate(_dpy);
            _initialized = false;
        }
        _image.image_id = VA_INVALID_ID;
        _coded = _context = _vaConfig = VA_INVALID_ID;
        _input = _recon[0] = _recon[1] = VA_INVALID_SURFACE;
        _dpy = IntPtr.Zero;

        if (_fd >= 0) close(_fd);
        _fd = -1;
        NativeMemory.Free(_out);
        _out = null;
        _outCap = 0;
    }
}
