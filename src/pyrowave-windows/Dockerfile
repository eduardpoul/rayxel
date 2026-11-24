# Builds the PyroWave codec's library for Windows, for Rayxel to decode a Steam Deck's
# PyroWave stream with:
#
#   docker build --output type=local,dest=out .     -> out/libpyrowave-shared-1.dll
#
# The same pinned commits as the Deck's side (src/rayxel-send/Dockerfile): the two ends
# have to agree on the bitstream, and the codec's interface is not declared stable yet.
# Cross-compiled with MinGW, which the codec's own build supports; libstdc++, libgcc and
# winpthread go in statically, so the DLL needs nothing beside it but a Vulkan driver.
# Debian 13 for CMake 3.27 or later, which the codec's build asks for on Windows.

FROM debian:13-slim AS build
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates git cmake ninja-build python3 \
      g++-mingw-w64-x86-64-posix gcc-mingw-w64-x86-64-posix \
 && rm -rf /var/lib/apt/lists/*

ARG PYROWAVE_COMMIT=a32b05e03d70826b19d4d11ff74fe166ed74371b
ARG GRANITE_COMMIT=53090429539bf3404250d18fe58db971f0907c04
WORKDIR /pyrowave
RUN git init -q . && git remote add origin https://github.com/Themaister/pyrowave \
 && git fetch -q --depth 1 origin "$PYROWAVE_COMMIT" && git checkout -q FETCH_HEAD \
 && git init -q Granite && cd Granite && git remote add origin https://github.com/Themaister/Granite \
 && git fetch -q --depth 1 origin "$GRANITE_COMMIT" && git checkout -q FETCH_HEAD \
 && git submodule update -q --init --depth 1 third_party/volk third_party/khronos/vulkan-headers

RUN printf '%s\n' \
      'set(CMAKE_SYSTEM_NAME Windows)' \
      'set(CMAKE_SYSTEM_PROCESSOR AMD64)' \
      'set(CMAKE_C_COMPILER x86_64-w64-mingw32-gcc-posix)' \
      'set(CMAKE_CXX_COMPILER x86_64-w64-mingw32-g++-posix)' \
      'set(CMAKE_RC_COMPILER x86_64-w64-mingw32-windres)' \
      'set(CMAKE_FIND_ROOT_PATH /usr/x86_64-w64-mingw32)' \
      'set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)' \
      'set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)' \
      'set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)' > /mingw.cmake \
 && cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_TOOLCHAIN_FILE=/mingw.cmake \
 && ninja -C build pyrowave-shared \
 && mkdir /out && cp build/libpyrowave-shared-1.dll /out/ \
 && x86_64-w64-mingw32-strip /out/libpyrowave-shared-1.dll

FROM scratch AS out
COPY --from=build /out/ /
