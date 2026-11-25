"""Writes a clip's true motion out as vector files, one per frame, in the form Rayxel writes
its own estimate - for `Rayxel --clip-flow=truth --clip-truth=FOLDER`, which hands an
upscaler these in place of the estimate: what it makes of the clip with vectors that could
not be better.

Usage: truth_vectors.py NAME.npz FOLDER [--spread N]
  --spread N  each pixel takes the vector of whatever is in front within N pixels of it,
              which is what a game does with its own vectors before an upscaler has them
              (it knows what is in front by depth): the outline of a thing, and anything
              thin, then goes with the thing and not with what is behind it
It needs numpy.
"""
import os
import struct
import sys

import numpy as np

# What lies over what: the background and its box, the cut-outs in the order they are
# pasted, the panel, the text. A pixel shared by two things is under everything.
DEPTH = {0: 1, 1: 1, 2: 2, 3: 3, 6: 4, 4: 5, 5: 6, 255: 0}


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    spread = int(sys.argv[sys.argv.index("--spread") + 1]) if "--spread" in sys.argv else 0
    if spread:
        args = args[:2]
    data = np.load(args[0])
    truth, label = data["flow"], data["label"]
    os.makedirs(args[1], exist_ok=True)
    order = np.zeros(256, np.int32)
    for whose, depth in DEPTH.items():
        order[whose] = depth

    for t in range(truth.shape[0]):
        flow = truth[t]
        h, w = flow.shape[:2]
        if spread:
            depth = order[label[t]]
            best_depth, best = depth.copy(), flow.copy()
            pad_d = np.pad(depth, spread, mode="edge")
            pad_f = np.pad(flow, ((spread, spread), (spread, spread), (0, 0)), mode="edge")
            for dy in range(2 * spread + 1):
                for dx in range(2 * spread + 1):
                    d = pad_d[dy:dy + h, dx:dx + w]
                    nearer = d > best_depth
                    best_depth = np.where(nearer, d, best_depth)
                    best[nearer] = pad_f[dy:dy + h, dx:dx + w][nearer]
            flow = best
        with open(os.path.join(args[1], f"{t + 1:04d}.flow"), "wb") as f:
            f.write(b"RXFP" + struct.pack("<2i", w, h))
            f.write(flow.astype("<f2").tobytes())
    print(f"{truth.shape[0]} frames' vectors written to {args[1]}" + (f", spread by {spread}" if spread else ""))


if __name__ == "__main__":
    main()
