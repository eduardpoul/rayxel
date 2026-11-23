#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

#include <va/va.h>
#include <va/va_drm.h>

#include "common.h"
#include "encoder.h"
#include "hevc_headers.h"

/* The Deck's encoder offers exactly one coding tree block size (see --probe). */
#define CTB_SIZE 64

/* HEVC nal_unit_type and slice_type values used here. */
#define NAL_TRAIL_R 1
#define NAL_IDR_W_RADL 19
#define SLICE_P 1
#define SLICE_I 2

struct encoder {
	struct encoder_config config;
	int fd;
	VADisplay dpy;
	bool initialized;
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

	uint64_t frames;         /* encoded so far */
	uint32_t since_keyframe; /* also the picture order count */
	int current;             /* which reconstruction surface this frame writes */

	uint8_t *out;
	size_t out_cap;

	char description[256];
	char error[256];
};

static bool check(struct encoder *e, VAStatus status, const char *what)
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

static bool has_hevc_encoder(VADisplay dpy)
{
	int count = vaMaxNumEntrypoints(dpy);
	VAEntrypoint *entrypoints = calloc((size_t)(count > 0 ? count : 1), sizeof(*entrypoints));
	bool found = false;

	if (entrypoints && vaQueryConfigEntrypoints(dpy, VAProfileHEVCMain, entrypoints, &count) == VA_STATUS_SUCCESS)
		for (int i = 0; i < count; i++)
			if (entrypoints[i] == VAEntrypointEncSlice) found = true;
	free(entrypoints);
	return found;
}

static bool open_display(struct encoder *e)
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
				if (has_hevc_encoder(dpy)) {
					e->fd = fd;
					e->dpy = dpy;
					e->initialized = true;
					return true;
				}
				vaTerminate(dpy);
			}
		}
		close(fd);
	}
	snprintf(e->error, sizeof(e->error), "no GPU with a hardware HEVC encoder was found");
	return false;
}

/* How the picture gets into the input surface: mapped directly if the driver allows it,
 * otherwise through an image of our own that the driver then copies in. */
static bool choose_upload(struct encoder *e)
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

struct encoder *encoder_open(const struct encoder_config *config)
{
	struct encoder *e = calloc(1, sizeof(*e));
	if (!e) return NULL;
	e->config = *config;
	e->fd = -1;
	e->va_config = e->context = VA_INVALID_ID;
	e->input = e->recon[0] = e->recon[1] = VA_INVALID_SURFACE;
	e->coded = VA_INVALID_ID;
	e->image.image_id = VA_INVALID_ID;

	uint32_t w = config->width, h = config->height;
	if (w < 130 || h < 128 || (w & 1) || (h & 1)) {
		snprintf(e->error, sizeof(e->error), "%ux%u cannot be encoded: both sides must be even and at least 130x128", w, h);
		goto fail;
	}
	if (!open_display(e)) goto fail;

	/* The driver writes no parameter set and no usable slice header unless it is given
	 * one to work from; see hevc_headers.h. */
	VAConfigAttrib attribs[] = {
		{ VAConfigAttribRTFormat, VA_RT_FORMAT_YUV420 },
		{ VAConfigAttribRateControl, VA_RC_CBR },
		{ VAConfigAttribEncPackedHeaders,
		  VA_ENC_PACKED_HEADER_SEQUENCE | VA_ENC_PACKED_HEADER_PICTURE | VA_ENC_PACKED_HEADER_SLICE },
	};
	if (!check(e, vaCreateConfig(e->dpy, VAProfileHEVCMain, VAEntrypointEncSlice, attribs, 3, &e->va_config),
		   "vaCreateConfig"))
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
	fprintf(stderr, "encoder: %s\n", e->error);
	encoder_close(e);
	return NULL;
}

static bool upload(struct encoder *e, const uint8_t *nv12, uint32_t stride, const struct overlay *overlay)
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

static bool add_buffer(struct encoder *e, VABufferType type, const void *data, size_t size, VABufferID *ids, int *count)
{
	VABufferID id;
	if (!check(e, vaCreateBuffer(e->dpy, e->context, type, (unsigned)size, 1, (void *)data, &id), "vaCreateBuffer"))
		return false;
	ids[(*count)++] = id;
	return true;
}

static bool add_misc(struct encoder *e, VAEncMiscParameterType type, const void *payload, size_t size, VABufferID *ids,
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

static bool add_packed(struct encoder *e, uint32_t type, const uint8_t *data, size_t size, VABufferID *ids, int *count)
{
	VAEncPackedHeaderParameterBuffer header = {
		.type = type,
		.bit_length = (uint32_t)(size * 8),
		.has_emulation_bytes = 1,
	};
	return add_buffer(e, VAEncPackedHeaderParameterBufferType, &header, sizeof(header), ids, count) &&
	       add_buffer(e, VAEncPackedHeaderDataBufferType, data, size, ids, count);
}

/* Sent with every keyframe: what the stream is, and how many bits it may use. */
static bool add_sequence(struct encoder *e, VABufferID *ids, int *count)
{
	const struct encoder_config *c = &e->config;
	uint32_t bits_per_second = c->bitrate_kbps * 1000;
	uint32_t gop = c->gop ? c->gop : 0x7fffffff;

	VAEncSequenceParameterBufferHEVC seq = {
		.general_profile_idc = 1, /* Main */
		.general_level_idc = e->stream.level_idc,
		.general_tier_flag = e->stream.tier,
		.intra_period = gop,
		.intra_idr_period = gop,
		.ip_period = 1, /* no B frames: nothing is ever held back for reordering */
		.bits_per_second = bits_per_second,
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
	if (!add_buffer(e, VAEncSequenceParameterBufferType, &seq, sizeof(seq), ids, count)) return false;

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

bool encoder_encode(struct encoder *e, const uint8_t *nv12, uint32_t stride, bool force_keyframe,
		    const struct overlay *overlay, struct encoded_frame *out)
{
	const struct encoder_config *c = &e->config;
	bool keyframe = force_keyframe || e->frames == 0 || (c->gop && e->since_keyframe >= c->gop);
	VABufferID ids[16];
	uint8_t headers[HEVC_HEADERS_MAX];
	int count = 0;
	bool ok = false;

	uint64_t start = now_ns();
	if (!upload(e, nv12, stride, overlay)) return false;
	uint64_t uploaded = now_ns();

	if (keyframe) e->since_keyframe = 0;
	int previous = e->current ^ 1;

	if (keyframe) {
		if (!add_sequence(e, ids, &count)) goto done;
		size_t size = hevc_write_parameter_sets(&e->stream, headers);
		if (!add_packed(e, VAEncPackedHeaderSequence, headers, size, ids, &count)) goto done;
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
	if (!add_buffer(e, VAEncPictureParameterBufferType, &pic, sizeof(pic), ids, &count)) goto done;

	size_t header_size = hevc_write_slice_header(&e->stream, keyframe, e->since_keyframe, headers);
	if (!add_packed(e, VAEncPackedHeaderSlice, headers, header_size, ids, &count)) goto done;

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
	if (!add_buffer(e, VAEncSliceParameterBufferType, &slice, sizeof(slice), ids, &count)) goto done;

	if (!check(e, vaBeginPicture(e->dpy, e->context, e->input), "vaBeginPicture")) goto done;
	if (!check(e, vaRenderPicture(e->dpy, e->context, ids, count), "vaRenderPicture")) {
		vaEndPicture(e->dpy, e->context);
		goto done;
	}
	if (!check(e, vaEndPicture(e->dpy, e->context), "vaEndPicture")) goto done;
	if (!check(e, vaSyncSurface(e->dpy, e->input), "vaSyncSurface")) goto done;

	VACodedBufferSegment *segment = NULL;
	if (!check(e, vaMapBuffer(e->dpy, e->coded, (void **)&segment), "vaMapBuffer(coded)")) goto done;

	size_t total = 0;
	for (VACodedBufferSegment *s = segment; s; s = s->next) total += s->size;
	if (total > e->out_cap) {
		uint8_t *grown = realloc(e->out, total + total / 2);
		if (grown) {
			e->out = grown;
			e->out_cap = total + total / 2;
		}
	}
	if (total <= e->out_cap && total > 0) {
		size_t at = 0;
		for (VACodedBufferSegment *s = segment; s; s = s->next) {
			memcpy(e->out + at, s->buf, s->size);
			at += s->size;
		}
		ok = true;
	} else {
		snprintf(e->error, sizeof(e->error), total ? "out of memory for a %zu-byte frame" : "the encoder returned no data", total);
	}
	vaUnmapBuffer(e->dpy, e->coded);

	if (ok) {
		out->data = e->out;
		out->size = total;
		out->keyframe = keyframe;
		out->upload_ns = uploaded - start;
		out->encode_ns = now_ns() - uploaded;

		e->frames++;
		e->since_keyframe++;
		e->current = previous;
	}

done:
	for (int i = 0; i < count; i++) vaDestroyBuffer(e->dpy, ids[i]);
	return ok;
}

const char *encoder_describe(const struct encoder *e)
{
	return e->description;
}

const char *encoder_error(const struct encoder *e)
{
	return e->error;
}

void encoder_close(struct encoder *e)
{
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
