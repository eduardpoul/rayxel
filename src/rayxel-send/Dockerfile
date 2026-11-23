# Builds rayxel-send for the Steam Deck.
#
#   docker build --target out --output type=local,dest=out .     -> out/rayxel-send
#   docker build --target test -t rayxel-send-test . ; docker run --rm rayxel-send-test
#
# Debian 12 on purpose: its glibc (2.36) is older than any SteamOS 3 release's, and the
# libraries linked against - libpipewire-0.3.so.0, libva.so.2, libva-drm.so.2, libX11.so.6,
# libXfixes.so.3 - keep their ABI, so a binary built here runs against the newer ones on
# the Deck.

FROM debian:12-slim AS build
RUN apt-get update \
 && apt-get install -y --no-install-recommends gcc libc6-dev make pkg-config libpipewire-0.3-dev libva-dev libx11-dev libxfixes-dev \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /work
COPY Makefile ./
COPY src ./src
COPY test ./test
RUN make -j"$(nproc)"

# Nothing but the binary, for `--output`.
FROM scratch AS out
COPY --from=build /work/build/rayxel-send /rayxel-send

# A PipeWire session with a stand-in for gamescope in it, to run the probe against.
FROM build AS test
RUN apt-get update \
 && apt-get install -y --no-install-recommends pipewire wireplumber dbus dbus-user-session \
 && rm -rf /var/lib/apt/lists/*
ENTRYPOINT ["sh", "/work/test/run-in-container.sh"]
