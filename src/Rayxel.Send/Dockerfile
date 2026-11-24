# Builds rayxel-send - the sender in C# - for Linux: one native file made by NativeAOT, which
# needs no .NET on the machine, with PyroWave's library beside it. Run from src/:
#
#   docker build -f Rayxel.Send/Dockerfile --output type=local,dest=Rayxel.Send/out/linux-x64 .
#
# leaves Rayxel.Send/out/linux-x64/rayxel-send and libpyrowave-shared.so.1, which is what the
# Decky plugin carries in its bin folder (see rayxel-decky/build.sh). SteamOS has a newer
# glibc than the build image, so the file runs there; the libraries it calls at run time -
# libpipewire-0.3, libva, libva-drm, libX11, libXfixes, libssl - are the system's own.

# PyroWave, pinned as for the C sender (src/rayxel-send/Dockerfile): both ends must agree on
# the bitstream, and the codec's interface is not declared stable yet.
FROM debian:12-slim AS pyrowave
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates git cmake ninja-build g++ python3 \
 && rm -rf /var/lib/apt/lists/*
ARG PYROWAVE_COMMIT=a32b05e03d70826b19d4d11ff74fe166ed74371b
ARG GRANITE_COMMIT=53090429539bf3404250d18fe58db971f0907c04
WORKDIR /pyrowave
RUN git init -q . && git remote add origin https://github.com/Themaister/pyrowave \
 && git fetch -q --depth 1 origin "$PYROWAVE_COMMIT" && git checkout -q FETCH_HEAD \
 && git init -q Granite && cd Granite && git remote add origin https://github.com/Themaister/Granite \
 && git fetch -q --depth 1 origin "$GRANITE_COMMIT" && git checkout -q FETCH_HEAD \
 && git submodule update -q --init --depth 1 third_party/volk third_party/khronos/vulkan-headers
RUN cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Release \
      -DCMAKE_SHARED_LINKER_FLAGS="-static-libstdc++ -static-libgcc" \
 && ninja -C build pyrowave-shared \
 && mkdir -p /out && cp -L build/libpyrowave-shared.so.1 /out/

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# NativeAOT links with clang and needs zlib's headers.
RUN apt-get update \
 && apt-get install -y --no-install-recommends clang zlib1g-dev \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY Rayxel.Link/ Rayxel.Link/
COPY Rayxel.Send.Linux/ Rayxel.Send.Linux/
# Only for its project file: the Windows build of the same project refers to it, and the
# restore reads every build's references.
COPY Rayxel.Send.Windows/ Rayxel.Send.Windows/
COPY Rayxel.Send/ Rayxel.Send/
RUN dotnet publish Rayxel.Send/Rayxel.Send.csproj -c Release -f net10.0 -r linux-x64 \
      -p:EnableWindowsTargeting=true -o /out

FROM scratch AS out
COPY --from=build /out/rayxel-send /rayxel-send
COPY --from=pyrowave /out/libpyrowave-shared.so.1 /libpyrowave-shared.so.1
