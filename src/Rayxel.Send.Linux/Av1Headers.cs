namespace Rayxel.Send.Linux;

/// <summary>What an AV1 stream is, as its sequence header says it.</summary>
public struct Av1Stream
{
    /// <summary>The picture's own size.</summary>
    public uint Width, Height;

    /// <summary>seq_level_idx: 0 is 2.0, 8 is 4.0, 31 is "no limits".</summary>
    public byte LevelIdx;
    public byte Tier;
    public bool FullRange;
    public byte ColourPrimaries, TransferCharacteristics, MatrixCoefficients;

    /// <summary>Else TX_MODE_LARGEST.</summary>
    public bool TxModeSelect;

    /// <summary>
    /// Whether to say the picture's size apart from the coded one: true when the encoder pads
    /// the picture out to whole blocks, which the decoder then has to crop.
    /// </summary>
    public bool RenderSize;

    /// <summary>Superblocks across and down; filled in by <see cref="Av1Headers.Init"/>.</summary>
    public uint SbCols, SbRows;
}

/// <summary>One frame's header, and where its parts landed.</summary>
public unsafe struct Av1Frame
{
    public bool Keyframe;
    public byte OrderHint;

    /// <summary>Inter frames; a key frame refreshes every slot.</summary>
    public byte RefreshFrameFlags;

    /// <summary>Inter frames: the slot every reference points at.</summary>
    public byte RefSlot;

    /// <summary>Nonzero; the encoder's rate control replaces it.</summary>
    public byte BaseQIdx;

    // What the filters are set to, derived from BaseQIdx by Av1Headers.WriteFrameHeader.
    public fixed byte LoopFilterLevel[4];
    public byte CdefDampingMinus3, CdefBits;

    /// <summary>Primary &lt;&lt; 2 | secondary code, as are <see cref="CdefUvStrengths"/>.</summary>
    public fixed byte CdefYStrengths[8];
    public fixed byte CdefUvStrengths[8];

    // Where things landed in the OBU, in bits from its first byte - what an encoder that
    // patches the header in place asks for.
    public uint BitOffsetQindex, BitOffsetLoopfilter, BitOffsetCdef, SizeInBitsCdef;
    public uint SizeInBits;
}

/// <summary>
/// The AV1 headers this program writes itself, for the same reason as the HEVC ones (see
/// <see cref="HevcHeaders"/>): Mesa's radeonsi encoder takes what it needs from a sequence
/// header OBU and a frame header OBU the application hands it - which frame this is, whether
/// it is shown, which reference slots it refreshes and reads - and writes its own in their
/// place. Without the frame header it would take every frame as not to be shown.
/// <para>
/// The stream is the simplest low-latency one AV1 allows: 8-bit 4:2:0, 64x64 superblocks,
/// one tile, a key frame and then inter frames that refer only to the frame before them, no
/// reordering, 8-bit order hints. The headers are complete and valid AV1 in their own right,
/// so a driver that copies them rather than rewriting them still makes a stream.
/// </para>
/// </summary>
public static unsafe class Av1Headers
{
    /// <summary>The most bytes any writer here returns.</summary>
    public const int HeadersMax = 256;
    public const int OrderHintBits = 8;
    public const byte PrimaryRefNone = 7;

    private const int ObuSequenceHeader = 1;
    private const int ObuTemporalDelimiter = 2;
    private const int ObuFrameHeader = 3;

    private const uint FrameKey = 0;
    private const uint FrameInter = 1;

    /// <summary>Annex A: the lowest level whose picture size, display rate and bitrate hold this.</summary>
    private static readonly (byte Idx, uint PicSize, ulong DisplayRate, uint MainKbps, uint HighKbps)[] Levels =
    [
        // HighKbps 0: the level has no high tier.
        (0, 147456, 4423680, 1500, 0),              // 2.0
        (1, 278784, 8363520, 3000, 0),              // 2.1
        (4, 665856, 19975680, 6000, 0),             // 3.0
        (5, 1065024, 31950720, 10000, 0),           // 3.1
        (8, 2359296, 70778880, 12000, 30000),       // 4.0
        (9, 2359296, 141557760, 20000, 50000),      // 4.1
        (12, 8912896, 267386880, 30000, 100000),    // 5.0
        (13, 8912896, 534773760, 40000, 160000),    // 5.1
        (14, 8912896, 1069547520, 60000, 240000),   // 5.2
        (16, 35651584, 1069547520, 60000, 240000),  // 6.0
        (17, 35651584, 2139095040, 100000, 480000), // 6.1
        (18, 35651584, 4278190080, 160000, 800000), // 6.2
    ];

    /// <summary>Picks the level and the tiling for a picture. Nothing else in the stream is touched.</summary>
    public static void Init(ref Av1Stream s, uint width, uint height, uint fps, uint bitrateKbps)
    {
        ulong samples = (ulong)width * height, rate = samples * fps;

        s.Width = width;
        s.Height = height;
        s.LevelIdx = 31; // no level fits: "maximum parameters"
        s.Tier = 1;
        foreach (var level in Levels)
        {
            if (samples > level.PicSize || rate > level.DisplayRate) continue;
            if (bitrateKbps <= level.MainKbps)
            {
                s.LevelIdx = level.Idx;
                s.Tier = 0;
                break;
            }
            if (bitrateKbps <= level.HighKbps)
            {
                s.LevelIdx = level.Idx;
                s.Tier = 1;
                break;
            }
        }

        uint miCols = 2 * ((width + 7) >> 3), miRows = 2 * ((height + 7) >> 3);
        s.SbCols = (miCols + 15) >> 4;
        s.SbRows = (miRows + 15) >> 4;
    }

    public static int WriteTemporalDelimiter(Span<byte> output)
    {
        output[0] = ObuTemporalDelimiter << 3 | 1 << 1;
        output[1] = 0;
        return 2;
    }

    public static int WriteSequenceHeader(in Av1Stream s, Span<byte> output)
    {
        var b = new BitWriter(stackalloc byte[HeadersMax]);
        int widthBits = FloorLog2(s.Width) + 1, heightBits = FloorLog2(s.Height) + 1;

        b.Put(0, 3); // seq_profile: Main, 8-bit 4:2:0
        b.Put(0, 1); // still_picture
        b.Put(0, 1); // reduced_still_picture_header
        b.Put(0, 1); // timing_info_present_flag: frames come when they come
        b.Put(0, 1); // initial_display_delay_present_flag
        b.Put(0, 5); // operating_points_cnt_minus_1
        b.Put(0, 12); // operating_point_idc[0]
        b.Put(s.LevelIdx, 5);
        if (s.LevelIdx > 7) b.Put(s.Tier, 1);

        b.Put((uint)widthBits - 1, 4);  // frame_width_bits_minus_1
        b.Put((uint)heightBits - 1, 4); // frame_height_bits_minus_1
        b.Put(s.Width - 1, widthBits);   // max_frame_width_minus_1; the driver puts its coded size here
        b.Put(s.Height - 1, heightBits); // max_frame_height_minus_1

        b.Put(0, 1); // frame_id_numbers_present_flag
        b.Put(0, 1); // use_128x128_superblock
        b.Put(0, 1); // enable_filter_intra
        b.Put(0, 1); // enable_intra_edge_filter
        b.Put(0, 1); // enable_interintra_compound
        b.Put(0, 1); // enable_masked_compound
        b.Put(0, 1); // enable_warped_motion
        b.Put(0, 1); // enable_dual_filter
        b.Put(1, 1); // enable_order_hint
        b.Put(0, 1); // enable_jnt_comp
        b.Put(0, 1); // enable_ref_frame_mvs
        b.Put(0, 1); // seq_choose_screen_content_tools
        b.Put(0, 1); // seq_force_screen_content_tools: off, so no integer-mv choice follows
        b.Put(OrderHintBits - 1, 3); // order_hint_bits_minus_1
        b.Put(0, 1); // enable_superres
        b.Put(1, 1); // enable_cdef
        b.Put(0, 1); // enable_restoration

        // color_config()
        b.Put(0, 1); // high_bitdepth
        b.Put(0, 1); // mono_chrome
        b.Put(1, 1); // color_description_present_flag
        b.Put(s.ColourPrimaries, 8);
        b.Put(s.TransferCharacteristics, 8);
        b.Put(s.MatrixCoefficients, 8);
        b.Put(s.FullRange, 1); // color_range
        b.Put(0, 2); // chroma_sample_position: unknown, as 4:2:0 with subsampling in both
        b.Put(0, 1); // separate_uv_delta_q

        b.Put(0, 1); // film_grain_params_present
        b.PutTrailing();
        return EmitObu(output, ObuSequenceHeader, b.Written, out _);
    }

    /// <summary>
    /// The frame header OBU for <paramref name="f"/>, whose filters this sets from its
    /// quantiser and whose offsets this fills in.
    /// </summary>
    public static int WriteFrameHeader(in Av1Stream s, ref Av1Frame f, Span<byte> output)
    {
        var b = new BitWriter(stackalloc byte[HeadersMax]);
        bool intra = f.Keyframe;
        uint offsetQ, offsetLf, offsetCdef, endCdef;

        if (f.BaseQIdx == 0) f.BaseQIdx = 128;
        ChooseFilters(ref f);

        // uncompressed_header()
        b.Put(0, 1);                                // show_existing_frame
        b.Put(intra ? FrameKey : FrameInter, 2);    // frame_type
        b.Put(1, 1);                                // show_frame
        if (!intra) b.Put(0, 1);                    // error_resilient_mode; a shown key frame is resilient by rule
        b.Put(0, 1);                                // disable_cdf_update
        // Screen content tools are off in the sequence header, so neither they nor integer
        // motion vectors are chosen here; frame ids are off too.
        b.Put(0, 1);                                // frame_size_override_flag
        b.Put(f.OrderHint, OrderHintBits);
        if (!intra) b.Put(0, 3);                    // primary_ref_frame: LAST, the frame before
        if (!intra) b.Put(f.RefreshFrameFlags, 8);

        if (intra)
        {
            // frame_size() says nothing without an override; superres is off.
            b.Put(s.RenderSize, 1);                 // render_and_frame_size_different
            if (s.RenderSize)
            {
                b.Put(s.Width - 1, 16);
                b.Put(s.Height - 1, 16);
            }
        }
        else
        {
            b.Put(0, 1);                            // frame_refs_short_signaling
            for (int i = 0; i < 7; i++) b.Put(f.RefSlot, 3); // ref_frame_idx[i]
            b.Put(s.RenderSize, 1);                 // render_and_frame_size_different
            if (s.RenderSize)
            {
                b.Put(s.Width - 1, 16);
                b.Put(s.Height - 1, 16);
            }
            b.Put(0, 1);                            // allow_high_precision_mv
            b.Put(0, 1);                            // is_filter_switchable
            b.Put(0, 2);                            // interpolation_filter: EIGHTTAP
            b.Put(0, 1);                            // is_motion_mode_switchable
            // use_ref_frame_mvs is off in the sequence header.
        }
        b.Put(0, 1);                                // disable_frame_end_update_cdf

        // tile_info(): one tile. With uniform spacing that is "never one more column or row"
        // for as long as the specification asks.
        b.Put(1, 1);                                // uniform_tile_spacing_flag
        {
            uint maxTileWidthSb = 4096 >> 6, maxTileAreaSb = (4096 * 2304) >> 12;
            uint minLog2Cols = TileLog2(maxTileWidthSb, s.SbCols);
            uint maxLog2Cols = TileLog2(1, s.SbCols < 64 ? s.SbCols : 64);
            uint maxLog2Rows = TileLog2(1, s.SbRows < 64 ? s.SbRows : 64);
            uint minLog2Tiles = TileLog2(maxTileAreaSb, s.SbCols * s.SbRows);
            if (minLog2Tiles < minLog2Cols) minLog2Tiles = minLog2Cols;
            uint colsLog2 = minLog2Cols;
            if (colsLog2 < maxLog2Cols) b.Put(0, 1); // increment_tile_cols_log2
            uint minLog2Rows = minLog2Tiles > colsLog2 ? minLog2Tiles - colsLog2 : 0;
            if (minLog2Rows < maxLog2Rows) b.Put(0, 1); // increment_tile_rows_log2
            // Pictures too wide or too large for one tile would need more; none of the
            // sizes this program sends comes near (one tile holds 4096 by 2304).
        }

        // quantization_params()
        offsetQ = (uint)b.Count;
        b.Put(f.BaseQIdx, 8);
        b.Put(0, 1);                                // DeltaQYDc: delta_coded
        b.Put(0, 1);                                // DeltaQUDc: delta_coded
        b.Put(0, 1);                                // DeltaQUAc: delta_coded
        b.Put(0, 1);                                // using_qmatrix

        b.Put(0, 1);                                // segmentation_enabled
        b.Put(0, 1);                                // delta_q_present, base_q_idx being nonzero

        // loop_filter_params()
        offsetLf = (uint)b.Count;
        b.Put(f.LoopFilterLevel[0], 6);
        b.Put(f.LoopFilterLevel[1], 6);
        if (f.LoopFilterLevel[0] != 0 || f.LoopFilterLevel[1] != 0)
        {
            b.Put(f.LoopFilterLevel[2], 6);
            b.Put(f.LoopFilterLevel[3], 6);
        }
        b.Put(0, 3);                                // loop_filter_sharpness
        b.Put(0, 1);                                // loop_filter_delta_enabled

        // cdef_params()
        offsetCdef = (uint)b.Count;
        b.Put(f.CdefDampingMinus3, 2);
        b.Put(f.CdefBits, 2);
        for (int i = 0; i < 1 << f.CdefBits; i++)
        {
            b.Put((uint)(f.CdefYStrengths[i] >> 2), 4);
            b.Put((uint)(f.CdefYStrengths[i] & 3), 2);
            b.Put((uint)(f.CdefUvStrengths[i] >> 2), 4);
            b.Put((uint)(f.CdefUvStrengths[i] & 3), 2);
        }
        endCdef = (uint)b.Count;

        // Loop restoration is off in the sequence header.
        b.Put(s.TxModeSelect, 1);                   // tx_mode_select
        if (!intra) b.Put(0, 1);                    // reference_select
        // No skip mode without reference_select; no warped motion in the sequence.
        b.Put(0, 1);                                // reduced_tx_set
        if (!intra)
            for (int reference = 1; reference <= 7; reference++) b.Put(0, 1); // is_global
        // No film grain in the sequence.
        b.PutTrailing();

        int n = EmitObu(output, ObuFrameHeader, b.Written, out int payloadAt);
        f.BitOffsetQindex = (uint)(payloadAt * 8 + offsetQ);
        f.BitOffsetLoopfilter = (uint)(payloadAt * 8 + offsetLf);
        f.BitOffsetCdef = (uint)(payloadAt * 8 + offsetCdef);
        f.SizeInBitsCdef = endCdef - offsetCdef;
        f.SizeInBits = (uint)(n * 8);
        return n;
    }

    /// <summary>
    /// The in-loop filters for a quantiser, as ffmpeg's VA-API encoder chooses them:
    /// deblocking that grows with the square of the step, and four CDEF candidates around
    /// one strength. An encoder with its own rate control rewrites them; they are here so the
    /// header is whole.
    /// </summary>
    private static void ChooseFilters(ref Av1Frame f)
    {
        int q = f.BaseQIdx;
        int level = q > 32 ? ((q - 32) * (q - 32) * 7 >> 12) : 0;
        if (level > 63) level = 63;
        int pri = q >> 5;
        ReadOnlySpan<int> cdef = [pri, 1, 0, 0, pri >> 1, 1, pri << 1, 2]; // four { primary, secondary } pairs

        f.LoopFilterLevel[0] = f.LoopFilterLevel[1] = (byte)level;
        f.LoopFilterLevel[2] = f.LoopFilterLevel[3] = (byte)((level + 1) >> 1);

        for (int i = 0; i < 8; i++) f.CdefYStrengths[i] = f.CdefUvStrengths[i] = 0;
        f.CdefDampingMinus3 = 0;
        f.CdefBits = 0;
        if (pri == 0) return;
        f.CdefDampingMinus3 = (byte)(q >> 6);
        f.CdefBits = 2;
        for (int i = 0; i < 4; i++)
        {
            int p = cdef[i * 2] > 15 ? 15 : cdef[i * 2];
            f.CdefYStrengths[i] = f.CdefUvStrengths[i] = (byte)(p << 2 | cdef[i * 2 + 1]);
        }
    }

    /// <summary>
    /// Header byte, then the payload's size, then the payload. Returns the bytes written, and
    /// in <paramref name="payloadAt"/> where the payload begins.
    /// </summary>
    private static int EmitObu(Span<byte> output, int type, ReadOnlySpan<byte> payload, out int payloadAt)
    {
        int n = 0;
        output[n++] = (byte)(type << 3 | 1 << 1); // forbidden 0, type, no extension, has size, reserved 0
        n += Leb128(output[n..], (ulong)payload.Length);
        payloadAt = n;
        payload.CopyTo(output[n..]);
        return n + payload.Length;
    }

    private static int Leb128(Span<byte> output, ulong value)
    {
        int n = 0;
        do
        {
            byte low = (byte)(value & 0x7f);
            value >>= 7;
            output[n++] = (byte)(low | (value != 0 ? 0x80 : 0));
        } while (value != 0);
        return n;
    }

    /// <summary>The smallest k with blk &lt;&lt; k &gt;= target, as the specification's tile_log2.</summary>
    private static uint TileLog2(uint blk, uint target)
    {
        int k = 0;
        while ((blk << k) < target) k++;
        return (uint)k;
    }

    private static int FloorLog2(uint v)
    {
        int k = 0;
        while ((v >>= 1) != 0) k++;
        return k;
    }
}
