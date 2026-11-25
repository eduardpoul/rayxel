"""How well a clip's estimated vectors explain it, for a clip whose true motion is not known:
each frame is rebuilt from the one before by moving it along the vectors, and compared with
the frame itself. What is left over is what the vectors got wrong - together with what no
vectors could have got right: things appearing from behind others, light changing, noise.

Usage: explains.py CLIP.y4m FLOWDIR [FLOWDIR...]
  FLOWDIR  a folder of NNNN.flow files written by `Rayxel --clip-file=CLIP.y4m --clip-vectors`

For each folder: the mean difference in grey levels (of 255) over the whole picture and over
the pixels with detail, beside the same with no motion assumed at all, and the share of
detailed pixels rebuilt to within 4 grey levels.
It needs numpy.
"""
import glob
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from measure import brightness, detailed, load  # noqa: E402


def moved(previous, flow):
    """The previous frame read where each pixel came from, between its pixels."""
    h, w = previous.shape
    ys, xs = np.mgrid[0:h, 0:w].astype(np.float32)
    x = np.clip(xs + flow[..., 0], 0, w - 1.001)
    y = np.clip(ys + flow[..., 1], 0, h - 1.001)
    x0, y0 = np.floor(x).astype(int), np.floor(y).astype(int)
    fx, fy = x - x0, y - y0
    top = previous[y0, x0] * (1 - fx) + previous[y0, x0 + 1] * fx
    bottom = previous[y0 + 1, x0] * (1 - fx) + previous[y0 + 1, x0 + 1] * fx
    return top * (1 - fy) + bottom * fy


def main():
    clip, folders = sys.argv[1], sys.argv[2:]
    numbers = sorted(int(os.path.basename(p)[:4]) for p in glob.glob(os.path.join(folders[0], "*.flow")))
    planes = brightness(clip, max(numbers) + 1)
    print(f"{'':<16}{'whole picture':>15}{'with detail':>13}{'within 4 levels':>17}")
    still_all = still_detail = 0.0
    rows = []
    for folder in folders:
        total = detail_total = close = 0.0
        frames = 0
        for t in numbers:
            path = os.path.join(folder, f"{t:04d}.flow")
            if not os.path.exists(path):
                continue
            left = np.abs(moved(planes[t - 1], load(path)) - planes[t])
            mask = detailed(planes[t])
            total += left.mean()
            detail_total += left[mask].mean()
            close += (left[mask] <= 4).mean()
            frames += 1
            if folder == folders[0]:
                still = np.abs(planes[t - 1] - planes[t])
                still_all += still.mean()
                still_detail += still[mask].mean()
        rows.append((os.path.basename(os.path.normpath(folder)), total / frames, detail_total / frames, 100 * close / frames))
    print(f"{'no motion':<16}{still_all / len(numbers):15.2f}{still_detail / len(numbers):13.2f}")
    for name, a, d, c in rows:
        print(f"{name:<16}{a:15.2f}{d:13.2f}{c:16.1f}%")


if __name__ == "__main__":
    main()
