#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "overlay.h"
#include "wire.h"

/*
 * Turns NV12 pictures into a codec's bitstream, one at a time. Three codecs sit behind it:
 * HEVC and AV1 on the GPU's own encoder through VA-API (va_encoder.c), and PyroWave on its
 * shaders through the codec's library, loaded when first asked for (pw_encoder.c).
 */

struct encoder;

struct encoder_config {
	uint8_t codec; /* WIRE_CODEC_*; zero is HEVC */

	/* The picture's own size. Whatever alignment the hardware wants is the encoder's
	 * business and comes back out as a crop in the stream. */
	uint32_t width, height;

	/* Nominal rate. Frames come when gamescope draws them; this only tells the rate
	 * control how many bits one frame may have. */
	uint32_t fps;

	uint32_t bitrate_kbps;

	/* How many frames' worth of bits the rate control may run ahead or behind. Small
	 * keeps every frame near the same size, which is what a network wants; large lets a
	 * keyframe be much bigger than the frames around it. */
	double buffer_frames;

	/* Hard cap on one encoded frame, or 0 for none. A frame is sent as a burst of
	 * packets, and the bigger the burst the likelier one of them is lost. */
	uint32_t max_frame_bytes;

	/* Frames between keyframes, or 0 for "only the first and when asked". */
	uint32_t gop;

	/* How the picture's colours are coded, so the stream can say so: BT.709 unless
	 * bt601, and the limited 16-235 range unless full_range. */
	bool bt601, full_range;
};

struct encoded_frame {
	/* The whole frame. Valid until the next encoder_encode. HEVC: an access unit in Annex B
	 * form. AV1: a temporal unit of OBUs, temporal delimiter first. PyroWave: the codec's
	 * packets one after another, which its decoder takes all at once. */
	const uint8_t *data;
	size_t size;
	bool keyframe; /* true for every PyroWave frame: each one stands alone */

	uint64_t upload_ns; /* the picture into the encoder's surface */
	uint64_t encode_ns; /* from handing it over to holding the bitstream */
};

/* Null on failure, with the reason in `why` when it is given. */
struct encoder *encoder_open(const struct encoder_config *config, char *why, size_t why_cap);

/* `nv12`: `height` luma rows of `stride` bytes, then the interleaved chroma rows.
 * `overlay`, when there is one, is laid over the picture on its way in. */
bool encoder_encode(struct encoder *encoder, const uint8_t *nv12, uint32_t stride, bool force_keyframe,
		    const struct overlay *overlay, struct encoded_frame *out);

/* One line on what was opened: codec, profile, level, upload path. */
const char *encoder_describe(const struct encoder *encoder);
const char *encoder_error(const struct encoder *encoder);
uint8_t encoder_codec(const struct encoder *encoder);
void encoder_close(struct encoder *encoder);

/* "HEVC", "AV1", "PyroWave", or "codec N" for a number this build does not know. */
const char *encoder_codec_name(uint8_t codec);

/* Whether this machine can encode `codec` at all; when not, why. Opens and closes what it
 * needs to find out, so it is for a probe, not for every frame. */
bool encoder_available(uint8_t codec, char *why, size_t why_cap);
