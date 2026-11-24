using System.Runtime.InteropServices;

namespace Rayxel.Send.Linux.Va;

// The HEVC encoding structs of va_enc_hevc.h, laid out as VaTypes.cs describes: libva 1.17's
// sizes and offsets on x86-64, only the fields used here named.

/// <summary>VAEncSequenceParameterBufferHEVC.</summary>
[StructLayout(LayoutKind.Explicit, Size = 116)]
internal struct VAEncSequenceParameterBufferHEVC
{
    [FieldOffset(0)] public byte general_profile_idc;
    [FieldOffset(1)] public byte general_level_idc;
    [FieldOffset(2)] public byte general_tier_flag;
    [FieldOffset(4)] public uint intra_period;
    [FieldOffset(8)] public uint intra_idr_period;
    [FieldOffset(12)] public uint ip_period;
    [FieldOffset(16)] public uint bits_per_second;
    [FieldOffset(20)] public ushort pic_width_in_luma_samples;
    [FieldOffset(22)] public ushort pic_height_in_luma_samples;
    [FieldOffset(24)] public uint seq_fields;
    [FieldOffset(28)] public byte log2_min_luma_coding_block_size_minus3;
    [FieldOffset(29)] public byte log2_diff_max_min_luma_coding_block_size;
    [FieldOffset(30)] public byte log2_min_transform_block_size_minus2;
    [FieldOffset(31)] public byte log2_diff_max_min_transform_block_size;
    [FieldOffset(32)] public byte max_transform_hierarchy_depth_inter;
    [FieldOffset(33)] public byte max_transform_hierarchy_depth_intra;

    /// <summary>
    /// seq_fields.bits: chroma_format_idc is bits 0-1, then separate_colour_plane_flag (2),
    /// bit_depth_luma_minus8 (3-5), bit_depth_chroma_minus8 (6-8) and
    /// scaling_list_enabled_flag (9), all 0 here; strong_intra_smoothing_enabled_flag is bit
    /// 10, amp_enabled_flag bit 11, sample_adaptive_offset_enabled_flag bit 12, and the PCM,
    /// temporal MVP and GOP flags above them stay 0.
    /// </summary>
    public static uint SeqFields(uint chromaFormatIdc, bool strongIntraSmoothing, bool amp, bool sampleAdaptiveOffset) =>
        VaBits.Field(chromaFormatIdc, 0, 2) | VaBits.Flag(strongIntraSmoothing, 10) | VaBits.Flag(amp, 11) |
        VaBits.Flag(sampleAdaptiveOffset, 12);
}

/// <summary>VAEncPictureParameterBufferHEVC.</summary>
[StructLayout(LayoutKind.Explicit, Size = 576)]
internal unsafe struct VAEncPictureParameterBufferHEVC
{
    [FieldOffset(0)] public VAPictureHEVC decoded_curr_pic;

    /// <summary>VAPictureHEVC[15], seven words each: see <see cref="References"/>.</summary>
    [FieldOffset(28)] public fixed uint reference_frames[15 * 7];

    [FieldOffset(448)] public uint coded_buf; // VABufferID
    [FieldOffset(452)] public byte collocated_ref_pic_index;
    [FieldOffset(453)] public byte last_picture;
    [FieldOffset(454)] public byte pic_init_qp;
    [FieldOffset(505)] public byte nal_unit_type;
    [FieldOffset(508)] public uint pic_fields;

    public const int ReferenceCount = 15;

    /// <summary>reference_frames as what it is, for a struct that does not move.</summary>
    public static VAPictureHEVC* References(VAEncPictureParameterBufferHEVC* pic) => (VAPictureHEVC*)pic->reference_frames;

    /// <summary>
    /// pic_fields.bits: idr_pic_flag is bit 0, coding_type bits 1-3, reference_pic_flag bit 4,
    /// then dependent_slice_segments_enabled_flag (5), sign_data_hiding_enabled_flag (6) and
    /// constrained_intra_pred_flag (7), all 0 here; transform_skip_enabled_flag is bit 8,
    /// cu_qp_delta_enabled_flag bit 9, then the weighted prediction, transquant bypass, tile,
    /// wavefront and across-tiles flags (10-15) stay 0, and
    /// pps_loop_filter_across_slices_enabled_flag is bit 16.
    /// </summary>
    public static uint PicFields(bool idrPic, uint codingType, bool referencePic, bool transformSkip, bool cuQpDelta,
                                 bool loopFilterAcrossSlices) =>
        VaBits.Flag(idrPic, 0) | VaBits.Field(codingType, 1, 3) | VaBits.Flag(referencePic, 4) |
        VaBits.Flag(transformSkip, 8) | VaBits.Flag(cuQpDelta, 9) | VaBits.Flag(loopFilterAcrossSlices, 16);
}

/// <summary>VAEncSliceParameterBufferHEVC.</summary>
[StructLayout(LayoutKind.Explicit, Size = 1076)]
internal unsafe struct VAEncSliceParameterBufferHEVC
{
    [FieldOffset(0)] public uint slice_segment_address;
    [FieldOffset(4)] public uint num_ctu_in_slice;
    [FieldOffset(8)] public byte slice_type;
    [FieldOffset(9)] public byte slice_pic_parameter_set_id;

    /// <summary>VAPictureHEVC[15] each, seven words apiece: see <see cref="List0"/> and <see cref="List1"/>.</summary>
    [FieldOffset(12)] public fixed uint ref_pic_list0[15 * 7];
    [FieldOffset(432)] public fixed uint ref_pic_list1[15 * 7];

    [FieldOffset(1034)] public byte max_num_merge_cand;
    [FieldOffset(1035)] public sbyte slice_qp_delta;
    [FieldOffset(1040)] public uint slice_fields;

    public const int ReferenceCount = 15;

    public static VAPictureHEVC* List0(VAEncSliceParameterBufferHEVC* slice) => (VAPictureHEVC*)slice->ref_pic_list0;
    public static VAPictureHEVC* List1(VAEncSliceParameterBufferHEVC* slice) => (VAPictureHEVC*)slice->ref_pic_list1;

    /// <summary>
    /// slice_fields.bits: last_slice_of_pic_flag is bit 0, then dependent_slice_segment_flag
    /// (1), colour_plane_id (2-3) and slice_temporal_mvp_enabled_flag (4), all 0 here;
    /// slice_sao_luma_flag is bit 5, slice_sao_chroma_flag bit 6, then
    /// num_ref_idx_active_override_flag (7), mvd_l1_zero_flag (8), cabac_init_flag (9) and the
    /// two bits of slice_deblocking_filter_disabled_flag (10-11) stay 0;
    /// slice_loop_filter_across_slices_enabled_flag is bit 12 and collocated_from_l0_flag bit 13.
    /// </summary>
    public static uint SliceFields(bool lastSliceOfPic, bool saoLuma, bool saoChroma, bool loopFilterAcrossSlices,
                                   bool collocatedFromL0) =>
        VaBits.Flag(lastSliceOfPic, 0) | VaBits.Flag(saoLuma, 5) | VaBits.Flag(saoChroma, 6) |
        VaBits.Flag(loopFilterAcrossSlices, 12) | VaBits.Flag(collocatedFromL0, 13);
}
