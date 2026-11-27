"""How close each run of a clip through an upscaler came to the clip's larger reference,
part of the picture by part: `Rayxel --clip-file=NAME.y4m --shot=OUT.png ...` writes the
runs into OUT-clip/RUN/NNNN.png, and the same with NAME-ref.y4m and --clip-ways=none writes
what they ought to have arrived at.

Usage: quality.py NAME.npz REFERENCE_FOLDER RUN_FOLDER... [--from N] [--every N]
  --from N   the first frame compared (default 8: an upscaler that gathers detail over
             frames has not got far before that)
  --every N  compare only every Nth frame

For each run, PSNR in dB against the reference (more is closer; a difference under about
0.2 dB is nothing) over these parts of the picture, as the truth beside the clip marks them:
  all        every pixel
  detail     pixels well inside one thing, with something drawn there
  outlines   pixels within 4 px of where one thing meets another
  cut-outs   the things crossing the picture
  text       the still text over the moving picture, and 3 px around it
  panel      the still panel
and `sharp`: how much fine detail the detailed pixels have beside the reference's, as a
percentage - an upscaler that only softens scores low here whatever its PSNR.
It needs numpy and Pillow.
"""
import os
import sys

import numpy as np
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from measure import brightness, detailed, spread  # noqa: E402


def load(path):
    return np.asarray(Image.open(path).convert("RGB"), np.float32)


def enlarge(mask, shape):
    """A mask of the clip's pixels spread over a picture of another size, each pixel of
    the picture taking the clip's pixel it lies in."""
    rows = np.arange(shape[0]) * mask.shape[0] // shape[0]
    columns = np.arange(shape[1]) * mask.shape[1] // shape[1]
    return mask[rows][:, columns]


def fine(picture):
    """What is left of the brightness when its local average is taken away."""
    y = picture @ np.array([0.2126, 0.7152, 0.0722], np.float32)
    blur = (y + np.roll(y, 1, 0) + np.roll(y, -1, 0) + np.roll(y, 1, 1) + np.roll(y, -1, 1)) / 5.0
    return np.abs(y - blur)


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    first = int(sys.argv[sys.argv.index("--from") + 1]) if "--from" in sys.argv else 8
    every = int(sys.argv[sys.argv.index("--every") + 1]) if "--every" in sys.argv else 1
    if "--from" in sys.argv or "--every" in sys.argv:
        args = [a for a in args if not a.isdigit()]
    truth = np.load(args[0])
    label, hidden = truth["label"], truth["hidden"]
    planes = brightness(os.path.splitext(args[0])[0] + ".y4m", label.shape[0] + 1)
    reference, runs = args[1], args[2:]

    parts = ("all", "detail", "outlines", "cut-outs", "text", "panel")
    print(f"{'':<24}" + "".join(f"{p:>10}" for p in parts) + f"{'sharp':>9}")
    for run in runs:
        squares = {p: 0.0 for p in parts}
        counts = {p: 0 for p in parts}
        fine_run = fine_ref = 0.0
        for t in range(first, label.shape[0] + 1, every):
            a, b = os.path.join(run, f"{t:04d}.png"), os.path.join(reference, f"{t:04d}.png")
            if not (os.path.exists(a) and os.path.exists(b)):
                continue
            got, want = load(a), load(b)
            if got.shape != want.shape:
                continue
            err = ((got - want) ** 2).mean(axis=2)
            lab = label[t - 1]
            one = lab != 255
            meet = np.zeros_like(one)
            meet[:, 1:] |= lab[:, 1:] != lab[:, :-1]
            meet[1:, :] |= lab[1:, :] != lab[:-1, :]
            meet |= ~one
            near, far = spread(meet, 4), ~spread(meet, 8)
            masks = {
                "all": np.ones_like(one),
                "detail": one & far & ~hidden[t - 1] & detailed(planes[t]),
                "outlines": near,
                "cut-outs": (lab == 2) | (lab == 3),
                "text": spread(lab == 5, 3),
                "panel": lab == 4,
            }
            for p, m in masks.items():
                m2 = enlarge(m, err.shape)
                squares[p] += float(err[m2].sum())
                counts[p] += int(m2.sum())
            d2 = enlarge(masks["detail"], err.shape)
            fine_run += float(fine(got)[d2].sum())
            fine_ref += float(fine(want)[d2].sum())

        def psnr(p):
            return 10 * np.log10(255.0 ** 2 / (squares[p] / counts[p])) if counts[p] else float("nan")

        name = os.path.basename(os.path.normpath(run))
        print(f"{name:<24}" + "".join(f"{psnr(p):10.2f}" for p in parts) + f"{100 * fine_run / max(fine_ref, 1e-9):8.1f}%")


if __name__ == "__main__":
    main()
