#pragma once

#include <stdbool.h>
#include <stdint.h>

/* The PipeWire node the picture comes from. */
struct pw_target {
	bool found;
	bool is_gamescope;
	uint32_t node_id;
	char serial[32];
	char name[128];
};

struct va_summary {
	bool hevc_main;
	bool hevc_main10;
	char driver[192];
	char rate_control[128];
	char packed_headers[128];
	uint32_t max_width, max_height;
};

struct trial_summary {
	bool ran;
	int frames;
	double fps;
	uint32_t width, height;
};

void probe_system(void);
bool probe_pipewire(struct pw_target *target);
bool probe_va(struct va_summary *summary);
bool probe_capture(const struct pw_target *target, uint32_t width, uint32_t height, int seconds,
		   struct trial_summary *summary);
