#include <stdio.h>
#include <stdlib.h>

#include "encoder_impl.h"

/* Which implementation a codec has, behind the one interface the sender uses. */

struct encoder {
	uint8_t codec;
	void *impl;
};

const char *encoder_codec_name(uint8_t codec)
{
	switch (codec) {
	case WIRE_CODEC_HEVC: return "HEVC";
	case WIRE_CODEC_AV1: return "AV1";
	case WIRE_CODEC_PYROWAVE: return "PyroWave";
	default: return "an unknown codec";
	}
}

struct encoder *encoder_open(const struct encoder_config *config, char *why, size_t why_cap)
{
	if (config->codec >= WIRE_CODEC_COUNT) {
		if (why) snprintf(why, why_cap, "codec %u is not one this program knows", config->codec);
		return NULL;
	}

	struct encoder *e = calloc(1, sizeof(*e));
	if (!e) return NULL;
	e->codec = config->codec;
	e->impl = config->codec == WIRE_CODEC_PYROWAVE ? pw_encoder_open(config, why, why_cap)
						       : va_encoder_open(config, why, why_cap);
	if (!e->impl) {
		free(e);
		return NULL;
	}
	return e;
}

bool encoder_encode(struct encoder *e, const uint8_t *nv12, uint32_t stride, bool force_keyframe,
		    const struct overlay *overlay, struct encoded_frame *out)
{
	return e->codec == WIRE_CODEC_PYROWAVE ? pw_encoder_encode(e->impl, nv12, stride, force_keyframe, overlay, out)
					       : va_encoder_encode(e->impl, nv12, stride, force_keyframe, overlay, out);
}

const char *encoder_describe(const struct encoder *e)
{
	return e->codec == WIRE_CODEC_PYROWAVE ? pw_encoder_describe(e->impl) : va_encoder_describe(e->impl);
}

const char *encoder_error(const struct encoder *e)
{
	return e->codec == WIRE_CODEC_PYROWAVE ? pw_encoder_error(e->impl) : va_encoder_error(e->impl);
}

uint8_t encoder_codec(const struct encoder *e)
{
	return e->codec;
}

void encoder_close(struct encoder *e)
{
	if (!e) return;
	if (e->codec == WIRE_CODEC_PYROWAVE) pw_encoder_close(e->impl);
	else va_encoder_close(e->impl);
	free(e);
}

bool encoder_available(uint8_t codec, char *why, size_t why_cap)
{
	if (codec == WIRE_CODEC_PYROWAVE) return pw_encoder_available(why, why_cap);
	if (codec >= WIRE_CODEC_COUNT) {
		if (why) snprintf(why, why_cap, "codec %u is not one this program knows", codec);
		return false;
	}
	return va_encoder_available(codec, why, why_cap);
}
