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

## The uncomfortable part

Cross-checking every classified mode against the hardware's own frame lists, matched on
resolution **and** rate:

```
44 mode(s) flagged as secretly compressed
   39 DO exist uncompressed in hardware: 3840x2160@30, 3840x2160@25,
      2560x1440@60, 2560x1440@50, 2560x1440@30, 2560x1440@25 ...
43 mode(s) called genuinely raw
   all of them exist uncompressed in hardware - no mode was let through wrongly
```

Read carefully:

- **No false negatives.** Nothing the app calls raw is compressed in hardware. The `raw`
  label can be trusted.
- **Many false positives.** 39 of 44 `mjpeg?` flags are on modes the device really does
  offer uncompressed at exactly that resolution and rate.

The cause is structural. The card offers *both* NV12 and MJPEG at, say, 2560×1440@60, so
Windows lists three entries: the genuine NV12, the MJPEG, and the decoded-MJPEG-as-NV12.
The adjacency rule flags whichever NV12 precedes the MJPEG — and it has no way to tell
which of the two NV12 entries that is.

So `mjpeg?` should be read as **"one of the NV12 entries at this rate is fake"**, not
"this one is". The app is right that something is being invented; it is not reliable about
which entry.

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

1. **Use the descriptors to correct the labelling** rather than only to check it: for each
   resolution, the highest uncompressed rate the hardware declares is the truth, and any
   Windows "uncompressed" entry above it is provably synthesized. That turns two inferences
   into one fact, and would clear the 39 false positives above.
2. Nothing to do about the bandwidth ceiling. There is no declared figure to read — this
   card moves video over **bulk** transfers, and bulk endpoints reserve no bandwidth, so
   they state no rate the way isochronous ones do. The inferred value has now been checked
   against the link and against the hardware's own frame list, and both agree with it.

## What this does not do

It reads. Capturing frames this way would mean displacing `usbvideo.sys` with WinUSB, which
takes the card away from every other application on the machine — no browser, no OBS. The
realistic latency gain is one to two milliseconds against the 8.3 ms that borderless
fullscreen already recovered.
