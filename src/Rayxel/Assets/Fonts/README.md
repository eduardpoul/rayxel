# Fonts

The design specifies **Space Grotesk** (labels and copy) and **Roboto Mono** (every measured
value, resolution, counter and code-ish string). Both are SIL Open Font License, and the
handoff requires them bundled with the app rather than fetched at runtime.

They are **not committed here** — dropping someone else's font binaries into the repo is a
licensing decision, not a build step, so it is left to whoever ships this.

## To use the designed typefaces

Put these files in this folder:

```
SpaceGrotesk-Medium.ttf
RobotoMono-Regular.ttf
```

They are copied to the output directory on build and picked up automatically. The log line
at start-up says which set was loaded:

```
fonts: design set from ...\Assets\Fonts
```

Any `SpaceGrotesk*.ttf` / `RobotoMono*.ttf` will be found if the exact names above are absent.

## Without them

The app falls back to **Segoe UI** and **Cascadia Mono** from `C:\Windows\Fonts`. Both carry
the Cyrillic the UI needs, so nothing breaks and nothing renders as boxes — the shapes are
just not the designer's. The start-up log says so plainly:

```
fonts: design set not found in ..., using segoeui.ttf / CascadiaMono.ttf
```

## Glyph coverage

The atlas is built for the five shipped languages plus the punctuation the copy leans on —
the arrow in `SDR → HDR`, middot separators, em dashes, ellipses and the multiplication sign
in `1920×1080`. ImGui's built-in Cyrillic range does **not** include those, and without them
the copy renders with holes in it. See `GlyphRanges` in `Ui/Fonts.cs`.
