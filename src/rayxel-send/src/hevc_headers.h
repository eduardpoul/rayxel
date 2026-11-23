#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

/*
 * The HEVC headers this program has to write itself.
 *
 * Mesa's radeonsi encoder does not invent a stream's headers. It emits a VPS, SPS and PPS
 * only when the application hands it one of each, parses those for everything the VA
 * parameter structs have no field for (reorder depth, the crop window, colour
 * description, how wide the picture order count is), and writes its own version. The
 * same goes for every slice: it reads the application's slice header to learn the
 * picture order count and the reference picture set, then writes the real one around
 * the parts only its firmware knows. Without them the output is slices nothing can
 * decode. So these are templates for the driver, and they follow exactly the syntax its
 * parser reads.
 */

struct hevc_stream {
	/* The picture, and the size it is coded at: the encoder works in whole 64-pixel
	 * columns and 16-pixel rows, and the difference is declared as a crop. */
	uint32_t width, height;
	uint32_t coded_width, coded_height;

	uint8_t level_idc; /* thirty times the level number */
	uint8_t tier;      /* 0 main, 1 high */

	/* Width in bits of the picture order count each slice carries. */
	unsigned poc_bits;

	/* Colour description, as the H.273 code points; 1/1/1 is BT.709. */
	bool full_range;
	uint8_t colour_primaries, transfer_characteristics, matrix_coefficients;
};

/* Largest output of either function. */
#define HEVC_HEADERS_MAX 256

/* VPS, SPS and PPS as three Annex B NAL units. Returns the number of bytes. */
size_t hevc_write_parameter_sets(const struct hevc_stream *stream, uint8_t *out);

/* The header of a picture's only slice: a keyframe, or a P frame that refers to the
 * picture before it. `poc` counts pictures since the last keyframe. */
size_t hevc_write_slice_header(const struct hevc_stream *stream, bool keyframe, uint32_t poc, uint8_t *out);
