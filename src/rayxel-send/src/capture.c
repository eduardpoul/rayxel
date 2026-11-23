#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <pipewire/pipewire.h>
#include <spa/buffer/meta.h>
#include <spa/param/buffers.h>
#include <spa/param/video/format-utils.h>
#include <spa/pod/builder.h>
#include <spa/utils/result.h>

#include "capture.h"
#include "common.h"
#include "gamescope_pw.h"

struct capture {
	struct capture_config config;
	struct pw_main_loop *loop;
	struct pw_context *context;
	struct pw_core *core;
	struct pw_stream *stream;
	struct spa_hook stream_listener;
	struct spa_source *timer;
	struct capture_format format;
	bool owns_loop;
	bool timed_out;
	char error[256];
};

/* The capture cannot go on. On a loop of its own that ends the run; on a borrowed one the
 * owner is told and decides, because the loop has other work. */
static void failed(struct capture *c, const char *reason)
{
	snprintf(c->error, sizeof(c->error), "%s", reason);
	if (c->owns_loop) pw_main_loop_quit(c->loop);
	else if (c->config.on_failed) c->config.on_failed(c->config.user, c->error);
}

static void on_state_changed(void *data, enum pw_stream_state old, enum pw_stream_state state, const char *error)
{
	struct capture *c = data;

	if (c->config.on_state) c->config.on_state(c->config.user, pw_stream_state_as_string(state), error);

	if (state == PW_STREAM_STATE_ERROR) failed(c, error ? error : "stream error");
	else if (state == PW_STREAM_STATE_UNCONNECTED && old != PW_STREAM_STATE_UNCONNECTED)
		failed(c, "disconnected from PipeWire");
}

static void on_param_changed(void *data, uint32_t id, const struct spa_pod *param)
{
	struct capture *c = data;
	uint32_t media_type, media_subtype;
	struct spa_video_info_raw info;

	if (param == NULL || id != SPA_PARAM_Format) return;
	if (spa_format_parse(param, &media_type, &media_subtype) < 0) return;
	if (media_type != SPA_MEDIA_TYPE_video || media_subtype != SPA_MEDIA_SUBTYPE_raw) return;

	spa_zero(info);
	if (spa_format_video_raw_parse(param, &info) < 0) {
		failed(c, "the negotiated format could not be parsed");
		return;
	}

	/* Through the parser, not spa_pod_get_rectangle: a property that was negotiated is
	 * still wrapped in a one-value choice after fixation, and only the parser unwraps it. */
	struct spa_rectangle requested = SPA_RECTANGLE(0, 0);
	spa_pod_parse_object(param, SPA_TYPE_OBJECT_Format, NULL,
		GAMESCOPE_FORMAT_VIDEO_requested_size, SPA_POD_OPT_Rectangle(&requested));

	c->format = (struct capture_format){
		.width = info.size.width,
		.height = info.size.height,
		.spa_format = info.format,
		.rate_num = info.framerate.num,
		.rate_den = info.framerate.denom,
		.color_matrix = info.color_matrix,
		.color_range = info.color_range,
		/* A modifier in the agreed format is how a source says "DMA-BUF". */
		.dmabuf = spa_pod_find_prop(param, NULL, SPA_FORMAT_VIDEO_modifier) != NULL,
		.requested_width = requested.width,
		.requested_height = requested.height,
	};
	if (c->config.on_format) c->config.on_format(c->config.user, &c->format);

	/* The source allocates the buffers and says how large they are. All this side has
	 * to state is which kinds of memory it can read and that it wants the frame header. */
	uint8_t buffer[512];
	struct spa_pod_builder b = SPA_POD_BUILDER_INIT(buffer, sizeof(buffer));
	const struct spa_pod *params[2];

	params[0] = spa_pod_builder_add_object(&b,
		SPA_TYPE_OBJECT_ParamBuffers, SPA_PARAM_Buffers,
		SPA_PARAM_BUFFERS_dataType, SPA_POD_CHOICE_FLAGS_Int((1 << SPA_DATA_MemFd) | (1 << SPA_DATA_MemPtr)));
	params[1] = spa_pod_builder_add_object(&b,
		SPA_TYPE_OBJECT_ParamMeta, SPA_PARAM_Meta,
		SPA_PARAM_META_type, SPA_POD_Id(SPA_META_Header),
		SPA_PARAM_META_size, SPA_POD_Int(sizeof(struct spa_meta_header)));

	pw_stream_update_params(c->stream, params, 2);
}

static void on_process(void *data)
{
	struct capture *c = data;
	struct pw_buffer *newest = NULL, *next;
	uint32_t coalesced = 0;

	/* If more than one frame is waiting we are already behind, and the oldest of them
	 * is the least worth sending. Take the newest and hand the rest straight back. */
	while ((next = pw_stream_dequeue_buffer(c->stream)) != NULL) {
		if (newest) {
			pw_stream_queue_buffer(c->stream, newest);
			coalesced++;
		}
		newest = next;
	}
	if (!newest) return;

	uint64_t arrival = now_ns();
	struct spa_buffer *buffer = newest->buffer;

	if (buffer->n_datas > 0 && c->config.on_frame) {
		struct spa_data *d = &buffer->datas[0];
		struct spa_meta_header *header = spa_buffer_find_meta_data(buffer, SPA_META_Header, sizeof(*header));
		struct capture_frame frame = {
			.arrival_ns = arrival,
			.coalesced = coalesced,
			.has_header = header != NULL,
			.seq = header ? header->seq : 0,
			.pts = header ? header->pts : -1,
		};

		if (d->chunk) {
			frame.size = d->chunk->size;
			frame.stride = d->chunk->stride > 0 ? (uint32_t)d->chunk->stride : 0;
			frame.corrupted = (d->chunk->flags & SPA_CHUNK_FLAG_CORRUPTED) != 0;
			if (d->data && d->chunk->offset <= d->maxsize && frame.size <= d->maxsize - d->chunk->offset)
				frame.data = SPA_PTROFF(d->data, d->chunk->offset, const uint8_t);
		}
		if (header && (header->flags & SPA_META_HEADER_FLAG_CORRUPTED)) frame.corrupted = true;

		c->config.on_frame(c->config.user, &frame);
	}

	pw_stream_queue_buffer(c->stream, newest);
}

static const struct pw_stream_events stream_events = {
	PW_VERSION_STREAM_EVENTS,
	.state_changed = on_state_changed,
	.param_changed = on_param_changed,
	.process = on_process,
};

static void on_timeout(void *data, uint64_t expirations)
{
	struct capture *c = data;
	c->timed_out = true;
	pw_main_loop_quit(c->loop);
}

struct capture *capture_open(const struct capture_config *config)
{
	struct capture *c = calloc(1, sizeof(*c));
	if (!c) return NULL;
	c->config = *config;
	c->config.target_object = NULL; /* only used below; the caller's string need not outlive this call */

	c->owns_loop = config->loop == NULL;
	c->loop = config->loop ? config->loop : pw_main_loop_new(NULL);
	if (!c->loop) goto fail;
	struct pw_loop *loop = pw_main_loop_get_loop(c->loop);

	c->context = pw_context_new(loop, NULL, 0);
	c->core = c->context ? pw_context_connect(c->context, NULL, 0) : NULL;
	if (!c->core) {
		fprintf(stderr, "capture: cannot connect to PipeWire: %s\n", strerror(errno));
		goto fail;
	}

	struct pw_properties *props = pw_properties_new(
		PW_KEY_MEDIA_TYPE, "Video",
		PW_KEY_MEDIA_CATEGORY, "Capture",
		PW_KEY_MEDIA_ROLE, "Screen",
		/* If the source goes away, stay unlinked rather than be moved to a webcam. */
		PW_KEY_NODE_DONT_RECONNECT, "true",
		NULL);
	if (config->target_object && config->target_object[0])
		pw_properties_set(props, PW_KEY_TARGET_OBJECT, config->target_object);

	c->stream = pw_stream_new(c->core, "rayxel-send", props);
	if (!c->stream) {
		fprintf(stderr, "capture: cannot create a stream: %s\n", strerror(errno));
		goto fail;
	}
	pw_stream_add_listener(c->stream, &c->stream_listener, &stream_events, c);

	/*
	 * What we will take: NV12 through shared memory, at whatever size and rate the
	 * source has. No modifier is offered, and that is what selects shared memory -
	 * gamescope's DMA-BUF variants make the modifier mandatory and so do not match.
	 * BT.709 limited is asked for because that is what an HEVC decoder assumes for HD
	 * when the stream says nothing; gamescope's own default is BT.601.
	 */
	uint8_t buffer[1024];
	struct spa_pod_builder b = SPA_POD_BUILDER_INIT(buffer, sizeof(buffer));
	struct spa_pod_frame frame;
	struct spa_rectangle size_default = SPA_RECTANGLE(config->width ? config->width : 1280,
							  config->height ? config->height : 800);
	struct spa_rectangle size_min = SPA_RECTANGLE(1, 1), size_max = SPA_RECTANGLE(16384, 16384);
	struct spa_fraction rate_default = SPA_FRACTION(0, 1), rate_min = SPA_FRACTION(0, 1),
			    rate_max = SPA_FRACTION(1000, 1);

	spa_pod_builder_push_object(&b, &frame, SPA_TYPE_OBJECT_Format, SPA_PARAM_EnumFormat);
	spa_pod_builder_add(&b,
		SPA_FORMAT_mediaType, SPA_POD_Id(SPA_MEDIA_TYPE_video),
		SPA_FORMAT_mediaSubtype, SPA_POD_Id(SPA_MEDIA_SUBTYPE_raw),
		SPA_FORMAT_VIDEO_format, SPA_POD_Id(SPA_VIDEO_FORMAT_NV12),
		SPA_FORMAT_VIDEO_size, SPA_POD_CHOICE_RANGE_Rectangle(&size_default, &size_min, &size_max),
		SPA_FORMAT_VIDEO_framerate, SPA_POD_CHOICE_RANGE_Fraction(&rate_default, &rate_min, &rate_max),
		SPA_FORMAT_VIDEO_colorMatrix, SPA_POD_Id(SPA_VIDEO_COLOR_MATRIX_BT709),
		SPA_FORMAT_VIDEO_colorRange, SPA_POD_Id(SPA_VIDEO_COLOR_RANGE_16_235),
		0);
	if (config->width && config->height) {
		struct spa_rectangle requested = SPA_RECTANGLE(config->width, config->height);
		spa_pod_builder_add(&b, GAMESCOPE_FORMAT_VIDEO_requested_size, SPA_POD_Rectangle(&requested), 0);
	}
	const struct spa_pod *params[1] = { spa_pod_builder_pop(&b, &frame) };

	/*
	 * The node id, when there is one, goes in as the target the way screen-capture
	 * clients pass theirs, which every session manager honours; the target.object set
	 * above is the current way to say the same thing and also takes a node name. Frames
	 * are delivered on the main loop: one thread hop more than the realtime callback,
	 * in exchange for an encoder that may block without stalling PipeWire's data thread.
	 */
	int res = pw_stream_connect(c->stream, PW_DIRECTION_INPUT, config->target_id,
				    PW_STREAM_FLAG_AUTOCONNECT | PW_STREAM_FLAG_MAP_BUFFERS, params, 1);
	if (res < 0) {
		fprintf(stderr, "capture: cannot connect the stream: %s\n", spa_strerror(res));
		goto fail;
	}

	if (c->owns_loop) c->timer = pw_loop_add_timer(loop, on_timeout, c);
	return c;

fail:
	capture_close(c);
	return NULL;
}

int capture_run(struct capture *c, int timeout_ms)
{
	struct pw_loop *loop = pw_main_loop_get_loop(c->loop);

	c->timed_out = false;
	if (timeout_ms > 0) {
		struct timespec limit = { .tv_sec = timeout_ms / 1000, .tv_nsec = (long)(timeout_ms % 1000) * 1000000L };
		pw_loop_update_timer(loop, c->timer, &limit, NULL, false);
	}
	pw_main_loop_run(c->loop);
	pw_loop_update_timer(loop, c->timer, NULL, NULL, false);

	if (c->error[0]) return -1;
	return c->timed_out ? 1 : 0;
}

void capture_stop(struct capture *c)
{
	pw_main_loop_quit(c->loop);
}

const char *capture_error(const struct capture *c)
{
	return c->error;
}

void capture_close(struct capture *c)
{
	if (!c) return;
	if (c->stream) pw_stream_destroy(c->stream);
	if (c->timer) pw_loop_destroy_source(pw_main_loop_get_loop(c->loop), c->timer);
	if (c->core) pw_core_disconnect(c->core);
	if (c->context) pw_context_destroy(c->context);
	if (c->loop && c->owns_loop) pw_main_loop_destroy(c->loop);
	free(c);
}
