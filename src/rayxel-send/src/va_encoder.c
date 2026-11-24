#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

#include <va/va.h>
#include <va/va_drm.h>
#include <va/va_enc_av1.h>
#include <va/va_enc_hevc.h>

#include "av1_headers.h"
#include "common.h"
#include "encoder_impl.h"
#include "hevc_headers.h"

/*
 * HEVC and AV1 on the GPU's own video encoder, through VA-API. Everything but the
 * parameters and headers of a frame is the same for the two: the device, the surfaces, how
 * the picture gets in, the rate control and how the bitstream comes out.
 */

/* The Deck's encoder offers exactly one coding tree block size (see --probe). */
#define CTB_SIZE 64

/* HEVC nal_unit_type and slice_type values used here. */
#define NAL_TRAIL_R 1
#define NAL_IDR_W_RADL 19
#define SLICE_P 1
#define SLICE_I 2

/* AV1 reference frame names, as VA's search_idx fields number them. */
#define AV1_REF_LAST 1

struct va_encoder {
	struct encoder_config config;
	int fd;
	VADisplay dpy;
	bool initialized;
	VAProfile profile;
	VAEntrypoint entrypoint;
	VAConfigID va_config;
	VAContextID context;
	VASurfaceID input;
	VASurfaceID recon[2];
	VABufferID coded;

	/* Set when the input surface cannot be mapped directly and the picture has to go
	 * through an image of our own and a second copy by the driver. */
	bool put_image;
	VAImage image;

	/* What the stream is, as written into its headers. */
	struct hevc_stream stream;
	struct av1_stream av1;
	uint8_t av1_sequence[AV1_HEADERS_MAX];
	size_t av1_sequence_size;

	uint64_t frames;         /* encoded so far */
	uint32_t since_keyframe; /* also the picture order count */
	int current;             /* which reconstruction surface this frame writes */

	uint8_t *out;
	size_t out_cap;

	char description[256];
	char error[256];
};

static bool is_av1(const struct va_encoder *e)
{
	return e->config.codec == WIRE_CODEC_AV1;
}

static bool check(struct va_encoder *e, VAStatus status, const char *what)
{
	if (status == VA_STATUS_SUCCESS) return true;
	snprintf(e->error, sizeof(e->error), "%s: %s", what, vaErrorStr(status));
	return false;
}

static void quiet(void *context, const char *message)
{
}

/*
 * The lowest level, and tier, whose limits hold this picture size, sample rate and
 * bitrate. general_level_idc is thirty times the level number. Hardware decoders do not
 * enforce any of it, but a stream that states less than it uses is a wrong stream.
 */
static void pick_level(uint32_t width, uint32_t height, uint32_t fps, uint32_t bitrate_kbps, uint8_t *level, uint8_t *tier)
{
	static const struct {
		uint8_t idc;
		uint32_t samples;
		uint64_t rate;
		uint32_t main_kbps, high_kbps; /* high 0: the level has no high tier */
	} levels[] = {
		{ 90, 552960, 16588800, 6000, 0 },          /* 3   */
		{ 93, 983040, 33177600, 10000, 0 },         /* 3.1 */
		{ 120, 2228224, 66846720, 12000, 30000 },   /* 4   */
		{ 123, 2228224, 133693440, 20000, 50000 },  /* 4.1 */
		{ 150, 8912896, 267386880, 25000, 100000 }, /* 5   */
		{ 153, 8912896, 534773760, 40000, 160000 }, /* 5.1 */
		{ 156, 8912896, 1069547520, 60000, 240000 }, /* 5.2 */
		{ 180, 35651584, 1069547520, 60000, 240000 }, /* 6   */
		{ 183, 35651584, 2139095040, 120000, 480000 }, /* 6.1 */
		{ 186, 35651584, 4278190080ull, 240000, 800000 }, /* 6.2 */
	};
	uint64_t samples = (uint64_t)width * height, rate = samples * fps;

	for (size_t i = 0; i < sizeof(levels) / sizeof(levels[0]); i++) {
		if (samples > levels[i].samples || rate > levels[i].rate) continue;
		if (bitrate_kbps <= levels[i].main_kbps) {
			*level = levels[i].idc;
			*tier = 0;
			return;
		}
		if (bitrate_kbps <= levels[i].high_kbps) {
			*level = levels[i].idc;
			*tier = 1;
			return;
		}
	}
	*level = 186;
	*tier = 1;
}

/* The encode entry point this display offers for `profile`, or 0 when it has none. The
 * low-power one is a separate engine on some GPUs and the only one on others. */
static VAEntrypoint encode_entrypoint(VADisplay dpy, VAProfile profile)
{
	int count = vaMaxNumEntrypoints(dpy);
	VAEntrypoint *entrypoints = calloc((size_t)(count > 0 ? count : 1), sizeof(*entrypoints));
	VAEntrypoint found = 0;

	if (entrypoints && vaQueryConfigEntrypoints(dpy, profile, entrypoints, &count) == VA_STATUS_SUCCESS) {
		for (int i = 0; i < count; i++)
			if (entrypoints[i] == VAEntrypointEncSlice) found = VAEntrypointEncSlice;
		for (int i = 0; i < count && !found; i++)
			if (entrypoints[i] == VAEntrypointEncSliceLP) found = VAEntrypointEncSliceLP;
	}
	free(entrypoints);
	return found;
}

static bool open_display(struct va_encoder *e)
{
	for (int minor = 128; minor < 136; minor++) {
		char path[64];
		snprintf(path, sizeof(path), "/dev/dri/renderD%d", minor);
		int fd = open(path, O_RDWR | O_CLOEXEC);
		if (fd < 0) continue;

		VADisplay dpy = vaGetDisplayDRM(fd);
		int major, min;
		if (dpy) {
			vaSetInfoCallback(dpy, quiet, NULL);
			if (vaInitialize(dpy, &major, &min) == VA_STATUS_SUCCESS) {
				VAEntrypoint entrypoint = encode_entrypoint(dpy, e->profile);
				if (entrypoint) {
					e->fd = fd;
					e->dpy = dpy;
					e->entrypoint = entrypoint;
					e->initialized = true;
					return true;
				}
				vaTerminate(dpy);
			}
		}
		close(fd);
	}
	snprintf(e->error, sizeof(e->error), "no GPU here has a hardware %s encoder", encoder_codec_name(e->config.codec));
	return false;
}

/* How the picture gets into the input surface: mapped directly if the driver allows it,
 * otherwise through an image of our own that the driver then copies in. */
static bool choose_upload(struct va_encoder *e)
{
	VAImage derived;
	if (vaDeriveImage(e->dpy, e->input, &derived) == VA_STATUS_SUCCESS) {
		bool usable = derived.format.fourcc == VA_FOURCC_NV12 && derived.num_planes >= 2;
		vaDestroyImage(e->dpy, derived.image_id);
		if (usable) return true;
	}

	int count = vaMaxNumImageFormats(e->dpy);
	VAImageFormat *formats = calloc((size_t)(count > 0 ? count : 1), sizeof(*formats));
	bool created = false;
	if (formats && vaQueryImageFormats(e->dpy, formats, &count) == VA_STATUS_SUCCESS) {
		for (int i = 0; i < count && !created; i++) {
			if (formats[i].fourcc != VA_FOURCC_NV12) continue;
			created = vaCreateImage(e->dpy, &formats[i], (int)e->config.width, (int)e->config.height, &e->image) ==
				  VA_STATUS_SUCCESS;
		}
	}
	free(formats);
	if (!created) {
		snprintf(e->error, sizeof(e->error), "the driver offers no way to upload an NV12 picture");
		return false;
	}
	e->put_image = true;
	return true;
}

static bool get_attribute(struct va_encoder *e, VAConfigAttribType type, uint32_t *value)
{
	VAConfigAttrib attrib = { .type = type };
	if (vaGetConfigAttributes(e->dpy, e->profile, e->entrypoint, &attrib, 1) != VA_STATUS_SUCCESS) return false;
	if (attrib.value == VA_ATTRIB_NOT_SUPPORTED) return false;
	*value = attrib.value;
	return true;
}

/* AV1's headers come in two packed buffers, the sequence header and the frame header, and
 * a driver that does not take both cannot be told what each frame is. */
static bool configure_av1(struct va_encoder *e, uint32_t *packed)
{
	uint32_t offered = 0;
	if (!get_attribute(e, VAConfigAttribEncPackedHeaders, &offered) ||
	    (offered & (VA_ENC_PACKED_HEADER_SEQUENCE | VA_ENC_PACKED_HEADER_PICTURE)) !=
		    (VA_ENC_PACKED_HEADER_SEQUENCE | VA_ENC_PACKED_HEADER_PICTURE)) {
		snprintf(e->error, sizeof(e->error), "the AV1 encoder does not take sequence and frame headers from us");
		return false;
	}
	*packed = VA_ENC_PACKED_HEADER_SEQUENCE | VA_ENC_PACKED_HEADER_PICTURE;

	/* The transform-size modes it can signal: TX_MODE_SELECT when it can, the largest
	 * transform everywhere otherwise. Without the attribute, SELECT is what drivers do. */
	e->av1.tx_mode_select = true;
#if VA_CHECK_VERSION(1, 16, 0)
	uint32_t ext2 = 0;
	if (get_attribute(e, VAConfigAttribEncAV1Ext2, &ext2)) {
		VAConfigAttribValEncAV1Ext2 value = { .value = ext2 };
		uint32_t modes = value.bits.tx_mode_support;
		if (modes && !(modes & 0x04)) e->av1.tx_mode_select = false;
	}
#endif
	return true;
}

void *va_encoder_open(const struct encoder_config *config, char *why, size_t why_cap)
{
	struct va_encoder *e = calloc(1, sizeof(*e));
	if (!e) return NULL;
	e->config = *config;
	e->fd = -1;
	e->va_config = e->context = VA_INVALID_ID;
	e->input = e->recon[0] = e->recon[1] = VA_INVALID_SURFACE;
	e->coded = VA_INVALID_ID;
	e->image.image_id = VA_INVALID_ID;
	e->profile = is_av1(e) ? VAProfileAV1Profile0 : VAProfileHEVCMain;

	uint32_t w = config->width, h = config->height;
	if (w < 130 || h < 128 || (w & 1) || (h & 1)) {
		snprintf(e->error, sizeof(e->error), "%ux%u cannot be encoded: both sides must be even and at least 130x128", w, h);
		goto fail;
	}
	if (!open_display(e)) goto fail;

	/* The driver writes no parameter set and no usable slice or frame header unless it is
	 * given one to work from; see hevc_headers.h and av1_headers.h. */
	uint32_t packed = VA_ENC_PACKED_HEADER_SEQUENCE | VA_ENC_PACKED_HEADER_PICTURE | VA_ENC_PACKED_HEADER_SLICE;
	if (is_av1(e) && !configure_av1(e, &packed)) goto fail;

	VAConfigAttrib attribs[] = {
		{ VAConfigAttribRTFormat, VA_RT_FORMAT_YUV420 },
		{ VAConfigAttribRateControl, VA_RC_CBR },
		{ VAConfigAttribEncPackedHeaders, packed },
	};
	if (!check(e, vaCreateConfig(e->dpy, e->profile, e->entrypoint, attribs, 3, &e->va_config), "vaCreateConfig"))
		goto fail;

	VASurfaceAttrib format = {
		.type = VASurfaceAttribPixelFormat,
		.flags = VA_SURFACE_ATTRIB_SETTABLE,
		.value = { .type = VAGenericValueTypeInteger, .value.i = VA_FOURCC_NV12 },
	};
	VASurfaceID surfaces[3];
	if (!check(e, vaCreateSurfaces(e->dpy, VA_RT_FORMAT_YUV420, w, h, surfaces, 3, &format, 1), "vaCreateSurfaces"))
		goto fail;
	e->input = surfaces[0];
	e->recon[0] = surfaces[1];
	e->recon[1] = surfaces[2];

	if (!check(e, vaCreateContext(e->dpy, e->va_config, (int)w, (int)h, VA_PROGRESSIVE, surfaces, 3, &e->context),
		   "vaCreateContext"))
		goto fail;

	/* Generous on purpose: a keyframe of noise can exceed the raw picture. */
	unsigned coded_size = 3 * w * h + (1u << 16);
	if (!check(e, vaCreateBuffer(e->dpy, e->context, VAEncCodedBufferType, coded_size, 1, NULL, &e->coded),
		   "vaCreateBuffer(coded)"))
		goto fail;

	if (!choose_upload(e)) goto fail;

	if (is_av1(e)) {
		bool tx_select = e->av1.tx_mode_select;
		av1_stream_init(&e->av1, w, h, config->fps, config->bitrate_kbps);
		e->av1.tx_mode_select = tx_select;
		e->av1.full_range = config->full_range;
		e->av1.colour_primaries = e->av1.transfer_characteristics = e->av1.matrix_coefficients =
			config->bt601 ? 6 : 1;
		/* AMD's encoders before VCN 5 code whole 64-pixel columns and 16-pixel rows. Saying
		 * the picture's own size beside it costs four bytes when it is not needed. */
		e->av1.render_size = (w % 64) != 0 || (h % 16) != 0;
		e->av1_sequence_size = av1_write_sequence_header(&e->av1, e->av1_sequence);

		snprintf(e->description, sizeof(e->description),
			 "AV1 Main %ux%u, level %u.%u %s tier, CBR %.1f Mbit/s, %s upload, %s transforms", w, h,
			 2 + e->av1.level_idx / 4, e->av1.level_idx % 4, e->av1.tier ? "high" : "main",
			 config->bitrate_kbps / 1000.0, e->put_image ? "two-copy" : "direct",
			 e->av1.tx_mode_select ? "selected" : "largest");
		return e;
	}

	e->stream = (struct hevc_stream){
		.width = w,
		.height = h,
		/* The encoder codes whole 64-pixel columns and 16-pixel rows. */
		.coded_width = (w + CTB_SIZE - 1) / CTB_SIZE * CTB_SIZE,
		.coded_height = (h + 15) / 16 * 16,
		.poc_bits = 8,
		.full_range = config->full_range,
		.colour_primaries = config->bt601 ? 6 : 1,
		.transfer_characteristics = config->bt601 ? 6 : 1,
		.matrix_coefficients = config->bt601 ? 6 : 1,
	};
	pick_level(w, h, config->fps, config->bitrate_kbps, &e->stream.level_idc, &e->stream.tier);

	snprintf(e->description, sizeof(e->description),
		 "HEVC Main %ux%u (coded %ux%u), level %u.%u %s tier, CBR %.1f Mbit/s, %s upload", w, h,
		 e->stream.coded_width, e->stream.coded_height, e->stream.level_idc / 30, (e->stream.level_idc % 30) / 3,
		 e->stream.tier ? "high" : "main", config->bitrate_kbps / 1000.0, e->put_image ? "two-copy" : "direct");
	return e;

fail:
	if (why) snprintf(why, why_cap, "%s", e->error);
	va_encoder_close(e);
	return NULL;
}

bool va_encoder_available(uint8_t codec, char *why, size_t why_cap)
{
	struct va_encoder probe = { .config = { .codec = codec }, .fd = -1 };
	probe.profile = codec == WIRE_CODEC_AV1 ? VAProfileAV1Profile0 : VAProfileHEVCMain;
	bool found = open_display(&probe);
	if (!found && why) snprintf(why, why_cap, "%s", probe.error);
	if (found) {
		vaTerminate(probe.dpy);
		close(probe.fd);
	}
	return found;
}

static bool upload(struct va_encoder *e, const uint8_t *nv12, uint32_t stride, const struct overlay *overlay)
{
	uint32_t w = e->config.width, h = e->config.height;
	VAImage image = e->image;
	void *mapped = NULL;

	if (!e->put_image && !check(e, vaDeriveImage(e->dpy, e->input, &image), "vaDeriveImage")) return false;
	if (!check(e, vaMapBuffer(e->dpy, image.buf, &mapped), "vaMapBuffer(image)")) {
		if (!e->put_image) vaDestroyImage(e->dpy, image.image_id);
		return false;
	}

	uint8_t *luma = (uint8_t *)mapped + image.offsets[0], *chroma = (uint8_t *)mapped + image.offsets[1];
	const uint8_t *source_chroma = nv12 + (size_t)stride * h;
	for (uint32_t y = 0; y < h; y++) memcpy(luma + (size_t)y * image.pitches[0], nv12 + (size_t)y * stride, w);
	for (uint32_t y = 0; y < h / 2; y++)
		memcpy(chroma + (size_t)y * image.pitches[1], source_chroma + (size_t)y * stride, w);
	if (overlay) overlay_blend_nv12(overlay, nv12, stride, luma, image.pitches[0], chroma, image.pitches[1], w, h);

	vaUnmapBuffer(e->dpy, image.buf);

	if (e->put_image)
		return check(e, vaPutImage(e->dpy, e->input, image.image_id, 0, 0, w, h, 0, 0, w, h), "vaPutImage");
	vaDestroyImage(e->dpy, image.image_id);
	return true;
}

static bool add_buffer(struct va_encoder *e, VABufferType type, const void *data, size_t size, VABufferID *ids, int *count)
{
	VABufferID id;
	if (!check(e, vaCreateBuffer(e->dpy, e->context, type, (unsigned)size, 1, (void *)data, &id), "vaCreateBuffer"))
		return false;
	ids[(*count)++] = id;
	return true;
}

static bool add_misc(struct va_encoder *e, VAEncMiscParameterType type, const void *payload, size_t size, VABufferID *ids,
		     int *count)
{
	VABufferID id;
	VAEncMiscParameterBuffer *misc = NULL;

	if (!check(e, vaCreateBuffer(e->dpy, e->context, VAEncMiscParameterBufferType,
				     (unsigned)(sizeof(VAEncMiscParameterBuffer) + size), 1, NULL, &id),
		   "vaCreateBuffer(misc)"))
		return false;
	if (!check(e, vaMapBuffer(e->dpy, id, (void **)&misc), "vaMapBuffer(misc)")) {
		vaDestroyBuffer(e->dpy, id);
		return false;
	}
	misc->type = type;
	memcpy(misc->data, payload, size);
	vaUnmapBuffer(e->dpy, id);
	ids[(*count)++] = id;
	return true;
}

static bool add_packed(struct va_encoder *e, uint32_t type, const uint8_t *data, size_t size, VABufferID *ids, int *count)
{
	VAEncPackedHeaderParameterBuffer header = {
		.type = type,
		.bit_length = (uint32_t)(size * 8),
		/* HEVC's headers come with their emulation prevention bytes in; AV1 has none. */
		.has_emulation_bytes = is_av1(e) ? 0 : 1,
	};
	return add_buffer(e, VAEncPackedHeaderParameterBufferType, &header, sizeof(header), ids, count) &&
	       add_buffer(e, VAEncPackedHeaderDataBufferType, data, size, ids, count);
}

/* How many bits the stream may use, for either codec. */
static bool add_rate_control(struct va_encoder *e, VABufferID *ids, int *count)
{
	const struct encoder_config *c = &e->config;
	uint32_t bits_per_second = c->bitrate_kbps * 1000;
	double buffer_frames = c->buffer_frames > 0 ? c->buffer_frames : 2.0;
	uint32_t buffer_bits = (uint32_t)((double)bits_per_second / (double)c->fps * buffer_frames);

	VAEncMiscParameterRateControl rc = {
		.bits_per_second = bits_per_second,
		.target_percentage = 100,
		.window_size = (uint32_t)(1000.0 * buffer_frames / (double)c->fps) + 1,
		/* Never pad a quiet frame up to the bitrate, and never skip a frame to stay under it. */
		.rc_flags.bits = { .disable_bit_stuffing = 1, .disable_frame_skip = 1 },
	};
	VAEncMiscParameterHRD hrd = {
		.initial_buffer_fullness = buffer_bits / 2,
		.buffer_size = buffer_bits,
	};
	VAEncMiscParameterFrameRate rate = { .framerate = c->fps };

	if (!add_misc(e, VAEncMiscParameterTypeRateControl, &rc, sizeof(rc), ids, count) ||
	    !add_misc(e, VAEncMiscParameterTypeHRD, &hrd, sizeof(hrd), ids, count) ||
	    !add_misc(e, VAEncMiscParameterTypeFrameRate, &rate, sizeof(rate), ids, count))
		return false;

	if (c->max_frame_bytes) {
		/* This struct opens with a copy of the type the misc header already carries.
		 * libva marks that field deprecated, but drivers still read the struct with it
		 * in place, so it is filled by position rather than by its name. */
		VAEncMiscParameterBufferMaxFrameSize cap;
		uint32_t type = VAEncMiscParameterTypeMaxFrameSize;
		memset(&cap, 0, sizeof(cap));
		memcpy(&cap, &type, sizeof(type));
		cap.max_frame_size = c->max_frame_bytes * 8; /* in bits */
		if (!add_misc(e, VAEncMiscParameterTypeMaxFrameSize, &cap, sizeof(cap), ids, count)) return false;
	}
	return true;
}

/* Sent with every HEVC keyframe: what the stream is, and how many bits it may use. */
static bool add_sequence_hevc(struct va_encoder *e, VABufferID *ids, int *count)
{
	const struct encoder_config *c = &e->config;
	uint32_t gop = c->gop ? c->gop : 0x7fffffff;

	VAEncSequenceParameterBufferHEVC seq = {
		.general_profile_idc = 1, /* Main */
		.general_level_idc = e->stream.level_idc,
		.general_tier_flag = e->stream.tier,
		.intra_period = gop,
		.intra_idr_period = gop,
		.ip_period = 1, /* no B frames: nothing is ever held back for reordering */
		.bits_per_second = c->bitrate_kbps * 1000,
		.pic_width_in_luma_samples = (uint16_t)e->stream.coded_width,
		.pic_height_in_luma_samples = (uint16_t)e->stream.coded_height,
		.seq_fields.bits = {
			.chroma_format_idc = 1,
			.strong_intra_smoothing_enabled_flag = 1,
			.amp_enabled_flag = 1,
			.sample_adaptive_offset_enabled_flag = 1,
		},
		/* 8 to 64 for coding blocks, 4 to 32 for transforms: the one set this encoder has. */
		.log2_min_luma_coding_block_size_minus3 = 0,
		.log2_diff_max_min_luma_coding_block_size = 3,
		.log2_min_transform_block_size_minus2 = 0,
		.log2_diff_max_min_transform_block_size = 3,
	};
	return add_buffer(e, VAEncSequenceParameterBufferType, &seq, sizeof(seq), ids, count) &&
	       add_rate_control(e, ids, count);
}

/* The HEVC part of a frame: picture parameters, the slice header the driver works from,
 * and the slice. */
static bool add_picture_hevc(struct va_encoder *e, bool keyframe, int previous, VABufferID *ids, int *count)
{
	const struct encoder_config *c = &e->config;
	uint8_t headers[HEVC_HEADERS_MAX];

	if (keyframe) {
		if (!add_sequence_hevc(e, ids, count)) return false;
		size_t size = hevc_write_parameter_sets(&e->stream, headers);
		if (!add_packed(e, VAEncPackedHeaderSequence, headers, size, ids, count)) return false;
	}

	VAEncPictureParameterBufferHEVC pic = {
		.decoded_curr_pic = { .picture_id = e->recon[e->current], .pic_order_cnt = (int32_t)e->since_keyframe },
		.coded_buf = e->coded,
		.collocated_ref_pic_index = 0xff,
		.pic_init_qp = 26,
		.nal_unit_type = keyframe ? NAL_IDR_W_RADL : NAL_TRAIL_R,
		.pic_fields.bits = {
			.idr_pic_flag = keyframe,
			.coding_type = keyframe ? 1 : 2,
			.reference_pic_flag = 1,
			.transform_skip_enabled_flag = 1,
			.cu_qp_delta_enabled_flag = 1, /* rate control moves the QP inside a picture */
			.pps_loop_filter_across_slices_enabled_flag = 1,
		},
	};
	for (size_t i = 0; i < sizeof(pic.reference_frames) / sizeof(pic.reference_frames[0]); i++)
		pic.reference_frames[i] = (VAPictureHEVC){ .picture_id = VA_INVALID_ID, .flags = VA_PICTURE_HEVC_INVALID };
	if (!keyframe)
		pic.reference_frames[0] = (VAPictureHEVC){
			.picture_id = e->recon[previous],
			.pic_order_cnt = (int32_t)e->since_keyframe - 1,
			.flags = VA_PICTURE_HEVC_RPS_ST_CURR_BEFORE,
		};
	if (!add_buffer(e, VAEncPictureParameterBufferType, &pic, sizeof(pic), ids, count)) return false;

	size_t header_size = hevc_write_slice_header(&e->stream, keyframe, e->since_keyframe, headers);
	if (!add_packed(e, VAEncPackedHeaderSlice, headers, header_size, ids, count)) return false;

	uint32_t ctbs = ((c->width + CTB_SIZE - 1) / CTB_SIZE) * ((c->height + CTB_SIZE - 1) / CTB_SIZE);
	VAEncSliceParameterBufferHEVC slice = {
		.slice_segment_address = 0,
		.num_ctu_in_slice = ctbs,
		.slice_type = keyframe ? SLICE_I : SLICE_P,
		.max_num_merge_cand = 5,
		.slice_fields.bits = {
			.last_slice_of_pic_flag = 1,
			.slice_sao_luma_flag = 1,
			.slice_sao_chroma_flag = 1,
			.slice_loop_filter_across_slices_enabled_flag = 1,
			.collocated_from_l0_flag = 1,
		},
	};
	for (size_t i = 0; i < sizeof(slice.ref_pic_list0) / sizeof(slice.ref_pic_list0[0]); i++) {
		slice.ref_pic_list0[i] = (VAPictureHEVC){ .picture_id = VA_INVALID_ID, .flags = VA_PICTURE_HEVC_INVALID };
		slice.ref_pic_list1[i] = slice.ref_pic_list0[i];
	}
	if (!keyframe) slice.ref_pic_list0[0] = pic.reference_frames[0];
	return add_buffer(e, VAEncSliceParameterBufferType, &slice, sizeof(slice), ids, count);
}

/*
 * The AV1 part of a frame. Two reconstruction surfaces take turns, and each frame's
 * reference slot is its surface's number: a key frame fills every slot, and each inter
 * frame reads the slot the frame before it wrote and writes the other one. That is all
 * the reference structure a stream that never looks ahead needs.
 */
static bool add_picture_av1(struct va_encoder *e, bool keyframe, int previous, VABufferID *ids, int *count)
{
	const struct encoder_config *c = &e->config;
	uint8_t header[AV1_HEADERS_MAX];

	if (keyframe) {
		uint32_t gop = c->gop ? c->gop : 0x7fffffff;
		VAEncSequenceParameterBufferAV1 seq = {
			.seq_profile = 0,
			.seq_level_idx = e->av1.level_idx,
			.seq_tier = e->av1.tier,
			.intra_period = gop,
			.ip_period = 1,
			.bits_per_second = c->bitrate_kbps * 1000,
			.seq_fields.bits = {
				.enable_order_hint = 1,
				.enable_cdef = 1,
				.subsampling_x = 1,
				.subsampling_y = 1,
			},
			.order_hint_bits_minus_1 = AV1_ORDER_HINT_BITS - 1,
		};
		if (!add_buffer(e, VAEncSequenceParameterBufferType, &seq, sizeof(seq), ids, count) ||
		    !add_rate_control(e, ids, count) ||
		    !add_packed(e, VAEncPackedHeaderSequence, e->av1_sequence, e->av1_sequence_size, ids, count))
			return false;
	}

	struct av1_frame f = {
		.keyframe = keyframe,
		.order_hint = (uint8_t)(e->since_keyframe & ((1u << AV1_ORDER_HINT_BITS) - 1)),
		.refresh_frame_flags = keyframe ? 0xff : (uint8_t)(1u << e->current),
		.ref_slot = (uint8_t)previous,
		.base_q_idx = 128,
	};
	size_t header_size = av1_write_frame_header(&e->av1, &f, header);

	VAEncPictureParameterBufferAV1 pic = {
		.frame_width_minus_1 = (uint16_t)(c->width - 1),
		.frame_height_minus_1 = (uint16_t)(c->height - 1),
		.reconstructed_frame = e->recon[e->current],
		.coded_buf = e->coded,
		.primary_ref_frame = keyframe ? AV1_PRIMARY_REF_NONE : 0,
		.order_hint = f.order_hint,
		.refresh_frame_flags = f.refresh_frame_flags,
		.picture_flags.bits = {
			.frame_type = keyframe ? 0 : 1, /* KEY_FRAME, INTER_FRAME */
			.error_resilient_mode = keyframe,
			.enable_frame_obu = 0, /* a frame header and a tile group, as the header says */
		},
		.filter_level = { f.loop_filter_level[0], f.loop_filter_level[1] },
		.filter_level_u = f.loop_filter_level[2],
		.filter_level_v = f.loop_filter_level[3],
		.interpolation_filter = 0,
		.base_qindex = f.base_q_idx,
		.min_base_qindex = 1,
		.max_base_qindex = 255,
		.mode_control_flags.bits = {
			.tx_mode = e->av1.tx_mode_select ? 2 : 1, /* TX_MODE_SELECT, TX_MODE_LARGEST */
			.reference_mode = 0,                       /* single reference */
		},
		.tile_cols = 1,
		.tile_rows = 1,
		.cdef_damping_minus_3 = f.cdef_damping_minus_3,
		.cdef_bits = f.cdef_bits,
		.bit_offset_qindex = f.bit_offset_qindex,
		.bit_offset_loopfilter_params = f.bit_offset_loopfilter,
		.bit_offset_cdef_params = f.bit_offset_cdef,
		.size_in_bits_cdef_params = f.size_in_bits_cdef,
		.size_in_bits_frame_hdr_obu = f.size_in_bits,
		/* Counted as drivers that patch the header count it: past the sequence header
		 * when one goes with the frame, then past the frame header's one header byte. */
		.byte_offset_frame_hdr_obu_size = (uint32_t)((keyframe ? e->av1_sequence_size : 0) + 1),
		.tile_group_obu_hdr_info.bits = { .obu_has_size_field = 1 },
	};
	pic.width_in_sbs_minus_1[0] = (uint16_t)(e->av1.sb_cols - 1);
	pic.height_in_sbs_minus_1[0] = (uint16_t)(e->av1.sb_rows - 1);
	memcpy(pic.cdef_y_strengths, f.cdef_y_strengths, sizeof(pic.cdef_y_strengths));
	memcpy(pic.cdef_uv_strengths, f.cdef_uv_strengths, sizeof(pic.cdef_uv_strengths));
	for (int i = 0; i < 8; i++) pic.reference_frames[i] = VA_INVALID_SURFACE;
	for (int i = 0; i < 7; i++) pic.ref_frame_idx[i] = keyframe ? 0 : (uint8_t)previous;
	if (!keyframe) {
		pic.reference_frames[previous] = e->recon[previous];
		pic.ref_frame_ctrl_l0.fields.search_idx0 = AV1_REF_LAST;
	}

	VAEncTileGroupBufferAV1 tile_group = { .tg_start = 0, .tg_end = 0 };
	return add_buffer(e, VAEncPictureParameterBufferType, &pic, sizeof(pic), ids, count) &&
	       add_packed(e, VAEncPackedHeaderPicture, header, header_size, ids, count) &&
	       add_buffer(e, VAEncSliceParameterBufferType, &tile_group, sizeof(tile_group), ids, count);
}

bool va_encoder_encode(void *encoder, const uint8_t *nv12, uint32_t stride, bool force_keyframe,
		       const struct overlay *overlay, struct encoded_frame *out)
{
	struct va_encoder *e = encoder;
	const struct encoder_config *c = &e->config;
	bool keyframe = force_keyframe || e->frames == 0 || (c->gop && e->since_keyframe >= c->gop);
	VABufferID ids[16];
	int count = 0;
	bool ok = false;

	uint64_t start = now_ns();
	if (!upload(e, nv12, stride, overlay)) return false;
	uint64_t uploaded = now_ns();

	if (keyframe) e->since_keyframe = 0;
	int previous = e->current ^ 1;

	if (!(is_av1(e) ? add_picture_av1(e, keyframe, previous, ids, &count)
			: add_picture_hevc(e, keyframe, previous, ids, &count)))
		goto done;

	if (!check(e, vaBeginPicture(e->dpy, e->context, e->input), "vaBeginPicture")) goto done;
	if (!check(e, vaRenderPicture(e->dpy, e->context, ids, count), "vaRenderPicture")) {
		vaEndPicture(e->dpy, e->context);
		goto done;
	}
	if (!check(e, vaEndPicture(e->dpy, e->context), "vaEndPicture")) goto done;
	if (!check(e, vaSyncSurface(e->dpy, e->input), "vaSyncSurface")) goto done;

	VACodedBufferSegment *segment = NULL;
	if (!check(e, vaMapBuffer(e->dpy, e->coded, (void **)&segment), "vaMapBuffer(coded)")) goto done;

	/* An AV1 temporal unit begins with a temporal delimiter, which no encoder writes. */
	uint8_t delimiter[2];
	size_t lead = is_av1(e) ? av1_write_temporal_delimiter(delimiter) : 0;

	size_t total = lead;
	for (VACodedBufferSegment *s = segment; s; s = s->next) total += s->size;
	if (total > e->out_cap) {
		uint8_t *grown = realloc(e->out, total + total / 2);
		if (grown) {
			e->out = grown;
			e->out_cap = total + total / 2;
		}
	}
	if (total <= e->out_cap && total > lead) {
		memcpy(e->out, delimiter, lead);
		size_t at = lead;
		for (VACodedBufferSegment *s = segment; s; s = s->next) {
			memcpy(e->out + at, s->buf, s->size);
			at += s->size;
		}
		ok = true;
	} else {
		snprintf(e->error, sizeof(e->error), total > lead ? "out of memory for a %zu-byte frame" : "the encoder returned no data", total);
	}
	vaUnmapBuffer(e->dpy, e->coded);

	if (ok) {
		*out = (struct encoded_frame){
			.data = e->out,
			.size = total,
			.keyframe = keyframe,
			.upload_ns = uploaded - start,
			.encode_ns = now_ns() - uploaded,
		};
		e->frames++;
		e->since_keyframe++;
		e->current = previous;
	}

done:
	for (int i = 0; i < count; i++) vaDestroyBuffer(e->dpy, ids[i]);
	return ok;
}

const char *va_encoder_describe(const void *encoder)
{
	return ((const struct va_encoder *)encoder)->description;
}

const char *va_encoder_error(const void *encoder)
{
	return ((const struct va_encoder *)encoder)->error;
}

void va_encoder_close(void *encoder)
{
	struct va_encoder *e = encoder;
	if (!e) return;
	if (e->initialized) {
		if (e->image.image_id != VA_INVALID_ID) vaDestroyImage(e->dpy, e->image.image_id);
		if (e->coded != VA_INVALID_ID) vaDestroyBuffer(e->dpy, e->coded);
		if (e->context != VA_INVALID_ID) vaDestroyContext(e->dpy, e->context);
		if (e->input != VA_INVALID_SURFACE) {
			VASurfaceID surfaces[3] = { e->input, e->recon[0], e->recon[1] };
			vaDestroySurfaces(e->dpy, surfaces, 3);
		}
		if (e->va_config != VA_INVALID_ID) vaDestroyConfig(e->dpy, e->va_config);
		vaTerminate(e->dpy);
	}
	if (e->fd >= 0) close(e->fd);
	free(e->out);
	free(e);
}
