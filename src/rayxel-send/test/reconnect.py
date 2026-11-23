#!/usr/bin/env python3
"""Asks a `rayxel-send --listen` for the picture and leaves again, over and over.

A viewer that goes away must not be noticed by gamescope at all: stopping the capture
while pictures are flowing can take gamescope down, and the Steam session with it (see
capture.h), so the sender keeps its capture from one viewer to the next. Run this, then
look in the Deck's journal for what gamescope said meanwhile:

    journalctl --since "-5 min" | grep "pipewire: stream state"

There should be one line at most, "streaming", from the first round; a "paused" means a
viewer leaving still reaches gamescope. It reports any round in which no picture came,
which with a kept capture includes rounds where nothing on the screen was moving.

It speaks the protocol in src/wire.h and has no dependencies beyond Python itself.
"""

import argparse
import random
import select
import socket
import struct
import sys
import time

MAGIC, VERSION = 0x5852, 1
HELLO, VIDEO, BYE = 1, 5, 6
HEADER = struct.Struct("<HBB")
HELLO_BODY = struct.Struct("<IHHHHI")
SESSION_BODY = struct.Struct("<I")


def packet(kind, body=b""):
    return HEADER.pack(MAGIC, VERSION, kind) + body


def main():
    args = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    args.add_argument("host")
    args.add_argument("--port", type=int, default=45800)
    args.add_argument("--rounds", type=int, default=100)
    args.add_argument("--sizes", default="1280x800",
                      help="WIDTHxHEIGHT to ask for; several, comma-separated, are taken in turn")
    args.add_argument("--silent-every", type=int, default=0, metavar="N",
                      help="every Nth round leaves without saying goodbye")
    o = args.parse_args()
    sizes = [tuple(int(v) for v in s.lower().split("x")) for s in o.sizes.split(",")]
    sender = (o.host, o.port)

    failed = 0
    waits = []
    for number in range(1, o.rounds + 1):
        width, height = sizes[(number - 1) % len(sizes)]
        session = random.getrandbits(32) or 1
        hello = packet(HELLO, HELLO_BODY.pack(session, width, height, 60, 0, 20000))

        # A socket of its own each round, as a viewer that was restarted would have.
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 4 << 20)
        sock.setblocking(False)

        started = time.perf_counter()
        first = None
        watch = random.uniform(0.15, 0.6)  # so that leaving falls anywhere within a frame
        next_hello = 0.0
        packets = 0
        while True:
            now = time.perf_counter()
            if first is not None and now - first >= watch:
                break
            if first is None and now - started > 8:
                break
            if now >= next_hello:
                sock.sendto(hello, sender)
                next_hello = now + 0.1
            ready, _, _ = select.select([sock], [], [], 0.005)
            if not ready:
                continue
            while True:
                try:
                    data, _ = sock.recvfrom(2048)
                except (BlockingIOError, ConnectionResetError):
                    break
                if len(data) < HEADER.size + 4:
                    continue
                magic, version, kind = HEADER.unpack_from(data)
                if magic != MAGIC or version != VERSION or kind != VIDEO:
                    continue
                if struct.unpack_from("<I", data, HEADER.size)[0] != session:
                    continue
                packets += 1
                if first is None:
                    first = time.perf_counter()

        silent = o.silent_every > 0 and number % o.silent_every == 0
        if not silent:
            sock.sendto(packet(BYE, SESSION_BODY.pack(session)), sender)
        sock.close()

        if first is None:
            failed += 1
            print(f"round {number}: no picture within 8 s at {width}x{height}", flush=True)
            if failed >= 3:
                print("giving up: three rounds without a picture")
                sys.exit(1)
        else:
            waits.append(first - started)
        if number % 20 == 0:
            print(f"round {number}: {number - failed} with a picture so far", flush=True)

    waits.sort()
    middle = waits[len(waits) // 2] if waits else float("nan")
    print(f"{o.rounds} rounds, {failed} without a picture; "
          f"first picture after {middle * 1000:.0f} ms (median), {waits[-1] * 1000:.0f} ms at most"
          if waits else f"{o.rounds} rounds, none with a picture")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
