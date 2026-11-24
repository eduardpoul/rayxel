# The sender in C#

The sender is what runs on the machine whose picture Rayxel shows: a Steam Deck in Game Mode,
a Linux desktop, or a Windows PC. It replaces `src/rayxel-send` (C), which stays in the
repository as the reference it was ported from until the C# one has been proven on a Deck.

## The pieces

| Project | What it is |
|---|---|
| `src/Rayxel.Link` | Everything every side shares, with no user interface and nothing tied to one operating system: the wire protocol (`Wire`), pairing and the keys it leaves (`Security`), finding senders (`Discovery`), the sender's logic (`Sender/SenderService`), the control socket (`Control`), the media and input contracts (`Media`, `Input`) and PyroWave's encoder. Referenced by all of the below and by the viewer. |
| `src/Rayxel.Send.Linux` | The Linux half: gamescope's picture through PipeWire, HEVC and AV1 through VA-API, the pointer from gamescope's X servers, a virtual gamepad, keyboard and mouse through uinput. P/Invoke into the system's own libraries. |
| `src/Rayxel.Send.Windows` | The Windows half: a display by DXGI desktop duplication, HEVC and AV1 through Media Foundation's hardware encoders, keyboard and mouse through SendInput. |
| `src/Rayxel.Send` | `rayxel-send`, the sender itself: no window, runs in the background, answers its user interfaces on the control socket. One project, two builds - `net10.0` with the Linux half (published with NativeAOT as one native file), `net10.0-windows` with the Windows half. |
| `src/Rayxel.Send.App` | `rayxel-send-ui`, the sender's window (Avalonia), for Windows and Linux desktops: state, PIN, paired computers, settings. |
| `src/rayxel-decky` | The Decky Loader plugin: the same, in the Deck's Quick Access menu, plus the systemd user service that keeps `rayxel-send` running. |

Only the user interface differs between a desktop and a Deck; both talk to the same
`rayxel-send` over the same socket.

## Protocol version 2

Version 1 (what the C sender speaks) let anyone on the network ask for the picture, and with
input allowed, type on the sender. Version 2 adds who is talking:

- **Pairing** - once per viewer and sender. The sender is made discoverable (a button in its
  window or the plugin, for a few minutes); Rayxel finds it, both exchange long-term P-256 keys
  with a commitment first, and the sender shows a six-digit PIN that is a fingerprint of the
  exchange. Typed into Rayxel, it proves nobody stood in between - one chance in a million for
  whoever tries. Each side keeps the other's public key: Rayxel in
  `%LOCALAPPDATA%\Rayxel\links.json`, the sender in `viewers.json`.
- **Sessions** - every stream starts with CONNECT (viewer id, fresh nonce) and WELCOME
  (sender's nonce, proof it holds the paired key). The two nonces and the pair's shared secret
  give the session's own keys. Every message of version 1's set - HELLO through MOUSE, video
  included - then travels sealed with AES-GCM, with a counter that also refuses replays.
- **Discovery** - DISCOVER broadcast by Rayxel, ANNOUNCE back from every sender that is
  discoverable or already knows this viewer. A Deck runs no Avahi, so the senders answer for
  themselves; the last known address is asked directly too, for routers that keep broadcasts
  between Wi-Fi clients apart.

A sender can also take unpaired viewers speaking version 1 ("Allow unpaired" in its settings,
off by default) - never with input. Rayxel still speaks version 1 to a sender named by address
with `--net=`.

## What is kept where

| | Linux | Windows |
|---|---|---|
| Settings, key, paired viewers | `~/.config/rayxel-send/` (`settings.json`, `identity.key` readable by the user only, `viewers.json`) | `%LOCALAPPDATA%\Rayxel\Send\` (the key under DPAPI) |
| Control socket | `$XDG_RUNTIME_DIR/rayxel-send.sock` | `%LOCALAPPDATA%\Rayxel\Send\control.sock` |

The control socket takes one JSON object per line: `status`, `subscribe`, `set_enabled`,
`set_discoverable`, `cancel_pairing`, `forget`, `get_settings`, `set_settings`, `stop_stream`,
`quit` - see `src/Rayxel.Link/Control/ControlModel.cs`.

## Off is not ending

gamescope can abort - taking the game with it - when a capture leaves its stream while a frame
is on its way (the long comment in `src/rayxel-send/src/capture.h`). So the sender keeps a
capture from the first viewer until it ends, and **switching it off does not end it**: it stops
streaming and stops answering, and keeps the capture. Ending the program, or stopping its
service, is a separate action; both user interfaces warn while a capture is held.

## Building

Windows, for development (the viewer, the sender's Windows build and its window):

```
dotnet build Rayxel.slnx -c Release
```

The Linux sender, as one native file with PyroWave's library beside it:

```
cd src
docker build -f Rayxel.Send/Dockerfile --output type=local,dest=Rayxel.Send/out/linux-x64 .
```

The Decky plugin, which carries that build in its `bin` folder: see `src/rayxel-decky/README.md`.

The sender with its window, as one folder to copy to a PC or a Linux desktop
(`publish/sender-win-x64` or `publish/sender-linux-x64`):

```
powershell -File src/Rayxel.Send/publish.ps1 [-Target linux-x64]
```

`rayxel-send --probe` reports what a machine offers - the encoders, the pointer, input, the
key and the paired viewers - without opening a capture, so it is safe while a game runs.

## Not carried over from the C sender

- `--record`, which captured, encoded to a file and measured each stage. Measuring now goes
  through a viewer (`Rayxel --bench`) and `--frames-file`.
- The probe's trial capture: opening a capture to see that frames come is exactly what is
  risky under a running game.

## Not there yet

- A virtual gamepad on Windows (needs the ViGEmBus driver); keyboard and mouse work.
- Starting with Windows or with a Linux desktop session. On a Deck the plugin's service does it.
- The window and the plugin speak English, Russian, Ukrainian, German and Spanish; the reasons
  the core gives (why a pairing failed, why a codec is missing) are English.

## Status

Written, compiled, not yet run: nothing here has been tried on a Deck or a PC yet. What needs
checking first, in order: the PipeWire capture against gamescope (format negotiation and the
pod bytes), the VA-API encoder's structure layouts against the Deck's libva, a pairing and a
sealed session between Rayxel and the sender on the same network, then the plugin's service and
panel.
