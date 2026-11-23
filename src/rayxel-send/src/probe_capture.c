#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "capture.h"
#include "common.h"
#include "pod_print.h"
#include "probe.h"

#define MAX_INTERVALS 8192

struct trial {
	struct capture *capture;
	uint64_t run_ns;

	struct capture_format format;
	int formats;

	/* Whole-run counts. Buffers flagged as empty are what a renegotiation looks like from
	 * here, so they are kept across it rather than reset with the rest. */
	int corrupted, unmapped;

	/* Everything below is for the format currently in force and starts again when it
	 * changes, so a size renegotiation does not end up averaged into the result. */
	int frames;
	uint32_t coalesced;
	uint64_t first_ns, last_ns;
	double *intervals;
	int interval_count;
	bool have_seq, have_header;
	uint64_t last_seq;
	uint64_t missing;
	int64_t pts;
	uint32_t stride, size;
	unsigned luma_min, luma_max;
	double luma_mean, u_mean, v_mean;

	uint8_t *copy;
	size_t copy_cap;
	double copy_ns;
	int copies;

	char states[256];
};

static void reset_counters(struct trial *t)
{
	t->frames = 0;
	t->coalesced = 0;
	t->first_ns = t->last_ns = 0;
	t->interval_count = 0;
	t->have_seq = t->have_header = false;
	t->missing = 0;
	t->copy_ns = 0;
	t->copies = 0;
}

static void on_state(void *user, const char *state, const char *error)
{
	struct trial *t = user;
	size_t used = strlen(t->states);
	if (used + strlen(state) + 5 < sizeof(t->states))
		snprintf(t->states + used, sizeof(t->states) - used, "%s%s", used ? " > " : "", state);
	if (error) printf("stream error: %s\n", error);
}

static void on_format(void *user, const struct capture_format *f)
{
	struct trial *t = user;

	t->format = *f;
	t->formats++;
	reset_counters(t);

	printf("agreed %d : %s %ux%u, ", t->formats, video_format_name(f->spa_format), f->width, f->height);
	if (f->rate_num == 0) printf("variable rate, ");
	else printf("%u/%u fps, ", f->rate_num, f->rate_den);
	printf("%s %s, %s", color_matrix_name(f->color_matrix), color_range_name(f->color_range),
	       f->dmabuf ? "DMA-BUF" : "shared memory");
	if (f->requested_width) printf(", asked for %ux%u", f->requested_width, f->requested_height);
	printf("\n");
}

/* A coarse look at the picture: enough to tell a real image from a black or grey one. */
static void measure_picture(struct trial *t, const uint8_t *data, uint32_t stride)
{
	uint32_t w = t->format.width, h = t->format.height;
	uint64_t luma = 0, u = 0, v = 0, luma_n = 0, chroma_n = 0;
	unsigned lo = 255, hi = 0;

	for (uint32_t y = 0; y < h; y += 8) {
		const uint8_t *row = data + (size_t)y * stride;
		for (uint32_t x = 0; x < w; x += 8) {
			unsigned s = row[x];
			luma += s;
			luma_n++;
			if (s < lo) lo = s;
			if (s > hi) hi = s;
		}
	}
	const uint8_t *chroma = data + (size_t)h * stride;
	for (uint32_t y = 0; y < h / 2; y += 8) {
		const uint8_t *row = chroma + (size_t)y * stride;
		for (uint32_t x = 0; x + 1 < w; x += 16) {
			u += row[x];
			v += row[x + 1];
			chroma_n++;
		}
	}

	t->luma_min = lo;
	t->luma_max = hi;
	t->luma_mean = luma_n ? (double)luma / (double)luma_n : 0;
	t->u_mean = chroma_n ? (double)u / (double)chroma_n : 0;
	t->v_mean = chroma_n ? (double)v / (double)chroma_n : 0;
}

static void on_frame(void *user, const struct capture_frame *f)
{
	struct trial *t = user;

	t->coalesced += f->coalesced;
	if (f->corrupted) {
		t->corrupted++;
		return;
	}
	if (f->data == NULL) {
		t->unmapped++;
		return;
	}

	if (t->frames > 0 && t->interval_count < MAX_INTERVALS)
		t->intervals[t->interval_count++] = (double)(f->arrival_ns - t->last_ns) / 1e6;
	if (t->frames == 0) t->first_ns = f->arrival_ns;
	t->last_ns = f->arrival_ns;
	t->frames++;

	if (f->has_header) {
		if (t->have_seq && f->seq > t->last_seq + 1) t->missing += f->seq - t->last_seq - 1;
		t->last_seq = f->seq;
		t->have_seq = true;
		t->have_header = true;
		t->pts = f->pts;
	}

	t->stride = f->stride;
	t->size = f->size;

	uint32_t stride = f->stride ? f->stride : t->format.width;
	size_t needed = (size_t)stride * t->format.height + (size_t)stride * ((t->format.height + 1) / 2);
	if (f->size >= needed) {
		if (t->copy_cap < needed) {
			free(t->copy);
			t->copy = malloc(needed);
			t->copy_cap = t->copy ? needed : 0;
		}
		if (t->copy) {
			/* What one pass over a frame costs here - the encoder's upload will be
			 * a copy of exactly this size. */
			uint64_t start = now_ns();
			memcpy(t->copy, f->data, needed);
			t->copy_ns += (double)(now_ns() - start);
			t->copies++;
			if (t->frames == 1 || t->frames % 30 == 0) measure_picture(t, t->copy, stride);
		}
	}

	if (f->arrival_ns - t->first_ns >= t->run_ns) capture_stop(t->capture);
}

static int compare_doubles(const void *a, const void *b)
{
	double x = *(const double *)a, y = *(const double *)b;
	return (x > y) - (x < y);
}

static double percentile(const double *sorted, int count, double p)
{
	if (count == 0) return 0;
	int index = (int)(p * (count - 1) + 0.5);
	return sorted[index < 0 ? 0 : index >= count ? count - 1 : index];
}

bool probe_capture(const struct pw_target *target, uint32_t width, uint32_t height, int seconds,
		   struct trial_summary *summary)
{
	struct trial t = { 0 };
	bool ok = false;

	memset(summary, 0, sizeof(*summary));
	section("trial capture");

	t.intervals = calloc(MAX_INTERVALS, sizeof(*t.intervals));
	if (!t.intervals) return false;
	t.run_ns = (uint64_t)seconds * 1000000000ull;

	if (width && height) printf("asking   : %ux%u, NV12, for %d s\n", width, height, seconds);
	else printf("asking   : the output's own size, NV12, for %d s\n", seconds);

	struct capture_config config = {
		.target_id = target->node_id,
		.target_object = target->serial,
		.width = width,
		.height = height,
		.on_format = on_format,
		.on_frame = on_frame,
		.on_state = on_state,
		.user = &t,
	};
	t.capture = capture_open(&config);
	if (!t.capture) goto out;

	/* The clock for `seconds` starts at the first good frame; the extra covers linking
	 * and the renegotiation a size request causes. */
	int result = capture_run(t.capture, seconds * 1000 + 5000);
	summary->ran = true;

	printf("states   : %s\n", t.states[0] ? t.states : "(none)");
	if (result < 0) printf("failed   : %s\n", capture_error(t.capture));

	if (t.formats == 0) {
		printf("result   : no format was agreed - the stream was never linked to the source\n");
		goto out;
	}
	if (t.frames == 0) {
		printf("result   : a format was agreed but no frame arrived");
		if (t.corrupted) printf(" (%d buffers flagged as carrying no picture)", t.corrupted);
		if (t.unmapped) printf(" (%d buffers could not be read)", t.unmapped);
		printf("\n           gamescope draws when something on screen changes - move something and retry\n");
		goto out;
	}

	double span = (double)(t.last_ns - t.first_ns) / 1e9;
	double fps = span > 0 ? (t.frames - 1) / span : 0;
	printf("frames   : %d in %.2f s = %.2f fps\n", t.frames, span, fps);

	if (t.interval_count > 0) {
		qsort(t.intervals, (size_t)t.interval_count, sizeof(double), compare_doubles);
		printf("interval : p50 %.2f  p95 %.2f  p99 %.2f  max %.2f ms\n",
		       percentile(t.intervals, t.interval_count, 0.50), percentile(t.intervals, t.interval_count, 0.95),
		       percentile(t.intervals, t.interval_count, 0.99), t.intervals[t.interval_count - 1]);
	}

	if (!t.have_header) printf("sequence : the source attached no frame header\n");
	else if (t.missing) printf("sequence : %llu frames missing between the ones received\n", (unsigned long long)t.missing);
	else printf("sequence : no gaps\n");

	printf("skipped  : %d flagged as carrying no picture, %u dropped as stale, %d unreadable\n", t.corrupted,
	       t.coalesced, t.unmapped);

	uint32_t stride = t.stride ? t.stride : t.format.width;
	size_t expected = (size_t)stride * t.format.height + (size_t)stride * ((t.format.height + 1) / 2);
	printf("buffer   : stride %u, %u bytes (%s %zu for NV12 %ux%u)\n", t.stride, t.size,
	       t.size == expected ? "exactly the" : "expected", expected, t.format.width, t.format.height);

	if (t.copies > 0) {
		printf("picture  : luma %u to %u, mean %.0f; chroma mean U %.0f, V %.0f\n", t.luma_min, t.luma_max,
		       t.luma_mean, t.u_mean, t.v_mean);
		printf("copy     : %.3f ms for one pass over a frame\n", t.copy_ns / t.copies / 1e6);
	}

	if (t.have_header) {
		if (t.pts < 0) printf("timestamp: none from the source (pts %lld) - frames are stamped on arrival\n", (long long)t.pts);
		else printf("timestamp: pts %lld\n", (long long)t.pts);
	}

	summary->frames = t.frames;
	summary->fps = fps;
	summary->width = t.format.width;
	summary->height = t.format.height;
	ok = true;

out:
	capture_close(t.capture);
	free(t.copy);
	free(t.intervals);
	return ok;
}
