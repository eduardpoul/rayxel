#include "cursor.h"

#include <math.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <X11/Xatom.h>
#include <X11/Xlib.h>
#include <X11/extensions/Xfixes.h>

/* gamescope runs one X server for Steam and one for each game it keeps apart; the Deck
 * has two. */
#define MAX_SCREENS 4

/* The first of them is gamescope's own: it is where gamescope says which of them the
 * mouse is on. */
#define FIRST_SCREEN ":0"

/* No pointer is drawn larger than this, whatever an X server says. */
#define MAX_SIDE 256

/* The size of the window under the pointer is asked for again after this many frames
 * even though nothing said it had changed. */
#define MEASURE_EVERY 30

struct screen {
	char name[16];
	Display *dpy;
	Window root;
	int fixes_event; /* the first of XFixes' event numbers, or -1 without XFixes */
	Atom visible_atom;
	bool dead; /* the connection broke; never touched again */

	/* What gamescope last said, and whether it has since said something new. */
	bool visible, visible_stale;
	bool image_stale;
};

struct cursor {
	struct screen screens[MAX_SCREENS];
	int count;
	unsigned screen_height; /* of the first screen: the Deck's picture at its full size */

	Atom mouse_display_atom, focus_display_atom, focused_window_atom;
	bool focus_stale;
	struct screen *mouse; /* the screen the mouse is on */
	Window window;        /* the window the picture is of, or that screen's root */
	unsigned window_width, window_height;
	unsigned since_measured;

	/* The pointer's picture as last made, and what it was made for. */
	struct screen *made_for;
	uint32_t made_height;
	bool made_bt601, made_full_range;
	bool empty;
	int hot_x, hot_y;
	uint8_t *planes;
	struct overlay overlay;

	bool said_drawn;
};

/* A window can go between being named and being asked about. That is an answer, not a
 * failure, and Xlib's own handler would end the process over it. */
static int on_error(Display *dpy, XErrorEvent *event)
{
	return 0;
}

static int on_io_error(Display *dpy)
{
	return 0;
}

/* Xlib's answer to a broken connection is to exit. A picture without a pointer is the
 * better answer here. */
static void on_io_exit(Display *dpy, void *user)
{
	struct screen *s = user;
	s->dead = true;
}

static struct screen *screen_open(struct cursor *c, const char *name)
{
	for (int i = 0; i < c->count; i++)
		if (strcmp(c->screens[i].name, name) == 0) return &c->screens[i];
	if (c->count == MAX_SCREENS || strlen(name) >= sizeof(c->screens[0].name)) return NULL;

	Display *dpy = XOpenDisplay(name);
	if (!dpy) return NULL;

	struct screen *s = &c->screens[c->count++];
	int error;
	memset(s, 0, sizeof(*s));
	strcpy(s->name, name);
	s->dpy = dpy;
	s->root = DefaultRootWindow(dpy);
	XSetIOErrorExitHandler(dpy, on_io_exit, s);

	s->visible_atom = XInternAtom(dpy, "GAMESCOPE_CURSOR_VISIBLE_FEEDBACK", False);
	XSelectInput(dpy, s->root, PropertyChangeMask);
	if (XFixesQueryExtension(dpy, &s->fixes_event, &error))
		XFixesSelectCursorInput(dpy, s->root, XFixesDisplayCursorNotifyMask);
	else
		s->fixes_event = -1;

	s->visible_stale = s->image_stale = true;
	return s;
}

/* A CARDINAL property of a screen's root window: how many of its values were read. */
static int read_cardinals(struct screen *s, Atom atom, uint32_t *out, int cap)
{
	Atom type;
	int format, n = 0;
	unsigned long count, left;
	unsigned char *data = NULL;

	if (XGetWindowProperty(s->dpy, s->root, atom, 0, cap, False, XA_CARDINAL, &type, &format, &count, &left, &data) != Success)
		return 0;
	if (!data) return 0;
	/* Xlib hands 32-bit values over as longs. */
	if (format == 32)
		for (; n < cap && n < (int)count; n++) out[n] = (uint32_t)((unsigned long *)data)[n];
	XFree(data);
	return n;
}

/* gamescope names a screen by writing the characters of its name, ":1", into a property
 * that is declared to hold numbers. So the name is read back out of the numbers. */
static bool read_screen_name(struct screen *s, Atom atom, char *out, size_t cap)
{
	uint32_t words[8];
	int n = read_cardinals(s, atom, words, 8);
	size_t length = 0;

	for (int i = 0; i < n; i++) {
		for (int byte = 0; byte < 4; byte++) {
			char ch = (char)(words[i] >> (8 * byte));
			if (ch == 0 || length + 1 == cap) goto done;
			out[length++] = ch;
		}
	}
done:
	out[length] = 0;
	return length > 0;
}

/* Reads what a screen has had to say, which is only ever "something changed". */
static void drain(struct cursor *c, struct screen *s)
{
	while (!s->dead && XPending(s->dpy) > 0) {
		XEvent event;
		XNextEvent(s->dpy, &event);

		if (s->fixes_event >= 0 && event.type == s->fixes_event + XFixesCursorNotify) {
			s->image_stale = true;
		} else if (event.type == PropertyNotify) {
			Atom atom = event.xproperty.atom;
			if (atom == s->visible_atom)
				s->visible_stale = true;
			else if (s == &c->screens[0]
				 && (atom == c->mouse_display_atom || atom == c->focus_display_atom || atom == c->focused_window_atom))
				c->focus_stale = true;
		}
	}
}

/* Which screen the mouse is on, and which window there the picture is of. */
static void find_focus(struct cursor *c)
{
	struct screen *first = &c->screens[0];
	char name[16], shown[16];
	uint32_t window = 0;

	c->focus_stale = false;
	c->mouse = first;
	if (read_screen_name(first, c->mouse_display_atom, name, sizeof(name))) {
		struct screen *s = screen_open(c, name);
		if (s && !s->dead) c->mouse = s;
	}

	/* The pointer's place is counted from the window being shown, when the mouse is on
	 * the same screen as that window, and from the screen's corner otherwise. */
	c->window = c->mouse->root;
	if (read_screen_name(first, c->focus_display_atom, shown, sizeof(shown)) && strcmp(shown, c->mouse->name) == 0
	    && read_cardinals(first, c->focused_window_atom, &window, 1) == 1 && window != 0)
		c->window = window;

	c->since_measured = MEASURE_EVERY;
}

static bool measure(struct cursor *c)
{
	for (;;) {
		Window root;
		int x, y;
		unsigned width = 0, height = 0, border, depth;

		if (XGetGeometry(c->mouse->dpy, c->window, &root, &x, &y, &width, &height, &border, &depth) && width && height) {
			c->window_width = width;
			c->window_height = height;
			return true;
		}
		if (c->window == c->mouse->root) return false;
		c->window = c->mouse->root; /* the window has gone */
	}
}

static uint8_t to_byte(double value)
{
	return value <= 0 ? 0 : value >= 255 ? 255 : (uint8_t)lround(value);
}

/* Asks the screen what its pointer looks like and makes of it a picture to lay over a
 * frame `frame_height` tall, in that frame's colour coding. */
static void make_picture(struct cursor *c, struct screen *s, uint32_t frame_height, bool bt601, bool full_range)
{
	c->made_for = s;
	c->made_height = frame_height;
	c->made_bt601 = bt601;
	c->made_full_range = full_range;
	c->empty = true;
	s->image_stale = false;

	XFixesCursorImage *image = s->fixes_event >= 0 ? XFixesGetCursorImage(s->dpy) : NULL;
	if (!image) return;
	if (image->width == 0 || image->height == 0) {
		XFree(image);
		return;
	}

	/* gamescope shows the pointer on the Deck's screen as large as its picture is. A frame
	 * that is a smaller copy of that screen gets a pointer smaller by as much. */
	double scale = c->screen_height ? (double)frame_height / c->screen_height : 1.0;
	if (scale > 1.0) scale = 1.0;
	unsigned width = (unsigned)lround(image->width * scale), height = (unsigned)lround(image->height * scale);
	if (width < 1) width = 1;
	if (height < 1) height = 1;
	if (width > MAX_SIDE) width = MAX_SIDE;
	if (height > MAX_SIDE) height = MAX_SIDE;

	size_t pixels = (size_t)width * height;
	uint8_t *planes = realloc(c->planes, pixels * 4);
	if (!planes) {
		XFree(image);
		return;
	}
	c->planes = planes;
	uint8_t *luma = planes, *cb = planes + pixels, *cr = planes + pixels * 2, *alpha = planes + pixels * 3;

	const double kr = bt601 ? 0.299 : 0.2126, kb = bt601 ? 0.114 : 0.0722, kg = 1.0 - kr - kb;
	bool any = false;

	for (unsigned y = 0; y < height; y++) {
		for (unsigned x = 0; x < width; x++) {
			/* What this pixel covers of the original, taken at 4x4 points across it.
			 * At full size that is the original pixel sixteen times over. */
			unsigned a = 0, r = 0, g = 0, b = 0;
			for (int j = 0; j < 4; j++) {
				unsigned sy = (unsigned)((y + (j + 0.5) / 4.0) * image->height / height);
				if (sy >= image->height) sy = image->height - 1;
				for (int i = 0; i < 4; i++) {
					unsigned sx = (unsigned)((x + (i + 0.5) / 4.0) * image->width / width);
					if (sx >= image->width) sx = image->width - 1;
					uint32_t p = (uint32_t)image->pixels[(size_t)sy * image->width + sx];
					a += p >> 24;
					r += (p >> 16) & 255;
					g += (p >> 8) & 255;
					b += p & 255;
				}
			}

			size_t at = (size_t)y * width + x;
			alpha[at] = (uint8_t)((a + 8) / 16);
			if (alpha[at] == 0) {
				luma[at] = 0;
				cb[at] = cr[at] = 128;
				continue;
			}
			any = true;

			/* X gives the colours already multiplied by the coverage. */
			double red = fmin(1.0, (double)r / a), green = fmin(1.0, (double)g / a), blue = fmin(1.0, (double)b / a);
			double grey = kr * red + kg * green + kb * blue;
			double blueness = (blue - grey) / (2.0 * (1.0 - kb)), redness = (red - grey) / (2.0 * (1.0 - kr));

			luma[at] = to_byte(full_range ? grey * 255.0 : 16.0 + grey * 219.0);
			cb[at] = to_byte(128.0 + blueness * (full_range ? 255.0 : 224.0));
			cr[at] = to_byte(128.0 + redness * (full_range ? 255.0 : 224.0));
		}
	}

	c->hot_x = (int)lround((double)image->xhot * width / image->width);
	c->hot_y = (int)lround((double)image->yhot * height / image->height);
	c->overlay = (struct overlay){ .width = width, .height = height, .luma = luma, .cb = cb, .cr = cr, .alpha = alpha };
	c->empty = !any;
	XFree(image);
}

struct cursor *cursor_open(void)
{
	struct cursor *c = calloc(1, sizeof(*c));
	if (!c) return NULL;

	/* A write to an X server that has gone must fail, not end the process. */
	signal(SIGPIPE, SIG_IGN);
	XSetErrorHandler(on_error);
	XSetIOErrorHandler(on_io_error);

	struct screen *first = screen_open(c, FIRST_SCREEN);
	if (!first) {
		printf("pointer  : gamescope's X server %s cannot be reached, so the picture will have no mouse pointer\n",
		       FIRST_SCREEN);
		return c;
	}

	c->mouse_display_atom = XInternAtom(first->dpy, "GAMESCOPE_MOUSE_FOCUS_DISPLAY", False);
	c->focus_display_atom = XInternAtom(first->dpy, "GAMESCOPE_FOCUS_DISPLAY", False);
	c->focused_window_atom = XInternAtom(first->dpy, "GAMESCOPE_FOCUSED_WINDOW", False);
	c->screen_height = (unsigned)DisplayHeight(first->dpy, DefaultScreen(first->dpy));
	c->focus_stale = true;

	printf("pointer  : gamescope leaves the mouse pointer out of the stream; it is put into the picture here, where and "
	       "when gamescope shows it%s\n",
	       first->fixes_event < 0 ? " (but this X server cannot say what it looks like)" : "");
	return c;
}

void cursor_poll(struct cursor *c)
{
	if (!c) return;
	for (int i = 0; i < c->count; i++) drain(c, &c->screens[i]);
}

const struct overlay *cursor_overlay(struct cursor *c, uint32_t width, uint32_t height, bool bt601, bool full_range)
{
	if (!c || c->count == 0 || c->screens[0].dead) return NULL;

	cursor_poll(c);
	if (c->screens[0].dead) return NULL;
	if (c->focus_stale || !c->mouse || c->mouse->dead) find_focus(c);

	struct screen *s = c->mouse;
	if (s->dead) return NULL;

	if (s->visible_stale) {
		uint32_t value = 0;
		s->visible_stale = false;
		s->visible = read_cardinals(s, s->visible_atom, &value, 1) == 1 && value != 0;
	}
	if (!s->visible) return NULL;

	if (s->image_stale || c->made_for != s || c->made_height != height || c->made_bt601 != bt601
	    || c->made_full_range != full_range)
		make_picture(c, s, height, bt601, full_range);
	if (c->empty) return NULL;

	if (++c->since_measured >= MEASURE_EVERY) {
		c->since_measured = 0;
		if (!measure(c)) return NULL;
	}

	Window root, child;
	int root_x, root_y, x, y;
	unsigned buttons;
	if (!XQueryPointer(s->dpy, c->window, &root, &child, &root_x, &root_y, &x, &y, &buttons)) {
		/* The window has gone; by the next frame gamescope will have named another. */
		c->focus_stale = true;
		return NULL;
	}

	/* gamescope fits the window into the picture, as large as it goes with its shape
	 * kept, and centres it. The pointer's place in the window follows. */
	double fit = fmin((double)width / c->window_width, (double)height / c->window_height);
	double left = (width - c->window_width * fit) / 2.0, top = (height - c->window_height * fit) / 2.0;
	c->overlay.x = (int)floor(x * fit + left) - c->hot_x;
	c->overlay.y = (int)floor(y * fit + top) - c->hot_y;

	if (!c->said_drawn) {
		c->said_drawn = true;
		printf("pointer  : the first one is in the picture, %ux%u, on screen %s\n", c->overlay.width, c->overlay.height,
		       s->name);
	}
	return &c->overlay;
}

void cursor_close(struct cursor *c)
{
	if (!c) return;
	for (int i = 0; i < c->count; i++) XCloseDisplay(c->screens[i].dpy);
	free(c->planes);
	free(c);
}
