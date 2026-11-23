#pragma once

#include <stdbool.h>
#include <stdint.h>

#include "overlay.h"

/* The Deck's mouse pointer, for putting into the picture.
 *
 * gamescope draws the pointer on the Deck's own screen and leaves it out of the stream it
 * gives to whoever captures: that stream is the windows and nothing else. So the picture a
 * viewer gets has no pointer in it unless it is put there. Nothing here is made up, though:
 * whether there is a pointer, where it is and what it looks like are all asked of
 * gamescope's X servers, which is where gamescope itself gets them. */
struct cursor;

/* Never fails outright: with no X server to ask, there is simply never a pointer. Says
 * which it is in one line. Null only when out of memory, which every call here accepts. */
struct cursor *cursor_open(void);

/* The pointer as it should be laid over a frame of this size and colour coding, or null
 * when gamescope is not showing one. Valid until the next call. */
const struct overlay *cursor_overlay(struct cursor *cursor, uint32_t width, uint32_t height, bool bt601, bool full_range);

/* Reads what the X servers have sent since last time. To be called now and then while no
 * frames are being made, so that it does not pile up. */
void cursor_poll(struct cursor *cursor);

void cursor_close(struct cursor *cursor);
