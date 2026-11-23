# rayxel-send: the Steam Deck side of Rayxel's network source.
#
# Built in the container described by the Dockerfile next to this file, not on the Deck:
# SteamOS ships no compiler, and a binary linked against an older glibc than the Deck's
# runs there, while one linked against a newer glibc does not.

CC ?= gcc
PACKAGES := libpipewire-0.3 libva libva-drm x11 xfixes

CFLAGS ?= -O2 -g
CFLAGS += -std=gnu11 -Wall -Wextra -Wno-unused-parameter -Wno-missing-field-initializers
CFLAGS += $(shell pkg-config --cflags $(PACKAGES))
LDLIBS += $(shell pkg-config --libs $(PACKAGES)) -lm

SOURCES := $(wildcard src/*.c)
HEADERS := $(wildcard src/*.h)
OBJECTS := $(SOURCES:src/%.c=build/%.o)

all: build/rayxel-send build/fake-gamescope

build/rayxel-send: $(OBJECTS)
	$(CC) $(LDFLAGS) -o $@ $^ $(LDLIBS)

build/%.o: src/%.c $(HEADERS) | build
	$(CC) $(CFLAGS) -c -o $@ $<

# The stand-in for gamescope that the container test runs against.
build/fake-gamescope: test/fake_gamescope.c $(HEADERS) | build
	$(CC) $(CFLAGS) -o $@ $< $(shell pkg-config --libs libpipewire-0.3) -lm

build:
	mkdir -p build

clean:
	rm -rf build

.PHONY: all clean
