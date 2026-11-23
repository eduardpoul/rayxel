/*
 * A stand-in for gamescope's PipeWire stream, so the capture side can be exercised on a
 * machine that is not a Steam Deck. It publishes a "gamescope" Video/Source node that
 * behaves the way gamescope's does (see src/pipewire.cpp in ValveSoftware/gamescope):
 * the same four formats, the same private requested-size property, buffers it allocates
 * itself in shared memory, a size renegotiation when a consumer asks for a smaller
 * picture, and buffers flagged as corrupted while that renegotiation is in flight.
 *
 * The picture is a moving gradient. It is a test double, not a compositor.
 */
#define _GNU_SOURCE

#include <errno.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <unistd.h>

#include <pipewire/pipewire.h>
#include <spa/param/video/format-utils.h>
#include <spa/pod/builder.h>

#include "../src/gamescope_pw.h"

struct frame_buffer {
	int fd;
	uint8_t *data;
	size_t size;
	uint32_t stride;
	/* The size the stream had agreed on when this buffer was made, and the size the
	 * picture for it would be drawn at. They differ while a renegotiation is pending. */
	struct spa_rectangle agreed;
	uint32_t picture_width, picture_height;
};

static struct {
	struct pw_main_loop *loop;
	struct pw_stream *stream;
	struct spa_hook listener;
	struct spa_video_info_raw info;
	bool have_format;
	bool streaming;
	uint32_t stride;
	uint64_t seq, tick;
	uint32_t output_width, output_height;
	uint32_t requested_width, requested_height;
	uint32_t capture_width, capture_height;
	uint64_t sent, flagged;
} S;

static void calculate_capture_size(void)
{
	S.capture_width = S.output_width;
	S.capture_height = S.output_height;

	if (S.requested_width > 0 && S.requested_height > 0 &&
	    (S.output_width > S.requested_width || S.output_height > S.requested_height)) {
		float ratio_w = (float)S.requested_width / (float)S.output_width;
		float ratio_h = (float)S.requested_height / (float)S.output_height;
		if (ratio_w <= ratio_h) {
			S.capture_width = S.requested_width;
			S.capture_height = (uint32_t)ceilf(ratio_w * (float)S.output_height);
		} else {
			S.capture_width = (uint32_t)ceilf(ratio_h * (float)S.output_width);
			S.capture_height = S.requested_height;
		}
	}

	S.capture_width = SPA_ROUND_UP_N(S.capture_width, 2);
	S.capture_height = SPA_ROUND_UP_N(S.capture_height, 2);
}

static const struct spa_pod *build_format(struct spa_pod_builder *b, uint32_t format, bool with_modifier)
{
	struct spa_rectangle size = SPA_RECTANGLE(S.capture_width, S.capture_height);
	struct spa_rectangle min_requested = SPA_RECTANGLE(0, 0), max_requested = SPA_RECTANGLE(UINT32_MAX, UINT32_MAX);
	struct spa_fraction framerate = SPA_FRACTION(0, 1);
	struct spa_pod_frame object, choice;

	spa_pod_builder_push_object(b, &object, SPA_TYPE_OBJECT_Format, SPA_PARAM_EnumFormat);
	spa_pod_builder_add(b,
		SPA_FORMAT_mediaType, SPA_POD_Id(SPA_MEDIA_TYPE_video),
		SPA_FORMAT_mediaSubtype, SPA_POD_Id(SPA_MEDIA_SUBTYPE_raw),
		SPA_FORMAT_VIDEO_format, SPA_POD_Id(format),
		SPA_FORMAT_VIDEO_size, SPA_POD_Rectangle(&size),
		SPA_FORMAT_VIDEO_framerate, SPA_POD_Fraction(&framerate),
		GAMESCOPE_FORMAT_VIDEO_requested_size,
			SPA_POD_CHOICE_RANGE_Rectangle(&min_requested, &min_requested, &max_requested),
		GAMESCOPE_FORMAT_VIDEO_focus_appid, SPA_POD_CHOICE_RANGE_Long(0ll, INT64_MIN, INT64_MAX),
		0);
	if (format == SPA_VIDEO_FORMAT_NV12) {
		spa_pod_builder_add(b,
			SPA_FORMAT_VIDEO_colorMatrix, SPA_POD_CHOICE_ENUM_Id(3,
				SPA_VIDEO_COLOR_MATRIX_BT601, SPA_VIDEO_COLOR_MATRIX_BT601, SPA_VIDEO_COLOR_MATRIX_BT709),
			SPA_FORMAT_VIDEO_colorRange, SPA_POD_CHOICE_ENUM_Id(3,
				SPA_VIDEO_COLOR_RANGE_16_235, SPA_VIDEO_COLOR_RANGE_16_235, SPA_VIDEO_COLOR_RANGE_0_255),
			0);
	}
	if (with_modifier) {
		spa_pod_builder_prop(b, SPA_FORMAT_VIDEO_modifier, SPA_POD_PROP_FLAG_MANDATORY);
		spa_pod_builder_push_choice(b, &choice, SPA_CHOICE_Enum, 0);
		spa_pod_builder_long(b, 0); /* DRM_FORMAT_MOD_LINEAR, as the default */
		spa_pod_builder_long(b, 0);
		spa_pod_builder_pop(b, &choice);
	}
	return spa_pod_builder_pop(b, &object);
}

static uint32_t build_formats(struct spa_pod_builder *b, const struct spa_pod **params)
{
	params[0] = build_format(b, SPA_VIDEO_FORMAT_BGRx, true);
	params[1] = build_format(b, SPA_VIDEO_FORMAT_BGRx, false);
	params[2] = build_format(b, SPA_VIDEO_FORMAT_NV12, true);
	params[3] = build_format(b, SPA_VIDEO_FORMAT_NV12, false);
	return 4;
}

static size_t buffer_size(void)
{
	size_t size = (size_t)S.stride * S.info.size.height;
	if (S.info.format == SPA_VIDEO_FORMAT_NV12) size += (size_t)S.stride * ((S.info.size.height + 1) / 2);
	return size;
}

static void on_state_changed(void *data, enum pw_stream_state old, enum pw_stream_state state, const char *error)
{
	printf("fake-gamescope: stream %s%s%s\n", pw_stream_state_as_string(state), error ? ": " : "", error ? error : "");
	switch (state) {
	case PW_STREAM_STATE_PAUSED:
		S.streaming = false;
		S.seq = 0;
		break;
	case PW_STREAM_STATE_STREAMING:
		S.streaming = true;
		break;
	case PW_STREAM_STATE_ERROR:
	case PW_STREAM_STATE_UNCONNECTED:
		pw_main_loop_quit(S.loop);
		break;
	default:
		break;
	}
}

static void on_param_changed(void *data, uint32_t id, const struct spa_pod *param)
{
	if (param == NULL || id != SPA_PARAM_Format) return;

	spa_zero(S.info);
	if (spa_format_video_raw_parse(param, &S.info) < 0) {
		printf("fake-gamescope: could not parse the agreed format\n");
		return;
	}
	S.have_format = true;

	struct spa_rectangle requested = SPA_RECTANGLE(0, 0);
	spa_pod_parse_object(param, SPA_TYPE_OBJECT_Format, NULL,
		GAMESCOPE_FORMAT_VIDEO_requested_size, SPA_POD_OPT_Rectangle(&requested));
	S.requested_width = requested.width;
	S.requested_height = requested.height;
	calculate_capture_size();

	bool dmabuf = spa_pod_find_prop(param, NULL, SPA_FORMAT_VIDEO_modifier) != NULL;
	int bytes_per_pixel = S.info.format == SPA_VIDEO_FORMAT_NV12 ? 1 : 4;
	S.stride = SPA_ROUND_UP_N(S.info.size.width * bytes_per_pixel, 4);

	printf("fake-gamescope: format agreed: %ux%u format %u, asked for %ux%u, capture %ux%u%s\n", S.info.size.width,
	       S.info.size.height, S.info.format, S.requested_width, S.requested_height, S.capture_width,
	       S.capture_height, dmabuf ? " - DMA-BUF, which this stand-in cannot provide" : "");

	uint8_t buffer[1024];
	struct spa_pod_builder b = SPA_POD_BUILDER_INIT(buffer, sizeof(buffer));
	const struct spa_pod *params[3];

	params[0] = spa_pod_builder_add_object(&b,
		SPA_TYPE_OBJECT_ParamBuffers, SPA_PARAM_Buffers,
		SPA_PARAM_BUFFERS_buffers, SPA_POD_CHOICE_RANGE_Int(4, 1, 8),
		SPA_PARAM_BUFFERS_blocks, SPA_POD_Int(1),
		SPA_PARAM_BUFFERS_size, SPA_POD_Int((int)buffer_size()),
		SPA_PARAM_BUFFERS_stride, SPA_POD_Int((int)S.stride),
		SPA_PARAM_BUFFERS_dataType, SPA_POD_CHOICE_FLAGS_Int(dmabuf ? (1 << SPA_DATA_DmaBuf) : (1 << SPA_DATA_MemFd)));
	params[1] = spa_pod_builder_add_object(&b,
		SPA_TYPE_OBJECT_ParamMeta, SPA_PARAM_Meta,
		SPA_PARAM_META_type, SPA_POD_Id(SPA_META_Header),
		SPA_PARAM_META_size, SPA_POD_Int(sizeof(struct spa_meta_header)));
	params[2] = spa_pod_builder_add_object(&b,
		SPA_TYPE_OBJECT_ParamMeta, SPA_PARAM_Meta,
		SPA_PARAM_META_type, SPA_POD_Id(GAMESCOPE_META_requested_size_scale),
		SPA_PARAM_META_size, SPA_POD_Int(sizeof(float)));

	if (pw_stream_update_params(S.stream, params, 3) != 0) printf("fake-gamescope: pw_stream_update_params failed\n");
}

static void on_add_buffer(void *data, struct pw_buffer *buffer)
{
	struct spa_data *d = &buffer->buffer->datas[0];

	if (!(d->type & (1 << SPA_DATA_MemFd))) {
		printf("fake-gamescope: the consumer wants a buffer type other than shared memory (mask 0x%x)\n", d->type);
		d->type = SPA_DATA_Invalid;
		return;
	}

	struct frame_buffer *fb = calloc(1, sizeof(*fb));
	if (!fb) return;
	fb->size = buffer_size();
	fb->stride = S.stride;
	fb->agreed = S.info.size;
	fb->picture_width = S.capture_width;
	fb->picture_height = S.capture_height;
	fb->fd = memfd_create("fake-gamescope", MFD_CLOEXEC);
	if (fb->fd < 0 || ftruncate(fb->fd, (off_t)fb->size) != 0) {
		printf("fake-gamescope: cannot create shared memory: %s\n", strerror(errno));
		if (fb->fd >= 0) close(fb->fd);
		free(fb);
		return;
	}
	fb->data = mmap(NULL, fb->size, PROT_READ | PROT_WRITE, MAP_SHARED, fb->fd, 0);
	if (fb->data == MAP_FAILED) {
		printf("fake-gamescope: mmap failed: %s\n", strerror(errno));
		close(fb->fd);
		free(fb);
		return;
	}

	d->type = SPA_DATA_MemFd;
	d->flags = SPA_DATA_FLAG_READABLE;
	d->fd = fb->fd;
	d->mapoffset = 0;
	d->maxsize = (uint32_t)fb->size;
	d->data = fb->data;
	buffer->user_data = fb;
}

static void on_remove_buffer(void *data, struct pw_buffer *buffer)
{
	struct frame_buffer *fb = buffer->user_data;
	if (!fb) return;
	munmap(fb->data, fb->size);
	close(fb->fd);
	free(fb);
	buffer->user_data = NULL;
}

static const struct pw_stream_events stream_events = {
	PW_VERSION_STREAM_EVENTS,
	.state_changed = on_state_changed,
	.param_changed = on_param_changed,
	.add_buffer = on_add_buffer,
	.remove_buffer = on_remove_buffer,
};

static void draw(struct frame_buffer *fb)
{
	uint32_t w = fb->agreed.width, h = fb->agreed.height;
	uint32_t shift = (uint32_t)(S.tick * 6);

	for (uint32_t y = 0; y < h; y++) {
		uint8_t *row = fb->data + (size_t)y * fb->stride;
		for (uint32_t x = 0; x < w; x++) row[x] = (uint8_t)(16 + (x + y + shift) % 220);
	}
	uint8_t *chroma = fb->data + (size_t)h * fb->stride;
	for (uint32_t y = 0; y < (h + 1) / 2; y++) {
		uint8_t *row = chroma + (size_t)y * fb->stride;
		for (uint32_t x = 0; x + 1 < w; x += 2) {
			row[x] = (uint8_t)(96 + (y * 64) / ((h + 1) / 2));
			row[x + 1] = (uint8_t)(160 - (x * 64) / w);
		}
	}
}

static void on_tick(void *data, uint64_t expirations)
{
	S.tick++;

	/* gamescope notices on its next frame that the size it is capturing at is no longer
	 * the size the stream agreed on, and offers its formats again at the new one. */
	if (S.have_format && (S.capture_width != S.info.size.width || S.capture_height != S.info.size.height)) {
		uint8_t buffer[4096];
		struct spa_pod_builder b = SPA_POD_BUILDER_INIT(buffer, sizeof(buffer));
		const struct spa_pod *params[4];
		uint32_t count = build_formats(&b, params);
		printf("fake-gamescope: renegotiating at %ux%u\n", S.capture_width, S.capture_height);
		if (pw_stream_update_params(S.stream, params, count) < 0)
			printf("fake-gamescope: pw_stream_update_params failed\n");
	}

	if (!S.streaming) return;

	struct pw_buffer *buffer = pw_stream_dequeue_buffer(S.stream);
	if (!buffer) return;
	struct frame_buffer *fb = buffer->user_data;
	if (!fb) {
		pw_stream_queue_buffer(S.stream, buffer);
		return;
	}

	bool stale = fb->agreed.width != fb->picture_width || fb->agreed.height != fb->picture_height;

	struct spa_buffer *sb = buffer->buffer;
	struct spa_meta_header *header = spa_buffer_find_meta_data(sb, SPA_META_Header, sizeof(*header));
	if (header) {
		header->pts = -1;
		header->flags = stale ? SPA_META_HEADER_FLAG_CORRUPTED : 0;
		header->seq = S.seq++;
		header->dts_offset = 0;
	}
	float *scale = spa_buffer_find_meta_data(sb, GAMESCOPE_META_requested_size_scale, sizeof(*scale));
	if (scale) *scale = (float)fb->picture_width / (float)S.output_width;

	struct spa_chunk *chunk = sb->datas[0].chunk;
	chunk->flags = stale ? SPA_CHUNK_FLAG_CORRUPTED : 0;
	chunk->offset = 0;
	chunk->size = (uint32_t)buffer_size();
	chunk->stride = (int32_t)fb->stride;

	if (stale) S.flagged++;
	else draw(fb);
	S.sent++;

	/* gamescope drives the graph simply by queueing, without triggering a cycle itself. */
	if (pw_stream_queue_buffer(S.stream, buffer) < 0) printf("fake-gamescope: pw_stream_queue_buffer failed\n");
}

int main(int argc, char **argv)
{
	unsigned fps = 60;

	setvbuf(stdout, NULL, _IOLBF, 0);
	S.output_width = 1280;
	S.output_height = 800;

	for (int i = 1; i < argc; i++) {
		unsigned w, h;
		if (sscanf(argv[i], "--size=%ux%u", &w, &h) == 2) {
			S.output_width = w;
			S.output_height = h;
		} else if (sscanf(argv[i], "--fps=%u", &fps) == 1) {
			if (fps < 1 || fps > 1000) fps = 60;
		} else {
			fprintf(stderr, "usage: fake-gamescope [--size=WxH] [--fps=N]\n");
			return 2;
		}
	}

	pw_init(&argc, &argv);
	S.loop = pw_main_loop_new(NULL);
	struct pw_loop *loop = pw_main_loop_get_loop(S.loop);
	struct pw_context *context = pw_context_new(loop, NULL, 0);
	struct pw_core *core = context ? pw_context_connect(context, NULL, 0) : NULL;
	if (!core) {
		fprintf(stderr, "fake-gamescope: cannot connect to PipeWire: %s\n", strerror(errno));
		return 1;
	}

	/* gamescope leaves the node name to PipeWire, which takes it from the program's own
	 * name. This program is not called gamescope, so it has to say so. */
	S.stream = pw_stream_new(core, "gamescope",
				 pw_properties_new(PW_KEY_MEDIA_CLASS, "Video/Source", PW_KEY_NODE_NAME, "gamescope", NULL));
	pw_stream_add_listener(S.stream, &S.listener, &stream_events, NULL);
	calculate_capture_size();

	uint8_t buffer[4096];
	struct spa_pod_builder b = SPA_POD_BUILDER_INIT(buffer, sizeof(buffer));
	const struct spa_pod *params[4];
	uint32_t count = build_formats(&b, params);

	if (pw_stream_connect(S.stream, PW_DIRECTION_OUTPUT, PW_ID_ANY,
			      PW_STREAM_FLAG_DRIVER | PW_STREAM_FLAG_ALLOC_BUFFERS, params, count) != 0) {
		fprintf(stderr, "fake-gamescope: pw_stream_connect failed\n");
		return 1;
	}

	struct timespec period = { .tv_sec = 0, .tv_nsec = 1000000000L / (long)fps };
	struct spa_source *timer = pw_loop_add_timer(loop, on_tick, NULL);
	pw_loop_update_timer(loop, timer, &period, &period, false);

	printf("fake-gamescope: output %ux%u at %u fps\n", S.output_width, S.output_height, fps);
	pw_main_loop_run(S.loop);

	printf("fake-gamescope: %llu buffers sent, %llu of them flagged\n", (unsigned long long)S.sent,
	       (unsigned long long)S.flagged);
	pw_stream_destroy(S.stream);
	pw_core_disconnect(core);
	pw_context_destroy(context);
	pw_main_loop_destroy(S.loop);
	pw_deinit();
	return 0;
}
