# Builds what measuring the PyroWave codec on the Steam Deck needs: the codec's library and
# a small bench that encodes frames from a file, so nothing has to capture from gamescope.
#
#   docker build -f test/pyrowave/Dockerfile --target out --output type=local,dest=out/pyrowave .
#
# Run from src/rayxel-send: the bench reuses the sender's HEVC encoder for a like-for-like
# comparison. The codec is pinned, because its C interface is not declared stable yet.

FROM debian:12-slim AS build
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates git cmake ninja-build g++ gcc make pkg-config \
      libva-dev python3 \
 && rm -rf /var/lib/apt/lists/*

ARG PYROWAVE_COMMIT=a32b05e03d70826b19d4d11ff74fe166ed74371b
# The Granite commit the codec's own checkout script pins.
ARG GRANITE_COMMIT=53090429539bf3404250d18fe58db971f0907c04

WORKDIR /pyrowave
RUN git init -q . && git remote add origin https://github.com/Themaister/pyrowave \
 && git fetch -q --depth 1 origin "$PYROWAVE_COMMIT" && git checkout -q FETCH_HEAD \
 && git init -q Granite && cd Granite && git remote add origin https://github.com/Themaister/Granite \
 && git fetch -q --depth 1 origin "$GRANITE_COMMIT" && git checkout -q FETCH_HEAD \
 && git submodule update -q --init --depth 1 third_party/volk third_party/khronos/vulkan-headers

# libstdc++ goes in statically: the Deck has a newer one, but nothing is lost by not
# depending on it.
RUN cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Release \
      -DCMAKE_SHARED_LINKER_FLAGS="-static-libstdc++ -static-libgcc" \
 && ninja -C build pyrowave-shared

WORKDIR /work
COPY src ./src
COPY test/pyrowave ./test/pyrowave
RUN gcc -O2 -g -std=gnu11 -Wall -Wextra -Wno-unused-parameter -Wno-missing-field-initializers \
      -I src -I /pyrowave -I /pyrowave/Granite/third_party/khronos/vulkan-headers/include \
      -o pyrowave-bench test/pyrowave/bench.c src/encoder.c src/va_encoder.c src/pw_encoder.c src/av1_headers.c src/hevc_headers.c src/overlay.c \
      -L /pyrowave/build -lpyrowave-shared $(pkg-config --libs libva libva-drm) -lm \
      -Wl,-rpath,'$ORIGIN' \
 && mkdir /out && cp pyrowave-bench /out/ && cp -L /pyrowave/build/libpyrowave-shared.so.* /out/

FROM scratch AS out
COPY --from=build /out/ /
