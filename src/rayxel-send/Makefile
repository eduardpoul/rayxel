# rayxel-send: the Steam Deck side of Rayxel's network source.
#
# Built in the container described by the Dockerfile next to this file, not on the Deck:
# SteamOS ships no compiler, and a binary linked against an older glibc than the Deck's
# runs there, while one linked against a newer glibc does not.
#
# PYROWAVE=DIR builds in the PyroWave codec, from its source checkout in DIR (only its
# header is needed here; the library is loaded at run time, from beside the program).

CC ?= gcc
PACKAGES := libpipewire-0.3 libva libva-drm x11 xfixes

CFLAGS ?= -O2 -g
CFLAGS += -std=gnu11 -Wall -Wextra -Wno-unused-parameter -Wno-missing-field-initializers
CFLAGS += $(shell pkg-config --cflags $(PACKAGES))
LDLIBS += $(shell pkg-config --libs $(PACKAGES)) -ldl -lm

ifneq ($(PYROWAVE),)
CFLAGS += -DHAVE_PYROWAVE -I$(PYROWAVE) -I$(PYROWAVE)/Granite/third_party/khronos/vulkan-headers/include
endif

SOURCES := $(wildcard src/*.c)
HEADERS := $(wildcard src/*.h)
OBJECTS := $(SOURCES:src/%.c=build/%.o)

all: build/rayxel-send build/fake-gamescope build/av1-headers-test

build/rayxel-send: $(OBJECTS)
	$(CC) $(LDFLAGS) -o $@ $^ $(LDLIBS)

build/%.o: src/%.c $(HEADERS) | build
	$(CC) $(CFLAGS) -c -o $@ $<

# The stand-in for gamescope that the container test runs against.
build/fake-gamescope: test/fake_gamescope.c $(HEADERS) | build
	$(CC) $(CFLAGS) -o $@ $< $(shell pkg-config --libs libpipewire-0.3) -lm

# Writes the AV1 headers into a file for FFmpeg to parse: no Deck can encode AV1, so this
# is how they are checked (see the file for the command).
build/av1-headers-test: test/av1_headers_test.c src/av1_headers.c src/av1_headers.h | build
	$(CC) $(CFLAGS) -o $@ test/av1_headers_test.c src/av1_headers.c

build:
	mkdir -p build

clean:
	rm -rf build

.PHONY: all clean
