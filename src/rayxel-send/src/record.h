#pragma once

#include <stdint.h>

struct record_options {
	const char *path;       /* where the stream goes: Annex B for HEVC, OBUs for AV1 */
	uint8_t codec;          /* enum wire_codec */
	const char *source;     /* node name of the video source */
	uint32_t width, height; /* size to ask the source for; 0 keeps its own */
	uint32_t fps;           /* nominal, for rate control */
	uint32_t bitrate_kbps;
	double buffer_frames;
	double max_frame; /* cap on one frame, as a multiple of the average; 0: none */
	uint32_t gop;
	int seconds;
};

/* Captures, encodes and writes for `seconds`, then reports what it cost. 0 on success. */
int record_run(const struct record_options *options);
