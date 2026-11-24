#include <string.h>

#include "av1_headers.h"

#define OBU_SEQUENCE_HEADER 1
#define OBU_TEMPORAL_DELIMITER 2
#define OBU_FRAME_HEADER 3

#define FRAME_KEY 0
#define FRAME_INTER 1

/* An OBU's payload, built most significant bit first. AV1 has no emulation prevention. */
struct bits {
	uint8_t data[AV1_HEADERS_MAX];
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

/* A one, then zeros up to the byte boundary. */
static void put_trailing(struct bits *b)
{
	put(b, 1, 1);
	while (b->count % 8) put(b, 0, 1);
}

static size_t leb128(uint8_t *out, size_t value)
{
	size_t n = 0;
	do {
		uint8_t byte = value & 0x7f;
		value >>= 7;
		out[n++] = byte | (value ? 0x80 : 0);
	} while (value);
	return n;
}

/* Header byte, then the payload's size, then the payload. Returns the bytes written, and in
 * `payload_at` where the payload begins. */
static size_t emit_obu(uint8_t *out, int type, const struct bits *b, size_t *payload_at)
{
	size_t n = 0, bytes = b->count / 8;
	out[n++] = (uint8_t)(type << 3 | 1 << 1); /* forbidden 0, type, no extension, has size, reserved 0 */
	n += leb128(out + n, bytes);
	if (payload_at) *payload_at = n;
	memcpy(out + n, b->data, bytes);
	return n + bytes;
}

/* The smallest k with blk << k >= target, as the specification's tile_log2. */
static uint32_t tile_log2(uint32_t blk, uint32_t target)
{
	uint32_t k = 0;
	while ((blk << k) < target) k++;
	return k;
}

static uint32_t floor_log2(uint32_t v)
{
	uint32_t k = 0;
	while (v >>= 1) k++;
	return k;
}

void av1_stream_init(struct av1_stream *s, uint32_t width, uint32_t height, uint32_t fps, uint32_t bitrate_kbps)
{
	/* Annex A: the lowest level whose picture size, display rate and bitrate hold this. */
	static const struct {
		uint8_t idx;
		uint32_t pic_size;
		uint64_t display_rate;
		uint32_t main_kbps, high_kbps; /* high 0: the level has no high tier */
	} levels[] = {
		{ 0, 147456, 4423680, 1500, 0 },              /* 2.0 */
		{ 1, 278784, 8363520, 3000, 0 },              /* 2.1 */
		{ 4, 665856, 19975680, 6000, 0 },             /* 3.0 */
		{ 5, 1065024, 31950720, 10000, 0 },           /* 3.1 */
		{ 8, 2359296, 70778880, 12000, 30000 },       /* 4.0 */
		{ 9, 2359296, 141557760, 20000, 50000 },      /* 4.1 */
		{ 12, 8912896, 267386880, 30000, 100000 },    /* 5.0 */
		{ 13, 8912896, 534773760, 40000, 160000 },    /* 5.1 */
		{ 14, 8912896, 1069547520, 60000, 240000 },   /* 5.2 */
		{ 16, 35651584, 1069547520, 60000, 240000 },  /* 6.0 */
		{ 17, 35651584, 2139095040, 100000, 480000 }, /* 6.1 */
		{ 18, 35651584, 4278190080u, 160000, 800000 }, /* 6.2 */
	};
	uint64_t samples = (uint64_t)width * height, rate = samples * fps;

	s->width = width;
	s->height = height;
	s->level_idx = 31; /* no level fits: "maximum parameters" */
	s->tier = 1;
	for (size_t i = 0; i < sizeof(levels) / sizeof(levels[0]); i++) {
		if (samples > levels[i].pic_size || rate > levels[i].display_rate) continue;
		if (bitrate_kbps <= levels[i].main_kbps) {
			s->level_idx = levels[i].idx;
			s->tier = 0;
			break;
		}
		if (bitrate_kbps <= levels[i].high_kbps) {
			s->level_idx = levels[i].idx;
			s->tier = 1;
			break;
		}
	}

	uint32_t mi_cols = 2 * ((width + 7) >> 3), mi_rows = 2 * ((height + 7) >> 3);
	s->sb_cols = (mi_cols + 15) >> 4;
	s->sb_rows = (mi_rows + 15) >> 4;
}

size_t av1_write_temporal_delimiter(uint8_t *out)
{
	out[0] = OBU_TEMPORAL_DELIMITER << 3 | 1 << 1;
	out[1] = 0;
	return 2;
}

size_t av1_write_sequence_header(const struct av1_stream *s, uint8_t *out)
{
	struct bits b = { 0 };
	uint32_t width_bits = floor_log2(s->width) + 1, height_bits = floor_log2(s->height) + 1;

	put(&b, 0, 3); /* seq_profile: Main, 8-bit 4:2:0 */
	put(&b, 0, 1); /* still_picture */
	put(&b, 0, 1); /* reduced_still_picture_header */
	put(&b, 0, 1); /* timing_info_present_flag: frames come when they come */
	put(&b, 0, 1); /* initial_display_delay_present_flag */
	put(&b, 0, 5); /* operating_points_cnt_minus_1 */
	put(&b, 0, 12); /* operating_point_idc[0] */
	put(&b, s->level_idx, 5);
	if (s->level_idx > 7) put(&b, s->tier, 1);

	put(&b, width_bits - 1, 4);  /* frame_width_bits_minus_1 */
	put(&b, height_bits - 1, 4); /* frame_height_bits_minus_1 */
	put(&b, s->width - 1, (int)width_bits);   /* max_frame_width_minus_1; the driver puts its coded size here */
	put(&b, s->height - 1, (int)height_bits); /* max_frame_height_minus_1 */

	put(&b, 0, 1); /* frame_id_numbers_present_flag */
	put(&b, 0, 1); /* use_128x128_superblock */
	put(&b, 0, 1); /* enable_filter_intra */
	put(&b, 0, 1); /* enable_intra_edge_filter */
	put(&b, 0, 1); /* enable_interintra_compound */
	put(&b, 0, 1); /* enable_masked_compound */
	put(&b, 0, 1); /* enable_warped_motion */
	put(&b, 0, 1); /* enable_dual_filter */
	put(&b, 1, 1); /* enable_order_hint */
	put(&b, 0, 1); /* enable_jnt_comp */
	put(&b, 0, 1); /* enable_ref_frame_mvs */
	put(&b, 0, 1); /* seq_choose_screen_content_tools */
	put(&b, 0, 1); /* seq_force_screen_content_tools: off, so no integer-mv choice follows */
	put(&b, AV1_ORDER_HINT_BITS - 1, 3); /* order_hint_bits_minus_1 */
	put(&b, 0, 1); /* enable_superres */
	put(&b, 1, 1); /* enable_cdef */
	put(&b, 0, 1); /* enable_restoration */

	/* color_config() */
	put(&b, 0, 1); /* high_bitdepth */
	put(&b, 0, 1); /* mono_chrome */
	put(&b, 1, 1); /* color_description_present_flag */
	put(&b, s->colour_primaries, 8);
	put(&b, s->transfer_characteristics, 8);
	put(&b, s->matrix_coefficients, 8);
	put(&b, s->full_range, 1); /* color_range */
	put(&b, 0, 2); /* chroma_sample_position: unknown, as 4:2:0 with subsampling in both */
	put(&b, 0, 1); /* separate_uv_delta_q */

	put(&b, 0, 1); /* film_grain_params_present */
	put_trailing(&b);
	return emit_obu(out, OBU_SEQUENCE_HEADER, &b, NULL);
}

/* The in-loop filters for a quantiser, as ffmpeg's VA-API encoder chooses them: deblocking
 * that grows with the square of the step, and four CDEF candidates around one strength. An
 * encoder with its own rate control rewrites them; they are here so the header is whole. */
static void choose_filters(struct av1_frame *f)
{
	int q = f->base_q_idx;
	int level = q > 32 ? ((q - 32) * (q - 32) * 7 >> 12) : 0;
	if (level > 63) level = 63;
	int pri = q >> 5;
	const int cdef[4][2] = { { pri, 1 }, { 0, 0 }, { pri >> 1, 1 }, { pri << 1, 2 } };

	f->loop_filter_level[0] = f->loop_filter_level[1] = (uint8_t)level;
	f->loop_filter_level[2] = f->loop_filter_level[3] = (uint8_t)((level + 1) >> 1);

	memset(f->cdef_y_strengths, 0, sizeof(f->cdef_y_strengths));
	memset(f->cdef_uv_strengths, 0, sizeof(f->cdef_uv_strengths));
	f->cdef_damping_minus_3 = 0;
	f->cdef_bits = 0;
	if (!pri) return;
	f->cdef_damping_minus_3 = (uint8_t)(q >> 6);
	f->cdef_bits = 2;
	for (int i = 0; i < 4; i++) {
		int p = cdef[i][0] > 15 ? 15 : cdef[i][0];
		f->cdef_y_strengths[i] = f->cdef_uv_strengths[i] = (uint8_t)(p << 2 | cdef[i][1]);
	}
}

size_t av1_write_frame_header(const struct av1_stream *s, struct av1_frame *f, uint8_t *out)
{
	struct bits b = { 0 };
	bool intra = f->keyframe;
	uint32_t offset_q = 0, offset_lf = 0, offset_cdef = 0, end_cdef = 0;

	if (f->base_q_idx == 0) f->base_q_idx = 128;
	choose_filters(f);

	/* uncompressed_header() */
	put(&b, 0, 1);                                  /* show_existing_frame */
	put(&b, intra ? FRAME_KEY : FRAME_INTER, 2);    /* frame_type */
	put(&b, 1, 1);                                  /* show_frame */
	if (!intra) put(&b, 0, 1);                      /* error_resilient_mode; a shown key frame is resilient by rule */
	put(&b, 0, 1);                                  /* disable_cdf_update */
	/* Screen content tools are off in the sequence header, so neither they nor integer
	 * motion vectors are chosen here; frame ids are off too. */
	put(&b, 0, 1);                                  /* frame_size_override_flag */
	put(&b, f->order_hint, AV1_ORDER_HINT_BITS);
	if (!intra) put(&b, 0, 3);                      /* primary_ref_frame: LAST, the frame before */
	if (!intra) put(&b, f->refresh_frame_flags, 8);

	if (intra) {
		/* frame_size() says nothing without an override; superres is off. */
		put(&b, s->render_size, 1);                /* render_and_frame_size_different */
		if (s->render_size) {
			put(&b, s->width - 1, 16);
			put(&b, s->height - 1, 16);
		}
	} else {
		put(&b, 0, 1);                             /* frame_refs_short_signaling */
		for (int i = 0; i < 7; i++) put(&b, f->ref_slot, 3); /* ref_frame_idx[i] */
		put(&b, s->render_size, 1);                /* render_and_frame_size_different */
		if (s->render_size) {
			put(&b, s->width - 1, 16);
			put(&b, s->height - 1, 16);
		}
		put(&b, 0, 1);                             /* allow_high_precision_mv */
		put(&b, 0, 1);                             /* is_filter_switchable */
		put(&b, 0, 2);                             /* interpolation_filter: EIGHTTAP */
		put(&b, 0, 1);                             /* is_motion_mode_switchable */
		/* use_ref_frame_mvs is off in the sequence header. */
	}
	put(&b, 0, 1);                                  /* disable_frame_end_update_cdf */

	/* tile_info(): one tile. With uniform spacing that is "never one more column or row"
	 * for as long as the specification asks. */
	put(&b, 1, 1);                                  /* uniform_tile_spacing_flag */
	{
		uint32_t max_tile_width_sb = 4096 >> 6, max_tile_area_sb = (4096 * 2304) >> 12;
		uint32_t min_log2_cols = tile_log2(max_tile_width_sb, s->sb_cols);
		uint32_t max_log2_cols = tile_log2(1, s->sb_cols < 64 ? s->sb_cols : 64);
		uint32_t max_log2_rows = tile_log2(1, s->sb_rows < 64 ? s->sb_rows : 64);
		uint32_t min_log2_tiles = tile_log2(max_tile_area_sb, s->sb_cols * s->sb_rows);
		if (min_log2_tiles < min_log2_cols) min_log2_tiles = min_log2_cols;
		uint32_t cols_log2 = min_log2_cols;
		if (cols_log2 < max_log2_cols) put(&b, 0, 1); /* increment_tile_cols_log2 */
		uint32_t min_log2_rows = min_log2_tiles > cols_log2 ? min_log2_tiles - cols_log2 : 0;
		if (min_log2_rows < max_log2_rows) put(&b, 0, 1); /* increment_tile_rows_log2 */
		/* Pictures too wide or too large for one tile would need more; none of the
		 * sizes this program sends comes near (one tile holds 4096 by 2304). */
	}

	/* quantization_params() */
	offset_q = (uint32_t)b.count;
	put(&b, f->base_q_idx, 8);
	put(&b, 0, 1);                                  /* DeltaQYDc: delta_coded */
	put(&b, 0, 1);                                  /* DeltaQUDc: delta_coded */
	put(&b, 0, 1);                                  /* DeltaQUAc: delta_coded */
	put(&b, 0, 1);                                  /* using_qmatrix */

	put(&b, 0, 1);                                  /* segmentation_enabled */
	put(&b, 0, 1);                                  /* delta_q_present, base_q_idx being nonzero */

	/* loop_filter_params() */
	offset_lf = (uint32_t)b.count;
	put(&b, f->loop_filter_level[0], 6);
	put(&b, f->loop_filter_level[1], 6);
	if (f->loop_filter_level[0] || f->loop_filter_level[1]) {
		put(&b, f->loop_filter_level[2], 6);
		put(&b, f->loop_filter_level[3], 6);
	}
	put(&b, 0, 3);                                  /* loop_filter_sharpness */
	put(&b, 0, 1);                                  /* loop_filter_delta_enabled */

	/* cdef_params() */
	offset_cdef = (uint32_t)b.count;
	put(&b, f->cdef_damping_minus_3, 2);
	put(&b, f->cdef_bits, 2);
	for (int i = 0; i < (1 << f->cdef_bits); i++) {
		put(&b, f->cdef_y_strengths[i] >> 2, 4);
		put(&b, f->cdef_y_strengths[i] & 3, 2);
		put(&b, f->cdef_uv_strengths[i] >> 2, 4);
		put(&b, f->cdef_uv_strengths[i] & 3, 2);
	}
	end_cdef = (uint32_t)b.count;

	/* Loop restoration is off in the sequence header. */
	put(&b, s->tx_mode_select, 1);                  /* tx_mode_select */
	if (!intra) put(&b, 0, 1);                      /* reference_select */
	/* No skip mode without reference_select; no warped motion in the sequence. */
	put(&b, 0, 1);                                  /* reduced_tx_set */
	if (!intra)
		for (int ref = 1; ref <= 7; ref++) put(&b, 0, 1); /* is_global */
	/* No film grain in the sequence. */
	put_trailing(&b);

	size_t payload_at = 0;
	size_t n = emit_obu(out, OBU_FRAME_HEADER, &b, &payload_at);
	f->bit_offset_qindex = (uint32_t)(payload_at * 8 + offset_q);
	f->bit_offset_loopfilter = (uint32_t)(payload_at * 8 + offset_lf);
	f->bit_offset_cdef = (uint32_t)(payload_at * 8 + offset_cdef);
	f->size_in_bits_cdef = end_cdef - offset_cdef;
	f->size_in_bits = (uint32_t)(n * 8);
	return n;
}
