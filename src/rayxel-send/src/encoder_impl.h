#pragma once

/* What encoder.c dispatches to. Private to the encoders. */

#include "encoder.h"

void *va_encoder_open(const struct encoder_config *config, char *why, size_t why_cap);
bool va_encoder_encode(void *encoder, const uint8_t *nv12, uint32_t stride, bool force_keyframe,
		       const struct overlay *overlay, struct encoded_frame *out);
const char *va_encoder_describe(const void *encoder);
const char *va_encoder_error(const void *encoder);
void va_encoder_close(void *encoder);
bool va_encoder_available(uint8_t codec, char *why, size_t why_cap);

void *pw_encoder_open(const struct encoder_config *config, char *why, size_t why_cap);
bool pw_encoder_encode(void *encoder, const uint8_t *nv12, uint32_t stride, bool force_keyframe,
		       const struct overlay *overlay, struct encoded_frame *out);
const char *pw_encoder_describe(const void *encoder);
const char *pw_encoder_error(const void *encoder);
void pw_encoder_close(void *encoder);
bool pw_encoder_available(char *why, size_t why_cap);
