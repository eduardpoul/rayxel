#pragma once

#include <stdbool.h>
#include <stdint.h>

struct capture;
struct pw_main_loop;

/* What the source and this program agreed on. Changes mid-stream when the size does. */
struct capture_format {
	uint32_t width, height;
	uint32_t spa_format;         /* SPA_VIDEO_FORMAT_* */
	uint32_t rate_num, rate_den; /* 0/1: frames come when the source has one */
	uint32_t color_matrix;       /* SPA_VIDEO_COLOR_MATRIX_* */
	uint32_t color_range;        /* SPA_VIDEO_COLOR_RANGE_* */
	bool dmabuf;
	uint32_t requested_width, requested_height;
};

struct capture_frame {
	/* NV12: `height` luma rows of `stride` bytes, then the interleaved chroma rows.
	 * Only valid for the duration of the callback. Null when the buffer is not mapped. */
	const uint8_t *data;
	uint32_t size, stride;

	/* Buffers that straddle a size change are flagged by the source and carry no picture. */
	bool corrupted;

	bool has_header;
	uint64_t seq;
	int64_t pts;

	/* When this process was handed the frame, on CLOCK_MONOTONIC. gamescope puts no
	 * timestamp of its own on a frame, so this is the earliest one there is. */
	uint64_t arrival_ns;

	/* Older frames that were already waiting and were dropped in favour of this one. */
	uint32_t coalesced;
};

struct capture_config {
	/* Which node to capture: its id (or PW_ID_ANY, 0xffffffff, when not known) and, as
	 * text, its object.serial or its node name. Either one is enough. */
	uint32_t target_id;
	const char *target_object;

	/* Size to ask gamescope for; 0 leaves it at the output's own size. */
	uint32_t width, height;

	void (*on_format)(void *user, const struct capture_format *format);
	void (*on_frame)(void *user, const struct capture_frame *frame);
	void (*on_state)(void *user, const char *state, const char *error);
	void *user;

	/* Run on this loop instead of one of the capture's own, for a program that has other
	 * things on it. capture_run and capture_stop are then not used, and a capture that
	 * cannot go on reports through on_failed - from inside a PipeWire callback, so the
	 * capture must not be closed there, only later. */
	struct pw_main_loop *loop;
	void (*on_failed)(void *user, const char *error);
};

/* Connects to the source. Null on failure, with the reason printed. */
struct capture *capture_open(const struct capture_config *config);

/* Runs until capture_stop, an error, or the timeout (0: none).
 * Returns 0 when stopped, 1 on timeout, -1 on error. */
int capture_run(struct capture *capture, int timeout_ms);

/* May be called from inside a callback. */
void capture_stop(struct capture *capture);

const char *capture_error(const struct capture *capture);

/*
 * A capture is never closed while pictures are flowing. gamescope (3.16, src/pipewire.cpp)
 * hands its buffers to its compositor thread without a lock, and a consumer that vanishes
 * mid-stream has the buffers freed on one thread while the other is still picking one up.
 * That takes gamescope down, and the whole Steam session and the game in it with it. What
 * it survives is being paused first: it then stops handing buffers out, and once the one
 * in flight has come back there is nothing left to race over.
 *
 * So: capture_pause, then the loop runs for CAPTURE_SETTLE_MS, then capture_close. On a
 * loop of its own capture_close does all three. On a borrowed loop the owner does the
 * first two, because the waiting is the loop's to do and close may be called from inside
 * one of its callbacks.
 *
 * After capture_pause no callback is made again.
 */
#define CAPTURE_SETTLE_MS 200
void capture_pause(struct capture *capture);
void capture_close(struct capture *capture);
