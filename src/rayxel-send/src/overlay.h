#pragma once

#include <stdint.h>

/* A small picture to lay over a frame, already in the frame's own colour coding: one luma,
 * two chroma and one coverage sample for each of its pixels, `width * height` of each.
 * The colours are not multiplied by the coverage. */
struct overlay {
	/* Where its top left corner goes in the frame. It may hang over any edge. */
	int x, y;
	uint32_t width, height;
	const uint8_t *luma, *cb, *cr, *alpha;
};

/* Lays `overlay` over an NV12 picture that has just been copied from `source` into
 * `luma` and `chroma`.
 *
 * What is under the overlay is read from `source` and only the result is written to the
 * copy: the copy is the encoder's surface, memory that is slow to read back. */
void overlay_blend_nv12(const struct overlay *overlay, const uint8_t *source, uint32_t source_stride, uint8_t *luma,
			uint32_t luma_pitch, uint8_t *chroma, uint32_t chroma_pitch, uint32_t width, uint32_t height);
