#include <errno.h>
#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <unistd.h>

#include <linux/uinput.h>

#include "desk.h"
#include "wire.h"

/* Key codes above this are not keys on a keyboard. */
#define LAST_KEY 248

struct desk {
	int keyboard, mouse;
	uint8_t down[32];
	uint8_t buttons;
	int have_position;
	int32_t x, y, wheel, hwheel;
	int32_t wheel_rest, hwheel_rest; /* part of a notch not yet reported as a whole one */
};

static const struct {
	uint8_t bit;
	uint16_t key;
} mouse_buttons[] = {
	{ WIRE_MOUSE_LEFT, BTN_LEFT },   { WIRE_MOUSE_RIGHT, BTN_RIGHT },     { WIRE_MOUSE_MIDDLE, BTN_MIDDLE },
	{ WIRE_MOUSE_BACK, BTN_SIDE },   { WIRE_MOUSE_FORWARD, BTN_EXTRA },
};

#define COUNT(a) (sizeof(a) / sizeof((a)[0]))

static int create(int fd, const char *name, uint16_t product)
{
	struct uinput_setup setup = { .id = { .bustype = BUS_USB, .vendor = 0x5258, .product = product, .version = 1 } };
	snprintf(setup.name, sizeof(setup.name), "%s", name);
	return ioctl(fd, UI_DEV_SETUP, &setup) < 0 || ioctl(fd, UI_DEV_CREATE) < 0 ? -1 : 0;
}

static int open_keyboard(void)
{
	int fd = open("/dev/uinput", O_WRONLY | O_CLOEXEC);
	if (fd < 0) return -1;

	int failed = ioctl(fd, UI_SET_EVBIT, EV_KEY) < 0;
	for (int key = 1; key <= LAST_KEY && !failed; key++) failed = ioctl(fd, UI_SET_KEYBIT, key) < 0;
	if (failed || create(fd, "Rayxel keyboard", 0x0001) < 0) {
		close(fd);
		return -1;
	}
	return fd;
}

static int open_mouse(void)
{
	static const uint16_t axes[] = { REL_X, REL_Y, REL_WHEEL, REL_HWHEEL, REL_WHEEL_HI_RES, REL_HWHEEL_HI_RES };
	int fd = open("/dev/uinput", O_WRONLY | O_CLOEXEC);
	if (fd < 0) return -1;

	int failed = ioctl(fd, UI_SET_EVBIT, EV_KEY) < 0 || ioctl(fd, UI_SET_EVBIT, EV_REL) < 0;
	for (size_t i = 0; i < COUNT(mouse_buttons) && !failed; i++) failed = ioctl(fd, UI_SET_KEYBIT, mouse_buttons[i].key) < 0;
	for (size_t i = 0; i < COUNT(axes) && !failed; i++) failed = ioctl(fd, UI_SET_RELBIT, axes[i]) < 0;
	if (failed || create(fd, "Rayxel mouse", 0x0002) < 0) {
		close(fd);
		return -1;
	}
	return fd;
}

struct desk *desk_open(void)
{
	struct desk *desk = calloc(1, sizeof(*desk));
	if (!desk) return NULL;

	desk->keyboard = open_keyboard();
	desk->mouse = desk->keyboard >= 0 ? open_mouse() : -1;
	if (desk->mouse < 0) {
		printf("keyboard : the virtual keyboard and mouse could not be created: %s\n", strerror(errno));
		if (desk->keyboard >= 0) {
			ioctl(desk->keyboard, UI_DEV_DESTROY);
			close(desk->keyboard);
		}
		free(desk);
		return NULL;
	}
	return desk;
}

static void emit(int fd, uint16_t type, uint16_t code, int32_t value)
{
	struct input_event event = { .type = type, .code = code, .value = value };
	/* As for the gamepad: a failed write is put right by the next state to arrive. */
	if (write(fd, &event, sizeof(event)) < 0) return;
}

void desk_keys(struct desk *desk, const uint8_t down[32])
{
	int changed = 0;

	for (int key = 1; key <= LAST_KEY; key++) {
		uint8_t mask = (uint8_t)(1u << (key & 7));
		int now = (down[key >> 3] & mask) != 0, before = (desk->down[key >> 3] & mask) != 0;
		if (now == before) continue;
		emit(desk->keyboard, EV_KEY, (uint16_t)key, now);
		changed = 1;
	}
	if (changed) emit(desk->keyboard, EV_SYN, SYN_REPORT, 0);
	memcpy(desk->down, down, sizeof(desk->down));
}

/* The wheel arrives in 120ths of a notch. Programs that understand fine scrolling get
 * exactly that; the rest are given a whole notch each time the 120ths add up to one. */
static int scroll(struct desk *desk, int32_t delta, int32_t *rest, uint16_t fine, uint16_t coarse)
{
	if (delta == 0) return 0;
	emit(desk->mouse, EV_REL, fine, delta);

	*rest += delta;
	int32_t notches = *rest / 120;
	if (notches) {
		emit(desk->mouse, EV_REL, coarse, notches);
		*rest -= notches * 120;
	}
	return 1;
}

void desk_mouse(struct desk *desk, uint8_t buttons, int32_t x, int32_t y, int32_t wheel, int32_t hwheel)
{
	int changed = 0;

	for (size_t i = 0; i < COUNT(mouse_buttons); i++) {
		if (!((desk->buttons ^ buttons) & mouse_buttons[i].bit)) continue;
		emit(desk->mouse, EV_KEY, mouse_buttons[i].key, (buttons & mouse_buttons[i].bit) ? 1 : 0);
		changed = 1;
	}
	desk->buttons = buttons;

	if (desk->have_position) {
		/* Unsigned, so that a total that has wrapped round still gives the right step. */
		int32_t dx = (int32_t)((uint32_t)x - (uint32_t)desk->x), dy = (int32_t)((uint32_t)y - (uint32_t)desk->y);
		if (dx) emit(desk->mouse, EV_REL, REL_X, dx);
		if (dy) emit(desk->mouse, EV_REL, REL_Y, dy);
		changed |= dx || dy;
		changed |= scroll(desk, (int32_t)((uint32_t)wheel - (uint32_t)desk->wheel), &desk->wheel_rest, REL_WHEEL_HI_RES, REL_WHEEL);
		changed |= scroll(desk, (int32_t)((uint32_t)hwheel - (uint32_t)desk->hwheel), &desk->hwheel_rest, REL_HWHEEL_HI_RES,
				  REL_HWHEEL);
	}
	desk->have_position = 1;
	desk->x = x;
	desk->y = y;
	desk->wheel = wheel;
	desk->hwheel = hwheel;

	if (changed) emit(desk->mouse, EV_SYN, SYN_REPORT, 0);
}

int desk_is_active(const struct desk *desk)
{
	static const uint8_t none[32];
	return desk->buttons != 0 || memcmp(desk->down, none, sizeof(none)) != 0;
}

void desk_release(struct desk *desk)
{
	static const uint8_t none[32];
	desk_keys(desk, none);
	desk_mouse(desk, 0, desk->x, desk->y, desk->wheel, desk->hwheel);
}

void desk_close(struct desk *desk)
{
	if (!desk) return;
	desk_release(desk);
	ioctl(desk->keyboard, UI_DEV_DESTROY);
	ioctl(desk->mouse, UI_DEV_DESTROY);
	close(desk->keyboard);
	close(desk->mouse);
	free(desk);
}
