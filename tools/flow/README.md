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
| a 1280x800 frame | 0.33 ms | 0.85 ms | 1.05 ms |
| a 1920x1080 frame | 0.55 ms | 1.58 ms | 1.98 ms |
| median error where there is detail | 0.11-0.14 px | 0.03-0.05 px | 0.03-0.05 px |
| within 0.1 px there | 37-44 % | 76-88 % | 77-88 % |
| within 0.5 px by the outlines of things, all of it moving | 27-31 % | 38-42 % | 44-45 % |
| the same over a still background | 73 % | 79 % | 85 % |
| a clip of real video left unexplained | 6.5 grey levels | 6.0 | 5.0 |

and what DLSS (the driver ran its network M) made of the clips, in dB against the reference:

| clip | none given | fast | medium | max | the true vectors |
|---|---|---|---|---|---|
| layers, the whole picture | 32.65 | 33.88 | 34.06 | 34.02 | 34.23 |
| layers, moving detail | 32.65 | 34.92 | 35.33 | 35.40 | 35.90 |
| layers, the things crossing it | 35.77 | 38.39 | 39.35 | 39.47 | 41.02 |
| layers, still text over it | 19.02 | 18.45 | 18.30 | 18.15 | 18.75 |
| still background, the things crossing it | 36.54 | 37.35 | 37.77 | 37.67 | 40.75 |
| zoom, moving detail | 34.40 | 35.64 | 36.01 | 35.99 | 36.54 |
| real video, the whole picture | 32.69 | 32.90 | 32.98 | 32.85 | - |

FSR 4.1.2 follows the same order a little further apart: on "layers" 32.53, 32.75 and 32.87
for the whole picture, on the real clip 31.64, 31.76 and 31.65.

The true vectors are spread two pixels towards whatever is in front, as a game spreads its
own before an upscaler has them ("zoom" has them exact). Exact to the pixel, the ones of
"layers" do worse than the fast estimate on outlines and thin things (17.74 dB on the
text), and spread three pixels or more a thing is drawn over what is behind it (the things
crossing fall to 37.0 dB, and to 30.8 at five).

Where the rest of the gap to the truth lies, found by putting the truth into the estimate's
vectors in one place at a time (`mix_truth.py`):

- Inside things, away from their outlines, it is worth about 1 dB on the things crossing,
  in both clips. Only a third of that comes back when the truth replaces just the vectors
  that are off by more than a quarter of a pixel: the rest is lost to errors smaller than
  that, spread over everything. They do not average out between neighbouring blocks - they
  follow what is drawn there - and a median of 0.03-0.05 px is about where this way of
  estimating stops.
- Within 4 px of outlines, and on pixels that were hidden a frame ago, it was worth 3 dB
  on the things crossing a still background and nothing on "layers".
- Tried for the outlines and taken out again, for doing nothing an upscaler could show:
  asking blocks further from an outline for their vectors (closer vectors there, 59 %
  within half a pixel instead of 45 %, but a worse picture of real video and half as
  much time again); spreading the vector of what stands still over a moving picture round it, as
  something in front; and letting only blocks found without a doubt answer for flat ones.
  Which of two moving things is in front is still not known; telling it would take the
  estimate run backwards as well, to see what each frame hides of the other.
