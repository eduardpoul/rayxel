#!/bin/sh
# Starts a PipeWire session with the stand-in for gamescope in it and runs the probe
# against that. Arguments are passed on to `rayxel-send --probe`.
set -u

export XDG_RUNTIME_DIR=/tmp/xdg
mkdir -p "$XDG_RUNTIME_DIR"
chmod 700 "$XDG_RUNTIME_DIR"

exec dbus-run-session -- sh -c '
	pipewire >/tmp/pipewire.log 2>&1 &
	sleep 1
	wireplumber >/tmp/wireplumber.log 2>&1 &
	sleep 1
	/work/build/fake-gamescope ${FAKE_ARGS:-} >/tmp/fake.log 2>&1 &
	fake=$!
	sleep 1

	/work/build/rayxel-send --probe "$@"
	status=$?

	kill -INT "$fake" 2>/dev/null
	sleep 0.3
	echo
	echo "--- fake-gamescope ---"
	cat /tmp/fake.log
	if [ "${SHOW_LOGS:-0}" = 1 ]; then
		echo "--- pipewire ---"; cat /tmp/pipewire.log
		echo "--- wireplumber ---"; cat /tmp/wireplumber.log
	fi
	exit $status
' sh "$@"
