#pragma once

#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <time.h>

#define RAYXEL_SEND_VERSION "0.1.0"

/* CLOCK_MONOTONIC in nanoseconds: the clock every timestamp in this program is on. */
static inline uint64_t now_ns(void)
{
	struct timespec ts;
	clock_gettime(CLOCK_MONOTONIC, &ts);
	return (uint64_t)ts.tv_sec * 1000000000ull + (uint64_t)ts.tv_nsec;
}

static inline void section(const char *title)
{
	printf("\n== %s ==\n", title);
}
