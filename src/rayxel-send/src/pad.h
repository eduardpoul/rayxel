#pragma once

#include <stdint.h>

/*
 * A gamepad that exists only in software, driven by the viewer's real one.
 *
 * It is made through the kernel's uinput interface, the same way Steam makes the virtual
 * controller it hands to games, so nothing on the Deck can tell it from a pad plugged
 * in: Steam lists it, Steam Input remaps it, and a game sees it. It presents itself as
 * an Xbox 360 controller because that is the one layout everything already knows.
 */

struct pad;

/* The whole controller at one instant, in the units the wire uses (see wire.h). */
struct pad_state {
	uint16_t buttons;
	uint8_t left_trigger, right_trigger;
	int16_t left_x, left_y, right_x, right_y;
};

/* Creates the device. Null on failure, with the reason printed. */
struct pad *pad_open(void);

/* Makes the device match `state`, reporting only what differs from last time. */
void pad_apply(struct pad *pad, const struct pad_state *state);

/* True while anything is held or pushed away from rest. */
int pad_is_active(const struct pad *pad);

/* Lets go of everything: what has to happen when the viewer stops being heard from. */
void pad_release(struct pad *pad);

/* Removes the device; Steam sees a controller being unplugged. */
void pad_close(struct pad *pad);
