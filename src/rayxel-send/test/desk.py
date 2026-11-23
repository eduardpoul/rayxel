#!/usr/bin/env python3
"""Plays a short, harmless sequence on a rayxel-send's virtual keyboard and mouse.

Stands in for Rayxel with the keyboard and mouse captured: asks the sender for the
picture, as a viewer must before its input is listened to, then holds Scroll Lock for a
moment - a key nothing on the Deck reacts to - and moves the pointer a little way and
back. No button is clicked, so it can be run against a Deck that is in use.

With --hold it holds the key and then stops sending without letting go, which is the
case the sender has to notice by itself.

It speaks the protocol in src/wire.h and has no dependencies beyond Python itself.
"""

import argparse
import random
import socket
import struct
import time

MAGIC, VERSION = 0x5852, 1
HELLO, BYE, KEYS, MOUSE = 1, 6, 9, 10

HEADER = struct.Struct("<HBB")
HELLO_BODY = struct.Struct("<IHHHHI")
SESSION_BODY = struct.Struct("<I")
KEYS_BODY = struct.Struct("<II32s")
MOUSE_BODY = struct.Struct("<IIB3xiiii")

KEY_SCROLLLOCK = 70


def packet(kind, body=b""):
    return HEADER.pack(MAGIC, VERSION, kind) + body


def main():
    args = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    args.add_argument("host")
    args.add_argument("--port", type=int, default=45800)
    args.add_argument("--hold", action="store_true", help="hold a key and go silent without releasing it")
    o = args.parse_args()

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sender = (o.host, o.port)
    session = random.getrandbits(32) or 1
    sequence = {KEYS: 0, MOUSE: 0}
    position = [0, 0]
    last_hello = 0.0

    def tick(down=(), move=(0, 0)):
        """One round of what a viewer sends: its keyboard and its mouse, whole."""
        nonlocal last_hello
        if time.monotonic() - last_hello > 0.5:
            sock.sendto(packet(HELLO, HELLO_BODY.pack(session, 640, 400, 60, 0, 2000)), sender)
            last_hello = time.monotonic()

        bitmap = bytearray(32)
        for key in down:
            bitmap[key >> 3] |= 1 << (key & 7)
        sequence[KEYS] += 1
        sock.sendto(packet(KEYS, KEYS_BODY.pack(session, sequence[KEYS], bytes(bitmap))), sender)

        position[0] += move[0]
        position[1] += move[1]
        sequence[MOUSE] += 1
        sock.sendto(packet(MOUSE, MOUSE_BODY.pack(session, sequence[MOUSE], 0, position[0], position[1], 0, 0)), sender)

    def hold(seconds, down=(), step=(0, 0)):
        end = time.monotonic() + seconds
        while time.monotonic() < end:
            tick(down, step)
            time.sleep(0.02)

    tick()
    time.sleep(0.3)

    hold(0.8)                                # at rest: creates the devices and lets the Deck notice them
    hold(0.5, down=[KEY_SCROLLLOCK])         # a key held
    hold(0.3)                                # and released
    hold(0.4, step=(-6, -4))                 # the pointer up and to the left
    hold(0.3)
    hold(0.4, step=(6, 4))                   # and back
    if o.hold:
        hold(0.3, down=[KEY_SCROLLLOCK])
        print("holding Scroll Lock and going silent; the sender should release it within a second")
        time.sleep(1.5)
    else:
        hold(0.2)

    sock.sendto(packet(BYE, SESSION_BODY.pack(session)), sender)
    print(f"sent {sequence[KEYS]} keyboard and {sequence[MOUSE]} mouse states")


if __name__ == "__main__":
    main()
