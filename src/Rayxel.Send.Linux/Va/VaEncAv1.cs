using System.Runtime.InteropServices;

namespace Rayxel.Send.Linux.Va;

// The AV1 encoding structs of va_enc_av1.h, laid out as VaTypes.cs describes: libva 1.17's
// sizes and offsets on x86-64, only the fields used here named.

/// <summary>VAConfigAttribValEncAV1Ext2, the value of <see cref="VaApi.VAConfigAttribEncAV1Ext2"/>.</summary>
internal static class VAConfigAttribValEncAV1Ext2
{
    /// <summary>
    /// bits.tx_mode_support, bits 4-6 (after tile_size_bytes_minus1 at 0-1 and
    /// obu_size_bytes_minus1 at 2-3): 0x01 ONLY_4X4, 0x02 TX_MODE_LARGEST, 0x04 TX_MODE_SELECT.
    /// </summary>
    public static uint TxModeSupport(uint value) => (value >> 4) & 0x7;
}

/// <summary>VAEncSequenceParameterBufferAV1.</summary>
[StructLayout(LayoutKind.Explicit, Size = 88)]
internal struct VAEncSequenceParameterBufferAV1
{
    [FieldOffset(0)] public byte seq_profile;
    [FieldOffset(1)] public byte seq_level_idx;
    [FieldOffset(2)] public byte seq_tier;
    [FieldOffset(3)] public byte hierarchical_flag;
    [FieldOffset(4)] public uint intra_period;
    [FieldOffset(8)] public uint ip_period;
    [FieldOffset(12)] public uint bits_per_second;
    [FieldOffset(16)] public uint seq_fields;
    [FieldOffset(20)] public byte order_hint_bits_minus_1;

    /// <summary>
    /// seq_fields.bits, one bit each from still_picture at 0: use_128x128_superblock (1),
    /// enable_filter_intra (2), enable_intra_edge_filter (3), enable_interintra_compound (4),
    /// enable_masked_compound (5), enable_warped_motion (6) and enable_dual_filter (7) stay 0;
    /// enable_order_hint is bit 8; enable_jnt_comp (9), enable_ref_frame_mvs (10) and
    /// enable_superres (11) stay 0; enable_cdef is bit 12; enable_restoration (13) and the
    /// three bits of bit_depth_minus8 (14-16) stay 0; subsampling_x is bit 17 and
    /// subsampling_y bit 18.
    /// </summary>
    public static uint SeqFields(bool enableOrderHint, bool enableCdef, bool subsamplingX, bool subsamplingY) =>
        VaBits.Flag(enableOrderHint, 8) | VaBits.Flag(enableCdef, 12) | VaBits.Flag(subsamplingX, 17) |
        VaBits.Flag(subsamplingY, 18);
}

/// <summary>VAEncPictureParameterBufferAV1.</summary>
[StructLayout(LayoutKind.Explicit, Size = 1032)]
internal unsafe struct VAEncPictureParameterBufferAV1
{
    [FieldOffset(0)] public ushort frame_width_minus_1;
    [FieldOffset(2)] public ushort frame_height_minus_1;
    [FieldOffset(4)] public uint reconstructed_frame; // VASurfaceID
    [FieldOffset(8)] public uint coded_buf;           // VABufferID
    [FieldOffset(12)] public fixed uint reference_frames[8];
    [FieldOffset(44)] public fixed byte ref_frame_idx[7];
    [FieldOffset(52)] public byte primary_ref_frame;
    [FieldOffset(53)] public byte order_hint;
    [FieldOffset(54)] public byte refresh_frame_flags;
    [FieldOffset(56)] public uint ref_frame_ctrl_l0; // VARefFrameCtrlAV1
    [FieldOffset(60)] public uint ref_frame_ctrl_l1;
    [FieldOffset(64)] public uint picture_flags;
    [FieldOffset(71)] public fixed byte filter_level[2];
    [FieldOffset(73)] public byte filter_level_u;
    [FieldOffset(74)] public byte filter_level_v;
    [FieldOffset(77)] public byte interpolation_filter;
    [FieldOffset(88)] public byte base_qindex;
    [FieldOffset(94)] public byte min_base_qindex;
    [FieldOffset(95)] public byte max_base_qindex;
    [FieldOffset(100)] public uint mode_control_flags;
    [FieldOffset(260)] public byte tile_cols;
    [FieldOffset(261)] public byte tile_rows;
    [FieldOffset(264)] public fixed ushort width_in_sbs_minus_1[63];
    [FieldOffset(390)] public fixed ushort height_in_sbs_minus_1[63];
    [FieldOffset(518)] public byte cdef_damping_minus_3;
    [FieldOffset(519)] public byte cdef_bits;
    [FieldOffset(520)] public fixed byte cdef_y_strengths[8];
    [FieldOffset(528)] public fixed byte cdef_uv_strengths[8];
    [FieldOffset(932)] public uint bit_offset_qindex;
    [FieldOffset(936)] public uint bit_offset_segmentation;
    [FieldOffset(940)] public uint bit_offset_loopfilter_params;
    [FieldOffset(944)] public uint bit_offset_cdef_params;
    [FieldOffset(948)] public uint size_in_bits_cdef_params;
    [FieldOffset(952)] public uint byte_offset_frame_hdr_obu_size;
    [FieldOffset(956)] public uint size_in_bits_frame_hdr_obu;
    [FieldOffset(960)] public byte tile_group_obu_hdr_info;

    /// <summary>
    /// ref_frame_ctrl_l0.fields: search_idx0 is bits 0-2, search_idx1 3-5 and so on up to
    /// search_idx6; only the first is used here.
    /// </summary>
    public static uint RefFrameCtrl(uint searchIdx0) => VaBits.Field(searchIdx0, 0, 3);

    /// <summary>
    /// picture_flags.bits: frame_type is bits 0-1 and error_resilient_mode bit 2; then
    /// disable_cdf_update (3), use_superres (4), allow_high_precision_mv (5),
    /// use_ref_frame_mvs (6), disable_frame_end_update_cdf (7) and reduced_tx_set (8) stay 0;
    /// enable_frame_obu is bit 9, and long_term_reference and the rest above it stay 0.
    /// </summary>
    public static uint PictureFlags(uint frameType, bool errorResilientMode, bool enableFrameObu) =>
        VaBits.Field(frameType, 0, 2) | VaBits.Flag(errorResilientMode, 2) | VaBits.Flag(enableFrameObu, 9);

    /// <summary>
    /// mode_control_flags.bits: delta_q_present (0), delta_q_res (1-2), delta_lf_present (3),
    /// delta_lf_res (4-5) and delta_lf_multi (6) stay 0; tx_mode is bits 7-8 and
    /// reference_mode bits 9-10; skip_mode_present above them stays 0.
    /// </summary>
    public static uint ModeControlFlags(uint txMode, uint referenceMode) =>
        VaBits.Field(txMode, 7, 2) | VaBits.Field(referenceMode, 9, 2);

    /// <summary>
    /// tile_group_obu_hdr_info.bits, a byte: obu_extension_flag is bit 0, obu_has_size_field
    /// bit 1, temporal_id bits 2-4, spatial_id 5-6.
    /// </summary>
    public static byte TileGroupObuHdrInfo(bool obuHasSizeField) => (byte)VaBits.Flag(obuHasSizeField, 1);
}

/// <summary>VAEncTileGroupBufferAV1.</summary>
[StructLayout(LayoutKind.Explicit, Size = 20)]
internal struct VAEncTileGroupBufferAV1
{
    [FieldOffset(0)] public byte tg_start;
    [FieldOffset(1)] public byte tg_end;
}
