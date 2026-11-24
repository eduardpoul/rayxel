#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <spa/param/video/color.h>

#include "capture.h"
#include "common.h"
#include "encoder.h"
#include "pod_print.h"
#include "record.h"

#define MAX_SAMPLES 65536

struct series {
	double *values;
	int count;
};

struct recorder {
	const struct record_options *options;
	struct capture *capture;
	struct encoder *encoder;
	FILE *file;
	uint64_t run_ns;
	bool failed;

	struct capture_format format;
	int formats;
	bool format_changed;

	int frames, keyframes, skipped;
	uint32_t stale;
	uint64_t first_ns, last_ns;
	uint64_t bytes;
	size_t largest_keyframe;
	uint64_t nal_units[64];

	struct series upload_ms, encode_ms, total_ms, size_kb;
};

static void series_add(struct series *s, double value)
{
	if (s->count < MAX_SAMPLES) s->values[s->count++] = value;
}

static int compare_doubles(const void *a, const void *b)
{
	double x = *(const double *)a, y = *(const double *)b;
	return (x > y) - (x < y);
}

static void series_print(const char *label, struct series *s, const char *unit)
{
	if (s->count == 0) return;
	qsort(s->values, (size_t)s->count, sizeof(double), compare_doubles);
#define AT(p) s->values[(int)((p) * (s->count - 1) + 0.5)]
	printf("%s: p50 %.2f  p95 %.2f  p99 %.2f  max %.2f %s\n", label, AT(0.50), AT(0.95), AT(0.99),
	       s->values[s->count - 1], unit);
#undef AT
}

/* Counts the NAL units in an Annex B buffer by type. The type is six bits of the byte
 * after a 00 00 01 start code. */
static void count_nal_units(struct recorder *r, const uint8_t *data, size_t size)
{
	for (size_t i = 0; i + 3 < size; i++) {
		if (data[i] != 0 || data[i + 1] != 0 || data[i + 2] != 1) continue;
		r->nal_units[(data[i + 3] >> 1) & 0x3f]++;
		i += 3;
	}
}

static const char *nal_name(int type)
{
	switch (type) {
	case 0: return "TRAIL_N";
	case 1: return "P slices";
	case 19: return "IDR";
	case 20: return "IDR_N_LP";
	case 21: return "CRA";
	case 32: return "VPS";
	case 33: return "SPS";
	case 34: return "PPS";
	case 35: return "AUD";
	case 38: return "filler";
	case 39: return "SEI";
	case 40: return "SEI suffix";
	default: return NULL;
	}
}

static void on_state(void *user, const char *state, const char *error)
{
	if (error) printf("stream error: %s\n", error);
}

static void on_format(void *user, const struct capture_format *f)
{
	struct recorder *r = user;

	/* Only noted here. When a size was asked for, the source first agrees on the size it
	 * already had and then on the new one; the encoder is built for whichever format the
	 * first real picture arrives in. */
	r->format = *f;
	r->formats++;
	r->format_changed = true;
}

/* A new size is a new stream: the encoder is rebuilt and starts with a keyframe. */
static bool open_encoder(struct recorder *r)
{
	const struct capture_format *f = &r->format;

	printf("capture  : %s %ux%u, %s %s, %s\n", video_format_name(f->spa_format), f->width, f->height,
	       color_matrix_name(f->color_matrix), color_range_name(f->color_range), f->dmabuf ? "DMA-BUF" : "shared memory");

	encoder_close(r->encoder);
	struct encoder_config config = {
		.codec = r->options->codec,
		.width = f->width,
		.height = f->height,
		.fps = r->options->fps,
		.bitrate_kbps = r->options->bitrate_kbps,
		.buffer_frames = r->options->buffer_frames,
		.max_frame_bytes = r->options->max_frame > 0
			? (uint32_t)((double)r->options->bitrate_kbps * 125.0 / (double)r->options->fps * r->options->max_frame) : 0,
		.gop = r->options->gop,
		.bt601 = f->color_matrix == SPA_VIDEO_COLOR_MATRIX_BT601,
		.full_range = f->color_range == SPA_VIDEO_COLOR_RANGE_0_255,
	};
	char why[256] = "";
	r->encoder = encoder_open(&config, why, sizeof(why));
	r->format_changed = false;
	if (!r->encoder) {
		printf("encoder  : %s cannot be had: %s\n", encoder_codec_name(config.codec), why[0] ? why : "no reason given");
		r->failed = true;
		capture_stop(r->capture);
		return false;
	}
	printf("encoder  : %s\n", encoder_describe(r->encoder));
	return true;
}

static void on_frame(void *user, const struct capture_frame *f)
{
	struct recorder *r = user;
	struct encoded_frame out;

	r->stale += f->coalesced;
	if (r->failed) return;
	if (f->corrupted || f->data == NULL) {
		r->skipped++;
		return;
	}
	if (r->format_changed && !open_encoder(r)) return;
	if (!r->encoder) return;

	uint32_t stride = f->stride ? f->stride : r->format.width;
	size_t needed = (size_t)stride * r->format.height + (size_t)stride * (r->format.height / 2);
	if (f->size < needed) {
		r->skipped++;
		return;
	}

	if (!encoder_encode(r->encoder, f->data, stride, false, NULL, &out)) {
		printf("encoding failed: %s\n", encoder_error(r->encoder));
		r->failed = true;
		capture_stop(r->capture);
		return;
	}
	uint64_t done = now_ns();

	/* Everything from here on is bookkeeping and is not part of what is measured. */
	if (r->frames == 0) r->first_ns = f->arrival_ns;
	r->last_ns = f->arrival_ns;
	r->frames++;
	r->bytes += out.size;
	if (out.keyframe) {
		r->keyframes++;
		if (out.size > r->largest_keyframe) r->largest_keyframe = out.size;
	} else {
		series_add(&r->size_kb, (double)out.size / 1024.0);
	}
	series_add(&r->upload_ms, (double)out.upload_ns / 1e6);
	series_add(&r->encode_ms, (double)out.encode_ns / 1e6);
	series_add(&r->total_ms, (double)(done - f->arrival_ns) / 1e6);
	if (r->options->codec == WIRE_CODEC_HEVC) count_nal_units(r, out.data, out.size);

	if (r->file && fwrite(out.data, 1, out.size, r->file) != out.size) {
		printf("cannot write %s: %s\n", r->options->path, strerror(errno));
		r->failed = true;
		capture_stop(r->capture);
		return;
	}

	if (f->arrival_ns - r->first_ns >= r->run_ns) capture_stop(r->capture);
}

int record_run(const struct record_options *o)
{
	struct recorder r = { .options = o, .run_ns = (uint64_t)o->seconds * 1000000000ull };
	int status = 1;

	section("record");
	printf("source   : %s\n", o->source);

	r.upload_ms.values = calloc(MAX_SAMPLES, sizeof(double));
	r.encode_ms.values = calloc(MAX_SAMPLES, sizeof(double));
	r.total_ms.values = calloc(MAX_SAMPLES, sizeof(double));
	r.size_kb.values = calloc(MAX_SAMPLES, sizeof(double));
	if (!r.upload_ms.values || !r.encode_ms.values || !r.total_ms.values || !r.size_kb.values) goto out;

	if (o->path) {
		r.file = fopen(o->path, "wb");
		if (!r.file) {
			printf("cannot create %s: %s\n", o->path, strerror(errno));
			goto out;
		}
	}

	struct capture_config config = {
		.target_id = 0xffffffffu, /* PW_ID_ANY: found by name */
		.target_object = o->source,
		.width = o->width,
		.height = o->height,
		.on_format = on_format,
		.on_frame = on_frame,
		.on_state = on_state,
		.user = &r,
	};
	r.capture = capture_open(&config);
	if (!r.capture) goto out;

	int result = capture_run(r.capture, o->seconds * 1000 + 5000);
	if (result < 0) printf("capture failed: %s\n", capture_error(r.capture));

	if (r.formats == 0) {
		printf("result   : nothing was captured - is there a video source named '%s'?\n", o->source);
		goto out;
	}
	if (r.frames == 0) {
		if (!r.failed) printf("result   : no frame arrived - gamescope draws only when something on screen changes\n");
		goto out;
	}

	double span = (double)(r.last_ns - r.first_ns) / 1e9;
	double fps = span > 0 ? (r.frames - 1) / span : 0;
	printf("frames   : %d in %.2f s = %.2f fps, %d keyframe%s; %d skipped, %u dropped as stale\n", r.frames, span, fps,
	       r.keyframes, r.keyframes == 1 ? "" : "s", r.skipped, r.stale);
	if (span > 0)
		printf("bitrate  : %.2f Mbit/s produced, %.2f asked for at %u fps\n", (double)r.bytes * 8.0 / span / 1e6,
		       o->bitrate_kbps / 1000.0, o->fps);
	series_print("frame    ", &r.size_kb, "kB (keyframes apart)");
	printf("keyframe : largest %.1f kB\n", (double)r.largest_keyframe / 1024.0);
	series_print("upload   ", &r.upload_ms, "ms");
	series_print("encode   ", &r.encode_ms, "ms");
	series_print("in hand  ", &r.total_ms, "ms after the frame reached this process");

	if (o->codec == WIRE_CODEC_HEVC) {
		printf("stream   :");
		bool first = true;
		for (int type = 0; type < 64; type++) {
			if (!r.nal_units[type]) continue;
			const char *name = nal_name(type);
			if (name) printf("%s %llu %s", first ? "" : ",", (unsigned long long)r.nal_units[type], name);
			else printf("%s %llu of type %d", first ? "" : ",", (unsigned long long)r.nal_units[type], type);
			first = false;
		}
		printf("\n");
	}
	if (o->path) printf("file     : %s, %.2f MB\n", o->path, (double)r.bytes / 1048576.0);

	status = r.failed ? 1 : 0;

out:
	capture_close(r.capture);
	encoder_close(r.encoder);
	if (r.file) fclose(r.file);
	free(r.upload_ms.values);
	free(r.encode_ms.values);
	free(r.total_ms.values);
	free(r.size_kb.values);
	return status;
}
