#pragma once

#include <stdint.h>

struct spa_pod;

/* Prints every property of a PipeWire format object, one per line, with its choices. */
void format_describe(const struct spa_pod *format, const char *indent);

const char *video_format_name(uint32_t spa_video_format);
const char *color_matrix_name(uint32_t spa_color_matrix);
const char *color_range_name(uint32_t spa_color_range);
