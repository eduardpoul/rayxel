#pragma once

/*
 * The private ids gamescope adds to its PipeWire stream, as declared in gamescope's
 * src/pipewire_gamescope.hpp. They are not part of PipeWire, so no system header has them.
 */

/* Format property, a rectangle: the size the consumer would like. gamescope only ever
 * scales down to it, keeping the aspect ratio; 0x0 means "whatever the output is". */
#define GAMESCOPE_FORMAT_VIDEO_requested_size 0x70000u

/* Format property, a long: capture one application instead of the composited picture. */
#define GAMESCOPE_FORMAT_VIDEO_focus_appid 0x70001u

/* Buffer meta, a float: captured width over output width. */
#define GAMESCOPE_META_requested_size_scale 0x70000u
