# Where the latency actually goes

Measured with `--bench=<seconds>`, which records every frame for a fixed window and reports
percentiles rather than a moving average. Two configurations can share a mean and differ
entirely in the tail, and the tail is what is felt.

Pin the run so it is a comparison and not an anecdote:

```
Rayxel.exe --bench=12 --mode=1920x1080@120 --borderless --scaler=vsr
```

`--mode` takes `WIDTHxHEIGHT[@FPS][:FORMAT]`, `--scaler` takes `none|nis|vsr`.

## The budget at 1920×1080 @ 120, borderless

All figures are p50 on an RTX card against a ShadowCast 2 Pro on SuperSpeed.

| Stage | ms | Can it move? |
|---|---|---|
| USB transfer | **7.32** | No. Physics — see below. |
| Windows capture stack | **0.15** | Closed. Measured as a bound, not an estimate. |
| our GPU — nothing | 0.19 | baseline |
| our GPU — NIS | 0.19 | free, inside the noise |
| our GPU — RTX Video HDR | 1.11 | +0.92 |
| our GPU — RTX VSR | 4.29 | **+4.10** |
| our CPU | 0.10 | negligible |
| present — `Overlay` | ~0 | borderless |
| present — `Composed` | **+8.33** | windowed; a whole frame to DWM |

End to end, same mode and machine:

```
NIS, borderless          ~7.7 ms
RTX VSR, borderless     ~11.8 ms
RTX VSR + HDR, windowed ~21   ms
```

The capture delay was identical in every run — 7.65 to 7.68 ms across no scaler, NIS and
VSR — which is the check that the two halves really are independent.

## The wire is most of it, and it is measurable

Pipeline delay is the sample's own capture timestamp against the clock when `ReadSample`
returned. It tracks **frame size**, not frame rate:

| Mode | Frame | Predicted at 405 MiB/s | Measured p50 |
|---|---|---|---|
| 1280×720 @ 60 NV12 | 1.318 MiB | 3.25 ms | 3.43 |
| 1920×1080 @ 60 NV12 | 2.966 MiB | 7.32 ms | 7.48 |
| 1920×1080 @ 120 NV12 | 2.966 MiB | 7.32 ms | 7.68 |
| 2560×1440 @ 60 NV12 | 5.273 MiB | 13.01 ms | 13.21 |
| 1920×1080 @ 60 **P010** | 5.933 MiB | 14.64 ms | 15.42 |
| 3840×2160 @ 30 NV12 † | 11.865 MiB | 29.27 ms | 29.44 |

† The source here is 1440p, so the card cannot produce 4K from it and sent its own "source
content does not support this resolution" card instead. The transfer figure still stands —
an uncompressed frame is the same size whatever is in it — but the card was generating a
static image rather than processing live HDMI, so its **distribution** is not comparable
with the rest and is not used for anything below.

A line through the first and last gives **~400 MiB/s and an intercept near zero**, against
405 MiB/s computed from the USB descriptors. Two entirely separate routes to the same
number: the descriptors say what the link should carry, the clock says what it does.

The intercept is everything that is not the wire — driver, frame server, the hop into our
process, our own read. **0.15 ms.** That bounds what any change to the capture stack could
win, which is why WinUSB and the frame-server registry switch are both closed: the other
7.3 ms is bytes crossing a wire and no software moves them faster.

## 10-bit costs eight milliseconds, and the tail costs more

Same geometry, same rate, one format apart:

| | p50 | p95 | p99 | max |
|---|---|---|---|---|
| 1920×1080 @60 NV12 | 7.48 | 7.69 | 11.50 | 13.37 |
| 1920×1080 @60 P010 | **15.42** | 16.42 | **72.79** | **114.68** |

P010 is 3 bytes per pixel against NV12's 1.5, so it is exactly twice the frame and takes
exactly twice the wire. That accounts for the median. It does not account for the tail:
one frame in a hundred arrives five times late, and the worst is 115 ms.

Two obvious explanations were tested and both are wrong:

- **Not the frame size.** 2560×1440 NV12 is 5.27 MiB, within 12% of P010's 5.93, and its
  p99 is 21 ms against P010's 73.
- **Not the link being saturated.** 1080p120 NV12, 1080p60 P010 and 4K30 NV12 all sit at
  the same 88% of the link. Only the P010 one misbehaves.

What is left is the 10-bit path itself. The cleanest comparison is the first one: same
geometry, same rate, same downscale from the 1440p source, only the bit depth differs —
p99 goes from 11.5 ms to 72.8. Why the card or `usbvideo.sys` handles P010 so much worse is
not established here; that it does is.

So HDR capture is not a free quality setting. It doubles the median delay and turns a clean
distribution into a hitching one. Worth it for film, wrong for a game.

## What is worth pulling, largest first

1. **Windowed → borderless: 8.33 ms.** Already done, and still the biggest single item.
2. **10-bit → 8-bit: 8.0 ms**, and p99 from 73 ms back to 11.5.
3. **RTX VSR → NIS: 4.10 ms.** Not an engineering task — a quality choice, one keypress.
4. **RTX Video HDR: 0.92 ms.**
5. **A lower capture resolution** buys latency in proportion to bytes: 720p is 3.9 ms
   cheaper than 1080p on the wire.
6. The Windows capture stack: 0.15 ms. Nothing there.

Everything left is either physics or a preference. There is no engineering left to do on
this path — which is itself the result.

## What the app does with this

The measurements are in the interface rather than only in this file:

- The mode table has a **delay column**: one frame's bytes over the link speed, which the
  app now reads from the device's own USB descriptors rather than inferring. A mode with a
  codec in it shows `—`, because the bytes on the wire are whatever the encoder produced.
- **10-bit rows are amber**, with one line under the table saying why. It is the only
  choice there that quietly costs something.
- The wire tag is **`raw` or `zip`** — two words, because that is the whole of what someone
  choosing a mode needs. It used to read `mjpeg?`, which was precise and unreadable. The
  detail still exists as `WireDetail` and still goes in the log.
- The scaler rows carry **measured** costs, not vendor figures: NIS turned out to be free
  to inside the noise where the panel had been claiming 0.9 ms.
- The link figure under the table came from the design's copy deck with **`356 MiB/s`
  written into the string** in all five languages — this machine's number, baked in. It now
  comes from the device.

The one route that could beat the wire is not a faster stack but an earlier start:
uploading rows to the GPU as they arrive rather than waiting for a whole frame. That
changes *when* displaying begins rather than how fast the bytes move, and it is a much
larger change than it sounds.
