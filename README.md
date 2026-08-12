# Rayxel

A Windows viewer for USB capture cards that treats input lag as the thing being measured.
It shows the picture from a capture card and, optionally, runs it through RTX Video Super
Resolution, RTX Video HDR or NIS — while telling you exactly what each one costs in
milliseconds of the frame budget.

Built for a Steam Deck / Switch 2 on a Genki ShadowCast 2 Pro, but nothing in it is
specific to that card.

## What it does that other capture apps do not

**It tells you when a mode is lying.** Windows reports `NV12 1920x1080 @120` twice on this
card: once genuine, once as the decoded output of a JPEG stream. Same label, different
latency. Rayxel detects the difference with two independent tests — where the capture stack
placed the type in its list, and whether the payload could physically fit through the
device's own measured link budget — and labels the fake one `mjpeg?` in red.

**It measures the link rather than assuming it.** A single fastest mode proves nothing;
two *different* resolutions landing on the same byte rate is a wire running out. On the
ShadowCast four geometries agree at 356 MiB/s — USB 3.2 Gen 1.

**It is honest about what it cannot measure.** End-to-end latency is not knowable in
software: the card's own delay before it timestamps a frame, and the monitor's after
present, are both invisible. Numbers that cannot be measured show `n/a` rather than an
estimate.

## Running it

Just run `Rayxel.exe`. It picks the best capture device it can find, opens the fastest
uncompressed mode, and follows the Windows HDR state.

| Key | |
|---|---|
| `Tab` | settings panel |
| `T` | telemetry readout |
| `M` | capture-mode table |
| `F11` | borderless full screen |
| `1` / `2` / `3` | no processing / NIS / RTX VSR |
| `H` | RTX HDR |
| `Esc` | quit, with a confirmation |

**Press F11.** In a window the desktop compositor adds a whole frame — 8.3 ms at 120 fps,
more than RTX VSR (4.50 ms) and RTX HDR (0.66 ms) together. Borderless reaches a hardware
overlay plane instead. The telemetry's `present:` field says which one you have:
`Overlay` good, `Composed` costly.

The settings panel hides itself after five seconds of not being touched; the faint gear in
the top-right corner brings it back.

## Where it keeps things

```
%LOCALAPPDATA%\Rayxel\settings.json     what you last chose
%LOCALAPPDATA%\Rayxel\logs\             one rolling log per run
```

Beside the executable, not in it: `Assets\Fonts\`. Drop `SpaceGrotesk-Medium.ttf` and
`RobotoMono-Regular.ttf` there for the designed typefaces. Without them it falls back to
Segoe UI and Cascadia Mono, which carry the same Cyrillic, and says so in the log.

## Command line

Everything below is optional; the app needs no arguments.

| Flag | |
|---|---|
| `--list` | probe every capture device and print what it can do, then exit |
| `--device=<name\|index\|path>` | use a specific device instead of choosing |
| `--lang=ru\|en\|uk\|de\|es` | start in a language |
| `--borderless` | start in borderless full screen |
| `--tele` | start with the telemetry readout up |
| `--console` | attach to the terminal it was launched from and print the log there |
| `--verbose` | debug-level logging |
| `--shot=<file.png>` | render for a few seconds, write the frame to a PNG, exit |
| `--no-auto-hdr` | do not follow the Windows HDR state at start-up |
| `--screen=splash\|devices\|app\|nosignal\|modes\|quit` | force a screen, for inspecting it |

`--shot` captures the **back buffer**, not the screen, so it works when the window is
occluded or on a display nobody is looking at. It is how the interface was checked.

## Building

```
dotnet build Rayxel.slnx -c Release
```

To produce the shippable executable:

```
dotnet publish src/Rayxel/Rayxel.csproj -c Release -r win-x64 --self-contained -o publish
```

The runtime identifier belongs on that command line and **not** in the project file. As a
project property it moves every ordinary build into a `bin\...\win-x64\` subfolder and
leaves the previous executable sitting at the old path, where it goes on being launched and
looking as though the code changed nothing.

That yields **exactly one file**: `Rayxel.exe`, about 38 MB. No .NET install needed on the
target machine, no `.pdb`, nothing beside it to lose.

Everything is inside it. NVIDIA's NIS shader sources are embedded as assembly resources
rather than copied out, `cimgui.dll` is bundled, and debug symbols are embedded in the
assembly — so stack traces still carry file and line numbers without a separate symbol
file. Verified by copying the executable alone into an empty folder and running it.

The bundle is compressed, which roughly halves it. That costs about **0.35 s on the very
first launch** while .NET extracts to its cache, and nothing on later launches. Time to the
first video frame is unchanged at 145 ms.

Trimming is deliberately off: Vortice, NAudio and `System.Text.Json` all resolve types
reflectively, and a trimmed build of this fails at runtime instead of at build time.

## Known limits

- **Intel VSR is written but has never run on Intel hardware.** It is untested, not proven.
- **Two identical capture cards cannot be told apart for audio.** A WASAPI endpoint does not
  expose the PnP instance id of the hardware under it — only the adapter's name. The audio
  input selector exists for that case.
- **Hot-plug is noticed lazily**, when a device list is opened. Nothing listens for
  `WM_DEVICECHANGE`.
- **No named profiles.** There is one set of settings, not a switchable list per console.
- Only `NV12` and `P010` can be displayed; other formats are filtered out of the menus, as
  are modes slower than 30 fps.

## Licences

NVIDIA Image Scaling (`src/Rayxel/Render/NIS/`) is MIT, © NVIDIA Corporation. Dear ImGui
via ImGui.NET is MIT. NAudio is MIT. Vortice.Windows is MIT.
