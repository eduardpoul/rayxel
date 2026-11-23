#pragma once

#include <stdint.h>

/*
 * A keyboard and a mouse that exist only in software, driven by the viewer's real ones.
 *
 * Made through uinput like the gamepad in pad.h, and for the same reason indistinguishable
 * from hardware: gamescope picks them up the way it would a keyboard plugged into the
 * dock. They are two devices rather than one because that is what real ones are, and
 * what the libraries that sort devices into kinds expect to find.
 */

struct desk;

/* Creates both devices. Null on failure, with the reason printed. */
struct desk *desk_open(void);

/* Makes the keyboard match `down`: bit n of the array set means Linux key code n is held. */
void desk_keys(struct desk *desk, const uint8_t down[32]);

/*
 * Makes the mouse match. The positions are running totals kept by the viewer, not
 * movements, so what is applied is the difference from the totals seen last time - and
 * a datagram that went missing takes no movement with it. The first call only records
 * where the totals stand.
 */
void desk_mouse(struct desk *desk, uint8_t buttons, int32_t x, int32_t y, int32_t wheel, int32_t hwheel);

/* True while any key or mouse button is held. */
int desk_is_active(const struct desk *desk);

/* Lets go of every key and button. */
void desk_release(struct desk *desk);

void desk_close(struct desk *desk);
