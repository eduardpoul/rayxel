"""
The Decky side of Rayxel: the Quick Access panel's backend for rayxel-send, the sender that
streams this Deck to Rayxel on a PC.

The sender is not run by this process. It is a systemd user service of its own, so that
reloading Decky, updating the plugin or this backend crashing never ends it: ending the
sender while it holds gamescope's capture can take gamescope down, and the running game with
it. This backend installs that service, starts it when asked, and talks to the sender over
its control socket, one JSON object per line each way. "Off" is always the sender's own
switch (set_enabled false); ending the program is a separate action that refuses while a
capture is held unless forced.

Decky runs this as the deck user (plugin.json asks for no root): the PipeWire session and
systemctl --user both belong to that user.
"""

from __future__ import annotations

import asyncio
import json
import os
import shutil
import time
from typing import Any

import decky

SERVICE = "rayxel-send.service"

# How long a connection may take, and an answer once connected. The sender answers within
# milliseconds; these only bound the wait when something is wrong.
CONNECT_TIMEOUT = 2.0
ANSWER_TIMEOUT = 5.0

# How long a freshly started sender has to open its socket.
START_TIMEOUT = 12.0

# Decky updates a plugin by uninstalling the old copy and installing the new one, and calls
# _uninstall either way. A sender switched off for an uninstall is switched back on by the
# next copy if it loads within this long; after a real uninstall nothing loads, and the
# marker (on a tmpfs) is gone by the next boot.
RESUME_WITHIN = 180.0

# Status lines can carry a list of viewers and codecs; allow far more than they will need.
LINE_LIMIT = 1 << 20


def _runtime_dir() -> str:
    return f"/run/user/{os.getuid()}"


def _socket_path() -> str:
    # The sender's own rule (ControlPaths.SocketPath): $XDG_RUNTIME_DIR/rayxel-send.sock. As
    # a user service it has XDG_RUNTIME_DIR set to this; this process does not, so it is
    # worked out from the uid.
    return os.path.join(_runtime_dir(), "rayxel-send.sock")


def _binary_path() -> str:
    return os.path.join(decky.DECKY_PLUGIN_DIR, "bin", "rayxel-send")


def _unit_dir() -> str:
    return os.path.join(decky.DECKY_USER_HOME, ".config", "systemd", "user")


def _unit_path() -> str:
    return os.path.join(_unit_dir(), SERVICE)


def _resume_marker() -> str:
    return os.path.join(_runtime_dir(), "rayxel-decky-resume")


def _systemd_quote(path: str) -> str:
    # systemd splits ExecStart= on spaces and expands % specifiers and $ variables in it.
    escaped = path.replace("\\", "\\\\").replace('"', '\\"').replace("%", "%%").replace("$", "$$")
    return f'"{escaped}"'


def _unit_text() -> str:
    binary = _binary_path()
    # ConditionFileIsExecutable: once the plugin is uninstalled the binary is gone, and an
    # enabled unit is then skipped at boot instead of failing and restarting forever.
    return (
        "[Unit]\n"
        "Description=Rayxel sender: streams this Steam Deck to Rayxel on a PC\n"
        f"ConditionFileIsExecutable={binary.replace('%', '%%')}\n"
        "After=pipewire.service\n"
        "\n"
        "[Service]\n"
        "Type=simple\n"
        f"ExecStart={_systemd_quote(binary)}\n"
        "Restart=on-failure\n"
        "RestartSec=3\n"
        "\n"
        "[Install]\n"
        "WantedBy=default.target\n"
    )


def _session_env() -> dict[str, str]:
    """The environment for system programs started from here, systemctl above all."""
    env = dict(os.environ)
    # Decky's Python is a PyInstaller bundle, which points LD_LIBRARY_PATH at its own copies
    # of libraries. A system program must load the system's, so the original value, which
    # PyInstaller keeps aside, is put back.
    original = env.pop("LD_LIBRARY_PATH_ORIG", None)
    if original:
        env["LD_LIBRARY_PATH"] = original
    else:
        env.pop("LD_LIBRARY_PATH", None)
    runtime = _runtime_dir()
    env["XDG_RUNTIME_DIR"] = runtime
    env["DBUS_SESSION_BUS_ADDRESS"] = f"unix:path={runtime}/bus"
    return env


async def _systemctl(*args: str, timeout: float = 20.0) -> tuple[int, str]:
    """systemctl --user with the user's bus; the exit code and what it printed."""
    exe = shutil.which("systemctl", path="/usr/bin:/bin") or "systemctl"
    try:
        proc = await asyncio.create_subprocess_exec(
            exe, "--user", *args,
            stdin=asyncio.subprocess.DEVNULL,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.STDOUT,
            env=_session_env(),
        )
    except OSError as e:
        return 127, str(e)
    try:
        out, _ = await asyncio.wait_for(proc.communicate(), timeout)
    except asyncio.TimeoutError:
        proc.kill()
        await proc.wait()
        return 124, f"systemctl {' '.join(args)} timed out"
    return proc.returncode or 0, out.decode("utf-8", "replace").strip()


async def _open() -> tuple[asyncio.StreamReader, asyncio.StreamWriter] | None:
    try:
        return await asyncio.wait_for(
            asyncio.open_unix_connection(_socket_path(), limit=LINE_LIMIT), CONNECT_TIMEOUT)
    except (OSError, asyncio.TimeoutError):
        return None


async def _close(writer: asyncio.StreamWriter) -> None:
    writer.close()
    try:
        await writer.wait_closed()
    except OSError:
        pass


async def _request(request: dict[str, Any], timeout: float = ANSWER_TIMEOUT) -> dict[str, Any] | None:
    """
    One request on a connection of its own, and its answer: {"ok": true, ...} or
    {"ok": false, "error": ...}. None when no sender answers.
    """
    pair = await _open()
    if pair is None:
        return None
    reader, writer = pair
    try:
        writer.write((json.dumps(request) + "\n").encode("utf-8"))
        await writer.drain()
        while True:
            line = await asyncio.wait_for(reader.readline(), timeout)
            if not line:
                return None
            try:
                message = json.loads(line)
            except ValueError:
                continue
            # Events belong to subscribers; an answer is the first line that is not one.
            if isinstance(message, dict) and "event" not in message:
                return message
    except (OSError, asyncio.TimeoutError, asyncio.IncompleteReadError, asyncio.LimitOverrunError, ValueError):
        return None
    finally:
        await _close(writer)


def _no_answer() -> dict[str, Any]:
    return {"ok": False, "code": "no_answer", "error": "the sender did not answer"}


class Plugin:
    def __init__(self) -> None:
        self._tasks: list[asyncio.Task[Any]] = []
        self._running: bool | None = None
        self._status: dict[str, Any] | None = None
        self._last_sent: str | None = None
        self._pin: str | None = None
        self._reload_pending = False
        self._unit_lock = asyncio.Lock()

    # The frontend's calls.

    async def get_status(self) -> dict[str, Any]:
        """The same thing the rayxel_status event carries: whether it runs, and its state."""
        answer = await _request({"cmd": "status"})
        status = answer.get("status") if answer else None
        return {"running": status is not None, "status": status}

    async def set_enabled(self, value: bool) -> dict[str, Any]:
        """
        The panel's On/Off. On starts the service first when nothing is running; Off only
        ever switches the running sender off, and never ends it.
        """
        value = bool(value)
        answer = await _request({"cmd": "set_enabled", "value": value})
        if answer is not None:
            return answer
        if not value:
            return {"ok": True}  # nothing is running, so it is off already
        failure = await self._start_service()
        if failure is not None:
            return failure
        return await _request({"cmd": "set_enabled", "value": True}) or _no_answer()

    async def set_discoverable(self, value: bool) -> dict[str, Any]:
        return await _request({"cmd": "set_discoverable", "value": bool(value)}) or _no_answer()

    async def cancel_pairing(self) -> dict[str, Any]:
        return await _request({"cmd": "cancel_pairing"}) or _no_answer()

    async def forget(self, viewer_id: str) -> dict[str, Any]:
        return await _request({"cmd": "forget", "id": str(viewer_id)}) or _no_answer()

    async def get_settings(self) -> dict[str, Any]:
        return await _request({"cmd": "get_settings"}) or _no_answer()

    async def set_settings(self, settings: dict[str, Any]) -> dict[str, Any]:
        return await _request({"cmd": "set_settings", "settings": settings}) or _no_answer()

    async def set_allow_input(self, value: bool) -> dict[str, Any]:
        """The one setting the panel switches: sent alone, so nothing else is written back over."""
        return await _request({"cmd": "set_allow_input", "value": bool(value)}) or _no_answer()

    async def get_autostart(self) -> bool:
        code, out = await _systemctl("is-enabled", SERVICE)
        return code == 0 and out.splitlines()[:1] == ["enabled"]

    async def set_autostart(self, value: bool) -> dict[str, Any]:
        """Whether the service starts with the Deck's session. Leaves a running sender as it is."""
        failure = await self._ensure_unit()
        if failure is not None:
            return failure
        code, out = await _systemctl("enable" if value else "disable", SERVICE)
        result: dict[str, Any] = {"ok": code == 0, "autostart": await self.get_autostart()}
        if code != 0:
            result["error"] = out or f"systemctl exited with {code}"
        return result

    async def stop_service(self, force: bool = False) -> dict[str, Any]:
        """
        Ends the sender program. While it holds a capture this can crash gamescope and the
        game, so without force it refuses then, answering code "needs_force" for the panel
        to ask first. A sender that does not answer cannot say whether it holds one, so that
        needs force too.
        """
        force = bool(force)
        answer = await _request({"cmd": "status"})
        if answer is None:
            if await self._service_state() not in ("active", "activating", "reloading"):
                return {"ok": True}
            if not force:
                return {"ok": False, "code": "needs_force", "captureHeld": None,
                        "error": "the sender runs but does not answer"}
            code, out = await _systemctl("stop", SERVICE)
            return {"ok": code == 0, "error": out} if code != 0 else {"ok": True}

        held = bool((answer.get("status") or {}).get("captureHeld"))
        if held and not force:
            return {"ok": False, "code": "needs_force", "captureHeld": True}
        reply = await _request({"cmd": "quit", "force": force})
        if reply is not None and reply.get("ok") is False:
            # The sender's own check: a capture was opened between the two requests.
            return {"ok": False, "code": "needs_force", "captureHeld": True, "error": reply.get("error")}

        # The sender ends by itself, cleanly, so Restart=on-failure leaves it stopped. Once
        # its process is gone, a restart systemd might still have queued is cancelled; the
        # stop signals nothing then, because nothing is left to signal.
        state = await self._wait_until_ended(8.0)
        if state == "activating":
            await _systemctl("stop", SERVICE)
        return {"ok": True}

    # Loading and unloading.

    async def _main(self) -> None:
        loop = asyncio.get_running_loop()
        self._tasks.append(loop.create_task(self._follow()))
        self._tasks.append(loop.create_task(self._install_unit_when_possible()))
        self._tasks.append(loop.create_task(self._resume_after_update()))

    async def _unload(self) -> None:
        # The sender is left exactly as it is: it is a service of its own, and ending it here
        # could take the game down. Only this backend's own tasks stop.
        for task in self._tasks:
            task.cancel()
        self._tasks.clear()

    async def _uninstall(self) -> None:
        # Called for an update as well as a real uninstall (Decky updates by reinstalling).
        # Either way the sender is switched off, not ended; an update's new copy switches it
        # back on (see _resume_after_update). After a real uninstall it stays off until the
        # next boot, when its unit finds the binary gone and does not start. Decky allows a
        # few seconds for all of this, hence the short waits.
        answer = await _request({"cmd": "status"}, timeout=1.0)
        status = answer.get("status") if answer else None
        if not isinstance(status, dict) or not status.get("enabled"):
            return
        try:
            with open(_resume_marker(), "w", encoding="utf-8") as f:
                f.write(str(time.time()))
        except OSError as e:
            decky.logger.warning(f"cannot leave the resume marker: {e}")
        await _request({"cmd": "set_enabled", "value": False}, timeout=1.0)

    # The subscription.

    async def _follow(self) -> None:
        """
        Keeps a subscribe connection to the sender and forwards every state it pushes to the
        frontend. While nothing answers it tries again every two seconds, so a sender started
        any other way (autostart, by hand over ssh) is picked up too.
        """
        while True:
            pair = await _open()
            if pair is None:
                await self._publish(None)
                await asyncio.sleep(2.0)
                continue
            reader, writer = pair
            try:
                writer.write(b'{"cmd":"subscribe"}\n')
                await writer.drain()
                while True:
                    line = await reader.readline()
                    if not line:
                        break
                    try:
                        message = json.loads(line)
                    except ValueError:
                        continue
                    if isinstance(message, dict) and message.get("event") == "status":
                        status = message.get("status")
                        if isinstance(status, dict):
                            await self._publish(status)
            except (OSError, asyncio.IncompleteReadError, asyncio.LimitOverrunError, ValueError) as e:
                decky.logger.info(f"subscription ended: {e}")
            finally:
                await _close(writer)
            await self._publish(None)
            await asyncio.sleep(1.0)

    async def _publish(self, status: dict[str, Any] | None) -> None:
        running = status is not None
        payload = {"running": running, "status": status}
        # The sender pushes a status every second even when nothing changed; only real
        # changes go on to the frontend.
        text = json.dumps(payload, sort_keys=True)
        if text == self._last_sent:
            return
        self._last_sent = text
        self._running = running
        self._status = status
        await decky.emit("rayxel_status", payload)

        pairing = status.get("pairing") if status else None
        pin = pairing.get("pin") if isinstance(pairing, dict) else None
        if pin and pin != self._pin:
            # Once per PIN: the frontend shows it in a toast when the panel is closed, so it
            # can be read over a game.
            await decky.emit("rayxel_pairing", {
                "viewerName": pairing.get("viewerName", ""),
                "pin": pin,
                "expiresMs": pairing.get("expiresMs", 0),
            })
        self._pin = pin or None

    # The service.

    async def _ensure_unit(self) -> dict[str, Any] | None:
        """Writes the unit when it is missing or out of date. None when all is well, else the failure."""
        async with self._unit_lock:
            binary = _binary_path()
            if not os.path.isfile(binary):
                return {"ok": False, "code": "no_binary", "error": f"{binary} is missing"}
            if not os.access(binary, os.X_OK):
                # A ZIP keeps no Unix modes. Decky marks the plugin's files executable after
                # unpacking, and leaves them the deck user's, so this is only a fallback.
                try:
                    os.chmod(binary, 0o755)
                except OSError as e:
                    return {"ok": False, "code": "no_binary", "error": f"{binary} is not executable: {e}"}

            text = _unit_text()
            try:
                with open(_unit_path(), encoding="utf-8") as f:
                    current: str | None = f.read()
            except OSError:
                current = None
            if current != text:
                try:
                    os.makedirs(_unit_dir(), exist_ok=True)
                    temporary = _unit_path() + ".tmp"
                    with open(temporary, "w", encoding="utf-8") as f:
                        f.write(text)
                    os.replace(temporary, _unit_path())
                except OSError as e:
                    return {"ok": False, "code": "start_failed", "error": f"cannot write {_unit_path()}: {e}"}
                self._reload_pending = True

            if self._reload_pending:
                code, out = await _systemctl("daemon-reload")
                if code != 0:
                    return {"ok": False, "code": "start_failed", "error": out or "systemctl daemon-reload failed"}
                self._reload_pending = False
            return None

    async def _install_unit_when_possible(self) -> None:
        # Decky can load before the deck user's session bus is up; keep trying for a while,
        # quietly. Starting the sender from the panel installs it as well, so nothing is lost
        # if this gives up.
        for _ in range(60):
            failure = await self._ensure_unit()
            if failure is None or failure.get("code") == "no_binary":
                if failure is not None:
                    decky.logger.error(failure["error"])
                return
            await asyncio.sleep(5.0)

    async def _start_service(self) -> dict[str, Any] | None:
        failure = await self._ensure_unit()
        if failure is not None:
            return failure
        code, out = await _systemctl("start", SERVICE)
        if code != 0:
            return {"ok": False, "code": "start_failed", "error": out or f"systemctl exited with {code}"}
        deadline = time.monotonic() + START_TIMEOUT
        while time.monotonic() < deadline:
            if await _request({"cmd": "status"}, timeout=1.0) is not None:
                return None
            await asyncio.sleep(0.3)
        state = await self._service_state()
        return {"ok": False, "code": "start_failed",
                "error": f"it did not open its control socket (the service is {state}); see journalctl --user -u {SERVICE}"}

    async def _service_state(self) -> str:
        _, out = await _systemctl("is-active", SERVICE)
        return out.splitlines()[0] if out else "unknown"

    async def _wait_until_ended(self, timeout: float) -> str:
        deadline = time.monotonic() + timeout
        state = await self._service_state()
        while state in ("active", "deactivating", "reloading") and time.monotonic() < deadline:
            await asyncio.sleep(0.25)
            state = await self._service_state()
        return state

    async def _resume_after_update(self) -> None:
        try:
            with open(_resume_marker(), encoding="utf-8") as f:
                since = float(f.read().strip() or "0")
            os.remove(_resume_marker())
        except (OSError, ValueError):
            return
        if time.time() - since > RESUME_WITHIN:
            return
        # The sender kept running through the update; wait for it to answer, then switch it
        # back on as it was.
        for _ in range(30):
            if await _request({"cmd": "set_enabled", "value": True}) is not None:
                decky.logger.info("switched the sender back on after the plugin was updated")
                return
            await asyncio.sleep(2.0)
