#define _GNU_SOURCE

#include <arpa/inet.h>
#include <errno.h>
#include <netinet/in.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <unistd.h>

#include <pipewire/pipewire.h>
#include <spa/param/video/color.h>

#include "capture.h"
#include "common.h"
#include "cursor.h"
#include "desk.h"
#include "encoder.h"
#include "pad.h"
#include "server.h"
#include "wire.h"

/* A frame of more packets than this is not sent. At 1200 bytes each it is 1.2 MB, several
 * times what a keyframe of noise comes to at any bitrate this is used with. */
#define MAX_PACKETS 1024

/* The viewer says HELLO twice a second; this many missed in a row and it is gone. */
#define VIEWER_TIMEOUT_NS 3000000000ull

/* How recently the viewer must have been heard from for another one to be turned away.
 * Shorter than the timeout: a viewer that restarted without managing to say goodbye
 * should not have to wait the whole of it to be let back in. */
#define VIEWER_BUSY_NS 1500000000ull
#define REPORT_EVERY_NS 5000000000ull

/* With no frame to send for this long, say so instead. */
#define ALIVE_AFTER_NS 150000000ull

/* The viewer repeats its gamepad's state many times a second. This long without it and
 * the link is taken to be broken, and whatever was held is let go. */
#define PAD_TIMEOUT_NS 300000000ull

struct tally {
	uint64_t since_ns;
	uint32_t frames, keyframes, skipped;
	uint64_t bytes, packets;
	uint64_t encode_ns, encode_max_ns;
	uint64_t send_ns, send_max_ns;
	size_t largest;
};

struct server {
	const struct server_options *options;
	struct pw_main_loop *loop;
	int fd;

	/* The one viewer, when there is one. */
	bool watching;
	struct sockaddr_in viewer;
	uint32_t session;
	uint64_t last_hello_ns;
	uint64_t last_sent_ns;
	uint64_t last_refusal_ns;
	struct wire_hello wanted;

	struct capture *capture;
	struct encoder *encoder;
	struct cursor *cursor; /* the Deck's mouse pointer, which the capture does not have */
	struct capture_format format;
	bool format_changed;
	bool keyframe_wanted;
	bool broken; /* set from inside a callback; acted on by the next tick */
	char broken_reason[256];
	uint32_t frame;

	/* The viewer's gamepad, reproduced here. Made when its first state arrives, so a
	 * viewer without a controller never causes one to appear on the Deck. */
	struct pad *pad;
	bool pad_heard, input_refused;
	uint32_t pad_sequence;
	uint64_t last_pad_ns;

	/* And its keyboard and mouse, likewise. */
	struct desk *desk;
	bool keys_heard, mouse_heard;
	bool key_seen, motion_seen; /* said once per viewer: the first press and the first movement */
	int32_t mouse_x, mouse_y;
	uint32_t keys_sequence, mouse_sequence;
	uint64_t last_desk_ns;

	struct tally tally;

	struct mmsghdr messages[MAX_PACKETS];
	struct iovec parts[MAX_PACKETS][2];
	struct wire_video headers[MAX_PACKETS];
};

static const char *viewer_name(const struct server *s, char *out, size_t cap)
{
	char ip[INET_ADDRSTRLEN] = "?";
	inet_ntop(AF_INET, &s->viewer.sin_addr, ip, sizeof(ip));
	snprintf(out, cap, "%s:%u", ip, ntohs(s->viewer.sin_port));
	return out;
}

static void stop_stream(struct server *s, const char *why)
{
	if (!s->watching && !s->capture) return;
	char name[64];
	printf("stopped  : %s (%s)\n", why, viewer_name(s, name, sizeof(name)));

	/* Closing the capture is what makes gamescope stop producing pictures for us. */
	capture_close(s->capture);
	s->capture = NULL;
	encoder_close(s->encoder);
	s->encoder = NULL;
	cursor_close(s->cursor);
	s->cursor = NULL;
	pad_close(s->pad);
	s->pad = NULL;
	desk_close(s->desk);
	s->desk = NULL;
	s->watching = false;
	s->broken = false;
}

static void report(struct server *s, uint64_t now)
{
	struct tally *t = &s->tally;
	double seconds = (double)(now - t->since_ns) / 1e9;

	if (t->frames > 0 && seconds > 0)
		printf("  %5.1f fps  %6.2f Mbit/s  encode %.2f ms (max %.2f)  send %.2f ms (max %.2f)  largest %.0f kB  "
		       "%u keyframe%s%s\n",
		       t->frames / seconds, (double)t->bytes * 8 / seconds / 1e6, (double)t->encode_ns / t->frames / 1e6,
		       (double)t->encode_max_ns / 1e6, (double)t->send_ns / t->frames / 1e6, (double)t->send_max_ns / 1e6,
		       (double)t->largest / 1024.0, t->keyframes, t->keyframes == 1 ? "" : "s",
		       t->skipped ? ", some frames skipped" : "");
	else if (s->watching)
		printf("  no frames - gamescope draws only when something on screen changes\n");

	memset(t, 0, sizeof(*t));
	t->since_ns = now;
}

static void on_failed(void *user, const char *error)
{
	struct server *s = user;
	s->broken = true;
	snprintf(s->broken_reason, sizeof(s->broken_reason), "%s", error);
}

static void on_format(void *user, const struct capture_format *f)
{
	struct server *s = user;
	s->format = *f;
	s->format_changed = true;
}

static bool open_encoder(struct server *s)
{
	const struct capture_format *f = &s->format;
	uint32_t fps = s->wanted.fps ? s->wanted.fps : 60;
	uint32_t kbps = s->wanted.bitrate_kbps ? s->wanted.bitrate_kbps : 20000;

	encoder_close(s->encoder);
	struct encoder_config config = {
		.width = f->width,
		.height = f->height,
		.fps = fps,
		.bitrate_kbps = kbps,
		.buffer_frames = s->options->buffer_frames,
		.max_frame_bytes = s->options->max_frame > 0
			? (uint32_t)((double)kbps * 125.0 / (double)fps * s->options->max_frame) : 0,
		.gop = s->options->gop,
		.bt601 = f->color_matrix == SPA_VIDEO_COLOR_MATRIX_BT601,
		.full_range = f->color_range == SPA_VIDEO_COLOR_RANGE_0_255,
	};
	s->encoder = encoder_open(&config);
	s->format_changed = false;
	if (!s->encoder) {
		on_failed(s, "the encoder could not be opened");
		return false;
	}
	printf("stream   : %s\n", encoder_describe(s->encoder));
	return true;
}

/* One frame as `count` datagrams, handed to the kernel in a single call. The payloads are
 * not copied: each datagram is its own header followed by a slice of the frame. */
static bool send_frame(struct server *s, const struct encoded_frame *frame, uint64_t capture_ns, uint64_t encoded_ns)
{
	size_t count = (frame->size + WIRE_PAYLOAD_MAX - 1) / WIRE_PAYLOAD_MAX;
	if (count == 0 || count > MAX_PACKETS) return false;

	struct wire_video base = {
		.header = { .magic = WIRE_MAGIC, .version = WIRE_VERSION, .type = WIRE_VIDEO },
		.session = s->session,
		.frame = s->frame,
		.count = (uint16_t)count,
		.frame_bytes = (uint32_t)frame->size,
		.width = (uint16_t)s->format.width,
		.height = (uint16_t)s->format.height,
		.flags = frame->keyframe ? WIRE_FLAG_KEYFRAME : 0,
		.capture_time_ns = capture_ns,
		.encode_us = (uint32_t)((encoded_ns - capture_ns) / 1000),
	};

	for (size_t i = 0; i < count; i++) {
		size_t offset = i * WIRE_PAYLOAD_MAX;
		size_t length = frame->size - offset < WIRE_PAYLOAD_MAX ? frame->size - offset : WIRE_PAYLOAD_MAX;

		s->headers[i] = base;
		s->headers[i].index = (uint16_t)i;
		s->parts[i][0] = (struct iovec){ &s->headers[i], sizeof(s->headers[i]) };
		s->parts[i][1] = (struct iovec){ (void *)(frame->data + offset), length };
		s->messages[i].msg_hdr = (struct msghdr){
			.msg_name = &s->viewer,
			.msg_namelen = sizeof(s->viewer),
			.msg_iov = s->parts[i],
			.msg_iovlen = 2,
		};
	}

	size_t sent = 0;
	while (sent < count) {
		int n = sendmmsg(s->fd, s->messages + sent, (unsigned)(count - sent), 0);
		if (n < 0) {
			if (errno == EINTR) continue;
			/* Whatever went wrong, the viewer is missing part of a frame and will ask
			 * for a keyframe; there is nothing better to do with the rest of it. */
			return false;
		}
		sent += (size_t)n;
	}
	s->tally.packets += count;
	return true;
}

static void on_frame(void *user, const struct capture_frame *f)
{
	struct server *s = user;
	struct encoded_frame out;

	if (!s->watching || s->broken) return;
	if (f->corrupted || f->data == NULL) return;
	if (s->format_changed && !open_encoder(s)) return;
	if (!s->encoder) return;

	uint32_t stride = f->stride ? f->stride : s->format.width;
	if (f->size < (size_t)stride * s->format.height + (size_t)stride * (s->format.height / 2)) return;

	/* Asked for as late as it can be: where the pointer is now, not where it was when
	 * gamescope drew the frame. */
	const struct overlay *pointer = cursor_overlay(s->cursor, s->format.width, s->format.height,
						       s->format.color_matrix == SPA_VIDEO_COLOR_MATRIX_BT601,
						       s->format.color_range == SPA_VIDEO_COLOR_RANGE_0_255);

	bool keyframe = s->keyframe_wanted;
	s->keyframe_wanted = false;
	if (!encoder_encode(s->encoder, f->data, stride, keyframe, pointer, &out)) {
		on_failed(s, encoder_error(s->encoder));
		return;
	}
	uint64_t encoded = now_ns();

	bool sent = send_frame(s, &out, f->arrival_ns, encoded);
	uint64_t done = now_ns();
	s->last_sent_ns = done;

	struct tally *t = &s->tally;
	if (sent) {
		t->frames++;
		t->bytes += out.size;
		if (out.keyframe) t->keyframes++;
		if (out.size > t->largest) t->largest = out.size;
		t->encode_ns += encoded - f->arrival_ns;
		if (encoded - f->arrival_ns > t->encode_max_ns) t->encode_max_ns = encoded - f->arrival_ns;
		t->send_ns += done - encoded;
		if (done - encoded > t->send_max_ns) t->send_max_ns = done - encoded;
	} else {
		/* The viewer never got this frame, so the next one cannot refer to it. */
		t->skipped++;
		s->keyframe_wanted = true;
	}
	s->frame++;
}

static void start_stream(struct server *s, const struct sockaddr_in *from, const struct wire_hello *hello)
{
	char name[64];

	stop_stream(s, "replaced by a new request");

	s->viewer = *from;
	s->session = hello->session;
	s->wanted = *hello;
	s->watching = true;
	s->last_hello_ns = now_ns();
	s->last_sent_ns = s->last_hello_ns;
	s->frame = 0;
	s->pad_heard = s->input_refused = false;
	s->keys_heard = s->mouse_heard = false;
	s->key_seen = s->motion_seen = false;
	s->keyframe_wanted = false;
	s->format_changed = false;
	memset(&s->tally, 0, sizeof(s->tally));
	s->tally.since_ns = s->last_hello_ns;

	/* Too small to encode is treated as "no preference"; gamescope would refuse nothing. */
	uint32_t width = hello->width >= 160 && hello->height >= 128 ? hello->width : 0;
	uint32_t height = width ? hello->height : 0;

	printf("viewer   : %s asks for ", viewer_name(s, name, sizeof(name)));
	if (width) printf("%ux%u", width, height);
	else printf("the Deck's own size");
	printf(", %.1f Mbit/s at %u fps\n", (hello->bitrate_kbps ? hello->bitrate_kbps : 20000) / 1000.0,
	       hello->fps ? hello->fps : 60);

	struct capture_config config = {
		.target_id = 0xffffffffu, /* PW_ID_ANY: found by name */
		.target_object = s->options->source,
		.width = width,
		.height = height,
		.on_format = on_format,
		.on_frame = on_frame,
		.on_failed = on_failed,
		.user = s,
		.loop = s->loop,
	};
	s->cursor = cursor_open();
	s->capture = capture_open(&config);
	if (!s->capture) stop_stream(s, "the video source could not be opened");
}

/* Input is taken only from the viewer the picture is going to, and only when this
 * machine was started willing to be driven. */
static void on_pad(struct server *s, const struct wire_pad *message)
{
	if (message->session != s->session) return;
	if (!s->options->allow_input) {
		if (!s->input_refused) printf("gamepad  : the viewer is sending one, ignored - start with --allow-input to accept it\n");
		s->input_refused = true;
		return;
	}

	/* Datagrams can overtake each other; a state older than the one in force is stale. */
	if (s->pad_heard && (int32_t)(message->sequence - s->pad_sequence) <= 0) return;
	s->pad_heard = true;
	s->pad_sequence = message->sequence;
	s->last_pad_ns = now_ns();

	if (!s->pad) {
		s->pad = pad_open();
		if (!s->pad) {
			s->input_refused = true; /* said why once; do not retry on every datagram */
			return;
		}
		printf("gamepad  : the viewer's controller is connected here as 'Rayxel gamepad'\n");
	}

	struct pad_state state = {
		.buttons = message->buttons,
		.left_trigger = message->left_trigger,
		.right_trigger = message->right_trigger,
		.left_x = message->left_x,
		.left_y = message->left_y,
		.right_x = message->right_x,
		.right_y = message->right_y,
	};
	pad_apply(s->pad, &state);
}

/* The keyboard and mouse arrive separately but are one thing to set up and to refuse. */
static bool desk_ready(struct server *s, uint32_t session)
{
	if (session != s->session) return false;
	if (!s->options->allow_input || s->input_refused) {
		if (!s->input_refused) printf("keyboard : the viewer is sending one, ignored - start with --allow-input to accept it\n");
		s->input_refused = true;
		return false;
	}
	if (!s->desk) {
		s->desk = desk_open();
		if (!s->desk) {
			s->input_refused = true;
			return false;
		}
		printf("keyboard : the viewer's keyboard and mouse are connected here\n");
	}
	s->last_desk_ns = now_ns();
	return true;
}

static void on_keys(struct server *s, const struct wire_keys *message)
{
	if (s->keys_heard && (int32_t)(message->sequence - s->keys_sequence) <= 0) return;
	if (!desk_ready(s, message->session)) return;
	s->keys_heard = true;
	s->keys_sequence = message->sequence;
	desk_keys(s->desk, message->down);

	/* Not which key - that is nobody's business but the game's - only that one came. */
	if (!s->key_seen && desk_is_active(s->desk)) {
		s->key_seen = true;
		puts("keyboard : the first key press has arrived");
	}
}

static void on_mouse(struct server *s, const struct wire_mouse *message)
{
	if (s->mouse_heard && (int32_t)(message->sequence - s->mouse_sequence) <= 0) return;
	if (!desk_ready(s, message->session)) return;
	if (s->mouse_heard && !s->motion_seen && (message->x != s->mouse_x || message->y != s->mouse_y)) {
		s->motion_seen = true;
		puts("keyboard : the first mouse movement has arrived");
	}
	s->mouse_x = message->x;
	s->mouse_y = message->y;
	s->mouse_heard = true;
	s->mouse_sequence = message->sequence;
	desk_mouse(s->desk, message->buttons, message->x, message->y, message->wheel, message->hwheel);
}

static void on_socket(void *data, int fd, uint32_t mask)
{
	struct server *s = data;

	for (;;) {
		uint8_t packet[256];
		struct sockaddr_in from;
		socklen_t from_len = sizeof(from);
		ssize_t n = recvfrom(fd, packet, sizeof(packet), MSG_DONTWAIT, (struct sockaddr *)&from, &from_len);
		if (n < 0) {
			if (errno == EINTR) continue;
			return; /* nothing more waiting */
		}
		if ((size_t)n < sizeof(struct wire_header) || from_len != sizeof(from)) continue;

		const struct wire_header *header = (const struct wire_header *)packet;
		if (header->magic != WIRE_MAGIC || header->version != WIRE_VERSION) continue;

		bool from_viewer = s->watching && from.sin_addr.s_addr == s->viewer.sin_addr.s_addr &&
				   from.sin_port == s->viewer.sin_port;

		switch (header->type) {
		case WIRE_PING: {
			if ((size_t)n < sizeof(struct wire_ping)) break;
			const struct wire_ping *ping = (const struct wire_ping *)packet;
			struct wire_pong pong = {
				.header = { .magic = WIRE_MAGIC, .version = WIRE_VERSION, .type = WIRE_PONG },
				.viewer_time_ns = ping->viewer_time_ns,
				.sender_time_ns = now_ns(),
			};
			sendto(fd, &pong, sizeof(pong), 0, (struct sockaddr *)&from, sizeof(from));
			break;
		}
		case WIRE_HELLO: {
			if ((size_t)n < sizeof(struct wire_hello)) break;
			const struct wire_hello *hello = (const struct wire_hello *)packet;
			uint64_t now = now_ns();
			if (from_viewer && hello->session == s->session) {
				s->last_hello_ns = now;
			} else if (s->watching && now - s->last_hello_ns < VIEWER_BUSY_NS) {
				/* Someone is watching and was heard from a moment ago. A second
				 * request does not take the picture - or the controls - away from
				 * them; it waits until they say goodbye or go quiet. */
				if (now - s->last_refusal_ns > REPORT_EVERY_NS) {
					char ip[INET_ADDRSTRLEN] = "?";
					inet_ntop(AF_INET, &from.sin_addr, ip, sizeof(ip));
					printf("viewer   : %s:%u asked too, ignored while the first is watching\n", ip, ntohs(from.sin_port));
					s->last_refusal_ns = now;
				}
			} else {
				start_stream(s, &from, hello);
			}
			break;
		}
		case WIRE_KEYFRAME:
		case WIRE_BYE: {
			if ((size_t)n < sizeof(struct wire_session) || !from_viewer) break;
			if (((const struct wire_session *)packet)->session != s->session) break;
			if (header->type == WIRE_KEYFRAME) s->keyframe_wanted = true;
			else stop_stream(s, "the viewer said goodbye");
			break;
		}
		case WIRE_PAD: {
			if ((size_t)n < sizeof(struct wire_pad) || !from_viewer) break;
			on_pad(s, (const struct wire_pad *)packet);
			break;
		}
		case WIRE_KEYS: {
			if ((size_t)n < sizeof(struct wire_keys) || !from_viewer) break;
			on_keys(s, (const struct wire_keys *)packet);
			break;
		}
		case WIRE_MOUSE: {
			if ((size_t)n < sizeof(struct wire_mouse) || !from_viewer) break;
			on_mouse(s, (const struct wire_mouse *)packet);
			break;
		}
		default:
			break;
		}
	}
}

static void on_tick(void *data, uint64_t expirations)
{
	struct server *s = data;
	uint64_t now = now_ns();

	if (s->broken) stop_stream(s, s->broken_reason);
	else if (s->watching && now - s->last_hello_ns > VIEWER_TIMEOUT_NS) stop_stream(s, "the viewer went quiet");

	if (s->watching && now - s->last_sent_ns >= ALIVE_AFTER_NS) {
		struct wire_session alive = {
			.header = { .magic = WIRE_MAGIC, .version = WIRE_VERSION, .type = WIRE_ALIVE },
			.session = s->session,
		};
		sendto(s->fd, &alive, sizeof(alive), 0, (struct sockaddr *)&s->viewer, sizeof(s->viewer));
		s->last_sent_ns = now;
	}

	/* Frames are what make the pointer get looked at; without them, this does. */
	cursor_poll(s->cursor);

	/* A held button must not outlive the link it was held over. */
	if (s->pad && pad_is_active(s->pad) && now - s->last_pad_ns > PAD_TIMEOUT_NS) {
		pad_release(s->pad);
		printf("gamepad  : nothing heard for %.1f s, everything released\n", (double)(now - s->last_pad_ns) / 1e9);
	}

	if (s->desk && desk_is_active(s->desk) && now - s->last_desk_ns > PAD_TIMEOUT_NS) {
		desk_release(s->desk);
		printf("keyboard : nothing heard for %.1f s, every key and button released\n", (double)(now - s->last_desk_ns) / 1e9);
	}

	if (s->watching && now - s->tally.since_ns >= REPORT_EVERY_NS) report(s, now);
}

static void on_signal(void *data, int number)
{
	struct server *s = data;
	pw_main_loop_quit(s->loop);
}

int server_run(const struct server_options *options)
{
	struct server *s = calloc(1, sizeof(*s));
	int status = 1;

	if (!s) return 1;
	s->options = options;
	s->fd = socket(AF_INET, SOCK_DGRAM | SOCK_CLOEXEC, 0);
	if (s->fd < 0) {
		printf("cannot create a socket: %s\n", strerror(errno));
		goto out;
	}

	/* Room for a few frames, so a large one is never refused for want of buffer. The
	 * kernel caps the request at its own limit, hence reading back what was granted. */
	int wanted = 4 << 20, granted = 0;
	socklen_t length = sizeof(granted);
	setsockopt(s->fd, SOL_SOCKET, SO_SNDBUF, &wanted, sizeof(wanted));
	getsockopt(s->fd, SOL_SOCKET, SO_SNDBUF, &granted, &length);

	struct sockaddr_in address = { .sin_family = AF_INET, .sin_port = htons(options->port), .sin_addr.s_addr = htonl(INADDR_ANY) };
	if (bind(s->fd, (struct sockaddr *)&address, sizeof(address)) != 0) {
		printf("cannot listen on UDP port %u: %s\n", options->port, strerror(errno));
		goto out;
	}

	s->loop = pw_main_loop_new(NULL);
	if (!s->loop) {
		printf("cannot create a PipeWire loop\n");
		goto out;
	}
	struct pw_loop *loop = pw_main_loop_get_loop(s->loop);
	struct spa_source *io = pw_loop_add_io(loop, s->fd, SPA_IO_IN, false, on_socket, s);
	struct spa_source *timer = pw_loop_add_timer(loop, on_tick, s);
	struct spa_source *sigint = pw_loop_add_signal(loop, SIGINT, on_signal, s);
	struct spa_source *sigterm = pw_loop_add_signal(loop, SIGTERM, on_signal, s);
	struct timespec period = { .tv_nsec = 100000000 };
	pw_loop_update_timer(loop, timer, &period, &period, false);

	printf("listening: UDP port %u, source '%s', send buffer %d kB\n", options->port, options->source, granted / 1024);
	printf("           waiting for a viewer; nothing is captured until one asks\n");
	printf("           %s\n", options->allow_input ? "the viewer may play with its own gamepad, keyboard and mouse (--allow-input)"
						       : "input from the viewer is not accepted");

	pw_main_loop_run(s->loop);

	stop_stream(s, "shutting down");
	pw_loop_destroy_source(loop, sigterm);
	pw_loop_destroy_source(loop, sigint);
	pw_loop_destroy_source(loop, timer);
	pw_loop_destroy_source(loop, io);
	status = 0;

out:
	if (s->loop) pw_main_loop_destroy(s->loop);
	if (s->fd >= 0) close(s->fd);
	free(s);
	return status;
}
