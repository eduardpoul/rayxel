#include "overlay.h"

#include <stddef.h>

void overlay_blend_nv12(const struct overlay *o, const uint8_t *source, uint32_t source_stride, uint8_t *luma,
			uint32_t luma_pitch, uint8_t *chroma, uint32_t chroma_pitch, uint32_t width, uint32_t height)
{
	int x0 = o->x > 0 ? o->x : 0, y0 = o->y > 0 ? o->y : 0;
	int x1 = o->x + (int)o->width, y1 = o->y + (int)o->height;

	if (x1 > (int)width) x1 = (int)width;
	if (y1 > (int)height) y1 = (int)height;
	if (x0 >= x1 || y0 >= y1) return;

	for (int y = y0; y < y1; y++) {
		size_t row = (size_t)(y - o->y) * o->width;
		const uint8_t *under = source + (size_t)y * source_stride;
		uint8_t *out = luma + (size_t)y * luma_pitch;

		for (int x = x0; x < x1; x++) {
			size_t at = row + (size_t)(x - o->x);
			unsigned a = o->alpha[at];
			if (a) out[x] = (uint8_t)((o->luma[at] * a + under[x] * (255 - a) + 127) / 255);
		}
	}

	/* One chroma sample stands for a 2x2 block of pixels, and the overlay need not start
	 * on an even one. So each sample is the frame's own, covered by as much of the overlay
	 * as lies over its block: up to four of the overlay's pixels, weighed by coverage. */
	const uint8_t *source_chroma = source + (size_t)source_stride * height;
	int cx1 = (x1 + 1) / 2, cy1 = (y1 + 1) / 2;
	if (cx1 > (int)width / 2) cx1 = (int)width / 2;
	if (cy1 > (int)height / 2) cy1 = (int)height / 2;

	for (int cy = y0 / 2; cy < cy1; cy++) {
		const uint8_t *under = source_chroma + (size_t)cy * source_stride;
		uint8_t *out = chroma + (size_t)cy * chroma_pitch;

		for (int cx = x0 / 2; cx < cx1; cx++) {
			unsigned coverage = 0, cb = 0, cr = 0;

			for (int j = 0; j < 2; j++) {
				int oy = cy * 2 + j - o->y;
				if (oy < 0 || oy >= (int)o->height) continue;
				for (int i = 0; i < 2; i++) {
					int ox = cx * 2 + i - o->x;
					if (ox < 0 || ox >= (int)o->width) continue;
					size_t at = (size_t)oy * o->width + (size_t)ox;
					unsigned a = o->alpha[at];
					coverage += a;
					cb += o->cb[at] * a;
					cr += o->cr[at] * a;
				}
			}
			if (!coverage) continue;

			/* Four pixels of full coverage make 1020. */
			out[cx * 2] = (uint8_t)((cb + under[cx * 2] * (1020 - coverage) + 510) / 1020);
			out[cx * 2 + 1] = (uint8_t)((cr + under[cx * 2 + 1] * (1020 - coverage) + 510) / 1020);
		}
	}
}
