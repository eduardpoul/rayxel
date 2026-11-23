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
 * Stopping a capture is dangerous to the source, and is done as seldom as possible.
 *
 * gamescope (3.16, src/pipewire.cpp) takes a buffer from its PipeWire stream on its
 * compositor thread, once for every frame it draws, with no lock against the thread the
 * stream itself runs on. When the stream leaves its streaming state while a frame is being
 * drawn, the two collide over the stream's buffers, and gamescope aborts in destroy_buffer
 * - taking the whole Steam session down, and the game in it. It happens at the moment the
 * consumer stops, whether it stops by pausing or by disconnecting, and only when a frame
 * happens to be on its way just then: rarely on an idle screen, once in some tens of times
 * under a game drawing 60 frames a second.
 *
 * Nothing done from this side makes that moment safe. Pausing before closing was tried as
 * a cure and is not one: gamescope has since been seen to die on the pause itself. So the
 * owner of a capture should keep it for as long as it can, and the server does - see
 * server.c - which leaves the moment to come once, when the program ends.
 *
 * When a capture does have to go: capture_pause, then the loop runs for CAPTURE_SETTLE_MS,
 * then capture_close, so that at least the disconnect finds no frame in flight. On a loop
 * of its own capture_close does all three. On a borrowed loop the owner does the first
 * two, because the waiting is the loop's to do and close may be called from inside one of
 * its callbacks.
 *
 * After capture_pause no callback is made again.
 */
#define CAPTURE_SETTLE_MS 200
void capture_pause(struct capture *capture);
void capture_close(struct capture *capture);

/* Whether pictures can arrive: connected to the source and not paused. A capture that has
 * been kept while the source went away and came back is not, and is no use any more. */
bool capture_streaming(const struct capture *capture);
