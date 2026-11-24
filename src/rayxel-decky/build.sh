#!/bin/sh
# Builds the Decky plugin: out/Rayxel/ as Decky lays a plugin out, and out/Rayxel.zip for
# Decky's "Install plugin from ZIP".
#
#   ./build.sh [SENDER [PYROWAVE]]
#
# SENDER is the rayxel-send binary for Linux (NativeAOT, one file), PYROWAVE the PyroWave
# library it loads from beside itself. Either can also come from RAYXEL_SENDER and
# RAYXEL_PYROWAVE. Without PyroWave's library the plugin still works; the sender then
# offers HEVC and AV1 only and says why.
#
# The frontend is built with pnpm when it is on PATH, otherwise in a node:22 container.
# Works from Linux and from Git Bash on Windows.

set -eu

here=$(cd "$(dirname "$0")" && pwd)
sender=${1:-${RAYXEL_SENDER:-$here/../Rayxel.Send/out/linux-x64/rayxel-send}}
pyrowave=${2:-${RAYXEL_PYROWAVE:-$here/../Rayxel.Send/out/linux-x64/libpyrowave-shared.so.1}}

if [ ! -f "$sender" ]; then
	echo "build.sh: no sender at $sender - build rayxel-send for linux-x64 first, or pass its path" >&2
	exit 1
fi
if [ ! -f "$pyrowave" ]; then
	echo "build.sh: no PyroWave library at $pyrowave - packing without it (no PyroWave on the Deck)" >&2
	pyrowave=""
fi

# The frontend: dist/index.js.
cd "$here"
if command -v pnpm >/dev/null 2>&1; then
	pnpm install --frozen-lockfile
	pnpm build
elif command -v docker >/dev/null 2>&1; then
	if command -v cygpath >/dev/null 2>&1; then
		# Git Bash: Docker wants a Windows path for the mount, and MSYS must not rewrite
		# the container's own paths into Windows ones.
		mount=$(cygpath -w "$here")
		export MSYS_NO_PATHCONV=1
		user=""
	else
		# Linux: build as the caller, so node_modules and dist are not left owned by root.
		mount=$here
		user="--user $(id -u):$(id -g)"
	fi
	# shellcheck disable=SC2086 # $user is empty or two words on purpose
	docker run --rm $user -v "$mount:/w" -w /w \
		-e HOME=/tmp -e COREPACK_HOME=/tmp/corepack -e COREPACK_ENABLE_DOWNLOAD_PROMPT=0 \
		node:22 sh -c "corepack pnpm install --frozen-lockfile && corepack pnpm build"
else
	echo "build.sh: needs pnpm, or docker to build the frontend in a container" >&2
	exit 1
fi

# The plugin folder, as Decky expects it under ~/homebrew/plugins.
out=$here/out
stage=$out/Rayxel
rm -rf "$stage" "$out/Rayxel.zip"
mkdir -p "$stage/dist" "$stage/bin"
cp "$here/plugin.json" "$here/package.json" "$here/main.py" "$here/README.md" "$stage/"
cp "$here/../../LICENSE" "$stage/LICENSE"
cp "$here/dist/index.js" "$stage/dist/index.js"
cp "$sender" "$stage/bin/rayxel-send"
chmod 755 "$stage/bin/rayxel-send"
if [ -n "$pyrowave" ]; then
	cp "$pyrowave" "$stage/bin/libpyrowave-shared.so.1"
	chmod 644 "$stage/bin/libpyrowave-shared.so.1"
fi

# The ZIP, with Rayxel/ at its root. Decky unpacks with Python's zipfile, which keeps no
# Unix modes, and then marks every file of the plugin 755 itself, so the archive's own
# modes do not matter. Whichever zipper is at hand is used.
cd "$out"
if command -v zip >/dev/null 2>&1; then
	zip -qr -X Rayxel.zip Rayxel
elif command -v python3 >/dev/null 2>&1 && python3 -c "import zipfile" >/dev/null 2>&1; then
	python3 -m zipfile -c Rayxel.zip Rayxel
elif command -v python >/dev/null 2>&1 && python -c "import zipfile" >/dev/null 2>&1; then
	python -m zipfile -c Rayxel.zip Rayxel
elif [ -x /c/Windows/System32/tar.exe ]; then
	# Windows' own tar is bsdtar, which writes a ZIP when the name ends in .zip.
	/c/Windows/System32/tar.exe -a -cf Rayxel.zip Rayxel
else
	echo "build.sh: needs zip, Python or bsdtar to write the ZIP; out/Rayxel/ is ready as it is" >&2
	exit 1
fi

echo "build.sh: $out/Rayxel.zip"
