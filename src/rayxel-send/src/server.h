#pragma once

#include <stdbool.h>
#include <stdint.h>

struct server_options {
	uint16_t port;
	const char *source;    /* node name of the video source */
	double buffer_frames;  /* rate control slack, in frames */
	double max_frame;      /* cap on one frame, as a multiple of the average; 0: none */
	uint32_t gop;          /* a keyframe every N frames; 0: only when asked */
	bool allow_input;      /* let the viewer drive a virtual gamepad on this machine */

	/* A test source in place of the capture: raw NV12 pictures of this size, one after
	 * another in one file, sent in a loop at 60 frames a second. NULL: capture. */
	const char *frames_file;
	uint32_t frames_width, frames_height;
};

/* Waits for a viewer and streams to it until interrupted. 0 on a clean exit. */
int server_run(const struct server_options *options);
