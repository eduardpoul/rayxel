"""Checks the vectors `Rayxel --flow=only --shot=X.png` wrote (X.flow) against the picture
flowtest-make.sh renders and flowtest-show.sh puts on the Deck's screen.

That picture: a photograph scrolling left 4 px a frame (so each patch was 4 px to the
right in the frame before: the vector is (+4, 0)), a HUD that stays put at
(880,60)-(1220,210), a strip that stays put at y 540-680 with a square crossing it.
Somewhere on the photograph, moving with it, is a flat grey box 280x220, which counts
against the photograph's figures when it is left out.

Usage: flowtest-measure.py FILE.flow... [--truth X,Y] [--map OUT.png]
  --truth  the photograph's vector, when the picture was rendered to scroll otherwise
  --map    for the first file: green where the vector is right, red where it is 4 px or
           more off, blue where it says "did not move"

It needs nothing but Python, and Pillow for --map.
"""
import struct
import sys

TRUTH = (4.0, 0.0)


def load(path):
    data = open(path, "rb").read()
    assert data[:4] == b"RXFL", "not a vector file"
    w, h, vw, vh = struct.unpack_from("<4i", data, 4)
    values = struct.unpack_from(f"<{w * h * 4}f", data, 20)
    return w, h, vw, vh, values


def analyse(path, map_path=None):
    w, h, vw, vh, f = load(path)
    cell = vw / w

    def stats(name, x0, y0, x1, y1, truth, skip=None):
        errs, n = [], 0
        for cy in range(h):
            py = (cy + 0.5) * cell
            if not y0 <= py < y1:
                continue
            for cx in range(w):
                px = (cx + 0.5) * cell
                if not x0 <= px < x1:
                    continue
                if skip and skip[0] <= px < skip[2] and skip[1] <= py < skip[3]:
                    continue
                i = (cy * w + cx) * 4
                errs.append(((f[i] - truth[0]) ** 2 + (f[i + 1] - truth[1]) ** 2) ** 0.5)
        errs.sort()
        n = len(errs)
        share = lambda limit: sum(1 for e in errs if e <= limit) / n * 100
        print(f"  {name:<22} off by: median {errs[n // 2]:5.2f}  p90 {errs[int(n * 0.9)]:5.2f} px"
              f"   within 0.25 px {share(0.25):5.1f} %   0.5 px {share(0.5):5.1f} %   1 px {share(1.0):5.1f} %")

    print(path.replace("\\", "/").split("/")[-1], f"({w}x{h} vectors of {vw}x{vh})")
    hud = (864, 44, 1236, 226)
    stats("scrolling photograph", 8, 8, vw - 8, 524, TRUTH, skip=hud)
    stats("  its top band", 8, 4, vw - 8, 60, TRUTH, skip=hud)
    stats("HUD, inside", 896, 76, 1204, 194, (0.0, 0.0))
    stats("still strip", 8, 556, vw - 8, 664, (0.0, 0.0))

    if map_path:
        from PIL import Image
        # Green where the vector is right for the photograph, red where it is 4 px or more off,
        # blue where it says "did not move".
        image = Image.new("RGB", (w, h))
        px = image.load()
        for cy in range(h):
            for cx in range(w):
                i = (cy * w + cx) * 4
                x, y = f[i], f[i + 1]
                still = (x * x + y * y) ** 0.5
                err = ((x - TRUTH[0]) ** 2 + (y - TRUTH[1]) ** 2) ** 0.5
                if still < 0.25:
                    px[cx, cy] = (40, 60, 200)
                else:
                    t = min(err / 4.0, 1.0)
                    px[cx, cy] = (int(255 * t), int(220 * (1 - t)), 0)
        image.resize((w * 4, h * 4), Image.NEAREST).save(map_path)


args = sys.argv[1:]
map_path = None
if "--truth" in args:
    i = args.index("--truth")
    TRUTH = tuple(float(v) for v in args[i + 1].split(","))
    del args[i:i + 2]
if "--map" in args:
    i = args.index("--map")
    map_path = args[i + 1]
    del args[i:i + 2]
for n, p in enumerate(args):
    analyse(p, map_path if n == 0 else None)
