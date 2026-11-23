#!/usr/bin/env python3
"""Plays a short, harmless sequence on a rayxel-send's virtual gamepad, for testing it.

Stands in for Rayxel with a controller attached: asks the sender for the picture, as a
viewer must before its input is listened to, and then sends gamepad states. The default
sequence presses nothing a game or the Steam interface reacts to - it clicks the right
stick and nudges it a little - so it can be run against a Deck that is in use. Watch the
result on the Deck by reading the "Rayxel gamepad" input device.

With --hold it holds the right-stick click and then stops sending without letting go,
which is the case the sender has to notice by itself.

It speaks the protocol in src/wire.h and has no dependencies beyond Python itself.
"""

import argparse
import random
import socket
import struct
import time

MAGIC, VERSION = 0x5852, 1
HELLO, BYE, PAD = 1, 6, 8

HEADER = struct.Struct("<HBB")
HELLO_BODY = struct.Struct("<IHHHHI")
SESSION_BODY = struct.Struct("<I")
PAD_BODY = struct.Struct("<IIHBBhhhh")

RIGHT_THUMB = 0x0080


def packet(kind, body=b""):
    return HEADER.pack(MAGIC, VERSION, kind) + body


def main():
    args = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    args.add_argument("host")
    args.add_argument("--port", type=int, default=45800)
    args.add_argument("--hold", action="store_true", help="hold a button and go silent without releasing it")
    o = args.parse_args()

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sender = (o.host, o.port)
    session = random.getrandbits(32) or 1
    sequence = 0

    def hello():
        sock.sendto(packet(HELLO, HELLO_BODY.pack(session, 640, 400, 60, 0, 2000)), sender)

    def pad(buttons=0, lt=0, rt=0, lx=0, ly=0, rx=0, ry=0):
        nonlocal sequence
        sequence += 1
        sock.sendto(packet(PAD, PAD_BODY.pack(session, sequence, buttons, lt, rt, lx, ly, rx, ry)), sender)

    def hold(seconds, **state):
        """The same state, repeated the way a viewer repeats it."""
        end = time.monotonic() + seconds
        last_hello = 0
        while time.monotonic() < end:
            if time.monotonic() - last_hello > 0.5:
                hello()
                last_hello = time.monotonic()
            pad(**state)
            time.sleep(0.02)

    hello()
    time.sleep(0.3)

    hold(0.3)                                   # at rest: this is what creates the device
    hold(0.3, buttons=RIGHT_THUMB)              # right stick clicked
    hold(0.3)                                   # released
    hold(0.3, rx=3000, ry=-2000)                # nudged right and down, inside any dead zone
    hold(0.3, rt=40)                            # right trigger a little
    if o.hold:
        hold(0.3, buttons=RIGHT_THUMB)
        print("holding the right-stick click and going silent; the sender should release it within a second")
        time.sleep(1.5)
    else:
        hold(0.2)

    sock.sendto(packet(BYE, SESSION_BODY.pack(session)), sender)
    print(f"sent {sequence} gamepad states")


if __name__ == "__main__":
    main()
