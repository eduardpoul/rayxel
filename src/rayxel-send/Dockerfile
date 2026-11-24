# Builds rayxel-send for the Steam Deck.
#
#   docker build --target out --output type=local,dest=out .     -> out/rayxel-send,
#                                                                    out/libpyrowave-shared.so.1
#   docker build --target test -t rayxel-send-test . ; docker run --rm rayxel-send-test
#
# Debian 12 on purpose: its glibc (2.36) is older than any SteamOS 3 release's, and the
# libraries linked against - libpipewire-0.3.so.0, libva.so.2, libva-drm.so.2, libX11.so.6,
# libXfixes.so.3 - keep their ABI, so a binary built here runs against the newer ones on
# the Deck.

# The PyroWave codec, pinned: its C interface is not declared stable yet. rayxel-send loads
# it at run time from beside itself, and runs without it, offering HEVC and AV1 only.
FROM debian:12-slim AS pyrowave
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates git cmake ninja-build g++ python3 \
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
 && ninja -C build pyrowave-shared \
 && mkdir -p /out/include/vulkan /out/lib \
 && cp pyrowave.h /out/include/ \
 && cp -r Granite/third_party/khronos/vulkan-headers/include/. /out/include/ \
 && cp -L build/libpyrowave-shared.so.1 /out/lib/

FROM debian:12-slim AS build
RUN apt-get update \
 && apt-get install -y --no-install-recommends gcc libc6-dev make pkg-config libpipewire-0.3-dev libva-dev libx11-dev libxfixes-dev \
 && rm -rf /var/lib/apt/lists/*
COPY --from=pyrowave /out/include /pyrowave
COPY --from=pyrowave /out/lib/libpyrowave-shared.so.1 /usr/local/lib/
WORKDIR /work
COPY Makefile ./
COPY src ./src
COPY test ./test
RUN make -j"$(nproc)" PYROWAVE=/pyrowave \
 && cp /usr/local/lib/libpyrowave-shared.so.1 build/

# Nothing but the program and the codec's library beside it, for `--output`.
FROM scratch AS out
COPY --from=build /work/build/rayxel-send /rayxel-send
COPY --from=build /work/build/libpyrowave-shared.so.1 /libpyrowave-shared.so.1

# A PipeWire session with a stand-in for gamescope in it, to run the probe against.
FROM build AS test
RUN apt-get update \
 && apt-get install -y --no-install-recommends pipewire wireplumber dbus dbus-user-session \
 && rm -rf /var/lib/apt/lists/*
ENTRYPOINT ["sh", "/work/test/run-in-container.sh"]
