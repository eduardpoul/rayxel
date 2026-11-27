"""Checks the vectors `Rayxel --clip-file=NAME.y4m --clip-vectors` wrote against the truth
make_clips.py wrote beside the clip.

Usage: measure.py NAME.npz FLOWDIR [FLOWDIR...] [--map OUT.png]
  FLOWDIR  a folder of NNNN.flow files, one per frame ("flow-fast", "flow-max", ...)
  --map    for the first folder's frame 0003: the error as a picture - black where the
           vector is right, white where it is 2 px or more off

For each folder a line per kind of pixel: how far off the vectors are (mean, median, the
90th percentile, in pixels of the picture) and how many are within 0.1, 0.25, 0.5 and 1
pixel of the truth. The kinds:
  seen          every pixel that was in the frame before and belongs to one thing only
  - inside      those more than 8 px from where one thing meets another
  - by an edge  those within 4 px of such a place
  - with detail those with something to tell them by: brightness varying around them by
                more than a few grey levels. The rest - a clear sky, a shadow - looks the
                same wherever it is put, so a wrong vector there shows nowhere
  background, flat box, cut-outs, bare cut-out, panel, text
                the same pixels by whose they are
  hidden        pixels that were not in the frame before at all: nothing can be measured
                there, the truth being where their own layer came from
It needs numpy, and Pillow for --map.
"""
import glob
import os
import sys

import numpy as np


def load(path):
    with open(path, "rb") as f:
        head = f.read(12)
        assert head[:4] == b"RXFP", f"{path}: not a per-pixel vector file"
        w, h = np.frombuffer(head, "<i4", 2, 4)
        return np.frombuffer(f.read(), "<f2").reshape(h, w, 2).astype(np.float32)


def spread(mask, radius):
    """The mask grown by `radius` pixels every way."""
    out = mask.copy()
    for _ in range(radius):
        grown = out.copy()
        grown[1:, :] |= out[:-1, :]
        grown[:-1, :] |= out[1:, :]
        grown[:, 1:] |= out[:, :-1]
        grown[:, :-1] |= out[:, 1:]
        grown[1:, 1:] |= out[:-1, :-1]
        grown[:-1, :-1] |= out[1:, 1:]
        grown[1:, :-1] |= out[:-1, 1:]
        grown[:-1, 1:] |= out[1:, :-1]
        out = grown
    return out


def brightness(path, frames):
    """The first frames' brightness planes of a .y4m clip."""
    with open(path, "rb") as f:
        head = f.readline().split()
        w = int(next(t for t in head if t.startswith(b"W"))[1:])
        h = int(next(t for t in head if t.startswith(b"H"))[1:])
        out = []
        for _ in range(frames):
            if not f.readline().startswith(b"FRAME"):
                break
            out.append(np.frombuffer(f.read(w * h), np.uint8).reshape(h, w).astype(np.float32))
            f.seek(w * h // 2, 1)
        return out


def detailed(y, radius=3, least=4.0):
    """Where brightness varies around a pixel by more than `least` grey levels (as a deviation)."""
    n = 2 * radius + 1
    pad = np.pad(y, radius, mode="edge")
    total, squares = np.zeros_like(y), np.zeros_like(y)
    for dy in range(n):
        for dx in range(n):
            part = pad[dy:dy + y.shape[0], dx:dx + y.shape[1]]
            total += part
            squares += part * part
    mean = total / (n * n)
    return np.sqrt(np.maximum(squares / (n * n) - mean * mean, 0.0)) >= least


def line(name, errs):
    if errs.size == 0:
        return None
    share = lambda limit: (errs <= limit).mean() * 100
    return (f"  {name:<14} {errs.size / 1000:7.0f}k  mean {errs.mean():6.3f}  median {np.median(errs):6.3f}  "
            f"p90 {np.percentile(errs, 90):6.3f}   within 0.1 px {share(0.1):5.1f} %   0.25 {share(0.25):5.1f} %   "
            f"0.5 {share(0.5):5.1f} %   1 {share(1.0):5.1f} %")


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--map")]
    map_path = next((a.split("=", 1)[1] for a in sys.argv[1:] if a.startswith("--map=")), None)
    truth = np.load(args[0])
    flow, label, hidden = truth["flow"], truth["label"], truth["hidden"]

    clip = os.path.splitext(args[0])[0] + ".y4m"
    planes = brightness(clip, flow.shape[0] + 1) if os.path.exists(clip) else []
    detail = [detailed(y) for y in planes]

    for n, folder in enumerate(args[1:]):
        kinds = {k: [] for k in ("seen", "- inside", "- by an edge", "- with detail", "background", "flat box", "cut-outs",
                                 "bare cut-out", "panel", "text", "hidden")}
        frames = 0
        for t in range(flow.shape[0]):
            path = os.path.join(folder, f"{t + 1:04d}.flow")
            if not os.path.exists(path):
                continue
            frames += 1
            got = load(path)
            err = np.hypot(got[..., 0] - flow[t, ..., 0], got[..., 1] - flow[t, ..., 1])
            lab = label[t]
            one = lab != 255
            seen = one & ~hidden[t]
            # Where one thing meets another: a pixel whose neighbour is someone else's.
            meet = np.zeros_like(one)
            meet[:, 1:] |= lab[:, 1:] != lab[:, :-1]
            meet[1:, :] |= lab[1:, :] != lab[:-1, :]
            meet |= ~one
            near, far = spread(meet, 4), ~spread(meet, 8)
            # The picture's own edge is such a place too: what is beyond it was never seen.
            far[:8, :] = far[-8:, :] = False
            far[:, :8] = far[:, -8:] = False
            kinds["seen"].append(err[seen])
            kinds["- inside"].append(err[seen & far])
            kinds["- by an edge"].append(err[seen & near])
            if t + 1 < len(detail):
                kinds["- with detail"].append(err[seen & far & detail[t + 1]])
            kinds["background"].append(err[seen & (lab == 0)])
            kinds["flat box"].append(err[seen & (lab == 1)])
            kinds["cut-outs"].append(err[seen & ((lab == 2) | (lab == 3))])
            kinds["bare cut-out"].append(err[seen & (lab == 6)])
            kinds["panel"].append(err[seen & (lab == 4)])
            kinds["text"].append(err[seen & (lab == 5)])
            kinds["hidden"].append(err[one & hidden[t]])
            if n == 0 and map_path and t == 2:
                from PIL import Image
                Image.fromarray((np.clip(err / 2.0, 0, 1) * 255).astype(np.uint8)).save(map_path)

        print(f"{os.path.basename(os.path.normpath(folder))}  ({frames} frames against {os.path.basename(args[0])})")
        for name, parts in kinds.items():
            text = line(name, np.concatenate(parts)) if parts else None
            if text:
                print(text)


if __name__ == "__main__":
    main()
