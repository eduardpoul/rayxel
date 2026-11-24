#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

/*
 * The AV1 headers this program writes itself, for the same reason as the HEVC ones (see
 * hevc_headers.h): Mesa's radeonsi encoder takes what it needs from a sequence header OBU
 * and a frame header OBU the application hands it - which frame this is, whether it is
 * shown, which reference slots it refreshes and reads - and writes its own in their place.
 * Without the frame header it would take every frame as not to be shown.
 *
 * The stream is the simplest low-latency one AV1 allows: 8-bit 4:2:0, 64x64 superblocks,
 * one tile, a key frame and then inter frames that refer only to the frame before them,
 * no reordering, 8-bit order hints. The headers are complete and valid AV1 in their own
 * right, so a driver that copies them rather than rewriting them still makes a stream.
 */

#define AV1_HEADERS_MAX 256
#define AV1_ORDER_HINT_BITS 8
#define AV1_PRIMARY_REF_NONE 7

struct av1_stream {
	uint32_t width, height; /* the picture's own size */
	uint8_t level_idx;      /* seq_level_idx: 0 is 2.0, 8 is 4.0, 31 is "no limits" */
	uint8_t tier;
	bool full_range;
	uint8_t colour_primaries, transfer_characteristics, matrix_coefficients;
	bool tx_mode_select; /* else TX_MODE_LARGEST */

	/* Whether to say the picture's size apart from the coded one: true when the encoder
	 * pads the picture out to whole blocks, which the decoder then has to crop. */
	bool render_size;

	uint32_t sb_cols, sb_rows; /* superblocks across and down; filled in by av1_stream_init */
};

struct av1_frame {
	bool keyframe;
	uint8_t order_hint;
	uint8_t refresh_frame_flags; /* inter frames; a key frame refreshes every slot */
	uint8_t ref_slot;            /* inter frames: the slot every reference points at */
	uint8_t base_q_idx;          /* nonzero; the encoder's rate control replaces it */

	/* What the filters are set to, derived from base_q_idx by av1_write_frame_header. */
	uint8_t loop_filter_level[4];
	uint8_t cdef_damping_minus_3, cdef_bits;
	uint8_t cdef_y_strengths[8], cdef_uv_strengths[8]; /* primary << 2 | secondary code */

	/* Where things landed in the OBU, in bits from its first byte - what an encoder that
	 * patches the header in place asks for. */
	uint32_t bit_offset_qindex, bit_offset_loopfilter, bit_offset_cdef, size_in_bits_cdef;
	uint32_t size_in_bits;
};

/* Picks the level and the tiling for a picture. */
void av1_stream_init(struct av1_stream *s, uint32_t width, uint32_t height, uint32_t fps, uint32_t bitrate_kbps);

/* Each returns the number of bytes written, at most AV1_HEADERS_MAX. */
size_t av1_write_sequence_header(const struct av1_stream *s, uint8_t *out);
size_t av1_write_frame_header(const struct av1_stream *s, struct av1_frame *f, uint8_t *out);
size_t av1_write_temporal_delimiter(uint8_t *out);
