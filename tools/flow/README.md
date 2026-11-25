# Measuring the motion estimate

Rayxel estimates how the picture moved between frames (`src/Rayxel/Render/OpticalFlow.cs`),
and DLSS, FSR and frame generation go by that estimate. These scripts check it against clips
whose motion is known exactly, with nothing connected: Rayxel reads the clip from a file.

They need Python with numpy and Pillow, and ffmpeg on the path.

```
# 1. Clips with known motion: any two pictures will do, one for the background, one to cut pieces from
python make_clips.py clips --bg background.png --cut other.png --ref --truth 60

# 2. The vectors each estimate makes of a clip, and what each costs
Rayxel.exe --clip-file=clips\layers.y4m --shot=out\layers.png --window=1920x1080 --no-auto-hdr ^
           --clip-ways=nothing --clip-flow=fast,medium,max --clip-vectors --clip-time --clip=9

# 3. Those vectors against the truth
python measure.py clips\layers.npz out\layers-clip\flow-fast out\layers-clip\flow-medium out\layers-clip\flow-max

# 4. What the upscalers make of the clip with each estimate, with none, and with the truth itself
python truth_vectors.py clips\layers.npz clips\layers-truth --spread 2
Rayxel.exe --clip-file=clips\layers.y4m --shot=out\layers.png --window=1920x1080 --no-auto-hdr --scaler=dlss ^
           --clip-ways=none,dlss-L,fsr --clip-flow=zero,fast,medium,max,truth --clip-truth=clips\layers-truth
Rayxel.exe --clip-file=clips\layers-ref.y4m --shot=out\layers-ref.png --window=1920x1080 --no-auto-hdr --clip-ways=none
python quality.py clips\layers.npz out\layers-ref-clip\none out\layers-clip\dlss-L-fast out\layers-clip\dlss-L-max
```

For a clip of real video, whose motion nobody knows, `explains.py` says how well each
estimate's vectors rebuild every frame from the one before.

Each script says more about itself at its top. A run of Rayxel rewrites its settings file
on the way out, like any other; the times `--clip-time` logs are with the card at full load,
which a viewer drawing sixty light frames a second is not always given.

What they found on an RTX 5080, with clips of 960x540 doubled to 1920x1080:

| | fast | medium | max |
|---|---|---|---|
| vectors | one per 4x4 block | one per 2x2 block | one per pixel |
| a 1280x800 frame | 0.33 ms | 0.74 ms | 0.93 ms |
| a 1920x1080 frame | 0.54 ms | 1.35 ms | 1.75 ms |
| median error where there is detail | 0.11-0.14 px | 0.03-0.06 px | 0.03-0.05 px |
| within 0.1 px there | 37-44 % | 72-84 % | 73-84 % |
| within 0.5 px by the outlines of things, all of it moving | 27-31 % | 39-43 % | 46 % |
| the same over a still background | 73 % | 80 % | 85 % |
| a clip of real video left unexplained | 6.5 grey levels | 5.4 | 4.6 |

and what DLSS (the driver ran its network M) made of the clips, in dB against the reference:

| clip | none given | fast | medium | max | the true vectors |
|---|---|---|---|---|---|
| layers, moving detail | 32.65 | 34.92 | 35.37 | 35.45 | 35.90 |
| layers, the things crossing it | 35.77 | 38.39 | 39.46 | 39.57 | 41.02 |
| layers, still text over it | 19.02 | 18.45 | 17.70 | 18.02 | 18.75 |
| still background, the things crossing it | 36.54 | 37.26 | 36.90 | 36.22 | 40.04 |
| zoom, moving detail | 34.40 | 35.71 | 36.02 | 35.96 | 36.54 |
| real video, the whole picture | 32.69 | 32.88 | 32.91 | 32.78 | - |

The true vectors of "layers" are spread two pixels towards whatever is in front, as a game
spreads its own before an upscaler has them; those of the other two clips are exact. Exact
to the pixel, the ones of "layers" do worse than the fast estimate on outlines and thin
things (17.74 dB on the text), and spread three pixels or more a thing is drawn over what
is behind it (the things crossing fall to 37.0 dB, and to 30.8 at five).
