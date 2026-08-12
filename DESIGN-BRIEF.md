# Rayxel — design brief

A brief for designing the UI. The engine exists and works; what is missing is a real
interface. Everything below describes shipped, measured behaviour unless marked otherwise.

---

## What the app is

A Windows app that takes live video from a **USB capture card** and puts it on screen with
the lowest possible input lag, optionally running it through NVIDIA's RTX video filters.

The concrete setup it was built for: a **Steam Deck / Nintendo Switch 2 plugged into a Genki
ShadowCast 2 Pro**, played on a PC monitor. The console's picture arrives over USB, the app
displays it. Because someone is *playing a game* through this window, every millisecond of
delay is felt in the hands.

Nothing in the app is specific to that card. It works with any capture device Windows
exposes, measures each one's capabilities rather than assuming them, and handles several
being present at once — which is the normal case, since a laptop webcam or an OBS virtual
camera sits in the same device list as the real card.

That single fact drives the whole design. This is not a media player. It is a **latency
instrument that happens to show video**.

### Who uses it

One person, at their desk, playing a console game on their PC monitor. They are technical
enough to care that "1080p120" can secretly mean "JPEG-compressed", but they are *playing*,
not administering. The interface is in their peripheral vision most of the time.

---

## The core tension

Two things fight each other, and the UI has to make the fight visible:

1. **Quality** — upscaling 1080p to the monitor's resolution, adding HDR.
2. **Latency** — every filter costs GPU milliseconds, and every millisecond is input lag.

The app measures both, live. The user's job is to decide where on that line they want to sit.
The UI's job is to make that decision obvious and reversible in one keypress.

---

## Feature inventory

### 0. Device selection

Several capture devices are usually present. The app enumerates them all, identifies each by
its Windows interface path (so two identical cards are distinguishable, and so nothing breaks
when the list reorders), and reads the USB vendor/product id out of that path. Software
cameras — OBS Virtual Camera, phone-as-webcam bridges — are recognised and treated
differently, because their advertised modes describe no wire.

Devices are opened only when needed, never just to populate a list. A device the app has not
opened is shown as *not checked*, not guessed at.

### 1. Capture mode selection

A card exposes many modes — the ShadowCast 2 Pro offers **241**. They are not equal, and the
difference is invisible in every other capture app. On that card:

- **153 raw** — uncompressed pixels over USB
- **44 MJPEG** — compressed, the app decodes them
- **44 "MJPEG-backed"** — these *say* NV12 (uncompressed) but are secretly JPEG on the wire

That third category is the trap. Windows reports `NV12 1920x1080 @120` twice: once genuine,
once as decoded JPEG. Same label, different latency. **The app detects and labels which is
which** — this is arguably its most valuable feature and the UI must not bury it. The codec
is not always JPEG; H.264 and HEVC cards play the same trick, and the label follows the
actual codec.

Detection uses two independent tests, which is what makes it trustworthy on hardware nobody
has tried yet: the capture stack publishes a decoded type directly before the compressed one
it came from, *and* a mode claiming to be uncompressed cannot exceed the link budget the
device's own other modes reveal.

That budget is **measured per device, not assumed**. A single fastest mode proves nothing;
two *different* resolutions landing on the same byte rate is a wire running out. The
ShadowCast reports 356 MiB/s with four geometries agreeing — USB 3.2 Gen 1. Anything it
advertises above that can only be reached by compressing, which is why `4K60` and `1440p120`
exist only as MJPEG, and why `1080p120` raw is its sweet spot.

Modes worth surfacing prominently:

| Mode | Wire | Note |
|---|---|---|
| 1920x1080 @ 120 NV12 | raw | the default; best balance |
| 1920x1080 @ 60 P010 | raw | 10-bit, the only HDR-capable capture mode |
| 3840x2160 @ 30 NV12 | raw | max resolution without compression |
| 2560x1440 @ 60 NV12 | raw | |
| anything higher | MJPEG | costs latency and quality |

Only **NV12 and P010** can be displayed; other formats are filtered out of the menu.

### 2. RTX Video Super Resolution (toggle)

AI upscaling via the GPU driver. Only does anything when the window is **larger** than the
source — at a downscale it is inert, and the UI currently warns about this in words.

Measured cost: **4.50 ms** of GPU time per frame.

### 3. RTX Video HDR (toggle)

AI SDR-to-HDR conversion. Measured cost: **+0.66 ms** on top of VSR.

Availability is conditional: it requires Windows to have the display in HDR mode *right now*.
When it does not, the control is disabled and says why. When it does, the app **starts with
HDR on**, matching the desktop.

### 4. Live telemetry

The reason the app exists. Currently a plain text block in the corner. Real values:

```
FRAME BUDGET  8.33 ms  @ 120 fps
  age when delivered    7.7 ms  = 0.92 frames
  our CPU  arrive->present   0.14 ms
  our GPU  render pass       5.16 ms
  headroom left              3.03 ms

rate 120.2 fps   jitter 0.15 ms
frames 5880   late 135   dropped 0   present: Composed
```

What each number means, because the distinction matters for how they should be shown:

- **Frame budget** — 8.33 ms at 120 fps. The hard ceiling. Everything else is spent against it.
- **Age when delivered** — how old a frame is when the app first sees it. Includes the capture
  card and USB. Roughly one frame period, so it is shown in *frames* as well as ms; comparing
  the raw ms between different frame rates is meaningless and users get this wrong.
- **Our GPU** — where VSR and HDR show up. This is the cost of quality.
- **Headroom** — what is left. Goes negative if filters are too expensive.
- **Jitter** — steadiness, *not* delay. A pipeline can be very late and perfectly steady.
  This has been misread as latency more than once; the label needs to fight that.
- **dropped / late** — frames missed entirely vs frames that arrived behind schedule.
- **present: Overlay | Composed** — whether the window reached a hardware overlay plane
  (fast) or is being composited by the Windows desktop manager (**costs a whole extra frame,
  8.3 ms — more than VSR and HDR combined**). Users need to know when they have accidentally
  lost this, usually by putting something on top of the window.

### 5. Logging

Everything goes to console and a rolling file. On every mode switch it records what was
*requested* against what Windows actually *accepted*, because those silently differ.

---

## What the interface has to do

Ranked by how often it matters:

1. **Stay out of the way.** The video is the point. Most of the time the user is playing.
2. **Show the cost of what they turned on**, continuously and honestly.
3. **Warn when something is quietly wrong** — `Composed` instead of `Overlay`, a compressed
   mode selected by accident, dropped frames climbing, negative headroom.
4. **Switch modes and toggles fast**, without stopping the video for longer than necessary.

### States worth designing for

- **Nominal** — raw mode, filters on, zero drops, headroom positive. Should feel calm.
- **Degraded** — `Composed`, or drops climbing, or headroom near zero. Needs to be noticed
  without being alarming; this is a common and recoverable state.
- **Compromised** — a compressed mode is active. Not an error, a deliberate trade-off, but
  the user should never be in it without knowing.
- **Unavailable** — HDR toggle when Windows is in SDR; VSR when the window is smaller than
  the source. Disabled controls must explain themselves, not just grey out.
- **No signal** — console asleep, unplugged, or changing resolution. Detected (frames stop
  arriving while the device stays healthy) and reported; it usually recovers by itself, so
  the design should be reassuring rather than alarming. Needs visuals.
- **Device busy** — a browser tab or OBS is streaming from the card. Detected and reported
  in about 4 ms, and the app stays open so another device can be picked. Needs visuals.

  The mechanism is worth knowing, because it is not what it looks like: Windows lets a
  second application **open** the device and **accept a media type** without complaint. Only
  the first *read* fails, with `MF_E_HW_MFT_FAILED_START_STREAMING`. So "is this card free?"
  cannot be answered by inspection — the app finds out by trying, and when it has several
  cards it moves on to the next one automatically.

### Practical constraints

- The video window must not be composited over by anything, or it loses its overlay plane
  and gains a frame of latency. **Any UI drawn on top of the video must be part of our own
  render, not a separate window or layer over it.**
- Settings UI that lives in a *separate window* is free — it does not touch the video path.
- The overlay is drawn every frame at 120–200 fps. It must be cheap.
- The window is resizable; it is often maximised on an ultrawide (3440x1440), where a 16:9
  source is pillarboxed and there are large black bars either side. **That dead space is
  usable real estate.**
- Colour: when the display is in HDR the whole surface is PQ/BT.2020. Anything drawn is
  re-encoded to roughly 200 nits so it does not sear on an OLED. Very bright large UI areas
  are a genuinely bad idea here.

---

## UI technology options

The engine is **C# / .NET 10** with **Vortice.Windows** for Direct3D 11. The overlay today is
GDI+ text rendered to a bitmap and uploaded as a texture — functional, ugly, and a dead end.

### For the in-video overlay (must render inside our swapchain)

| Option | Verdict |
|---|---|
| **ImGui.NET** | **Recommended.** Immediate-mode, draws with our own D3D11 device, no extra composition, fully skinnable, built for exactly this (tool overlays with live numbers). Needs a ~250-line D3D11 backend written once. |
| Direct2D + DirectWrite | Native, sharp text, good gradients. More code than ImGui for the same result, and no widget model — every control is hand-built. |
| Keep GDI+ bitmaps | Only viable for static text. No interaction, no animation. |

### For settings / mode selection (can live in a separate window, no latency cost)

| Option | Verdict |
|---|---|
| **WinUI 3** | **Recommended if the design wants to look modern and native.** Real Windows 11 design language, proper controls, theming, animation. Ships as a normal .NET window alongside the render window. |
| **Avalonia** | XAML, very flexible styling, easier to make look non-standard. Good if the design is opinionated rather than native-looking. |
| WPF | Mature and easy, but dated visuals and awkward HiDPI. |
| WebView2 (HTML/CSS) | Maximum design freedom, web tooling, easy to iterate. Heavier and adds a browser process. Reasonable if the design is ambitious. |

### Recommended split

**Separate control window in WinUI 3 or Avalonia** for mode selection, profiles and settings —
where a designer has full freedom and zero latency consequences. **ImGui.NET overlay** on the
video itself for the always-visible telemetry and the two toggles, where the constraint is
that it must be cheap and cannot be a separate window.

If the design calls for a single unified surface, everything can go into the ImGui overlay —
it just means the visual language is limited to what an immediate-mode renderer does well.

---

## Anti-goals

- Not a streaming or recording tool. No OBS features.
- Not a media player. No playlists, no seeking, no files.
- Not configurable in the "hundreds of settings" sense. A handful of decisions, made well.
- No telemetry that cannot be measured honestly. Numbers the app cannot actually determine
  are labelled unavailable rather than estimated — this is a deliberate stance and the
  design should support showing "n/a" without it looking broken.

---

## Known gaps

- **End-to-end latency is not measurable in software.** The app sees from "frame arrives in
  our process" onward. The capture card's own delay, and the monitor's, are invisible. Any
  UI claiming a single total latency number would be lying.
- No named profiles. Settings *are* remembered now — `%LOCALAPPDATA%\Rayxel\settings.json` —
  but there is one set of them, not a switchable list per console or per game.
- Audio works: the card's HDMI input is captured and played to a selectable output. What it
  cannot do is tell two identical cards apart automatically, because a WASAPI endpoint does
  not expose the PnP instance id of the hardware under it — only the adapter's name. The
  input selector exists for that case.
- "No signal" and "device busy" are detected and logged, but have no visual design yet.
- Hot-plug is only noticed when the menu is opened, which re-enumerates. Nothing listens for
  device-arrival messages, so a card plugged in while the menu is closed goes unseen until
  the next time it opens.
