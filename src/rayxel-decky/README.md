# Rayxel for Decky

A Quick Access panel for the Steam Deck side of Rayxel: switch the sender on and off, make
the Deck discoverable, read the pairing code, forget paired computers, choose whether a
computer's controller, keyboard and mouse may play here, and whether the sender starts with
the Deck. Its words are the viewer's: what Rayxel on the PC calls pairing, a paired computer
and discoverable, the panel calls the same, in all five languages.

The sender itself, `rayxel-send`, is not run by Decky. The plugin installs it as a systemd
user service, `rayxel-send.service`, and talks to it over its control socket
(`/run/user/1000/rayxel-send.sock`). Reloading Decky, updating the plugin or the plugin
failing leaves a stream running.

## Off is not stop

Ending the sender while it holds gamescope's capture can crash gamescope, and the running
game with it. So the panel's switch never ends the program: **Off** only tells the sender to
stop streaming and stop answering. There is no button that ends it: whether it runs at all
is whether it starts with the Deck. The backend still has a guarded stop (`stop_service`),
which asks for force while the sender holds the game's picture.

## Building

```sh
./build.sh [SENDER [PYROWAVE]]
```

- `SENDER`: the Linux `rayxel-send` binary (NativeAOT, one file). Default
  `../Rayxel.Send/out/linux-x64/rayxel-send`, or `$RAYXEL_SENDER`.
- `PYROWAVE`: `libpyrowave-shared.so.1`, which the sender loads from beside itself. Default
  `../rayxel-send/out/libpyrowave-shared.so.1`, or `$RAYXEL_PYROWAVE`. Optional: without it
  the Deck offers HEVC and AV1 only.

The frontend is built with `pnpm` if it is installed, otherwise in a `node:22` container, so
Docker is enough. The script runs on Linux and in Git Bash on Windows. It leaves the plugin
folder in `out/Rayxel/` and the archive in `out/Rayxel.zip`.

## Installing on the Deck

`~/homebrew/plugins` belongs to root, so the plugin cannot simply be copied there. Decky
installs it from the ZIP instead:

1. Copy `out/Rayxel.zip` to the Deck, for example `scp out/Rayxel.zip deck:Downloads/`.
2. In Game Mode open Decky's settings (the gear in the Decky tab of the Quick Access menu),
   and under **General** switch on **Developer mode**.
3. In the new **Developer** section choose **Install Plugin from ZIP File** and pick
   `Rayxel.zip`.

An update is installed the same way. Decky replaces a plugin by uninstalling the old copy
first; the sender is switched off for that moment and the new copy switches it back on.

## What it puts on the Deck

- `~/homebrew/plugins/Rayxel/`: the panel, its backend (`main.py`), and in `bin/` the sender
  and PyroWave's library.
- `~/.config/systemd/user/rayxel-send.service`: the service, written when the plugin loads.
  **Start with the Deck** enables or disables it.
- `~/.config/rayxel-send/`: the sender's own settings, its key and the PCs paired with it.

The sender's log is the service's journal:

```sh
journalctl --user -u rayxel-send -f
```

## Uninstalling

Uninstalling from Decky switches the sender off but does not end it, for the same reason as
above; it goes with the next reboot, and after that the service finds its binary gone and
does not start. To remove the service file too:

```sh
systemctl --user disable rayxel-send.service
rm ~/.config/systemd/user/rayxel-send.service
systemctl --user daemon-reload
```

The pairings in `~/.config/rayxel-send/` are kept, so a reinstall finds the same PCs.

## The backend's calls

For reference, what `src/index.tsx` calls through `@decky/api` (all in `main.py`):

| Call | What it does |
| --- | --- |
| `get_status()` | `{running, status}`: the sender's whole state, or `status: null` when nothing answers |
| `set_enabled(value)` | On starts the service first when it is not running; Off is `set_enabled false`, never a stop |
| `set_discoverable(value)`, `cancel_pairing()`, `forget(id)` | passed to the sender as they are |
| `get_settings()`, `set_settings(settings)` | the sender's settings, all at once |
| `set_allow_input(value)` | reads the settings, changes `allowInput`, writes them back |
| `get_autostart()`, `set_autostart(value)` | `systemctl --user is-enabled / enable / disable` |
| `stop_service(force)` | ends the sender; without `force` it refuses while a capture is held (`code: "needs_force"`) |

Events: `rayxel_status` with `{running, status}` on every change, and `rayxel_pairing` with
`{viewerName, pin, expiresMs}` once per new PIN, which the panel turns into a toast when it is
not open.
