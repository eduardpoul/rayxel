namespace Rayxel.Send.Linux;

/// <summary>
/// What an HEVC stream is, as its headers say it.
/// </summary>
public struct HevcStream
{
    /// <summary>
    /// The picture, and the size it is coded at: the encoder works in whole 64-pixel columns
    /// and 16-pixel rows, and the difference is declared as a crop.
    /// </summary>
    public uint Width, Height;
    public uint CodedWidth, CodedHeight;

    /// <summary>Thirty times the level number.</summary>
    public byte LevelIdc;

    /// <summary>0 main, 1 high.</summary>
    public byte Tier;

    /// <summary>Width in bits of the picture order count each slice carries.</summary>
    public uint PocBits;

    /// <summary>Colour description, as the H.273 code points; 1/1/1 is BT.709.</summary>
    public bool FullRange;
    public byte ColourPrimaries, TransferCharacteristics, MatrixCoefficients;
}

/// <summary>
/// The HEVC headers this program has to write itself.
/// <para>
/// Mesa's radeonsi encoder does not invent a stream's headers. It emits a VPS, SPS and PPS
/// only when the application hands it one of each, parses those for everything the VA
/// parameter structs have no field for (reorder depth, the crop window, colour
/// description, how wide the picture order count is), and writes its own version. The
/// same goes for every slice: it reads the application's slice header to learn the
/// picture order count and the reference picture set, then writes the real one around the
/// parts only its firmware knows. Without them the output is slices nothing can decode.
/// So these are templates for the driver, and they follow exactly the syntax its parser
/// reads.
/// </para>
/// </summary>
public static class HevcHeaders
{
    /// <summary>Largest output of either writer.</summary>
    public const int MaxBytes = 256;

    private const int NalTrailR = 1;
    private const int NalIdrWRadl = 19;
    private const int NalVps = 32;
    private const int NalSps = 33;
    private const int NalPps = 34;

    /// <summary>VPS, SPS and PPS as three Annex B NAL units. Returns the number of bytes.</summary>
    public static int WriteParameterSets(in HevcStream s, Span<byte> output)
    {
        int n = WriteVps(s, output);
        n += WriteSps(s, output[n..]);
        n += WritePps(output[n..]);
        return n;
    }

    /// <summary>
    /// The header of a picture's only slice: a keyframe, or a P frame that refers to the
    /// picture before it. <paramref name="poc"/> counts pictures since the last keyframe.
    /// </summary>
    public static int WriteSliceHeader(in HevcStream s, bool keyframe, uint poc, Span<byte> output)
    {
        var b = new BitWriter(stackalloc byte[MaxBytes]);

        b.Put(1, 1);                    // first_slice_segment_in_pic_flag
        if (keyframe) b.Put(0, 1);      // no_output_of_prior_pics_flag
        b.PutUe(0);                     // slice_pic_parameter_set_id
        b.PutUe(keyframe ? 2u : 1u);    // slice_type: I, or P

        if (!keyframe)
        {
            b.Put(poc & ((1u << (int)s.PocBits) - 1), (int)s.PocBits); // slice_pic_order_cnt_lsb
            b.Put(0, 1);                // short_term_ref_pic_set_sps_flag: the set follows
            b.PutUe(1);                 // num_negative_pics: one picture,
            b.PutUe(0);                 // num_positive_pics: none after,
            b.PutUe(0);                 // delta_poc_s0_minus1: the one right before this,
            b.Put(1, 1);                // used_by_curr_pic_s0_flag: and it is the reference
        }

        b.Put(1, 1);                    // slice_sao_luma_flag
        b.Put(1, 1);                    // slice_sao_chroma_flag

        if (!keyframe)
        {
            b.Put(0, 1);                // num_ref_idx_active_override_flag
            b.PutUe(0);                 // five_minus_max_num_merge_cand
        }

        b.PutSe(0);                     // slice_qp_delta
        b.Put(1, 1);                    // slice_loop_filter_across_slices_enabled_flag
        b.PutTrailing();
        return EmitNal(output, keyframe ? NalIdrWRadl : NalTrailR, b.Written);
    }

    /// <summary>
    /// Wraps a payload as an Annex B NAL unit: start code, the two header bytes, and the
    /// payload with a 03 inserted wherever two zero bytes would otherwise be followed by a
    /// byte of 3 or less - the pattern a decoder would take for the next start code.
    /// </summary>
    private static int EmitNal(Span<byte> output, int type, ReadOnlySpan<byte> payload)
    {
        int n = 0, zeros = 0;

        output[n++] = 0;
        output[n++] = 0;
        output[n++] = 0;
        output[n++] = 1;
        output[n++] = (byte)(type << 1); // forbidden_zero_bit, nal_unit_type, top bit of layer id
        output[n++] = 1;                 // rest of layer id 0, temporal id 0 (coded plus one)

        foreach (byte value in payload)
        {
            if (zeros >= 2 && value <= 3)
            {
                output[n++] = 3;
                zeros = 0;
            }
            output[n++] = value;
            zeros = value == 0 ? zeros + 1 : 0;
        }
        return n;
    }

    private static void PutProfileTierLevel(ref BitWriter b, in HevcStream s)
    {
        b.Put(0, 2);            // general_profile_space
        b.Put(s.Tier, 1);       // general_tier_flag
        b.Put(1, 5);            // general_profile_idc: Main
        b.Put(0x60000000, 32);  // compatible with Main and Main 10
        b.Put(1, 1);            // general_progressive_source_flag
        b.Put(0, 1);            // general_interlaced_source_flag
        b.Put(1, 1);            // general_non_packed_constraint_flag
        b.Put(1, 1);            // general_frame_only_constraint_flag
        b.Put(0, 32);           // 44 reserved bits
        b.Put(0, 12);
        b.Put(s.LevelIdc, 8);
    }

    /// <summary>
    /// One reference picture is kept and nothing is ever reordered, so a decoder may show
    /// each picture the moment it has decoded it. This is the decoder half of low latency.
    /// </summary>
    private static void PutOrderingInfo(ref BitWriter b)
    {
        b.Put(1, 1); // sub_layer_ordering_info_present_flag
        b.PutUe(1);  // max_dec_pic_buffering_minus1
        b.PutUe(0);  // max_num_reorder_pics
        b.PutUe(0);  // max_latency_increase_plus1: no limit beyond the reorder count
    }

    private static int WriteVps(in HevcStream s, Span<byte> output)
    {
        var b = new BitWriter(stackalloc byte[MaxBytes]);

        b.Put(0, 4);       // vps_video_parameter_set_id
        b.Put(1, 1);       // vps_base_layer_internal_flag
        b.Put(1, 1);       // vps_base_layer_available_flag
        b.Put(0, 6);       // vps_max_layers_minus1
        b.Put(0, 3);       // vps_max_sub_layers_minus1
        b.Put(1, 1);       // vps_temporal_id_nesting_flag
        b.Put(0xffff, 16); // vps_reserved_0xffff_16bits
        PutProfileTierLevel(ref b, s);
        PutOrderingInfo(ref b);
        b.Put(0, 6);       // vps_max_layer_id
        b.PutUe(0);        // vps_num_layer_sets_minus1
        // Both left at zero on purpose. Mesa's parser reads one bit too many before the
        // timing flag, so it takes the next flag for it; two zeros read the same either way.
        b.Put(0, 1);       // vps_timing_info_present_flag
        b.Put(0, 1);       // vps_extension_flag
        b.PutTrailing();
        return EmitNal(output, NalVps, b.Written);
    }

    private static int WriteSps(in HevcStream s, Span<byte> output)
    {
        var b = new BitWriter(stackalloc byte[MaxBytes]);
        uint cropRight = (s.CodedWidth - s.Width) / 2, cropBottom = (s.CodedHeight - s.Height) / 2;

        b.Put(0, 4); // sps_video_parameter_set_id
        b.Put(0, 3); // sps_max_sub_layers_minus1
        b.Put(1, 1); // sps_temporal_id_nesting_flag
        PutProfileTierLevel(ref b, s);
        b.PutUe(0);  // sps_seq_parameter_set_id
        b.PutUe(1);  // chroma_format_idc: 4:2:0
        b.PutUe(s.CodedWidth);
        b.PutUe(s.CodedHeight);

        // The crop is in chroma samples, hence the halves. The driver takes it from here
        // and adds nothing of its own, so a size it has to pad is wrong without this.
        bool cropped = cropRight != 0 || cropBottom != 0;
        b.Put(cropped, 1); // conformance_window_flag
        if (cropped)
        {
            b.PutUe(0);
            b.PutUe(cropRight);
            b.PutUe(0);
            b.PutUe(cropBottom);
        }

        b.PutUe(0);              // bit_depth_luma_minus8
        b.PutUe(0);              // bit_depth_chroma_minus8
        b.PutUe(s.PocBits - 4);  // log2_max_pic_order_cnt_lsb_minus4
        PutOrderingInfo(ref b);

        // Coding blocks 8 to 64, transforms 4 to 32, as the encoder reports. The driver
        // replaces these six with its own on the way out; they match what it writes.
        b.PutUe(0); // log2_min_luma_coding_block_size_minus3
        b.PutUe(3); // log2_diff_max_min_luma_coding_block_size
        b.PutUe(0); // log2_min_luma_transform_block_size_minus2
        b.PutUe(3); // log2_diff_max_min_luma_transform_block_size
        b.PutUe(4); // max_transform_hierarchy_depth_inter
        b.PutUe(4); // max_transform_hierarchy_depth_intra

        b.Put(0, 1); // scaling_list_enabled_flag
        b.Put(1, 1); // amp_enabled_flag
        b.Put(1, 1); // sample_adaptive_offset_enabled_flag
        b.Put(0, 1); // pcm_enabled_flag

        // No reference picture sets here: each P slice states its own, which is the form
        // the driver's slice header writer is exercised with by everything else.
        b.PutUe(0);  // num_short_term_ref_pic_sets
        b.Put(0, 1); // long_term_ref_pics_present_flag
        b.Put(0, 1); // sps_temporal_mvp_enabled_flag
        b.Put(1, 1); // strong_intra_smoothing_enabled_flag

        b.Put(1, 1); // vui_parameters_present_flag
        b.Put(0, 1); // aspect_ratio_info_present_flag
        b.Put(0, 1); // overscan_info_present_flag
        b.Put(1, 1); // video_signal_type_present_flag
        b.Put(5, 3); // video_format: unspecified
        b.Put(s.FullRange, 1);
        b.Put(1, 1); // colour_description_present_flag
        b.Put(s.ColourPrimaries, 8);
        b.Put(s.TransferCharacteristics, 8);
        b.Put(s.MatrixCoefficients, 8);
        b.Put(0, 1); // chroma_loc_info_present_flag
        b.Put(0, 1); // neutral_chroma_indication_flag
        b.Put(0, 1); // field_seq_flag
        b.Put(0, 1); // frame_field_info_present_flag
        b.Put(0, 1); // default_display_window_flag
        b.Put(0, 1); // vui_timing_info_present_flag: frames come when they come
        b.Put(0, 1); // bitstream_restriction_flag

        b.Put(0, 1); // sps_extension_present_flag
        b.PutTrailing();
        return EmitNal(output, NalSps, b.Written);
    }

    private static int WritePps(Span<byte> output)
    {
        var b = new BitWriter(stackalloc byte[MaxBytes]);

        b.PutUe(0);  // pps_pic_parameter_set_id
        b.PutUe(0);  // pps_seq_parameter_set_id
        b.Put(0, 1); // dependent_slice_segments_enabled_flag
        b.Put(0, 1); // output_flag_present_flag
        b.Put(0, 3); // num_extra_slice_header_bits
        b.Put(0, 1); // sign_data_hiding_enabled_flag
        b.Put(0, 1); // cabac_init_present_flag
        b.PutUe(0);  // num_ref_idx_l0_default_active_minus1
        b.PutUe(0);  // num_ref_idx_l1_default_active_minus1
        b.PutSe(0);  // init_qp_minus26
        b.Put(0, 1); // constrained_intra_pred_flag
        b.Put(1, 1); // transform_skip_enabled_flag
        b.Put(1, 1); // cu_qp_delta_enabled_flag: rate control moves the QP inside a picture
        b.PutUe(0);  // diff_cu_qp_delta_depth
        b.PutSe(0);  // pps_cb_qp_offset
        b.PutSe(0);  // pps_cr_qp_offset
        b.Put(0, 1); // pps_slice_chroma_qp_offsets_present_flag
        b.Put(0, 1); // weighted_pred_flag
        b.Put(0, 1); // weighted_bipred_flag
        b.Put(0, 1); // transquant_bypass_enabled_flag
        b.Put(0, 1); // tiles_enabled_flag
        b.Put(0, 1); // entropy_coding_sync_enabled_flag
        b.Put(1, 1); // pps_loop_filter_across_slices_enabled_flag
        b.Put(0, 1); // deblocking_filter_control_present_flag
        b.Put(0, 1); // pps_scaling_list_data_present_flag
        b.Put(0, 1); // lists_modification_present_flag
        b.PutUe(0);  // log2_parallel_merge_level_minus2
        b.Put(0, 1); // slice_segment_header_extension_present_flag
        b.Put(0, 1); // pps_extension_present_flag
        b.PutTrailing();
        return EmitNal(output, NalPps, b.Written);
    }
}
