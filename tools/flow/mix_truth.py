"""An estimate's vectors with the truth put into them in one place only, to find where an
upscaler loses what it loses to the estimate: run it with the result as `--clip-truth` and
see how much of the gap to the truth itself comes back.

Usage: mix_truth.py NAME.npz ESTIMATE_FOLDER TRUTH_FOLDER OUT_FOLDER [--inside | --off PX] [--band N]
  ESTIMATE_FOLDER  NNNN.flow files of `Rayxel --clip-vectors`, one for every frame of the clip
  TRUTH_FOLDER     the same from truth_vectors.py
  default    the truth within N px (4) of where one thing meets another, and on pixels that
             were hidden a frame ago; the estimate everywhere else
  --inside   the other way round: the truth everywhere but there
  --off PX   the truth only where the estimate is further than PX from it, away from outlines
It needs numpy.
"""
import os
import struct
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from measure import load, spread  # noqa: E402


def main():
    argv = sys.argv[1:]
    band, off = 4, None
    if "--band" in argv:
        at = argv.index("--band")
        band = int(argv[at + 1])
        del argv[at:at + 2]
    if "--off" in argv:
        at = argv.index("--off")
        off = float(argv[at + 1])
        del argv[at:at + 2]
    inside = "--inside" in argv
    args = [a for a in argv if not a.startswith("--")]

    data = np.load(args[0])
    label, hidden = data["label"], data["hidden"]
    os.makedirs(args[3], exist_ok=True)
    done = 0
    for t in range(label.shape[0]):
        a, b = os.path.join(args[1], f"{t + 1:04d}.flow"), os.path.join(args[2], f"{t + 1:04d}.flow")
        if not (os.path.exists(a) and os.path.exists(b)):
            continue
        estimate, truth = load(a), load(b)
        lab = label[t]
        meet = np.zeros(lab.shape, bool)
        meet[:, 1:] |= lab[:, 1:] != lab[:, :-1]
        meet[1:, :] |= lab[1:, :] != lab[:-1, :]
        meet |= lab == 255
        near = spread(meet, band) | hidden[t]
        take = ~near if inside else near
        if off is not None:
            take = ~near & (np.hypot(estimate[..., 0] - truth[..., 0], estimate[..., 1] - truth[..., 1]) > off)
        out = np.where(take[..., None], truth, estimate)
        h, w = lab.shape
        with open(os.path.join(args[3], f"{t + 1:04d}.flow"), "wb") as f:
            f.write(b"RXFP" + struct.pack("<2i", w, h))
            f.write(out.astype("<f2").tobytes())
        done += 1
    print(f"{done} frames mixed into {args[3]}")


if __name__ == "__main__":
    main()
