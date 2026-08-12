# Reading the card's own USB descriptors

Branch `usb-descriptors`. An experiment: ask the hardware directly what it can do, instead
of inferring it from what Windows chooses to expose.

Run it with `Rayxel.exe --usb`. Read-only — no driver is rebound, no stream is opened, and
the card stays usable by every other application.

## Why

The app's headline finding is that some capture modes lie: Windows publishes the decoded
output of an MJPEG stream as an extra "uncompressed NV12" media type, indistinguishable
from the genuine one by subtype. Two indirect tests catch it — where the type sits in the
list, and whether its payload could fit the device's measured link budget.

Both are inferences. The USB configuration descriptor is the device's own statement, and
Windows never shows it.

## How

`SetupDiGetClassDevs` over `GUID_DEVINTERFACE_USB_HUB` to find every hub, then per port:

| IOCTL | For |
|---|---|
| `USB_GET_NODE_INFORMATION` | how many ports the hub has |
| `USB_GET_NODE_CONNECTION_INFORMATION_EX` | vendor/product id, device descriptor, and a link speed that cannot say SuperSpeed |
| `USB_GET_NODE_CONNECTION_INFORMATION_EX_V2` | the link speed that can — see below for the flag that makes it answer |
| `USB_GET_DESCRIPTOR_FROM_NODE_CONNECTION` | the full configuration descriptor |

The configuration descriptor is then walked for the VideoStreaming interface: format
descriptors (`VS_FORMAT_UNCOMPRESSED`, `VS_FORMAT_MJPEG`, `VS_FORMAT_FRAME_BASED`), their
frame descriptors (resolution plus the exact frame intervals), and the endpoints.

## What it found on the ShadowCast 2 Pro

The hardware declares **six formats**, and this is first-hand:

| # | Format | Kind | Top sizes |
|---|---|---|---|
| 1 | YUY2 | uncompressed | 2560×1440@50, 1920×1080@60 |
| 2 | NV12 | uncompressed | 3840×2160@**30**, 2560×1440@60, 1920×1080@**120** |
| 3 | I420 | uncompressed | 3840×2160@30, 1920×1080@120 |
| 4 | MJPEG | compressed | 3840×2160@**60**, 2560×1440@**144**, 1920×1080@**240** |
| 5 | (unknown fourcc) | uncompressed | 1920×1080@60 |
| 6 | P010 | uncompressed | 1920×1080@60, 1280×720@60 |

The shape of the trap is now visible: MJPEG reaches rates that no uncompressed format does
— 4K60 against 4K30, 1440p144 against 1440p60, 1080p240 against 1080p120. Every one of
those is a rate Windows also offers as "NV12", and cannot be.

## The cross-check, and the wrong question that nearly sank it

The first version of this cross-check asked *"does this resolution and rate exist
uncompressed in hardware?"* and reported **39 of 44 flags are false positives**. That was
wrong, and the fault was in the question. Of course 1440p60 exists uncompressed — the card
offers it both as NV12 and as MJPEG. Proving it exists proves nothing.

The question that has an answer is **how many times**. Where the card offers a resolution
both ways, Windows publishes three types: the real NV12, the MJPEG, and the MJPEG decoded
back into NV12. Two NV12 entries where the hardware declares one. The surplus *is* the
invention.

Counting per fourcc, resolution and rate — all three, because the card's I420 stream is no
excuse for a second NV12, and comparing sizes alone would wave 4K60 through:

```
69 uncompressed-looking mode(s) published by Windows
35 of them the hardware actually declares - 34 must be invented
the app flagged 34
  the count matches everywhere - every flag is accounted for by a real invention
```

Not one rate is over-flagged or under-flagged. Two shapes of invention, both caught:

1. **Rates nothing uncompressed reaches.** 4K@60, 4K@50, 1440p@144, 1440p@120, 1080p@240.
   The hardware's NV12 stops at 4K30, 1440p60 and 1080p120. Everything above is MJPEG
   wearing an NV12 label, and that is provable outright.
2. **Rates the hardware does reach.** 4K30, 1440p60/50/30, 1080p120/60/50/30 and the
   smaller sizes. Hardware declares one NV12 stream, Windows shows two, so exactly one of
   the pair is a decode.

P010 is the clean case and a useful control: 1080p and 720p, one stream in hardware, one
entry in Windows, no MJPEG twin, nothing flagged. Nothing is being invented there.

And the other direction: no mode labelled `raw` sits at a rate the hardware cannot do
uncompressed. Nothing was let through wrongly.

### What is still not provable

**Which** entry of a pair is the fake one. The adjacency rule flags whichever NV12 precedes
the MJPEG, and either could be the genuine one. The count is proven; the identity is not.
Getting it backwards costs a CPU JPEG decode and JPEG artefacts on a mode that could have
been clean — not a broken picture, but exactly the latency this app exists to remove.

No descriptor can settle it, because the invention happens on the host, not the device.
Telling them apart means opening both and looking: a decoded stream shows 8×8 ringing a raw
one does not, and it burns CPU that a raw one does not.

## Settled: the link really is SuperSpeed

An earlier version of this file left the link speed open, because
`USB_GET_NODE_CONNECTION_INFORMATION_EX` reported **high speed, 480 Mbit/s** for a card
visibly moving several gigabits, and the V2 query that would have settled it returned
`ERROR_INVALID_PARAMETER` on every port.

The V2 request was malformed. `SupportedUsbProtocols` is an **input**: the caller has to say
which protocols it understands, and the hub rejects the call outright when it is left empty.
Setting bit 2 (`Usb300`) makes it answer. An error that reads like "not supported" was
nothing but a missing flag.

Four independent sources now agree, and only the first was ever wrong:

| Source | Says |
|---|---|
| V1 connection query | high speed — **and is documented to say this for SuperSpeed devices** |
| Device descriptor, `bMaxPacketSize0` | **9** (512 bytes), a value only legal at SuperSpeed |
| Configuration descriptor | carries SuperSpeed endpoint companions, which the hub returns only for a device enumerated on the SuperSpeed bus |
| Bulk endpoint `0x83` | `wMaxPacketSize` **1024**, the SuperSpeed value; 512 would mean high speed |
| V2 connection query | flags `0x03` — operating at SuperSpeed, capable of SuperSpeed |

So: **SuperSpeed, 5 Gbit/s, on port 2 of `root_hub30`.** Nothing is being throttled. The V2
flags also separate *operating* from *capable*, and the card reports capable of SuperSpeed
but not SuperSpeed+ — the 5 Gbit/s limit is the card's own, not the port's, so a faster port
or cable would change nothing.

The single streaming endpoint is `0x83 in, bulk, 1024 bytes per packet, burst 16`.

### What that does to the inferred ceiling

A 5 Gbit/s link carries roughly **405 MiB/s** of payload after 8b/10b coding and bulk
protocol overhead. The app's bandwidth detector, which never asks the wire and works purely
from the shape of the mode list, inferred **356 MiB/s**. It fits.

Better than fits — the hardware's own numbers show the detector found the right value for
the right reason. Its rule is "two different geometries at the same byte rate means the wire
is running out", and the two top uncompressed modes the card declares are:

```
3840x2160 NV12 @ 30  =  12 441 600 x 30  =  373 248 000 B/s  =  356 MiB/s
1920x1080 NV12 @ 120 =   3 110 400 x 120 =  373 248 000 B/s  =  356 MiB/s
```

Identical to the byte. That is exactly the corroboration the detector looks for, and the
ceiling it inferred is the real one.

Meanwhile MJPEG's 4K60 would need 712 MiB/s uncompressed — well past the link — which is
the structural reason those modes cannot be what Windows claims they are.

## What is worth doing next

1. **Tell the pair apart.** The count is settled; the identity is not. Open both NV12
   entries at one rate and compare — the decoded one costs CPU and carries 8×8 ringing.
   That is the only remaining gap in the classification, and it needs a measurement rather
   than a descriptor.
2. **Feed the descriptors back into the app**, not just into this report. The highest
   uncompressed rate the hardware declares is the truth for a resolution, so any Windows
   entry above it can be marked synthesized with certainty instead of inference — turning
   the app's two indirect detectors into one fact wherever a device answers these IOCTLs.
2. Nothing to do about the bandwidth ceiling. There is no declared figure to read — this
   card moves video over **bulk** transfers, and bulk endpoints reserve no bandwidth, so
   they state no rate the way isochronous ones do. The inferred value has now been checked
   against the link and against the hardware's own frame list, and both agree with it.

## What this does not do

It reads. Capturing frames this way would mean displacing `usbvideo.sys` with WinUSB, which
takes the card away from every other application on the machine — no browser, no OBS. The
realistic latency gain is one to two milliseconds against the 8.3 ms that borderless
fullscreen already recovered.
