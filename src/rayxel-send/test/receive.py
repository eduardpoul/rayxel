#!/usr/bin/env python3
"""A stand-in for Rayxel's network source, for testing rayxel-send on its own.

Asks a `rayxel-send --listen` for the picture, puts the frames back together, and reports
what the network did to them: how many were lost, how big they were, and how long each
took from the moment gamescope handed it to the sender to the moment its last packet
arrived here. With --out the stream is also written as an Annex B file any HEVC decoder
can check.

It speaks the protocol in src/wire.h and has no dependencies beyond Python itself.
"""

import argparse
import random
import select
import socket
import struct
import time

MAGIC, VERSION = 0x5852, 1
HELLO, PING, PONG, KEYFRAME, VIDEO, BYE = 1, 2, 3, 4, 5, 6
PAYLOAD_MAX = 1200

HEADER = struct.Struct("<HBB")
HELLO_BODY = struct.Struct("<IHHHHI")
PING_BODY = struct.Struct("<IQ")
PONG_BODY = struct.Struct("<IQQ")
SESSION_BODY = struct.Struct("<I")
VIDEO_BODY = struct.Struct("<IIHHIHHB3xQI")


def packet(kind, body=b""):
    return HEADER.pack(MAGIC, VERSION, kind) + body


def percentiles(values):
    if not values:
        return "no samples"
    ordered = sorted(values)
    at = lambda p: ordered[min(len(ordered) - 1, int(p * (len(ordered) - 1) + 0.5))]
    return f"p50 {at(0.50):.2f}  p95 {at(0.95):.2f}  p99 {at(0.99):.2f}  max {ordered[-1]:.2f}"


def main():
    args = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    args.add_argument("host")
    args.add_argument("--port", type=int, default=45800)
    args.add_argument("--size", default="0x0", help="WIDTHxHEIGHT to ask for (default: the Deck's own)")
    args.add_argument("--bitrate", type=float, default=20, help="Mbit/s")
    args.add_argument("--fps", type=int, default=60)
    args.add_argument("--seconds", type=float, default=10)
    args.add_argument("--out", help="write the stream here")
    o = args.parse_args()
    width, height = (int(v) for v in o.size.lower().split("x"))

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 8 << 20)
    sock.setblocking(False)
    sender = (o.host, o.port)
    session = random.getrandbits(32) or 1
    hello = packet(HELLO, HELLO_BODY.pack(session, width, height, o.fps, 0, int(o.bitrate * 1000)))

    now = time.perf_counter_ns
    started = now()
    next_hello = next_ping = 0
    first_frame_at = None

    # Clock difference, sender minus viewer, from the quickest round trips seen.
    pings = []  # (round trip ns, offset ns)
    offset = None

    current, parts, got, parts_needed = None, {}, 0, 0  # the frame being put together
    waiting_for_keyframe = True
    frames = keyframes = lost = late_packets = 0
    total_bytes = 0
    sizes, transit, encode, total, gaps = [], [], [], [], []
    last_arrival = None
    geometry = None
    out = open(o.out, "wb") if o.out else None

    def ask_keyframe():
        sock.sendto(packet(KEYFRAME, SESSION_BODY.pack(session)), sender)

    while True:
        t = now()
        if first_frame_at is not None and t - first_frame_at > o.seconds * 1e9:
            break
        if first_frame_at is None and t - started > 8e9:
            break
        if t >= next_hello:
            sock.sendto(hello, sender)
            next_hello = t + 500_000_000
        if t >= next_ping:
            sock.sendto(packet(PING, PING_BODY.pack(0, t)), sender)
            # Often at first, to have a good offset before the first frames are judged by it.
            next_ping = t + (50_000_000 if len(pings) < 20 else 250_000_000)

        ready, _, _ = select.select([sock], [], [], 0.005)
        if not ready:
            continue
        while True:
            try:
                data, _ = sock.recvfrom(2048)
            except (BlockingIOError, ConnectionResetError):
                break
            arrived = now()
            if len(data) < HEADER.size:
                continue
            magic, version, kind = HEADER.unpack_from(data)
            if magic != MAGIC or version != VERSION:
                continue

            if kind == PONG and len(data) >= HEADER.size + PONG_BODY.size:
                _, sent, sender_time = PONG_BODY.unpack_from(data, HEADER.size)
                round_trip = arrived - sent
                pings.append((round_trip, sender_time - (sent + round_trip // 2)))
                pings[:] = pings[-64:]
                offset = min(pings)[1]
                continue

            if kind != VIDEO or len(data) < HEADER.size + VIDEO_BODY.size:
                continue
            (sess, frame, index, count, frame_bytes, w, h, flags, captured, encode_us) = VIDEO_BODY.unpack_from(
                data, HEADER.size
            )
            if sess != session:
                continue
            payload = data[HEADER.size + VIDEO_BODY.size :]

            if current is not None and frame < current:
                late_packets += 1
                continue
            if frame != current:
                # A newer frame has started. The one before it is lost if it never became
                # whole, and so is every frame whose packets did not arrive at all; either
                # way what follows cannot be decoded until the next keyframe.
                missing = 0
                if current is not None:
                    missing = (1 if got < parts_needed else 0) + (frame - current - 1)
                if missing:
                    lost += missing
                    waiting_for_keyframe = True
                    ask_keyframe()
                current, parts, got, parts_needed = frame, {}, 0, count
            if index in parts:
                continue
            parts[index] = payload
            got += 1
            if got < count:
                continue

            # The frame is whole.
            if first_frame_at is None:
                first_frame_at = arrived
            keyframe = bool(flags & 1)
            if keyframe:
                waiting_for_keyframe = False
            if waiting_for_keyframe:
                continue

            frames += 1
            keyframes += keyframe
            total_bytes += frame_bytes
            geometry = (w, h)
            if not keyframe:
                sizes.append(frame_bytes / 1024)
            encode.append(encode_us / 1000)
            if last_arrival is not None:
                gaps.append((arrived - last_arrival) / 1e6)
            last_arrival = arrived
            if offset is not None:
                age = (arrived - (captured - offset)) / 1e6
                total.append(age)
                transit.append(age - encode_us / 1000)
            if out:
                out.write(b"".join(parts[i] for i in range(count)))

    sock.sendto(packet(BYE, SESSION_BODY.pack(session)), sender)
    if out:
        out.close()

    if frames == 0:
        print("no frames arrived - is rayxel-send --listen running, and is something moving on the Deck?")
        return 1
    span = (last_arrival - first_frame_at) / 1e9
    best = min(pings) if pings else None
    print(f"stream     : {geometry[0]}x{geometry[1]}, {frames} frames in {span:.2f} s = {(frames - 1) / span:.2f} fps,"
          f" {keyframes} keyframe(s)")
    print(f"bitrate    : {total_bytes * 8 / span / 1e6:.2f} Mbit/s received, {o.bitrate:.1f} asked for")
    print(f"lost       : {lost} frame(s) incomplete or missing, {late_packets} packet(s) too late to use")
    print(f"frame kB   : {percentiles(sizes)}")
    print(f"arrival ms : {percentiles(gaps)}  (gap between frames; {1000 / o.fps:.2f} is even)")
    if best:
        print(f"round trip : best {best[0] / 1e6:.2f} ms over {len(pings)} pings")
    print(f"encode ms  : {percentiles(encode)}  (on the Deck: frame in hand to bitstream in hand)")
    print(f"network ms : {percentiles(transit)}  (bitstream in hand to last packet here)")
    print(f"total ms   : {percentiles(total)}  (frame reaches the sender to frame complete here)")
    if o.out:
        print(f"file       : {o.out}, {total_bytes / 1048576:.2f} MB")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
