#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

#include <va/va.h>
#include <va/va_drm.h>
#include <va/va_drmcommon.h>
#include <va/va_str.h>

#include "common.h"
#include "probe.h"

struct flag {
	uint32_t bit;
	const char *name;
};

#define COUNT(a) (sizeof(a) / sizeof((a)[0]))

static const struct flag rt_formats[] = {
	{ VA_RT_FORMAT_YUV420, "YUV420" },       { VA_RT_FORMAT_YUV422, "YUV422" },
	{ VA_RT_FORMAT_YUV444, "YUV444" },       { VA_RT_FORMAT_YUV411, "YUV411" },
	{ VA_RT_FORMAT_YUV400, "YUV400" },       { VA_RT_FORMAT_YUV420_10, "YUV420-10bit" },
	{ VA_RT_FORMAT_YUV422_10, "YUV422-10bit" }, { VA_RT_FORMAT_YUV444_10, "YUV444-10bit" },
	{ VA_RT_FORMAT_YUV420_12, "YUV420-12bit" }, { VA_RT_FORMAT_RGB16, "RGB16" },
	{ VA_RT_FORMAT_RGB32, "RGB32" },         { VA_RT_FORMAT_RGBP, "RGBP" },
	{ VA_RT_FORMAT_RGB32_10, "RGB32-10bit" }, { VA_RT_FORMAT_PROTECTED, "protected" },
};

static const struct flag rate_controls[] = {
	{ VA_RC_CQP, "CQP" },   { VA_RC_CBR, "CBR" },   { VA_RC_VBR, "VBR" },
	{ VA_RC_VCM, "VCM" },   { VA_RC_VBR_CONSTRAINED, "VBR-constrained" },
	{ VA_RC_ICQ, "ICQ" },   { VA_RC_MB, "MB" },     { VA_RC_CFS, "CFS" },
	{ VA_RC_PARALLEL, "parallel" }, { VA_RC_QVBR, "QVBR" }, { VA_RC_AVBR, "AVBR" },
	{ VA_RC_TCBRC, "TCBRC" }, { VA_RC_NONE, "none" },
};

static const struct flag packed_headers[] = {
	{ VA_ENC_PACKED_HEADER_SEQUENCE, "sequence" }, { VA_ENC_PACKED_HEADER_PICTURE, "picture" },
	{ VA_ENC_PACKED_HEADER_SLICE, "slice" },       { VA_ENC_PACKED_HEADER_MISC, "misc" },
	{ VA_ENC_PACKED_HEADER_RAW_DATA, "raw" },
};

static const struct flag slice_structures[] = {
	{ VA_ENC_SLICE_STRUCTURE_POWER_OF_TWO_ROWS, "power-of-two rows" },
	{ VA_ENC_SLICE_STRUCTURE_ARBITRARY_MACROBLOCKS, "arbitrary blocks" },
	{ VA_ENC_SLICE_STRUCTURE_EQUAL_ROWS, "equal rows" },
	{ VA_ENC_SLICE_STRUCTURE_MAX_SLICE_SIZE, "max slice size" },
	{ VA_ENC_SLICE_STRUCTURE_ARBITRARY_ROWS, "arbitrary rows" },
	{ VA_ENC_SLICE_STRUCTURE_EQUAL_MULTI_ROWS, "equal multi-rows" },
};

static const struct flag intra_refresh[] = {
	{ VA_ENC_INTRA_REFRESH_ROLLING_COLUMN, "rolling column" },
	{ VA_ENC_INTRA_REFRESH_ROLLING_ROW, "rolling row" },
	{ VA_ENC_INTRA_REFRESH_ADAPTIVE, "adaptive" },
	{ VA_ENC_INTRA_REFRESH_CYCLIC, "cyclic" },
	{ VA_ENC_INTRA_REFRESH_P_FRAME, "on P" },
	{ VA_ENC_INTRA_REFRESH_B_FRAME, "on B" },
	{ VA_ENC_INTRA_REFRESH_MULTI_REF, "multi-ref" },
};

static const struct flag prediction_directions[] = {
	{ VA_PREDICTION_DIRECTION_PREVIOUS, "previous" },
	{ VA_PREDICTION_DIRECTION_FUTURE, "future" },
	{ VA_PREDICTION_DIRECTION_BI_NOT_EMPTY, "bi-not-empty" },
};

static const struct flag memory_types[] = {
	{ VA_SURFACE_ATTRIB_MEM_TYPE_VA, "VA" },
	{ VA_SURFACE_ATTRIB_MEM_TYPE_V4L2, "V4L2" },
	{ VA_SURFACE_ATTRIB_MEM_TYPE_USER_PTR, "user pointer" },
	{ VA_SURFACE_ATTRIB_MEM_TYPE_KERNEL_DRM, "kernel DRM" },
	{ VA_SURFACE_ATTRIB_MEM_TYPE_DRM_PRIME, "DRM PRIME" },
	{ VA_SURFACE_ATTRIB_MEM_TYPE_DRM_PRIME_2, "DRM PRIME 2" },
};

static void flags_text(uint32_t value, const struct flag *table, size_t count, const char *none, char *out, size_t cap)
{
	out[0] = '\0';
	for (size_t i = 0; i < count; i++) {
		if (!(value & table[i].bit)) continue;
		size_t used = strlen(out);
		snprintf(out + used, cap - used, "%s%s", used ? ", " : "", table[i].name);
		value &= ~table[i].bit;
	}
	if (value) {
		size_t used = strlen(out);
		snprintf(out + used, cap - used, "%sunknown 0x%x", used ? ", " : "", value);
	}
	if (out[0] == '\0') snprintf(out, cap, "%s", none);
}

/* HEVC tools the encoder reports, two bits each: 0 unavailable, 1 optional, 2 always on. */
static void hevc_features_text(uint32_t v, char *out, size_t cap)
{
	static const char *const names[] = {
		"separate-planes", "scaling-lists", "amp", "sao", "pcm", "temporal-mvp", "strong-intra-smoothing",
		"dependent-slices", "sign-data-hiding", "constrained-intra", "transform-skip", "cu-qp-delta",
		"weighted-pred", "transquant-bypass", "deblock-disable",
	};
	out[0] = '\0';
	for (size_t i = 0; i < COUNT(names); i++) {
		uint32_t state = (v >> (2 * i)) & 3;
		if (state == 0) continue;
		size_t used = strlen(out);
		snprintf(out + used, cap - used, "%s%s%s", used ? ", " : "", names[i], state == 2 ? " (forced)" : "");
	}
	if (out[0] == '\0') snprintf(out, cap, "none");
}

static void hevc_block_sizes_text(uint32_t v, char *out, size_t cap)
{
	unsigned ctb_max = 1u << ((v & 3) + 3), ctb_min = 1u << (((v >> 2) & 3) + 3);
	unsigned cb_min = 1u << (((v >> 4) & 3) + 3);
	unsigned tb_max = 1u << (((v >> 6) & 3) + 2), tb_min = 1u << (((v >> 8) & 3) + 2);
	unsigned depth_inter_max = (v >> 10) & 3, depth_inter_min = (v >> 12) & 3;
	unsigned depth_intra_max = (v >> 14) & 3, depth_intra_min = (v >> 16) & 3;
	snprintf(out, cap, "CTB %u-%u, min CB %u, TB %u-%u, TU depth inter %u-%u intra %u-%u", ctb_min, ctb_max, cb_min,
		 tb_min, tb_max, depth_inter_min, depth_inter_max, depth_intra_min, depth_intra_max);
}

static void attribute_text(VAConfigAttribType type, uint32_t v, char *out, size_t cap)
{
	switch (type) {
	case VAConfigAttribRTFormat: flags_text(v, rt_formats, COUNT(rt_formats), "none", out, cap); break;
	case VAConfigAttribRateControl: flags_text(v, rate_controls, COUNT(rate_controls), "none", out, cap); break;
	case VAConfigAttribEncPackedHeaders:
		flags_text(v, packed_headers, COUNT(packed_headers), "none accepted", out, cap);
		break;
	case VAConfigAttribEncSliceStructure:
		flags_text(v, slice_structures, COUNT(slice_structures), "none", out, cap);
		break;
	case VAConfigAttribEncIntraRefresh: flags_text(v, intra_refresh, COUNT(intra_refresh), "none", out, cap); break;
	case VAConfigAttribPredictionDirection:
		flags_text(v, prediction_directions, COUNT(prediction_directions), "none", out, cap);
		break;
	case VAConfigAttribEncMaxRefFrames: snprintf(out, cap, "%u before, %u after", v & 0xffff, v >> 16); break;
	case VAConfigAttribEncROI:
		snprintf(out, cap, "%u regions%s%s", v & 0xff, (v & 0x100) ? ", by priority" : "",
			 (v & 0x200) ? ", by QP delta" : "");
		break;
	case VAConfigAttribEncHEVCFeatures: hevc_features_text(v, out, cap); break;
	case VAConfigAttribEncHEVCBlockSizes: hevc_block_sizes_text(v, out, cap); break;
	default: snprintf(out, cap, "%u", v); break;
	}
}

static bool get_attribute(VADisplay dpy, VAProfile profile, VAEntrypoint entrypoint, VAConfigAttribType type,
			  uint32_t *value)
{
	VAConfigAttrib attrib = { .type = type, .value = 0 };
	if (vaGetConfigAttributes(dpy, profile, entrypoint, &attrib, 1) != VA_STATUS_SUCCESS) return false;
	if (attrib.value == VA_ATTRIB_NOT_SUPPORTED) return false;
	*value = attrib.value;
	return true;
}

/* Every attribute the driver answers for, named and decoded. Asked one by one over the
 * whole range rather than from a list, so nothing the driver knows about is left out. */
static void dump_attributes(VADisplay dpy, VAProfile profile, VAEntrypoint entrypoint)
{
	for (int type = 0; type < VAConfigAttribTypeMax; type++) {
		uint32_t value;
		char text[400];
		if (!get_attribute(dpy, profile, entrypoint, (VAConfigAttribType)type, &value)) continue;

		const char *name = vaConfigAttribTypeStr((VAConfigAttribType)type);
		if (strncmp(name, "VAConfigAttrib", 14) == 0) name += 14;
		attribute_text((VAConfigAttribType)type, value, text, sizeof(text));
		printf("    %-26s 0x%08x  %s\n", name, value, text);
	}
}

/* What the encoder takes as input: pixel formats, sizes, and where the memory may come from. */
static void dump_surfaces(VADisplay dpy, VAProfile profile, VAEntrypoint entrypoint, uint32_t rt_format)
{
	VAConfigAttrib rt = { .type = VAConfigAttribRTFormat, .value = rt_format };
	VAConfigID config;
	VAStatus status = vaCreateConfig(dpy, profile, entrypoint, &rt, 1, &config);
	if (status != VA_STATUS_SUCCESS) {
		printf("    input surfaces             (no config could be created: %s)\n", vaErrorStr(status));
		return;
	}

	unsigned int count = 0;
	status = vaQuerySurfaceAttributes(dpy, config, NULL, &count);
	VASurfaceAttrib *attribs = count ? calloc(count, sizeof(*attribs)) : NULL;
	if (status == VA_STATUS_SUCCESS && attribs) status = vaQuerySurfaceAttributes(dpy, config, attribs, &count);

	if (status == VA_STATUS_SUCCESS && attribs) {
		char formats[256] = "", memory[160] = "not stated";
		int min_w = 0, max_w = 0, min_h = 0, max_h = 0;

		for (unsigned int i = 0; i < count; i++) {
			int v = attribs[i].value.value.i;
			switch (attribs[i].type) {
			case VASurfaceAttribPixelFormat: {
				size_t used = strlen(formats);
				uint32_t fourcc = (uint32_t)v;
				snprintf(formats + used, sizeof(formats) - used, "%s%c%c%c%c", used ? ", " : "",
					 (char)(fourcc & 0xff), (char)((fourcc >> 8) & 0xff), (char)((fourcc >> 16) & 0xff),
					 (char)((fourcc >> 24) & 0xff));
				break;
			}
			case VASurfaceAttribMinWidth: min_w = v; break;
			case VASurfaceAttribMaxWidth: max_w = v; break;
			case VASurfaceAttribMinHeight: min_h = v; break;
			case VASurfaceAttribMaxHeight: max_h = v; break;
			case VASurfaceAttribMemoryType:
				flags_text((uint32_t)v, memory_types, COUNT(memory_types), "none", memory, sizeof(memory));
				break;
			default: break;
			}
		}
		printf("    input pixel formats        %s\n", formats[0] ? formats : "not stated");
		printf("    input size                 %dx%d to %dx%d\n", min_w, min_h, max_w, max_h);
		printf("    input memory               %s\n", memory);
	} else {
		printf("    input surfaces             (query failed: %s)\n", vaErrorStr(status));
	}

	free(attribs);
	vaDestroyConfig(dpy, config);
}

static const char *short_profile(VAProfile profile)
{
	const char *name = vaProfileStr(profile);
	return strncmp(name, "VAProfile", 9) == 0 ? name + 9 : name;
}

static const char *short_entrypoint(VAEntrypoint entrypoint)
{
	switch (entrypoint) {
	case VAEntrypointEncSlice: return "encode";
	case VAEntrypointEncSliceLP: return "encode, low-power";
	default: return vaEntrypointStr(entrypoint);
	}
}

static void probe_display(VADisplay dpy, struct va_summary *summary)
{
	int profile_count = vaMaxNumProfiles(dpy), entrypoint_max = vaMaxNumEntrypoints(dpy);
	VAProfile *profiles = calloc((size_t)(profile_count > 0 ? profile_count : 1), sizeof(*profiles));
	VAEntrypoint *entrypoints = calloc((size_t)(entrypoint_max > 0 ? entrypoint_max : 1), sizeof(*entrypoints));

	if (!profiles || !entrypoints || vaQueryConfigProfiles(dpy, profiles, &profile_count) != VA_STATUS_SUCCESS) {
		printf("the driver would not list its profiles\n");
		goto out;
	}

	printf("encoders :\n");
	int encoders = 0;
	for (int i = 0; i < profile_count; i++) {
		int count = 0;
		if (vaQueryConfigEntrypoints(dpy, profiles[i], entrypoints, &count) != VA_STATUS_SUCCESS) continue;
		for (int j = 0; j < count; j++) {
			if (entrypoints[j] != VAEntrypointEncSlice && entrypoints[j] != VAEntrypointEncSliceLP) continue;
			uint32_t rc = 0, w = 0, h = 0;
			char rc_text[128];
			get_attribute(dpy, profiles[i], entrypoints[j], VAConfigAttribRateControl, &rc);
			get_attribute(dpy, profiles[i], entrypoints[j], VAConfigAttribMaxPictureWidth, &w);
			get_attribute(dpy, profiles[i], entrypoints[j], VAConfigAttribMaxPictureHeight, &h);
			flags_text(rc, rate_controls, COUNT(rate_controls), "none", rc_text, sizeof(rc_text));
			printf("  %-26s %-18s up to %ux%u, %s\n", short_profile(profiles[i]),
			       short_entrypoint(entrypoints[j]), w, h, rc_text);
			encoders++;
		}
	}
	if (encoders == 0) printf("  (none - this driver can only decode)\n");

	/* The two profiles this program can use, in full. */
	static const struct {
		VAProfile profile;
		uint32_t rt_format;
	} wanted[] = {
		{ VAProfileHEVCMain, VA_RT_FORMAT_YUV420 },
		{ VAProfileHEVCMain10, VA_RT_FORMAT_YUV420_10 },
	};
	for (size_t k = 0; k < COUNT(wanted); k++) {
		int count = 0;
		if (vaQueryConfigEntrypoints(dpy, wanted[k].profile, entrypoints, &count) != VA_STATUS_SUCCESS) continue;
		for (int j = 0; j < count; j++) {
			VAEntrypoint entrypoint = entrypoints[j];
			if (entrypoint != VAEntrypointEncSlice && entrypoint != VAEntrypointEncSliceLP) continue;

			printf("%s, %s:\n", short_profile(wanted[k].profile), short_entrypoint(entrypoint));
			dump_attributes(dpy, wanted[k].profile, entrypoint);
			dump_surfaces(dpy, wanted[k].profile, entrypoint, wanted[k].rt_format);

			if (wanted[k].profile == VAProfileHEVCMain10) summary->hevc_main10 = true;
			if (wanted[k].profile == VAProfileHEVCMain && !summary->hevc_main) {
				uint32_t value = 0;
				summary->hevc_main = true;
				get_attribute(dpy, wanted[k].profile, entrypoint, VAConfigAttribRateControl, &value);
				flags_text(value, rate_controls, COUNT(rate_controls), "none", summary->rate_control,
					   sizeof(summary->rate_control));
				value = 0;
				if (get_attribute(dpy, wanted[k].profile, entrypoint, VAConfigAttribEncPackedHeaders, &value))
					flags_text(value, packed_headers, COUNT(packed_headers), "none accepted",
						   summary->packed_headers, sizeof(summary->packed_headers));
				else
					snprintf(summary->packed_headers, sizeof(summary->packed_headers), "not reported");
				get_attribute(dpy, wanted[k].profile, entrypoint, VAConfigAttribMaxPictureWidth,
					      &summary->max_width);
				get_attribute(dpy, wanted[k].profile, entrypoint, VAConfigAttribMaxPictureHeight,
					      &summary->max_height);
			}
		}
	}

out:
	free(profiles);
	free(entrypoints);
}

bool probe_va(struct va_summary *summary)
{
	bool any_device = false, any_display = false;

	memset(summary, 0, sizeof(*summary));
	section("video encoder (VA-API)");

	for (int minor = 128; minor < 136; minor++) {
		char path[64];
		snprintf(path, sizeof(path), "/dev/dri/renderD%d", minor);
		int fd = open(path, O_RDWR | O_CLOEXEC);
		if (fd < 0) continue;
		any_device = true;

		VADisplay dpy = vaGetDisplayDRM(fd);
		int major = 0, min = 0;
		VAStatus status = dpy ? vaInitialize(dpy, &major, &min) : VA_STATUS_ERROR_INVALID_DISPLAY;
		if (status != VA_STATUS_SUCCESS) {
			printf("device   : %s - no VA-API driver would start (%s)\n", path, vaErrorStr(status));
			close(fd);
			continue;
		}
		any_display = true;

		const char *vendor = vaQueryVendorString(dpy);
		printf("device   : %s\n", path);
		printf("VA-API   : %d.%d\n", major, min);
		printf("driver   : %s\n", vendor ? vendor : "unknown");
		if (!summary->hevc_main) snprintf(summary->driver, sizeof(summary->driver), "%s", vendor ? vendor : "unknown");

		probe_display(dpy, summary);

		vaTerminate(dpy);
		close(fd);
	}

	if (!any_device) printf("no render device under /dev/dri - there is no GPU this user can open\n");
	return any_display;
}
