#define _GNU_SOURCE

#include <dlfcn.h>
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

#include "common.h"
#include "encoder_impl.h"

/*
 * PyroWave, through the codec's own library. It is loaded the first time it is asked for
 * and looked for beside this program first, so the sender runs as before without it and
 * gains the codec by having the file put next to it.
 *
 * The codec runs on the GPU's shaders through Vulkan, on a compute queue so that it can
 * overlap a game's drawing rather than queue behind it. The picture comes from CPU memory,
 * as everything here does; what it costs was measured in src/rayxel-send/test/pyrowave.
 */

#ifdef HAVE_PYROWAVE

#include <vulkan/vulkan.h>

#include "pyrowave.h"

#define LIBRARY "libpyrowave-shared.so.1"

static struct {
	bool tried, loaded;
	char problem[256];
	void *handle;
	pyrowave_device device;

	void (*get_api_version)(uint32_t *, uint32_t *, uint32_t *);
	pyrowave_result (*create_default_device)(pyrowave_device *);
	pyrowave_result (*device_set_queue_type)(pyrowave_device, VkQueueFlagBits);
	pyrowave_result (*encoder_create)(const pyrowave_encoder_create_info *, pyrowave_encoder *);
	pyrowave_result (*encoder_encode_cpu)(pyrowave_encoder, const pyrowave_cpu_buffer *, const pyrowave_rate_control *);
	pyrowave_result (*encoder_compute_num_packets)(pyrowave_encoder, size_t, size_t *);
	pyrowave_result (*encoder_packetize)(pyrowave_encoder, pyrowave_packet *, size_t, size_t *, void *, size_t);
	void (*encoder_destroy)(pyrowave_encoder);
} lib;

/* Beside the executable, then wherever the system's loader would find it. */
static void *open_library(void)
{
	char self[PATH_MAX];
	ssize_t n = readlink("/proc/self/exe", self, sizeof(self) - 1);
	if (n > 0) {
		self[n] = '\0';
		char *slash = strrchr(self, '/');
		if (slash && (size_t)(slash - self) + sizeof("/" LIBRARY) < sizeof(self)) {
			strcpy(slash + 1, LIBRARY);
			void *handle = dlopen(self, RTLD_NOW | RTLD_LOCAL);
			if (handle) return handle;
		}
	}
	return dlopen(LIBRARY, RTLD_NOW | RTLD_LOCAL);
}

/* Loads the library and makes the one Vulkan device every encoder shares: making one takes
 * long enough to be felt when the bitrate or the size changes, and there is only one GPU. */
static bool load(void)
{
	if (lib.tried) return lib.loaded;
	lib.tried = true;

	lib.handle = open_library();
	if (!lib.handle) {
		snprintf(lib.problem, sizeof(lib.problem), "%s is not beside rayxel-send or anywhere the loader looks", LIBRARY);
		return false;
	}

#define FIND(field, name)                                                                                      \
	do {                                                                                                   \
		*(void **)&lib.field = dlsym(lib.handle, name);                                               \
		if (!lib.field) {                                                                              \
			snprintf(lib.problem, sizeof(lib.problem), "%s has no %s - another version?", LIBRARY, name); \
			return false;                                                                          \
		}                                                                                              \
	} while (0)

	FIND(get_api_version, "pyrowave_get_api_version");
	FIND(create_default_device, "pyrowave_create_default_device");
	FIND(device_set_queue_type, "pyrowave_device_set_queue_type");
	FIND(encoder_create, "pyrowave_encoder_create");
	FIND(encoder_encode_cpu, "pyrowave_encoder_encode_cpu");
	FIND(encoder_compute_num_packets, "pyrowave_encoder_compute_num_packets");
	FIND(encoder_packetize, "pyrowave_encoder_packetize");
	FIND(encoder_destroy, "pyrowave_encoder_destroy");
#undef FIND

	uint32_t major = 0, minor = 0, patch = 0;
	lib.get_api_version(&major, &minor, &patch);
	if (major != PYROWAVE_API_VERSION_MAJOR) {
		snprintf(lib.problem, sizeof(lib.problem), "%s speaks version %u of the interface, this program %u", LIBRARY,
			 major, PYROWAVE_API_VERSION_MAJOR);
		return false;
	}

	if (lib.create_default_device(&lib.device) != PYROWAVE_SUCCESS) {
		snprintf(lib.problem, sizeof(lib.problem), "the codec found no Vulkan device it can use");
		return false;
	}
	if (lib.device_set_queue_type(lib.device, VK_QUEUE_COMPUTE_BIT) != PYROWAVE_SUCCESS)
		printf("pyrowave : no separate compute queue; encoding on the graphics one, behind whatever draws\n");

	lib.loaded = true;
	return true;
}

struct pw_encoder {
	struct encoder_config config;
	pyrowave_encoder encoder;
	size_t cap_bytes;

	uint8_t *scratch; /* the picture with the pointer laid over it, when there is one */
	uint8_t *bitstream;
	size_t bitstream_cap;
	pyrowave_packet packet;

	char description[256];
	char error[256];
};

void *pw_encoder_open(const struct encoder_config *config, char *why, size_t why_cap)
{
	if (!load()) {
		if (why) snprintf(why, why_cap, "%s", lib.problem);
		return NULL;
	}

	struct pw_encoder *e = calloc(1, sizeof(*e));
	if (!e) return NULL;
	e->config = *config;

	uint32_t w = config->width, h = config->height;
	uint32_t fps = config->fps ? config->fps : 60;
	e->cap_bytes = (size_t)config->bitrate_kbps * 1000 / 8 / fps;

	pyrowave_encoder_create_info info = {
		.device = lib.device,
		.width = (int)w,
		.height = (int)h,
		.chroma = PYROWAVE_CHROMA_SUBSAMPLING_420,
	};
	if ((w & 1) || (h & 1) || lib.encoder_create(&info, &e->encoder) != PYROWAVE_SUCCESS) {
		if (why) snprintf(why, why_cap, "the codec will not encode %ux%u", w, h);
		free(e);
		return NULL;
	}

	/* Room for a frame at the cap and the packets' own headers; the codec never exceeds
	 * the cap, so this is never grown. */
	e->bitstream_cap = e->cap_bytes + (64u << 10);
	e->bitstream = malloc(e->bitstream_cap);
	e->scratch = malloc((size_t)w * h * 3 / 2);
	if (!e->bitstream || !e->scratch) {
		if (why) snprintf(why, why_cap, "out of memory");
		pw_encoder_close(e);
		return NULL;
	}

	snprintf(e->description, sizeof(e->description),
		 "PyroWave %ux%u 4:2:0, at most %.0f kB a frame (%.1f Mbit/s at %u fps), every frame a keyframe, GPU compute queue",
		 w, h, e->cap_bytes / 1024.0, config->bitrate_kbps / 1000.0, fps);
	return e;
}

bool pw_encoder_available(char *why, size_t why_cap)
{
	if (load()) return true;
	if (why) snprintf(why, why_cap, "%s", lib.problem);
	return false;
}

bool pw_encoder_encode(void *encoder, const uint8_t *nv12, uint32_t stride, bool force_keyframe,
		       const struct overlay *overlay, struct encoded_frame *out)
{
	struct pw_encoder *e = encoder;
	uint32_t w = e->config.width, h = e->config.height;
	uint64_t start = now_ns();

	/* With a pointer to draw, the picture is copied first: the codec reads the frame as
	 * it lies, and the frame belongs to the capture. */
	const uint8_t *picture = nv12;
	uint32_t pitch = stride;
	if (overlay) {
		uint8_t *luma = e->scratch, *chroma = e->scratch + (size_t)w * h;
		for (uint32_t y = 0; y < h; y++) memcpy(luma + (size_t)y * w, nv12 + (size_t)y * stride, w);
		for (uint32_t y = 0; y < h / 2; y++)
			memcpy(chroma + (size_t)y * w, nv12 + (size_t)stride * h + (size_t)y * stride, w);
		overlay_blend_nv12(overlay, nv12, stride, luma, w, chroma, w, w, h);
		picture = e->scratch;
		pitch = w;
	}

	pyrowave_cpu_buffer buffer = { 0 };
	buffer.data[0] = (void *)picture;
	buffer.data[1] = (void *)(picture + (size_t)pitch * h);
	buffer.data[2] = buffer.data[1];
	buffer.row_stride_in_bytes[0] = pitch;
	buffer.row_stride_in_bytes[1] = pitch;
	buffer.plane_size_in_bytes[0] = (size_t)pitch * h;
	buffer.plane_size_in_bytes[1] = (size_t)pitch * (h / 2);
	buffer.width = (int)w;
	buffer.height = (int)h;
	buffer.format = PYROWAVE_CPU_BUFFER_FORMAT_NV12;

	pyrowave_rate_control rate = { .maximum_bitstream_size = e->cap_bytes };
	if (lib.encoder_encode_cpu(e->encoder, &buffer, &rate) != PYROWAVE_SUCCESS) {
		snprintf(e->error, sizeof(e->error), "the codec refused the picture");
		return false;
	}
	uint64_t handed = now_ns();

	/* Counting the packets is what waits for the GPU. They are asked for as one: the
	 * frame is cut into datagrams like any other, the codec's packets one after another
	 * with nothing between them, and the decoder takes them back all at once. Packets of
	 * the codec's own size, one per datagram, would let a frame be shown with a piece
	 * missing - but a packet is at least one 32x32 block, and a block of fine detail at a
	 * high rate is larger than a datagram may be. IP splitting those has the pieces
	 * arrive out of order, and lost more frames than whole-or-nothing ever does. */
	size_t count = 0, made = 0;
	if (lib.encoder_compute_num_packets(e->encoder, e->bitstream_cap, &count) != PYROWAVE_SUCCESS || count != 1 ||
	    lib.encoder_packetize(e->encoder, &e->packet, e->bitstream_cap, &made, e->bitstream, e->bitstream_cap) !=
		    PYROWAVE_SUCCESS ||
	    made != 1) {
		snprintf(e->error, sizeof(e->error), "the codec could not hand over the frame");
		return false;
	}

	*out = (struct encoded_frame){
		.data = e->bitstream + e->packet.offset,
		.size = e->packet.size,
		.keyframe = true,
		.upload_ns = handed - start,
		.encode_ns = now_ns() - handed,
	};
	(void)force_keyframe; /* every frame is one */
	return e->packet.size > 0;
}

const char *pw_encoder_describe(const void *encoder)
{
	return ((const struct pw_encoder *)encoder)->description;
}

const char *pw_encoder_error(const void *encoder)
{
	return ((const struct pw_encoder *)encoder)->error;
}

void pw_encoder_close(void *encoder)
{
	struct pw_encoder *e = encoder;
	if (!e) return;
	if (e->encoder) lib.encoder_destroy(e->encoder);
	free(e->scratch);
	free(e->bitstream);
	free(e);
}

#else /* !HAVE_PYROWAVE */

/* Built without the codec's header: the codec is simply not there. */

void *pw_encoder_open(const struct encoder_config *config, char *why, size_t why_cap)
{
	if (why) snprintf(why, why_cap, "this build of rayxel-send has no PyroWave");
	return NULL;
}

bool pw_encoder_available(char *why, size_t why_cap)
{
	if (why) snprintf(why, why_cap, "this build of rayxel-send has no PyroWave");
	return false;
}

bool pw_encoder_encode(void *encoder, const uint8_t *nv12, uint32_t stride, bool force_keyframe,
		       const struct overlay *overlay, struct encoded_frame *out)
{
	return false;
}

const char *pw_encoder_describe(const void *encoder)
{
	return "";
}

const char *pw_encoder_error(const void *encoder)
{
	return "this build of rayxel-send has no PyroWave";
}

void pw_encoder_close(void *encoder)
{
}

#endif
