#include <string.h>

#include "hevc_headers.h"

#define NAL_TRAIL_R 1
#define NAL_IDR_W_RADL 19
#define NAL_VPS 32
#define NAL_SPS 33
#define NAL_PPS 34

/* A NAL unit's payload, built most significant bit first. */
struct bits {
	uint8_t data[HEVC_HEADERS_MAX];
	size_t count;
};

static void put(struct bits *b, uint32_t value, int width)
{
	for (int i = width - 1; i >= 0; i--) {
		if (b->count >= sizeof(b->data) * 8) return;
		if ((value >> i) & 1) b->data[b->count / 8] |= (uint8_t)(0x80 >> (b->count % 8));
		b->count++;
	}
}

/* Exp-Golomb, unsigned: as many zeros as the value plus one has bits after its first. */
static void put_ue(struct bits *b, uint32_t value)
{
	uint32_t code = value + 1;
	int width = 0;
	while ((code >> width) > 1) width++;
	put(b, 0, width);
	put(b, code, width + 1);
}

static void put_se(struct bits *b, int32_t value)
{
	put_ue(b, value > 0 ? (uint32_t)(2 * value - 1) : (uint32_t)(-2 * value));
}

/* A one, then zeros up to the byte boundary: how every NAL payload here ends. */
static void put_trailing(struct bits *b)
{
	put(b, 1, 1);
	while (b->count % 8) put(b, 0, 1);
}

/*
 * Wraps a payload as an Annex B NAL unit: start code, the two header bytes, and the
 * payload with a 03 inserted wherever two zero bytes would otherwise be followed by a
 * byte of 3 or less - the pattern a decoder would take for the next start code.
 */
static size_t emit_nal(uint8_t *out, int type, const struct bits *b)
{
	size_t n = 0, bytes = b->count / 8;
	int zeros = 0;

	out[n++] = 0;
	out[n++] = 0;
	out[n++] = 0;
	out[n++] = 1;
	out[n++] = (uint8_t)(type << 1); /* forbidden_zero_bit, nal_unit_type, top bit of layer id */
	out[n++] = 1;                    /* rest of layer id 0, temporal id 0 (coded plus one) */

	for (size_t i = 0; i < bytes; i++) {
		if (zeros >= 2 && b->data[i] <= 3) {
			out[n++] = 3;
			zeros = 0;
		}
		out[n++] = b->data[i];
		zeros = b->data[i] == 0 ? zeros + 1 : 0;
	}
	return n;
}

static void put_profile_tier_level(struct bits *b, const struct hevc_stream *s)
{
	put(b, 0, 2);           /* general_profile_space */
	put(b, s->tier, 1);     /* general_tier_flag */
	put(b, 1, 5);           /* general_profile_idc: Main */
	put(b, 0x60000000, 32); /* compatible with Main and Main 10 */
	put(b, 1, 1);           /* general_progressive_source_flag */
	put(b, 0, 1);           /* general_interlaced_source_flag */
	put(b, 1, 1);           /* general_non_packed_constraint_flag */
	put(b, 1, 1);           /* general_frame_only_constraint_flag */
	put(b, 0, 32);          /* 44 reserved bits */
	put(b, 0, 12);
	put(b, s->level_idc, 8);
}

/* One reference picture is kept and nothing is ever reordered, so a decoder may show
 * each picture the moment it has decoded it. This is the decoder half of low latency. */
static void put_ordering_info(struct bits *b)
{
	put(b, 1, 1); /* sub_layer_ordering_info_present_flag */
	put_ue(b, 1); /* max_dec_pic_buffering_minus1 */
	put_ue(b, 0); /* max_num_reorder_pics */
	put_ue(b, 0); /* max_latency_increase_plus1: no limit beyond the reorder count */
}

static size_t write_vps(const struct hevc_stream *s, uint8_t *out)
{
	struct bits b = { 0 };

	put(&b, 0, 4);       /* vps_video_parameter_set_id */
	put(&b, 1, 1);       /* vps_base_layer_internal_flag */
	put(&b, 1, 1);       /* vps_base_layer_available_flag */
	put(&b, 0, 6);       /* vps_max_layers_minus1 */
	put(&b, 0, 3);       /* vps_max_sub_layers_minus1 */
	put(&b, 1, 1);       /* vps_temporal_id_nesting_flag */
	put(&b, 0xffff, 16); /* vps_reserved_0xffff_16bits */
	put_profile_tier_level(&b, s);
	put_ordering_info(&b);
	put(&b, 0, 6);  /* vps_max_layer_id */
	put_ue(&b, 0);  /* vps_num_layer_sets_minus1 */
	/* Both left at zero on purpose. Mesa's parser reads one bit too many before the
	 * timing flag, so it takes the next flag for it; two zeros read the same either way. */
	put(&b, 0, 1); /* vps_timing_info_present_flag */
	put(&b, 0, 1); /* vps_extension_flag */
	put_trailing(&b);
	return emit_nal(out, NAL_VPS, &b);
}

static size_t write_sps(const struct hevc_stream *s, uint8_t *out)
{
	struct bits b = { 0 };
	uint32_t crop_right = (s->coded_width - s->width) / 2, crop_bottom = (s->coded_height - s->height) / 2;

	put(&b, 0, 4); /* sps_video_parameter_set_id */
	put(&b, 0, 3); /* sps_max_sub_layers_minus1 */
	put(&b, 1, 1); /* sps_temporal_id_nesting_flag */
	put_profile_tier_level(&b, s);
	put_ue(&b, 0); /* sps_seq_parameter_set_id */
	put_ue(&b, 1); /* chroma_format_idc: 4:2:0 */
	put_ue(&b, s->coded_width);
	put_ue(&b, s->coded_height);

	/* The crop is in chroma samples, hence the halves. The driver takes it from here
	 * and adds nothing of its own, so a size it has to pad is wrong without this. */
	bool cropped = crop_right || crop_bottom;
	put(&b, cropped, 1); /* conformance_window_flag */
	if (cropped) {
		put_ue(&b, 0);
		put_ue(&b, crop_right);
		put_ue(&b, 0);
		put_ue(&b, crop_bottom);
	}

	put_ue(&b, 0);               /* bit_depth_luma_minus8 */
	put_ue(&b, 0);               /* bit_depth_chroma_minus8 */
	put_ue(&b, s->poc_bits - 4); /* log2_max_pic_order_cnt_lsb_minus4 */
	put_ordering_info(&b);

	/* Coding blocks 8 to 64, transforms 4 to 32, as the encoder reports. The driver
	 * replaces these six with its own on the way out; they match what it writes. */
	put_ue(&b, 0); /* log2_min_luma_coding_block_size_minus3 */
	put_ue(&b, 3); /* log2_diff_max_min_luma_coding_block_size */
	put_ue(&b, 0); /* log2_min_luma_transform_block_size_minus2 */
	put_ue(&b, 3); /* log2_diff_max_min_luma_transform_block_size */
	put_ue(&b, 4); /* max_transform_hierarchy_depth_inter */
	put_ue(&b, 4); /* max_transform_hierarchy_depth_intra */

	put(&b, 0, 1); /* scaling_list_enabled_flag */
	put(&b, 1, 1); /* amp_enabled_flag */
	put(&b, 1, 1); /* sample_adaptive_offset_enabled_flag */
	put(&b, 0, 1); /* pcm_enabled_flag */

	/* No reference picture sets here: each P slice states its own, which is the form
	 * the driver's slice header writer is exercised with by everything else. */
	put_ue(&b, 0); /* num_short_term_ref_pic_sets */
	put(&b, 0, 1); /* long_term_ref_pics_present_flag */
	put(&b, 0, 1); /* sps_temporal_mvp_enabled_flag */
	put(&b, 1, 1); /* strong_intra_smoothing_enabled_flag */

	put(&b, 1, 1); /* vui_parameters_present_flag */
	put(&b, 0, 1); /* aspect_ratio_info_present_flag */
	put(&b, 0, 1); /* overscan_info_present_flag */
	put(&b, 1, 1); /* video_signal_type_present_flag */
	put(&b, 5, 3); /* video_format: unspecified */
	put(&b, s->full_range, 1);
	put(&b, 1, 1); /* colour_description_present_flag */
	put(&b, s->colour_primaries, 8);
	put(&b, s->transfer_characteristics, 8);
	put(&b, s->matrix_coefficients, 8);
	put(&b, 0, 1); /* chroma_loc_info_present_flag */
	put(&b, 0, 1); /* neutral_chroma_indication_flag */
	put(&b, 0, 1); /* field_seq_flag */
	put(&b, 0, 1); /* frame_field_info_present_flag */
	put(&b, 0, 1); /* default_display_window_flag */
	put(&b, 0, 1); /* vui_timing_info_present_flag: frames come when they come */
	put(&b, 0, 1); /* bitstream_restriction_flag */

	put(&b, 0, 1); /* sps_extension_present_flag */
	put_trailing(&b);
	return emit_nal(out, NAL_SPS, &b);
}

static size_t write_pps(uint8_t *out)
{
	struct bits b = { 0 };

	put_ue(&b, 0); /* pps_pic_parameter_set_id */
	put_ue(&b, 0); /* pps_seq_parameter_set_id */
	put(&b, 0, 1); /* dependent_slice_segments_enabled_flag */
	put(&b, 0, 1); /* output_flag_present_flag */
	put(&b, 0, 3); /* num_extra_slice_header_bits */
	put(&b, 0, 1); /* sign_data_hiding_enabled_flag */
	put(&b, 0, 1); /* cabac_init_present_flag */
	put_ue(&b, 0); /* num_ref_idx_l0_default_active_minus1 */
	put_ue(&b, 0); /* num_ref_idx_l1_default_active_minus1 */
	put_se(&b, 0); /* init_qp_minus26 */
	put(&b, 0, 1); /* constrained_intra_pred_flag */
	put(&b, 1, 1); /* transform_skip_enabled_flag */
	put(&b, 1, 1); /* cu_qp_delta_enabled_flag: rate control moves the QP inside a picture */
	put_ue(&b, 0); /* diff_cu_qp_delta_depth */
	put_se(&b, 0); /* pps_cb_qp_offset */
	put_se(&b, 0); /* pps_cr_qp_offset */
	put(&b, 0, 1); /* pps_slice_chroma_qp_offsets_present_flag */
	put(&b, 0, 1); /* weighted_pred_flag */
	put(&b, 0, 1); /* weighted_bipred_flag */
	put(&b, 0, 1); /* transquant_bypass_enabled_flag */
	put(&b, 0, 1); /* tiles_enabled_flag */
	put(&b, 0, 1); /* entropy_coding_sync_enabled_flag */
	put(&b, 1, 1); /* pps_loop_filter_across_slices_enabled_flag */
	put(&b, 0, 1); /* deblocking_filter_control_present_flag */
	put(&b, 0, 1); /* pps_scaling_list_data_present_flag */
	put(&b, 0, 1); /* lists_modification_present_flag */
	put_ue(&b, 0); /* log2_parallel_merge_level_minus2 */
	put(&b, 0, 1); /* slice_segment_header_extension_present_flag */
	put(&b, 0, 1); /* pps_extension_present_flag */
	put_trailing(&b);
	return emit_nal(out, NAL_PPS, &b);
}

size_t hevc_write_parameter_sets(const struct hevc_stream *s, uint8_t *out)
{
	size_t n = write_vps(s, out);
	n += write_sps(s, out + n);
	n += write_pps(out + n);
	return n;
}

size_t hevc_write_slice_header(const struct hevc_stream *s, bool keyframe, uint32_t poc, uint8_t *out)
{
	struct bits b = { 0 };

	put(&b, 1, 1);               /* first_slice_segment_in_pic_flag */
	if (keyframe) put(&b, 0, 1); /* no_output_of_prior_pics_flag */
	put_ue(&b, 0);               /* slice_pic_parameter_set_id */
	put_ue(&b, keyframe ? 2 : 1); /* slice_type: I, or P */

	if (!keyframe) {
		put(&b, poc & ((1u << s->poc_bits) - 1), (int)s->poc_bits); /* slice_pic_order_cnt_lsb */
		put(&b, 0, 1); /* short_term_ref_pic_set_sps_flag: the set follows */
		put_ue(&b, 1); /* num_negative_pics: one picture, */
		put_ue(&b, 0); /* num_positive_pics: none after, */
		put_ue(&b, 0); /* delta_poc_s0_minus1: the one right before this, */
		put(&b, 1, 1); /* used_by_curr_pic_s0_flag: and it is the reference */
	}

	put(&b, 1, 1); /* slice_sao_luma_flag */
	put(&b, 1, 1); /* slice_sao_chroma_flag */

	if (!keyframe) {
		put(&b, 0, 1); /* num_ref_idx_active_override_flag */
		put_ue(&b, 0); /* five_minus_max_num_merge_cand */
	}

	put_se(&b, 0); /* slice_qp_delta */
	put(&b, 1, 1); /* slice_loop_filter_across_slices_enabled_flag */
	put_trailing(&b);
	return emit_nal(out, keyframe ? NAL_IDR_W_RADL : NAL_TRAIL_R, &b);
}
