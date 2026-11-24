#pragma once

#include <stdint.h>

/*
 * What goes over the network between rayxel-send (the sender, on the Deck) and Rayxel
 * (the viewer). One UDP socket each way, every datagram one of the structs below, all
 * fields little-endian. Rayxel's copy of this is Capture/NetWire.cs; the two have to be
 * changed together.
 *
 * The viewer speaks first and keeps speaking: a HELLO twice a second says "I am still
 * watching, at this size and bitrate". The sender streams to whoever said HELLO last and
 * stops capturing a few seconds after the HELLOs stop, so a Deck nobody is watching does
 * no work at all. Nothing is acknowledged and nothing is resent. A frame with a packet
 * missing is useless, and so is every frame after it until the next keyframe, so the
 * viewer asks for one.
 */

#define WIRE_PORT 45800
#define WIRE_MAGIC 0x5852u /* the bytes 'R' 'X' */
#define WIRE_VERSION 1

/* Bytes of picture per datagram. With the header and the IP and UDP headers this stays
 * well under the 1500-byte Ethernet frame, so no datagram is ever split in two. */
#define WIRE_PAYLOAD_MAX 1200

enum wire_type {
	WIRE_HELLO = 1,    /* viewer to sender */
	WIRE_PING = 2,     /* viewer to sender */
	WIRE_PONG = 3,     /* sender to viewer */
	WIRE_KEYFRAME = 4, /* viewer to sender */
	WIRE_VIDEO = 5,    /* sender to viewer */
	WIRE_BYE = 6,      /* viewer to sender */
	WIRE_ALIVE = 7,    /* sender to viewer */
	WIRE_PAD = 8,      /* viewer to sender */
	WIRE_KEYS = 9,     /* viewer to sender */
	WIRE_MOUSE = 10,   /* viewer to sender */
};

#pragma pack(push, 1)

struct wire_header {
	uint16_t magic;
	uint8_t version;
	uint8_t type;
};

struct wire_hello {
	struct wire_header header;
	uint32_t session;       /* picked by the viewer; a new number starts a new stream */
	uint16_t width, height; /* size wanted; 0x0 is the Deck's own. Only ever scaled down. */
	uint16_t fps;           /* the rate the bitrate is budgeted for */
	uint16_t reserved;
	uint32_t bitrate_kbps;  /* may change while the stream runs; the encoder follows */
};

/* The two clocks are unrelated, so the viewer measures the difference: it stamps a PING,
 * the sender adds its own time and sends it back, and half the round trip is the error. */
struct wire_ping {
	struct wire_header header;
	uint32_t reserved;
	uint64_t viewer_time_ns;
};

struct wire_pong {
	struct wire_header header;
	uint32_t reserved;
	uint64_t viewer_time_ns; /* echoed */
	uint64_t sender_time_ns; /* CLOCK_MONOTONIC when the PING was answered */
};

/* Also what the sender says when it has nothing to send. gamescope draws only when the
 * picture changes, so a paused game means no frames at all; ALIVE, a few times a second,
 * is how the viewer tells "nothing has changed" from "the sender is gone". */
struct wire_session {
	struct wire_header header; /* WIRE_KEYFRAME, WIRE_BYE or WIRE_ALIVE */
	uint32_t session;
};

struct wire_video {
	struct wire_header header;
	uint32_t session;
	uint32_t frame;        /* counts from zero in each session */
	uint16_t index, count; /* this packet among the frame's packets */
	uint32_t frame_bytes;  /* the whole frame; every packet but the last is full */
	uint16_t width, height;
	uint8_t flags;
	uint8_t reserved[3];
	uint64_t capture_time_ns; /* sender clock: when gamescope handed the frame over */
	uint32_t encode_us;       /* from then until the encoded frame was in hand */
};

/*
 * A gamepad on the viewer's side, to be reproduced on the sender's.
 *
 * It is the controller's whole state and never "this button went down". A lost datagram
 * is then repaired by the next one, and a button cannot stay held because the datagram
 * that released it went missing. The viewer sends one whenever the state changes and a
 * few dozen times a second besides; when they stop arriving the sender lets go of
 * everything by itself.
 */
struct wire_pad {
	struct wire_header header;
	uint32_t session;
	uint32_t sequence; /* counts up; one older than the last applied is ignored */
	uint16_t buttons;  /* WIRE_PAD_* */
	uint8_t left_trigger, right_trigger; /* 0 at rest to 255 fully pressed */
	int16_t left_x, left_y;              /* -32768 to 32767; right and up are positive */
	int16_t right_x, right_y;
};

/*
 * The viewer's keyboard: which keys are down, all of them, as one bit each. Bit n of the
 * array - byte n / 8, bit n % 8 - is Linux key code n, which for the main block of a
 * keyboard is also the scan code the key itself sends, so it names the physical key and
 * not the letter printed on it. State and not events, for the same reason as the gamepad.
 */
struct wire_keys {
	struct wire_header header;
	uint32_t session;
	uint32_t sequence;
	uint8_t down[32];
};

/*
 * The viewer's mouse. A mouse reports movements, and a movement lost in transit would be
 * gone for good, so what is sent is the running total of all of them since the viewer
 * started; the sender moves by the difference from the last total it saw. The totals
 * wrap round and that is expected.
 */
struct wire_mouse {
	struct wire_header header;
	uint32_t session;
	uint32_t sequence;
	uint8_t buttons; /* WIRE_MOUSE_* */
	uint8_t reserved[3];
	int32_t x, y;          /* in the mouse's own counts; right and towards the user are positive */
	int32_t wheel, hwheel; /* in 120ths of a notch; away from the user and right are positive */
};

#pragma pack(pop)

#define WIRE_FLAG_KEYFRAME 1u

#define WIRE_MOUSE_LEFT 0x01u
#define WIRE_MOUSE_RIGHT 0x02u
#define WIRE_MOUSE_MIDDLE 0x04u
#define WIRE_MOUSE_BACK 0x08u
#define WIRE_MOUSE_FORWARD 0x10u

/* The bits of wire_pad.buttons. They are the ones Windows' XInput uses, so the viewer
 * passes its reading through untouched. */
#define WIRE_PAD_UP 0x0001u
#define WIRE_PAD_DOWN 0x0002u
#define WIRE_PAD_LEFT 0x0004u
#define WIRE_PAD_RIGHT 0x0008u
#define WIRE_PAD_START 0x0010u
#define WIRE_PAD_BACK 0x0020u
#define WIRE_PAD_LEFT_THUMB 0x0040u
#define WIRE_PAD_RIGHT_THUMB 0x0080u
#define WIRE_PAD_LEFT_SHOULDER 0x0100u
#define WIRE_PAD_RIGHT_SHOULDER 0x0200u
#define WIRE_PAD_GUIDE 0x0400u
#define WIRE_PAD_A 0x1000u
#define WIRE_PAD_B 0x2000u
#define WIRE_PAD_X 0x4000u
#define WIRE_PAD_Y 0x8000u
