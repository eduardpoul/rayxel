#include <errno.h>
#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <unistd.h>

#include <linux/uinput.h>

#include "pad.h"
#include "wire.h"

struct pad {
	int fd;
	struct pad_state state;
};

/* Wire bit to Linux key code, in the order the kernel's own Xbox driver declares them.
 * That order is what game libraries count buttons by, so it is part of the disguise. */
static const struct {
	uint16_t bit;
	uint16_t key;
} buttons[] = {
	{ WIRE_PAD_A, BTN_A },
	{ WIRE_PAD_B, BTN_B },
	{ WIRE_PAD_X, BTN_X },
	{ WIRE_PAD_Y, BTN_Y },
	{ WIRE_PAD_LEFT_SHOULDER, BTN_TL },
	{ WIRE_PAD_RIGHT_SHOULDER, BTN_TR },
	{ WIRE_PAD_BACK, BTN_SELECT },
	{ WIRE_PAD_START, BTN_START },
	{ WIRE_PAD_GUIDE, BTN_MODE },
	{ WIRE_PAD_LEFT_THUMB, BTN_THUMBL },
	{ WIRE_PAD_RIGHT_THUMB, BTN_THUMBR },
};

#define COUNT(a) (sizeof(a) / sizeof((a)[0]))

static int add_axis(int fd, uint16_t code, int32_t minimum, int32_t maximum, int32_t flat)
{
	struct uinput_abs_setup setup = {
		.code = code,
		.absinfo = { .minimum = minimum, .maximum = maximum, .flat = flat },
	};
	if (ioctl(fd, UI_SET_ABSBIT, code) < 0) return -1;
	return ioctl(fd, UI_ABS_SETUP, &setup);
}

struct pad *pad_open(void)
{
	int fd = open("/dev/uinput", O_WRONLY | O_CLOEXEC);
	if (fd < 0) {
		printf("gamepad  : cannot open /dev/uinput: %s\n", strerror(errno));
		return NULL;
	}

	int failed = ioctl(fd, UI_SET_EVBIT, EV_KEY) < 0 || ioctl(fd, UI_SET_EVBIT, EV_ABS) < 0;
	for (size_t i = 0; i < COUNT(buttons) && !failed; i++) failed = ioctl(fd, UI_SET_KEYBIT, buttons[i].key) < 0;

	/* Sticks, triggers and the d-pad as the kernel's Xbox driver reports them: the d-pad
	 * is a pair of axes there, not four buttons. */
	failed = failed || add_axis(fd, ABS_X, -32768, 32767, 128) < 0 || add_axis(fd, ABS_Y, -32768, 32767, 128) < 0 ||
		 add_axis(fd, ABS_RX, -32768, 32767, 128) < 0 || add_axis(fd, ABS_RY, -32768, 32767, 128) < 0 ||
		 add_axis(fd, ABS_Z, 0, 255, 0) < 0 || add_axis(fd, ABS_RZ, 0, 255, 0) < 0 ||
		 add_axis(fd, ABS_HAT0X, -1, 1, 0) < 0 || add_axis(fd, ABS_HAT0Y, -1, 1, 0) < 0;

	/* The ids of a wired Xbox 360 controller. They, not the name, are what Steam and
	 * SDL look a controller up by, so the name is free to say what this really is. */
	struct uinput_setup setup = {
		.id = { .bustype = BUS_USB, .vendor = 0x045e, .product = 0x028e, .version = 0x0110 },
		.name = "Rayxel gamepad",
	};
	failed = failed || ioctl(fd, UI_DEV_SETUP, &setup) < 0 || ioctl(fd, UI_DEV_CREATE) < 0;

	if (failed) {
		printf("gamepad  : the virtual controller could not be created: %s\n", strerror(errno));
		close(fd);
		return NULL;
	}

	struct pad *pad = calloc(1, sizeof(*pad));
	if (!pad) {
		ioctl(fd, UI_DEV_DESTROY);
		close(fd);
		return NULL;
	}
	pad->fd = fd;
	return pad;
}

static void emit(const struct pad *pad, uint16_t type, uint16_t code, int32_t value)
{
	struct input_event event = { .type = type, .code = code, .value = value };
	/* A write that fails leaves the device one event behind until the next state, which
	 * arrives within milliseconds and carries everything again. */
	if (write(pad->fd, &event, sizeof(event)) < 0) return;
}

/* The wire has up as positive, like the controller's own API; Linux has it negative. */
static int32_t flip(int16_t value)
{
	return value == INT16_MIN ? INT16_MAX : -value;
}

static int32_t hat(uint16_t state, uint16_t negative, uint16_t positive)
{
	return ((state & positive) ? 1 : 0) - ((state & negative) ? 1 : 0);
}

void pad_apply(struct pad *pad, const struct pad_state *next)
{
	const struct pad_state *last = &pad->state;
	int changed = 0;

#define AXIS(code, before, after)                               \
	do {                                                    \
		if ((before) != (after)) {                      \
			emit(pad, EV_ABS, (code), (after));     \
			changed = 1;                            \
		}                                               \
	} while (0)

	for (size_t i = 0; i < COUNT(buttons); i++) {
		if (!((last->buttons ^ next->buttons) & buttons[i].bit)) continue;
		emit(pad, EV_KEY, buttons[i].key, (next->buttons & buttons[i].bit) ? 1 : 0);
		changed = 1;
	}

	AXIS(ABS_HAT0X, hat(last->buttons, WIRE_PAD_LEFT, WIRE_PAD_RIGHT), hat(next->buttons, WIRE_PAD_LEFT, WIRE_PAD_RIGHT));
	AXIS(ABS_HAT0Y, hat(last->buttons, WIRE_PAD_UP, WIRE_PAD_DOWN), hat(next->buttons, WIRE_PAD_UP, WIRE_PAD_DOWN));
	AXIS(ABS_X, last->left_x, next->left_x);
	AXIS(ABS_Y, flip(last->left_y), flip(next->left_y));
	AXIS(ABS_RX, last->right_x, next->right_x);
	AXIS(ABS_RY, flip(last->right_y), flip(next->right_y));
	AXIS(ABS_Z, last->left_trigger, next->left_trigger);
	AXIS(ABS_RZ, last->right_trigger, next->right_trigger);
#undef AXIS

	/* Everything above becomes one report: a game sees the state change at once. */
	if (changed) emit(pad, EV_SYN, SYN_REPORT, 0);
	pad->state = *next;
}

int pad_is_active(const struct pad *pad)
{
	static const struct pad_state rest;
	return memcmp(&pad->state, &rest, sizeof(rest)) != 0;
}

void pad_release(struct pad *pad)
{
	static const struct pad_state rest;
	pad_apply(pad, &rest);
}

void pad_close(struct pad *pad)
{
	if (!pad) return;
	pad_release(pad);
	ioctl(pad->fd, UI_DEV_DESTROY);
	close(pad->fd);
	free(pad);
}
