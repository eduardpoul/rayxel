#include <inttypes.h>
#include <stdio.h>
#include <string.h>

#include <spa/debug/types.h>
#include <spa/param/format.h>
#include <spa/param/video/format.h>
#include <spa/pod/iter.h>

#include "gamescope_pw.h"
#include "pod_print.h"

const char *video_format_name(uint32_t format)
{
	const char *name = spa_debug_type_find_short_name(spa_type_video_format, format);
	return name ? name : "unknown";
}

const char *color_matrix_name(uint32_t matrix)
{
	switch (matrix) {
	case SPA_VIDEO_COLOR_MATRIX_RGB: return "RGB";
	case SPA_VIDEO_COLOR_MATRIX_FCC: return "FCC";
	case SPA_VIDEO_COLOR_MATRIX_BT709: return "BT.709";
	case SPA_VIDEO_COLOR_MATRIX_BT601: return "BT.601";
	case SPA_VIDEO_COLOR_MATRIX_SMPTE240M: return "SMPTE 240M";
	case SPA_VIDEO_COLOR_MATRIX_BT2020: return "BT.2020";
	default: return "unspecified";
	}
}

const char *color_range_name(uint32_t range)
{
	switch (range) {
	case SPA_VIDEO_COLOR_RANGE_0_255: return "full";
	case SPA_VIDEO_COLOR_RANGE_16_235: return "limited";
	default: return "unspecified";
	}
}

static const char *key_name(uint32_t key, char *scratch, size_t cap)
{
	if (key == GAMESCOPE_FORMAT_VIDEO_requested_size) return "gamescope.requestedSize";
	if (key == GAMESCOPE_FORMAT_VIDEO_focus_appid) return "gamescope.focusAppid";

	const char *name = spa_debug_type_find_short_name(spa_type_format, key);
	if (name) return name;

	snprintf(scratch, cap, "key 0x%x", key);
	return scratch;
}

static const char *id_name(uint32_t key, uint32_t value)
{
	switch (key) {
	case SPA_FORMAT_mediaType: return spa_debug_type_find_short_name(spa_type_media_type, value);
	case SPA_FORMAT_mediaSubtype: return spa_debug_type_find_short_name(spa_type_media_subtype, value);
	case SPA_FORMAT_VIDEO_format: return spa_debug_type_find_short_name(spa_type_video_format, value);
	case SPA_FORMAT_VIDEO_colorMatrix: return color_matrix_name(value);
	case SPA_FORMAT_VIDEO_colorRange: return color_range_name(value);
	default: return NULL;
	}
}

static void put_value(char *out, size_t cap, uint32_t key, uint32_t type, const void *body, uint32_t size)
{
	switch (type) {
	case SPA_TYPE_Id: {
		uint32_t v;
		if (size < sizeof(v)) break;
		memcpy(&v, body, sizeof(v));
		const char *name = id_name(key, v);
		if (name) snprintf(out, cap, "%s", name);
		else snprintf(out, cap, "%u", v);
		return;
	}
	case SPA_TYPE_Int: {
		int32_t v;
		if (size < sizeof(v)) break;
		memcpy(&v, body, sizeof(v));
		snprintf(out, cap, "%d", v);
		return;
	}
	case SPA_TYPE_Long: {
		int64_t v;
		if (size < sizeof(v)) break;
		memcpy(&v, body, sizeof(v));
		/* A DRM modifier is a bit pattern, not a quantity. */
		if (key == SPA_FORMAT_VIDEO_modifier) snprintf(out, cap, "0x%" PRIx64, (uint64_t)v);
		else snprintf(out, cap, "%" PRId64, v);
		return;
	}
	case SPA_TYPE_Bool: {
		int32_t v;
		if (size < sizeof(v)) break;
		memcpy(&v, body, sizeof(v));
		snprintf(out, cap, "%s", v ? "true" : "false");
		return;
	}
	case SPA_TYPE_Rectangle: {
		struct spa_rectangle r;
		if (size < sizeof(r)) break;
		memcpy(&r, body, sizeof(r));
		snprintf(out, cap, "%ux%u", r.width, r.height);
		return;
	}
	case SPA_TYPE_Fraction: {
		struct spa_fraction f;
		if (size < sizeof(f)) break;
		memcpy(&f, body, sizeof(f));
		snprintf(out, cap, "%u/%u", f.num, f.denom);
		return;
	}
	default:
		break;
	}
	snprintf(out, cap, "(type %u, %u bytes)", type, size);
}

static void append(char *line, size_t cap, const char *text)
{
	size_t used = strlen(line);
	if (used + 1 < cap) snprintf(line + used, cap - used, "%s", text);
}

void format_describe(const struct spa_pod *format, const char *indent)
{
	if (format == NULL || !spa_pod_is_object(format)) {
		printf("%s(not a format object)\n", indent);
		return;
	}

	const struct spa_pod_object *object = (const struct spa_pod_object *)format;
	const struct spa_pod_prop *prop;

	SPA_POD_OBJECT_FOREACH(object, prop) {
		char scratch[32], line[512] = "", item[96];
		uint32_t count = 0, choice = SPA_CHOICE_None;

		/* For a choice this is the header shared by all its values, which follow it
		 * packed at that header's size; for a plain value it is the value itself. */
		const struct spa_pod *value = spa_pod_get_values(&prop->value, &count, &choice);
		const uint8_t *body = SPA_POD_BODY_CONST(value);
		uint32_t size = SPA_POD_BODY_SIZE(value), type = SPA_POD_TYPE(value);

#define ITEM(i) (put_value(item, sizeof(item), prop->key, type, body + (size_t)(i) * size, size), item)

		if (count == 0) {
			append(line, sizeof(line), "(empty)");
		} else if (choice == SPA_CHOICE_Range && count >= 3) {
			append(line, sizeof(line), ITEM(1));
			append(line, sizeof(line), " to ");
			append(line, sizeof(line), ITEM(2));
			append(line, sizeof(line), ", default ");
			append(line, sizeof(line), ITEM(0));
		} else if (choice == SPA_CHOICE_Step && count >= 4) {
			append(line, sizeof(line), ITEM(1));
			append(line, sizeof(line), " to ");
			append(line, sizeof(line), ITEM(2));
			append(line, sizeof(line), " in steps of ");
			append(line, sizeof(line), ITEM(3));
			append(line, sizeof(line), ", default ");
			append(line, sizeof(line), ITEM(0));
		} else if (choice == SPA_CHOICE_Enum && count >= 2) {
			/* The first value is the default, repeated among the alternatives. */
			append(line, sizeof(line), "one of {");
			for (uint32_t i = 1; i < count; i++) {
				if (i > 1) append(line, sizeof(line), ", ");
				append(line, sizeof(line), ITEM(i));
			}
			append(line, sizeof(line), "}, default ");
			append(line, sizeof(line), ITEM(0));
		} else {
			for (uint32_t i = 0; i < count; i++) {
				if (i > 0) append(line, sizeof(line), ", ");
				append(line, sizeof(line), ITEM(i));
			}
			if (choice == SPA_CHOICE_Flags) append(line, sizeof(line), " (flags)");
		}
#undef ITEM

		printf("%s%-24s %s%s\n", indent, key_name(prop->key, scratch, sizeof(scratch)), line,
		       (prop->flags & SPA_POD_PROP_FLAG_MANDATORY) ? "  [mandatory]" : "");
	}
}
