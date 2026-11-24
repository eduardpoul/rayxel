<p align="center">
  <img src="src/Rayxel/Assets/Brand/svg/rayxel-mark.svg" alt="" width="96">
</p>

<h1 align="center">Rayxel</h1>

<p align="center">
  A low-latency Windows viewer for USB capture cards, for playing a console on a PC monitor.<br>
  DLSS, FSR and RTX Video HDR — with the measured cost of each.
</p>

<p align="center">
  <a href="../../releases/latest">Download</a> ·
  <a href="#getting-started">Getting started</a> ·
  <a href="#controls">Controls</a> ·
  <a href="#building">Building</a> ·
  <a href="FINDINGS-LATENCY.md">Latency findings</a>
</p>

<p align="center">
  <img src="docs/screenshots/overview.jpg" alt="Rayxel in borderless full screen on a 3440×1440 monitor, showing a Nintendo Switch 2 home screen with the one-line telemetry at the top left and the control bar at the bottom">
</p>

<table>
  <tr>
    <td width="50%"><img src="docs/screenshots/capture-modes.jpg" alt="The capture-mode list: uncompressed modes first, each with its rate, format, time on the USB wire and a raw or zip tag"></td>
    <td width="50%"><img src="docs/screenshots/scaling.jpg" alt="The scaling list as it was when it still offered NIS and RTX Video Super Resolution: each technique with its cost per frame"></td>
  </tr>
  <tr>
    <td><sub>Capture modes, uncompressed first, with the time each takes on the USB wire.</sub></td>
    <td><sub>Scaling, with what each option costs per frame.</sub></td>
  </tr>
</table>

---

Rayxel shows the picture from a USB capture card — a Nintendo Switch 2 or a Steam Deck on a
Genki ShadowCast 2 Pro is what it was built for, but nothing in it is specific to that card.
Someone is *playing* through this window, so it treats input lag as the thing being
measured: every option says what it costs in milliseconds of the frame budget, and every
number it shows is one it can actually measure.

## Features

- **Low-latency presentation.** A Direct3D 11 flip-model swapchain that reaches a hardware
  overlay plane in borderless full screen, and one present per captured frame, so a VRR
  display refreshes exactly when a picture arrives.
- **Upscaling and HDR.** DLSS Super Resolution or AMD's FSR, each listed with its measured
  per-frame cost, and RTX Video HDR (SDR to HDR). Output follows the Windows HDR state.
- **Capture modes that tell the truth.** Windows reports some modes as uncompressed `NV12`
  when the card is actually sending JPEG. Rayxel detects them with two independent tests,
  hides the ones that only duplicate a real raw mode, and marks every mode with a codec on
  the wire as `zip`. It measures the card's USB link instead of assuming it, and shows how
  long each mode takes to cross it.
- **Live telemetry.** One line to play with: the rate the picture arrives at, its delay as
  a single figure, and what Rayxel's own work takes out of the frame. Or a full card that
  shows what those are made of — the delay stage by stage (encoder, network and decoder for
  a source on the network), GPU and CPU time and headroom, a frame-time graph with jitter,
  late and lost frames, and whether the window is on an overlay plane or being composed by
  Windows. Trouble joins the line only while it is trouble.
- **Out of the way.** A Windows 11–style control bar at the bottom that appears when the
  mouse moves and hides itself, with the pointer, five seconds after it stops. A gamepad
  never brings it up.
- **HDMI audio passthrough** from the card to any output, with drift correction.
- **A Steam Deck or a PC over the network, without a card.** The sender (`rayxel-send`)
  captures and encodes there - HEVC by default, AV1 or PyroWave by choice - and plays your
  gamepad, keyboard and mouse back. Paired once with a PIN, every session proven and sealed;
  run from a Decky plugin on the Deck or its own window on a desktop. See
  [docs/sender.md](docs/sender.md).
- **Five languages:** English, Russian, Ukrainian, German, Spanish.

## Requirements

- Windows 10 or 11, x64.
- A UVC capture card that offers `NV12` or `P010` (tested with the Genki ShadowCast 2 Pro).
- For DLSS and RTX Video HDR: an NVIDIA RTX GPU and a recent driver. FSR runs on any card
  that does Direct3D 12, given AMD's library (see below). HDR output needs HDR turned on in
  Windows.
- For VRR: a G-SYNC or FreeSync monitor, with VRR enabled for windowed and full-screen
  applications in the NVIDIA control panel.

## Getting started

Download `Rayxel.exe` from the [latest release](../../releases/latest) and run it. It is a
single self-contained executable: nothing to install, no .NET runtime needed. The first
launch takes about a third of a second longer while it unpacks.

Rayxel picks the best capture device it can find, opens the fastest uncompressed mode, and
remembers what you chose for next time.

**Press F11.** In a normal window the Windows compositor adds a whole frame — 8.3 ms at
120 fps, more than any upscaler here costs. Borderless full screen reaches a
hardware overlay plane instead. The telemetry card says which one you have: `Overlay` is
good, `Composed` costs a frame — and while it is `Composed`, the one-line readout and a red
badge on the control bar say so too.

## Controls

Move the mouse over the picture to bring up the control bar:

- **Capture mode** — resolution and rate, with a dot in the wire's colour: green when the
  picture arrives uncompressed, amber when a codec carries it. Opens the list of modes,
  where every mode says `raw` or `zip`.
- **Scaling** — rows of settings, as in a game's graphics menu. The technique comes from a
  dropdown that gives each one's cost per frame: off, DLSS, FSR. Point at a row, or walk
  the list with the arrow keys, and what that row is appears beside it; one that cannot be
  used stays in the list, greyed, and says why there. Under it is whatever belongs to the
  one chosen: DLSS's model, or FSR's variant - 4.1.2, 4.1.2 Lite, 3.1. There is no
  "automatic" among them: what runs until another is picked is the newest the card runs.
  The box shows the one that is running. When the driver's own DLSS override names the
  model, nothing chosen in Rayxel would be obeyed, so the box is shut and the reason is
  written under it together with where in the NVIDIA app to switch that off. What concerns
  a row as a whole is behind the **ⓘ** beside its label.
- **Frame generation** — the same rows for the same kind of choice: the generator, and how
  many frames it shows for each one that arrives. **Nothing is behind it yet**: it needs
  Direct3D 12, so the generators are greyed and it can only be off. The info marks say
  why, and what a multiplier would come to on this display.
- **HDR** — for an SDR source, RTX Video HDR on or off; for an HDR source, shown as it is
  or converted to SDR. It explains itself when it is unavailable.
- **Audio, telemetry, full screen**, and **…** for device, audio routing, HDR brightness,
  window mode and language.

| Key | |
|---|---|
| `Esc` / `Tab` | show or hide the bar; `Esc` closes whatever is open first |
| `T` | telemetry: off → one line → full card |
| `M` | capture-mode list |
| `1` / `2` / `3` | no upscaling / DLSS / FSR; one that cannot be used opens the list, which says why |
| `G` | frame generation |
| `↑` / `↓`, `Enter` | in an open list: walk its rows, take the one you are on; `Esc` closes the list |
| `H` | RTX Video HDR |
| `F11` | window → borderless full screen → topmost full screen |
| `F1`, right click | the full mode menu |

Closing the window asks first, so a stray key never ends a game.

## Where it keeps things

```
%LOCALAPPDATA%\Rayxel\settings.json     what you last chose
%LOCALAPPDATA%\Rayxel\logs\             one rolling log per run
```

## Command line

Everything is optional; the app needs no arguments.

| Flag | |
|---|---|
| `--list` | probe every capture device, print what it can do, and exit |
| `--device=<name\|index\|path>` | use a specific device instead of choosing |
| `--lang=en\|ru\|uk\|de\|es` | start in a language |
| `--borderless` | start in borderless full screen |
| `--tele` | start with the telemetry up |
| `--mode=<w>x<h>@<fps>` | start in a specific mode, e.g. `--mode=1920x1080@120` |
| `--scaler=none\|dlss\|fsr` | start with a specific upscaler |
| `--dlss-model=k\|l\|m` | the network DLSS is asked for |
| `--fsr-model=full\|lite\|rdna2` | the network of FSR 4 to run, whatever card it is |
| `--fsr-version=<n>` | a version AMD's library offers by itself, e.g. `3.1` |
| `--fsr-dir=<folder>` | where to look for AMD's library first |
| `--net=<host>[:port]` | a Steam Deck running `rayxel-send --listen`, as a device |
| `--net-codec=hevc\|av1\|pyrowave` | the codec the Deck is asked for (default HEVC; a Deck sends HEVC for one it cannot encode, and has no AV1 encoder) |
| `--net-bitrate=auto\|<Mbit/s>` | what the Deck is asked to spend; for PyroWave, its own fixed figure (default 140) |
| `--no-auto-hdr` | do not follow the Windows HDR state at start-up |
| `--pacing=arrival\|refresh` | present once per captured frame (default), or on every swapchain slot |
| `--bench[=<seconds>]` | measure for a fixed time (default 20 s), log percentiles, and exit |
| `--console` | attach to the terminal it was started from and print the log there |
| `--verbose` | debug-level logging |
| `--shot=<file.png>` | render for a few seconds, save the frame as a PNG, and exit |
| `--screen=<name>` | force a state for inspection: `splash`, `devices`, `app`, `nosignal`, `busy`, `composed`, `modes`, `scaler`, `scaler-list`, `scaler-variants`, `scaler-info`, `scaler-variant-info`, `framegen`, `framegen-list`, `framegen-info`, `more`, `quit` |

`--shot` saves the back buffer rather than the screen, so it works even when the window is
covered or on a display nobody is looking at.

## Building

Requires the .NET 10 SDK on Windows.

```
dotnet build Rayxel.slnx -c Release
```

To produce the single-file executable:

```
dotnet publish src/Rayxel/Rayxel.csproj -c Release -r win-x64 --self-contained -o publish
```

For a Steam Deck's PyroWave stream the codec's library is needed beside the program. It is
built in Docker, and the build above picks it up and carries it inside when it is there:

```
cd src/pyrowave-windows
docker build --output type=local,dest=out .
```

which leaves `src/pyrowave-windows/out/libpyrowave-shared-1.dll`. The Deck's side,
`rayxel-send`, is built the same way from `src/rayxel-send` and ships its own copy.

The runtime identifier belongs on that command line and not in the project file: as a
project property it moves every ordinary build into a `bin\...\win-x64\` subfolder and
leaves the previous executable at the old path, where it goes on being launched.

The result is exactly one file, `Rayxel.exe`, about 40 MB. The native `cimgui.dll`, the
window icon and the debug symbols are all embedded, so stack traces
keep file and line numbers without a separate `.pdb`. Trimming is off on purpose: Vortice,
NAudio and `System.Text.Json` resolve types by reflection, and a trimmed build fails at
runtime rather than at build time.

## How it works

Media Foundation reads the card straight into Direct3D 11 textures. From there the frame
goes through DLSS, FSR, the driver's video processor (RTX Video HDR) or Rayxel's own
YUV-to-RGB shader, and is presented on a flip-model swapchain. DLSS and FSR need to be told
how the picture moved, which a video does not say, so Rayxel estimates it from one frame to
the next with a shader of its own. FSR exists only for Direct3D 12, so it runs on a second
device beside the first, on textures the two share. The
interface is Dear ImGui, drawn into the same swapchain rather than in a window of its own —
a second window over the video would take it off its overlay plane and cost a frame.

The measurements behind the design are written up in
[FINDINGS-LATENCY.md](FINDINGS-LATENCY.md) (where every millisecond goes, from the USB wire
to the present) and [FINDINGS-USB.md](FINDINGS-USB.md) (what the card's own descriptors say
about its modes). [DESIGN-BRIEF.md](DESIGN-BRIEF.md) describes what the interface has to do
and why.

## Known limitations

- **End-to-end latency cannot be measured in software.** The card's own delay before it
  timestamps a frame and the monitor's after a present are both invisible; anything Rayxel
  cannot measure is shown as `n/a` rather than estimated.
- **RTX Video HDR and an upscaler do not run together.** That filter lives on the driver's
  video path, which DLSS and FSR do not go through; switching one of them on switches it off.
- **An HDR source has only been tried as a test pattern.** Showing HDR10 as it is, and
  converting it to SDR, are measured against a generated HDR10 picture; no capture card
  passing real HDR was at hand. A card that passes HDR on without describing it has to be
  told so: **…** → source range.
- **DLSS works from estimated motion.** A game gives DLSS exact motion vectors, depth and a
  camera that shifts by a fraction of a pixel every frame; a video has none of these.
  Rayxel estimates the motion, supplies a flat depth, and cannot supply the shift, so DLSS
  here steadies and cleans the picture rather than recovering detail that was never sent,
  and it smears where the estimate is wrong.
- **DLSS needs an NVIDIA RTX card and a copy of `nvngx_dlss.dll`,** which is not part of
  Rayxel. The driver uses one placed beside `Rayxel.exe` (it comes with NVIDIA's DLSS SDK),
  or its own copy - which it only lends to a program it does not know while DLSS override
  is switched on for all programs in the NVIDIA app. The driver's settings may also replace
  the model and mode Rayxel asks for; the log says what was used.
- **FSR needs two DLLs of AMD's,** `amd_fidelityfx_loader_dx12.dll` and
  `amd_fidelityfx_upscaler_dx12.dll`, which are not part of Rayxel. They come with AMD's
  [FSR SDK](https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK) (`Kits/FidelityFX/signedbin`)
  and with many games; Rayxel looks for them beside `Rayxel.exe`, then in
  `%LOCALAPPDATA%\Rayxel\fsr`, or wherever `--fsr-dir=` says.
- **FSR 4 on a card AMD does not offer it for is not AMD's doing.** Left to itself, AMD's
  library offers FSR 4 - the one built on a neural network - on Radeon RX 6000, 7000 and
  9000 cards and some Ryzen processors, and FSR 3.1 on anything else. But the networks
  that work in whole numbers are ordinary compute shaders, and what keeps them from other
  cards is a check in the library, which Rayxel answers in its place. That this works at
  all was found by the [OptiScaler](https://github.com/optiscaler/OptiScaler) project. AMD
  neither tests nor supports it; on an RTX 5080 both networks run and look right, and
  nothing else has been tried. A network that will not start is dropped for the rest of
  the run and the next one down is taken. FSR 2, which the library also carries, is not
  listed.
- **FSR, like DLSS, works from estimated motion** and a flat depth. Unlike DLSS it also
  needs the picture shifted by part of a pixel each frame, which Rayxel does for it and
  which costs a little sharpness that FSR's own sharpening then puts back.
- **A network of FSR 4 is built once for each size of picture,** by the driver: about
  twenty seconds for the full one, one for the lighter, after which the driver keeps it.
  Rayxel has that done on a thread of its own and shows the picture unscaled meanwhile.
- **Two identical capture cards cannot be told apart for audio:** a WASAPI endpoint does not
  expose the hardware instance under it. The audio input selector exists for that case.
- **Hot-plug is noticed lazily,** when a device list is opened.
- **One set of settings,** not a profile per console or game.
- Only `NV12` and `P010` can be displayed; other formats and modes slower than 30 fps are
  left out of the menus.

## License

Rayxel is released under the [MIT License](LICENSE).

It includes or uses Dear ImGui through ImGui.NET (MIT), NAudio (MIT) and Vortice.Windows
(MIT). DLSS is NVIDIA's and no part of it is included: Rayxel calls the copy the graphics
driver runs. FSR is AMD's and no part of it is included either: Rayxel calls AMD's own
signed library when it finds one.

NVIDIA, RTX and G-SYNC are trademarks of NVIDIA Corporation; AMD, Radeon, Ryzen and
FidelityFX are trademarks of Advanced Micro Devices, Inc.; Nintendo Switch is a trademark
of Nintendo; Steam Deck is a trademark of Valve; ShadowCast is a trademark of Genki.
Rayxel is not affiliated with or endorsed by any of them.
