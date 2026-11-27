"""Renders short clips whose motion is known exactly, to check the estimated vectors against
and to put through the upscalers: `Rayxel --clip-file=X.y4m --shot=OUT.png --clip-vectors ...`.

Everything is drawn four times larger than it is written and averaged down, so a thing can
move by a quarter of a pixel a frame and still be exactly where the truth says it is. The
scenes are layers, each moving its own way: a background (any picture), on it a flat grey
box that goes with it, two cut-outs of a second picture crossing it, and over everything a
panel and a line of text that stay put, as a game's interface and a film's subtitles do.

For each scene it writes
  NAME.y4m       the clip, 8-bit 4:2:0, BT.709 limited range
  NAME.npz       the truth for the first frames: `flow` (frames-1, H, W, 2) - where each pixel
                 was in the frame before, relative to where it is now, the way the estimate
                 says it; `label` (frames-1, H, W) - whose pixel it is (0 background, 1 the
                 flat box, 2 and 3 the cut-outs, 4 the panel, 5 the text over the picture,
                 6 a third cut-out with next to nothing drawn on it, 255 a pixel shared by
                 two of them); `hidden` - pixels that were not in the
                 frame before at all: behind something, or outside the picture
  NAME-ref.y4m   with --ref: the same clip at twice the size, which is what an upscaler
                 doubling it ought to arrive at; with --ref=4/3 a third as large again, for
                 an upscaler that has that little to do (the size is then to divide by 3)

Usage: make_clips.py OUTDIR --bg PICTURE --cut PICTURE [--cut-at X,Y;X,Y;X,Y] [--size 960x540]
                     [--scenes a,b] [--frames N] [--truth N] [--ref[=2|4/3|4]]
It needs Pillow, numpy, and ffmpeg on the path.
"""
import argparse
import math
import os
import subprocess

import numpy as np
from PIL import Image, ImageDraw, ImageFont

S = 4  # drawn this many times larger than written

# name: (background's move a frame, zoom a frame, turn a frame in degrees, layers over it, frames by default)
SCENES = {
    "pan-slow": dict(bg=(0.75, 0.25), frames=8),
    "pan-mid": dict(bg=(-5.25, 2.75), frames=8),
    "pan-fast": dict(bg=(21.5, -9.25), frames=8),
    "layers": dict(bg=(-5.25, 2.75), over=True, cuts=((7.5, -3.25), (-17.5, 6.25), (4.25, 5.5)), frames=60),
    "still-bg": dict(bg=(0.0, 0.0), over=True, cuts=((6.75, 2.5), (-13.25, -4.5), (-3.5, 4.75)), frames=60),
    "zoom": dict(bg=(1.5, 0.5), zoom=1.012, turn=0.25, over=True, cuts=(), frames=60),
}


def font(size):
    for name in ("segoeuib.ttf", "arialbd.ttf", "DejaVuSans-Bold.ttf"):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            pass
    return ImageFont.load_default()


def consensus(labels_big, w, h):
    """One label for each written pixel: its sixteen parts' label when they agree, else 255."""
    blocks = labels_big.reshape(h, S, w, S)
    low, high = blocks.min(axis=(1, 3)), blocks.max(axis=(1, 3))
    return np.where(low == high, low, 255).astype(np.uint8)


def writer(path, w, h, fps=30):
    return subprocess.Popen(
        ["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24",
         "-s", f"{w}x{h}", "-r", str(fps), "-i", "-",
         "-vf", "scale=out_color_matrix=bt709:out_range=tv:flags=accurate_rnd+full_chroma_int",
         "-pix_fmt", "yuv420p", "-f", "yuv4mpegpipe", path],
        stdin=subprocess.PIPE)


def render(name, spec, out_dir, bg_big, cut_src, cut_at, w, h, frames, truth, ref):
    W, H = w * S, h * S
    over = spec.get("over", False)
    zoom, turn = spec.get("zoom", 1.0), math.radians(spec.get("turn", 0.0))
    affine = zoom != 1.0 or turn != 0.0
    vx, vy = spec["bg"]

    # Where the window on the background starts, so that it stays inside it all the way.
    travel_x, travel_y = vx * S * (frames - 1), vy * S * (frames - 1)
    start_x = (bg_big.width - W) // 2 + int(travel_x / 2)
    start_y = (bg_big.height - H) // 2 + int(travel_y / 2)
    start_x -= start_x % S
    start_y -= start_y % S

    # The flat box, painted onto the background: it goes wherever that goes.
    box = None
    if over:
        bg_big = bg_big.copy()
        bx, by, bw, bh = start_x + int(W * 0.08), start_y + int(H * 0.52), 150 * S, 110 * S
        ImageDraw.Draw(bg_big).rectangle([bx, by, bx + bw - 1, by + bh - 1], fill=(128, 128, 128))
        box = (bx, by, bw, bh)

    # The cut-outs, each with a hard outline: a large slow one and a small fast one with
    # plenty drawn on them, and a third with next to nothing, which can only be told by its rim.
    cuts = []
    for i, velocity in enumerate(spec.get("cuts", ())):
        cw, ch = ((150, 190), (44, 44), (96, 96))[i]
        sx, sy = cut_at[i]
        tile = cut_src.crop((sx, sy, sx + cw, sy + ch)).resize((cw * S, ch * S), Image.LANCZOS)
        mask = Image.new("L", (cw * S, ch * S), 0)
        ImageDraw.Draw(mask).ellipse([0, 0, cw * S - 1, ch * S - 1], fill=255)
        start = ((0.25, 0.30), (0.70, 0.55), (0.50, 0.12))[i]
        pos = [int(start[0] * W) - int(start[0] * W) % S, int(start[1] * H) - int(start[1] * H) % S]
        cuts.append(dict(tile=tile, mask=mask, pos=pos, v=[velocity[0] * S, velocity[1] * S], w=cw * S, h=ch * S,
                         label=(2, 3, 6)[i]))

    # What stays put: a panel with writing on it, a mark, and a line of text straight over the picture.
    hud = hud_label = None
    if over:
        hud = Image.new("RGBA", (W, H), (0, 0, 0, 0))
        hud_label = Image.new("L", (W, H), 0)
        draw, mark = ImageDraw.Draw(hud), ImageDraw.Draw(hud_label)
        px, py, pw, ph = int(W * 0.70), int(H * 0.06), int(W * 0.26), int(H * 0.20)
        draw.rounded_rectangle([px, py, px + pw, py + ph], radius=6 * S, fill=(18, 22, 30, 255), outline=(120, 170, 255, 255), width=S)
        mark.rounded_rectangle([px, py, px + pw, py + ph], radius=6 * S, fill=4)
        f1, f2 = font(13 * S), font(9 * S)
        draw.text((px + 8 * S, py + 6 * S), "QUEST LOG", font=f1, fill=(240, 240, 240, 255))
        for row, line in enumerate(("Reach the eastern gate", "Enemies defeated 12/40", "Time 03:27.416")):
            draw.text((px + 8 * S, py + (28 + row * 15) * S), line, font=f2, fill=(170, 200, 230, 255))
        draw.rectangle([px + 8 * S, py + ph - 14 * S, px + pw - 8 * S, py + ph - 9 * S], fill=(40, 46, 60, 255))
        draw.rectangle([px + 8 * S, py + ph - 14 * S, px + int(pw * 0.55), py + ph - 9 * S], fill=(90, 220, 120, 255))

        text = Image.new("L", (W, H), 0)
        ImageDraw.Draw(text).text((int(W * 0.5), int(H * 0.90)), "The picture moves; these words do not.",
                                  font=font(17 * S), fill=255, anchor="mm", stroke_width=0)
        hud.paste(Image.new("RGBA", (W, H), (255, 255, 255, 255)), (0, 0), text)
        # The soft rim of a letter is neither the text nor what is behind it.
        hud_label.paste(200, (0, 0), text.point(lambda a: 255 if a > 0 else 0))
        hud_label.paste(5, (0, 0), text.point(lambda a: 255 if a >= 128 else 0))

    clip = writer(os.path.join(out_dir, name + ".y4m"), w, h)
    # ref is what the big picture is reduced by for the reference: S / the enlargement.
    clip_ref = writer(os.path.join(out_dir, name + "-ref.y4m"), w * S // ref, h * S // ref) if ref else None

    # Pixel centres of the written picture, in the big picture's coordinates.
    grid_x, grid_y = np.meshgrid((np.arange(w) + 0.5) * S, (np.arange(h) + 0.5) * S)
    centre = np.array([W / 2.0, H / 2.0])
    src_centre = np.array([start_x + W / 2.0, start_y + H / 2.0])

    def background_map(t):
        """The window's coordinates to the background's, at frame t: scale, turn, then shift."""
        s, a = zoom ** t, turn * t
        m = np.array([[math.cos(a), -math.sin(a)], [math.sin(a), math.cos(a)]]) / s
        shift = src_centre - np.array([vx, vy]) * S * t - m @ centre
        return m, shift

    flows, labels, hiddens = [], [], []
    previous_label = None
    previous_map = None
    for t in range(frames):
        m, shift = background_map(t)
        if affine:
            canvas = bg_big.transform((W, H), Image.AFFINE,
                                      (m[0, 0], m[0, 1], shift[0], m[1, 0], m[1, 1], shift[1]), resample=Image.BICUBIC)
        else:
            x0, y0 = int(round(shift[0])), int(round(shift[1]))
            canvas = bg_big.crop((x0, y0, x0 + W, y0 + H))
        label_big = Image.new("L", (W, H), 0)

        if box is not None and not affine:
            x0, y0 = int(round(shift[0])), int(round(shift[1]))
            label_big.paste(1, (box[0] - x0, box[1] - y0, box[0] - x0 + box[2], box[1] - y0 + box[3]))
        elif box is not None:
            # Turned and scaled with the background: by its corners.
            inverse = np.linalg.inv(m)
            corners = [(box[0], box[1]), (box[0] + box[2], box[1]), (box[0] + box[2], box[1] + box[3]), (box[0], box[1] + box[3])]
            ImageDraw.Draw(label_big).polygon([tuple(inverse @ (np.array(c, float) - shift)) for c in corners], fill=1)

        moved = []
        for i, c in enumerate(cuts):
            if t > 0:
                # Off the edge it turns back, so that a long clip keeps it in the picture.
                for axis, limit in ((0, W - c["w"]), (1, H - c["h"])):
                    nxt = c["pos"][axis] + c["v"][axis]
                    if nxt < 0 or nxt > limit:
                        c["v"][axis] = -c["v"][axis]
                    c["pos"][axis] += c["v"][axis]
            at = (int(c["pos"][0]), int(c["pos"][1]))
            canvas.paste(c["tile"], at, c["mask"])
            label_big.paste(c["label"], at, c["mask"])
            moved.append((c["label"], c["v"][0] / S, c["v"][1] / S))

        if hud is not None:
            canvas.paste(hud, (0, 0), hud)
            label_big.paste(hud_label, (0, 0), hud_label.point(lambda a: 255 if a else 0))

        clip.stdin.write(canvas.reduce(S).convert("RGB").tobytes())
        if clip_ref is not None:
            clip_ref.stdin.write(canvas.reduce(ref).convert("RGB").tobytes())

        label = consensus(np.asarray(label_big), w, h)
        if 0 < t <= truth:
            # Where each pixel of the background was a frame ago: through the background and back.
            pm, pshift = previous_map
            inverse_prev = np.linalg.inv(pm)
            ux = m[0, 0] * grid_x + m[0, 1] * grid_y + shift[0] - pshift[0]
            uy = m[1, 0] * grid_x + m[1, 1] * grid_y + shift[1] - pshift[1]
            flow = np.empty((h, w, 2), np.float32)
            flow[..., 0] = (inverse_prev[0, 0] * ux + inverse_prev[0, 1] * uy - grid_x) / S
            flow[..., 1] = (inverse_prev[1, 0] * ux + inverse_prev[1, 1] * uy - grid_y) / S
            for whose, mx, my in moved:
                flow[label == whose] = (-mx, -my)
            flow[(label == 4) | (label == 5)] = (0.0, 0.0)
            flow[label == 255] = (0.0, 0.0)

            # Hidden: where it was is outside the picture, or was someone else's pixel then.
            qx = np.rint(grid_x / S - 0.5 + flow[..., 0]).astype(int)
            qy = np.rint(grid_y / S - 0.5 + flow[..., 1]).astype(int)
            outside = (qx < 0) | (qx >= w) | (qy < 0) | (qy >= h)
            was = previous_label[np.clip(qy, 0, h - 1), np.clip(qx, 0, w - 1)]
            hidden = outside | ((was != label) & (was != 255))
            flows.append(flow)
            labels.append(label)
            hiddens.append(hidden)
        previous_label, previous_map = label, (m, shift)

    clip.stdin.close()
    clip.wait()
    if clip_ref is not None:
        clip_ref.stdin.close()
        clip_ref.wait()
    np.savez_compressed(os.path.join(out_dir, name + ".npz"),
                        flow=np.stack(flows), label=np.stack(labels), hidden=np.stack(hiddens))
    print(f"{name}: {frames} frames of {w}x{h}, truth for {len(flows)}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("out")
    ap.add_argument("--bg", required=True)
    ap.add_argument("--cut", required=True)
    ap.add_argument("--cut-at", default="", help="where in it each cut-out is taken from: X,Y;X,Y;X,Y")
    ap.add_argument("--size", default="960x540")
    ap.add_argument("--scenes", default=",".join(SCENES))
    ap.add_argument("--frames", type=int, default=0)
    ap.add_argument("--truth", type=int, default=8)
    ap.add_argument("--ref", nargs="?", const="2", default="", choices=["", "2", "4/3", "4"])
    args = ap.parse_args()

    w, h = (int(v) for v in args.size.lower().split("x"))
    reduce_by = {"": 0, "2": S // 2, "4/3": 3, "4": 1}[args.ref]
    if reduce_by and (w * S % reduce_by or h * S % reduce_by):
        ap.error(f"--ref={args.ref} needs a size that divides by {reduce_by}")
    os.makedirs(args.out, exist_ok=True)
    bg = Image.open(args.bg).convert("RGB")
    # Large enough for the window and its travel: at least twice the window each way.
    scale = max(S, math.ceil(2 * w * S / bg.width), math.ceil(2 * h * S / bg.height))
    bg_big = bg.resize((bg.width * scale, bg.height * scale), Image.LANCZOS)
    cut = Image.open(args.cut).convert("RGB")
    if args.cut_at:
        cut_at = [tuple(int(v) for v in at.split(",")) for at in args.cut_at.split(";")]
    else:
        cut_at = [(int(cut.width * fx), int(cut.height * fy)) for fx, fy in ((0.62, 0.28), (0.78, 0.31), (0.20, 0.35))]
    for name in args.scenes.split(","):
        spec = SCENES[name]
        render(name, spec, args.out, bg_big, cut, cut_at, w, h, args.frames or spec["frames"], args.truth, reduce_by)


if __name__ == "__main__":
    main()
