/*
 * Measures what encoding a frame costs with PyroWave, against the hardware HEVC encoder
 * the sender uses now, on frames read from a file.
 *
 * Frames come from memory rather than from gamescope on purpose: closing a capture can take
 * gamescope down, and the game with it (see capture.h). Everything else is as in the
 * sender - NV12 from CPU memory, one frame at a time, paced at the frame rate.
 *
 *   pyrowave-bench --frames=FILE --size=WxH --codec=pyrowave|hevc [--mbit=N] [--fps=N]
 *                  [--seconds=N] [--queue=compute|graphics] [--dump=FILE]
 *   pyrowave-bench --stress --size=WxH [--seconds=N] [--queue=...]
 *
 * --stress encodes as fast as it can and prints how many frames a second it managed: a
 * stand-in for a game that has the GPU fully busy, to see what the paced encoder takes from
 * it and how its own timing holds up.
 */
#define _GNU_SOURCE

#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/resource.h>
#include <time.h>

#include <vulkan/vulkan.h>

#include "pyrowave.h"

#include "common.h"
#include "encoder.h"

#define PACKET_BYTES 1200
#define WARMUP_FRAMES 60
#define MAX_SAMPLES 100000

struct options {
	const char *frames, *codec, *queue, *dump;
	uint32_t width, height, fps;
	double mbit, seconds;
	int stress;
};

struct series {
	double *v;
	int n;
};

static void add(struct series *s, double value)
{
	if (s->n < MAX_SAMPLES) s->v[s->n++] = value;
}

static int compare(const void *a, const void *b)
{
	double x = *(const double *)a, y = *(const double *)b;
	return (x > y) - (x < y);
}

static void print_series(const char *label, struct series *s, const char *note)
{
	if (s->n == 0) return;
	qsort(s->v, (size_t)s->n, sizeof(double), compare);
#define AT(p) s->v[(int)((p) * (s->n - 1) + 0.5)]
	printf("%-10s: p50 %6.2f  p95 %6.2f  p99 %6.2f  max %6.2f   %s\n", label, AT(0.50), AT(0.95), AT(0.99),
	       s->v[s->n - 1], note);
#undef AT
}

static double cpu_ms(void)
{
	struct rusage u;
	getrusage(RUSAGE_SELF, &u);
	return (double)(u.ru_utime.tv_sec + u.ru_stime.tv_sec) * 1e3 +
	       (double)(u.ru_utime.tv_usec + u.ru_stime.tv_usec) / 1e3;
}

static void sleep_until(uint64_t ns)
{
	struct timespec ts = { .tv_sec = (time_t)(ns / 1000000000ull), .tv_nsec = (long)(ns % 1000000000ull) };
	while (clock_nanosleep(CLOCK_MONOTONIC, TIMER_ABSTIME, &ts, NULL) == EINTR) {
	}
}

/* NV12 frames from a file, or made up for --stress: a pattern with fine detail, so the
 * rate control has real work to do. */
static uint8_t *load_frames(const struct options *o, int *count)
{
	size_t frame = (size_t)o->width * o->height * 3 / 2;

	if (!o->frames) {
		*count = 4;
		uint8_t *data = malloc(frame * 4);
		if (!data) return NULL;
		uint32_t seed = 1;
		for (size_t i = 0; i < frame * 4; i++) {
			seed = seed * 1664525u + 1013904223u;
			data[i] = (uint8_t)(64 + ((i % o->width) ^ (i / o->width)) % 96 + (seed >> 28));
		}
		return data;
	}

	FILE *f = fopen(o->frames, "rb");
	if (!f) {
		printf("cannot open %s: %s\n", o->frames, strerror(errno));
		return NULL;
	}
	fseek(f, 0, SEEK_END);
	long size = ftell(f);
	fseek(f, 0, SEEK_SET);
	*count = (int)(size / (long)frame);
	if (*count == 0) {
		printf("%s holds no whole %ux%u NV12 frame\n", o->frames, o->width, o->height);
		fclose(f);
		return NULL;
	}
	uint8_t *data = malloc(frame * (size_t)*count);
	if (!data || fread(data, frame, (size_t)*count, f) != (size_t)*count) {
		printf("cannot read %s\n", o->frames);
		free(data);
		data = NULL;
	}
	fclose(f);
	return data;
}

static void on_stats(void *user, const char *message)
{
	printf("  gpu     : %s\n", message);
}

static void ignore(void *user, const char *message)
{
}

/* ---------------------------------------------------------------- PyroWave */

struct pw {
	pyrowave_device device;
	pyrowave_encoder encoder;
	pyrowave_packet *packets;
	size_t packet_cap;
	uint8_t *bitstream;
	size_t bitstream_cap;
	size_t cap_bytes;
};

static int pw_open(struct pw *p, const struct options *o)
{
	if (pyrowave_create_default_device(&p->device) != PYROWAVE_SUCCESS) {
		printf("PyroWave could not start Vulkan on this machine\n");
		return -1;
	}
	VkQueueFlagBits queue = strcmp(o->queue, "graphics") == 0 ? VK_QUEUE_GRAPHICS_BIT : VK_QUEUE_COMPUTE_BIT;
	if (pyrowave_device_set_queue_type(p->device, queue) != PYROWAVE_SUCCESS)
		printf("note: the %s queue was refused; the default is used\n", o->queue);

	pyrowave_encoder_create_info info = {
		.device = p->device,
		.width = (int)o->width,
		.height = (int)o->height,
		.chroma = PYROWAVE_CHROMA_SUBSAMPLING_420,
	};
	if (pyrowave_encoder_create(&info, &p->encoder) != PYROWAVE_SUCCESS) {
		printf("PyroWave would not create an encoder for %ux%u\n", o->width, o->height);
		return -1;
	}

	p->cap_bytes = (size_t)(o->mbit * 1e6 / 8.0 / o->fps);
	p->bitstream_cap = p->cap_bytes + (1u << 20);
	p->bitstream = malloc(p->bitstream_cap);
	p->packet_cap = p->bitstream_cap / 64;
	p->packets = calloc(p->packet_cap, sizeof(*p->packets));
	return p->bitstream && p->packets ? 0 : -1;
}

/* One frame: hand it over, wait for the GPU, cut the result into network packets.
 * Returns the encoded size, or 0 on failure. */
static size_t pw_encode(struct pw *p, const struct options *o, uint8_t *nv12, uint64_t *submitted, uint64_t *done_gpu,
			size_t *packets)
{
	pyrowave_cpu_buffer buffer = { 0 };
	buffer.data[0] = nv12;
	buffer.data[1] = nv12 + (size_t)o->width * o->height;
	buffer.data[2] = buffer.data[1];
	buffer.row_stride_in_bytes[0] = o->width;
	buffer.row_stride_in_bytes[1] = o->width;
	buffer.plane_size_in_bytes[0] = (size_t)o->width * o->height;
	buffer.plane_size_in_bytes[1] = (size_t)o->width * o->height / 2;
	buffer.width = (int)o->width;
	buffer.height = (int)o->height;
	buffer.format = PYROWAVE_CPU_BUFFER_FORMAT_NV12;

	pyrowave_rate_control rate = { .maximum_bitstream_size = p->cap_bytes };
	if (pyrowave_encoder_encode_cpu(p->encoder, &buffer, &rate) != PYROWAVE_SUCCESS) return 0;
	*submitted = now_ns();

	size_t count = 0;
	if (pyrowave_encoder_compute_num_packets(p->encoder, PACKET_BYTES, &count) != PYROWAVE_SUCCESS) return 0;
	*done_gpu = now_ns();
	if (count > p->packet_cap) return 0;

	size_t out = 0;
	if (pyrowave_encoder_packetize(p->encoder, p->packets, PACKET_BYTES, &out, p->bitstream, p->bitstream_cap) !=
	    PYROWAVE_SUCCESS)
		return 0;
	*packets = out;

	size_t total = 0;
	for (size_t i = 0; i < out; i++) total += p->packets[i].size;
	return total;
}

/* Decodes the frames kept from the run and writes them as raw YUV 4:2:0, for a quality
 * comparison against the source. Done after the timed part, so it cannot disturb it. */
static void pw_dump(struct pw *p, const struct options *o, uint8_t **frames, size_t **offsets, size_t *counts,
		    int n)
{
	pyrowave_decoder_create_info info = {
		.device = p->device,
		.width = (int)o->width,
		.height = (int)o->height,
		.chroma = PYROWAVE_CHROMA_SUBSAMPLING_420,
	};
	pyrowave_decoder decoder;
	if (pyrowave_decoder_create(&info, &decoder) != PYROWAVE_SUCCESS) {
		printf("PyroWave would not create a decoder\n");
		return;
	}

	size_t luma = (size_t)o->width * o->height, chroma = luma / 4;
	uint8_t *picture = malloc(luma + 2 * chroma);
	FILE *f = fopen(o->dump, "wb");
	int written = 0;

	for (int i = 0; i < n && picture && f; i++) {
		for (size_t k = 0; k < counts[i]; k++)
			pyrowave_decoder_push_packet(decoder, frames[i] + offsets[i][2 * k], offsets[i][2 * k + 1]);
		if (!pyrowave_decoder_decode_is_ready(decoder, false)) {
			printf("frame %d was not complete after all its packets\n", i);
			continue;
		}
		pyrowave_cpu_buffer out = { 0 };
		out.data[0] = picture;
		out.data[1] = picture + luma;
		out.data[2] = picture + luma + chroma;
		out.row_stride_in_bytes[0] = o->width;
		out.row_stride_in_bytes[1] = out.row_stride_in_bytes[2] = o->width / 2;
		out.plane_size_in_bytes[0] = luma;
		out.plane_size_in_bytes[1] = out.plane_size_in_bytes[2] = chroma;
		out.width = (int)o->width;
		out.height = (int)o->height;
		out.format = PYROWAVE_CPU_BUFFER_FORMAT_YUV420P;
		if (pyrowave_decoder_decode_cpu_buffer_synchronous(decoder, &out) != PYROWAVE_SUCCESS) {
			printf("frame %d did not decode\n", i);
			continue;
		}
		fwrite(picture, 1, luma + 2 * chroma, f);
		written++;
	}
	printf("dump      : %d decoded frames in %s\n", written, o->dump);
	if (f) fclose(f);
	free(picture);
	pyrowave_decoder_destroy(decoder);
}

/* ---------------------------------------------------------------- the run */

static int run(const struct options *o)
{
	int count = 0;
	uint8_t *frames = load_frames(o, &count);
	if (!frames) return 1;
	size_t frame_bytes = (size_t)o->width * o->height * 3 / 2;
	int pyrowave = strcmp(o->codec, "pyrowave") == 0;

	struct pw p = { 0 };
	struct encoder *hevc = NULL;
	if (pyrowave) {
		if (pw_open(&p, o) != 0) return 1;
		printf("codec     : PyroWave %ux%u 4:2:0, at most %zu bytes a frame (%.0f Mbit/s at %u fps), %s queue\n",
		       o->width, o->height, p.cap_bytes, o->mbit, o->fps, o->queue);
	} else {
		struct encoder_config config = {
			.width = o->width,
			.height = o->height,
			.fps = o->fps,
			.bitrate_kbps = (uint32_t)(o->mbit * 1000),
			.buffer_frames = 2,
		};
		char why[256] = "";
		hevc = encoder_open(&config, why, sizeof(why));
		if (!hevc) {
			printf("HEVC cannot be had: %s\n", why);
			return 1;
		}
		printf("codec     : %s\n", encoder_describe(hevc));
	}

	struct series total = { calloc(MAX_SAMPLES, sizeof(double)) }, submit = { calloc(MAX_SAMPLES, sizeof(double)) },
		      wait = { calloc(MAX_SAMPLES, sizeof(double)) }, pack = { calloc(MAX_SAMPLES, sizeof(double)) },
		      size_kb = { calloc(MAX_SAMPLES, sizeof(double)) };

	/* What the quality check needs: the encoded frames of the first pass through the clip. */
	int keep = o->dump ? (count < 600 ? count : 600) : 0;
	FILE *hevc_dump = o->dump && !pyrowave ? fopen(o->dump, "wb") : NULL;
	uint8_t **kept = keep && pyrowave ? calloc((size_t)keep, sizeof(*kept)) : NULL;
	size_t **kept_offsets = keep && pyrowave ? calloc((size_t)keep, sizeof(*kept_offsets)) : NULL;
	size_t *kept_counts = keep && pyrowave ? calloc((size_t)keep, sizeof(*kept_counts)) : NULL;
	int kept_n = 0;

	uint64_t period = o->stress ? 0 : 1000000000ull / o->fps;
	uint64_t start = now_ns(), end = start + (uint64_t)(o->seconds * 1e9), next_report = start + 1000000000ull;
	uint64_t measured_from = 0;
	double cpu_start = 0;
	long frames_done = 0, measured = 0, failed = 0, report_frames = 0;

	for (long i = 0;; i++) {
		uint64_t due = start + (uint64_t)i * period;
		if (period) sleep_until(due);
		uint64_t t0 = now_ns();
		if (t0 >= end) break;

		uint8_t *nv12 = frames + frame_bytes * (size_t)(i % count);
		size_t bytes = 0, packets = 0;
		uint64_t t1 = 0, t2 = 0, t3;

		if (pyrowave) {
			bytes = pw_encode(&p, o, nv12, &t1, &t2, &packets);
			t3 = now_ns();
		} else {
			struct encoded_frame out;
			if (encoder_encode(hevc, nv12, o->width, false, NULL, &out)) {
				bytes = out.size;
				if (hevc_dump && i < keep) fwrite(out.data, 1, out.size, hevc_dump);
			}
			t3 = now_ns();
		}
		if (bytes == 0) {
			failed++;
			continue;
		}
		frames_done++;
		report_frames++;

		if (kept && kept_n < keep && i == kept_n) {
			kept[kept_n] = malloc(bytes);
			kept_offsets[kept_n] = malloc(sizeof(size_t) * 2 * packets);
			size_t at = 0;
			for (size_t k = 0; k < packets; k++) {
				memcpy(kept[kept_n] + at, p.bitstream + p.packets[k].offset, p.packets[k].size);
				kept_offsets[kept_n][2 * k] = at;
				kept_offsets[kept_n][2 * k + 1] = p.packets[k].size;
				at += p.packets[k].size;
			}
			kept_counts[kept_n] = packets;
			kept_n++;
		}

		if (frames_done == WARMUP_FRAMES) {
			measured_from = t3;
			cpu_start = cpu_ms();
			if (pyrowave) pyrowave_device_report_performance_stats(p.device, ignore, NULL, true);
		} else if (frames_done > WARMUP_FRAMES) {
			measured++;
			add(&total, (double)(t3 - t0) / 1e6);
			add(&size_kb, (double)bytes / 1024.0);
			if (pyrowave) {
				add(&submit, (double)(t1 - t0) / 1e6);
				add(&wait, (double)(t2 - t1) / 1e6);
				add(&pack, (double)(t3 - t2) / 1e6);
			}
		}

		if (o->stress && t3 >= next_report) {
			printf("stress    : %ld frames/s\n", report_frames);
			report_frames = 0;
			next_report += 1000000000ull;
		}
	}

	uint64_t stopped = now_ns();
	double cpu = cpu_ms() - cpu_start;
	double span = measured_from ? (double)(stopped - measured_from) / 1e9 : 0;

	printf("frames    : %ld measured after %d of warm-up, %.1f a second%s\n", measured, WARMUP_FRAMES,
	       span > 0 ? measured / span : 0, failed ? " - SOME FAILED" : "");
	if (failed) printf("failed    : %ld\n", failed);
	print_series("size kB", &size_kb, "");
	if (pyrowave) {
		print_series("submit ms", &submit, "frame handed over, until the call returns");
		print_series("gpu ms", &wait, "until the GPU is done and the size is known");
		print_series("packet ms", &pack, "cutting the bitstream into packets");
	}
	print_series("total ms", &total, "frame in hand to packets in hand");
	if (measured) printf("cpu       : %.2f ms of process CPU time per frame\n", cpu / measured);
	if (pyrowave) pyrowave_device_report_performance_stats(p.device, on_stats, NULL, true);

	if (hevc_dump) {
		fclose(hevc_dump);
		printf("dump      : the first %d encoded frames in %s\n", keep, o->dump);
	}
	if (kept) pw_dump(&p, o, kept, kept_offsets, kept_counts, kept_n);

	if (hevc) encoder_close(hevc);
	if (pyrowave) {
		pyrowave_encoder_destroy(p.encoder);
		pyrowave_device_destroy(p.device);
	}
	return failed ? 1 : 0;
}

int main(int argc, char **argv)
{
	struct options o = { .codec = "pyrowave", .queue = "compute", .fps = 60, .mbit = 140, .seconds = 10 };
	setvbuf(stdout, NULL, _IOLBF, 0);

	for (int i = 1; i < argc; i++) {
		const char *a = argv[i];
		unsigned w, h;
		if (strncmp(a, "--frames=", 9) == 0) o.frames = a + 9;
		else if (strncmp(a, "--codec=", 8) == 0) o.codec = a + 8;
		else if (strncmp(a, "--queue=", 8) == 0) o.queue = a + 8;
		else if (strncmp(a, "--dump=", 7) == 0) o.dump = a + 7;
		else if (sscanf(a, "--size=%ux%u", &w, &h) == 2) {
			o.width = w;
			o.height = h;
		} else if (strncmp(a, "--fps=", 6) == 0) o.fps = (uint32_t)atoi(a + 6);
		else if (strncmp(a, "--mbit=", 7) == 0) o.mbit = atof(a + 7);
		else if (strncmp(a, "--seconds=", 10) == 0) o.seconds = atof(a + 10);
		else if (strcmp(a, "--stress") == 0) o.stress = 1;
		else {
			fprintf(stderr, "unknown argument: %s\n", a);
			return 2;
		}
	}
	if (!o.width || !o.height || (o.width & 1) || (o.height & 1) || !o.fps) {
		fprintf(stderr, "--size=WxH with even sides is required\n");
		return 2;
	}
	if (o.stress) o.codec = "pyrowave";
	return run(&o);
}
