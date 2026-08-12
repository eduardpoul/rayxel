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
| `USB_GET_NODE_CONNECTION_INFORMATION_EX` | vendor/product id, link speed, device descriptor |
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

## Open question: the link speed

`USB_GET_NODE_CONNECTION_INFORMATION_EX` reports **high speed, 480 Mbit/s**, while the
device descriptor says the card itself is **USB 3.0**. That matters: 480 Mbit/s is about
60 MB/s, and 1920×1080 NV12 at 120 fps needs 373 MB/s. Both cannot be true.

Three possibilities, none yet eliminated:

1. The card is genuinely negotiated down to USB 2 — a cable or port limitation. Then
   1080p120 "raw" is not raw at all, and the app's inferred 356 MiB/s ceiling is an
   artefact of believing the synthesized types.
2. The V1 structure simply cannot express SuperSpeed. It has no value for it, which is why
   `..._EX_V2` exists.
3. The matched port is the USB 2 companion of a SuperSpeed connection.

The V2 query was attempted and returns `ERROR_INVALID_PARAMETER` (87) on every port, so the
request is malformed rather than unsupported — **this is unfinished, not answered.** Until
it is, no claim is being made about the cable.

Independent evidence that does not settle it either way: Windows reports the device's parent
chain as `USB Composite Device → USB\ROOT_HUB30 → USB Root Hub (USB 3.0)`, but an xHCI root
hub hosts both buses, so that only proves the controller is USB 3.

## What is worth doing next

1. **Fix the V2 request** and settle the link speed. If the card really is on USB 2, that
   is the single most valuable thing this whole exercise produced — a cable change would
   unlock genuinely uncompressed capture.
2. **Use the descriptors to correct the labelling** rather than only to check it: for each
   resolution, the highest uncompressed rate the hardware declares is the truth, and any
   Windows "uncompressed" entry above it is provably synthesized. That turns two
   inferences into one fact.
3. Leave the bandwidth ceiling inferred until (1) is settled — the descriptor route found
   no isochronous endpoint, which means this card moves video over **bulk** transfers, and
   bulk endpoints do not declare a reserved rate the way isochronous ones do.

## What this does not do

It reads. Capturing frames this way would mean displacing `usbvideo.sys` with WinUSB, which
takes the card away from every other application on the machine — no browser, no OBS. The
realistic latency gain is one to two milliseconds against the 8.3 ms that borderless
fullscreen already recovered.
