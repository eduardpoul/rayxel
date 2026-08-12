# Brand

The mark is a diagonal chevron of five squares — a frame moving through the pipeline, drawn
in the same pixels the app scales. From the design handoff (`design_handoff_rayxel/brand/`),
unchanged.

```
rayxel.ico     16 · 20 · 24 · 32 · 48 · 64 · 128 · 256, PNG-compressed, 32-bit
svg/           the masters every raster was generated from
```

## Where each one is used

`rayxel.ico` does two jobs, and the project file wires it up twice for that reason:

- `<ApplicationIcon>` — what Explorer, the Start menu and a pinned taskbar entry show for
  `Rayxel.exe`. Baked into the executable at build time.
- `<EmbeddedResource>` — read back at run time by `Native/AppIcon.cs`, which picks the two
  sizes the shell asks for and sets them on the window with `WM_SETICON`. The window icon is
  a separate thing from the file's, and nothing sets it for us.

The **splash mark** is not either of these. It is drawn from the geometry in
`Ui/Brand.cs` — five rounded rectangles on a 64-unit grid — because a vector mark is sharp
at every zoom the layout produces, while a raster would have to be atlas'd at one size and
resampled at the rest.

## The geometry, so the code can be checked against it

On a 64-unit grid: 9×9 squares, corner radius 2, at (16,12) (28,20) (40,28) (28,36) (16,44),
fill `#8ab4ff` at opacity .4 / .7 / 1 / .7 / .4. No container, no gradient, no glow.

At **24px and below** the five squares mush together, so a three-square variant on a 16-unit
grid takes over: 4×4 squares, radius 1, at (4,2) (8,6) (4,10), opacity .5 / 1 / .5. Every
raster ≤24px in the bundle is already built from it, and `Ui/Brand.cs` switches to it at the
same threshold.

Clear space is half the mark's height on every side; the minimum size is 16px. Never
recolour the individual squares — the opacity ramp is the identity. On a coloured or
photographic background use a monochrome base rather than the accent.

## If the geometry ever changes

The rasters were generated from the SVGs at their exact pixel sizes, not downscaled from one
large bitmap. Regenerate them the same way, and update `Ui/Brand.cs` to match — it is a
transcription of `svg/rayxel-mark.svg`, not a reader of it.

The PNG rasters and the tile from the handoff are not committed: nothing in the app uses
them. There is no tray icon yet, which is the one place `png/rayxel-mono-{light,dark}-*`
would be needed — pick by DPI rather than scaling one bitmap when there is.
