#!/usr/bin/env bash
# Compila desde WSL llamando a build.cmd en una copia en el disco de Windows (cmd.exe no acepta rutas \\wsl$).
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
win_tmp="$(wslpath "$(cmd.exe /c 'echo %TEMP%' 2>/dev/null | tr -d '\r')")/classic-forever-build"
rm -rf "$win_tmp"; mkdir -p "$win_tmp"
cp -r "$here/src" "$here/build.cmd" "$win_tmp/"
(cd "$win_tmp" && cmd.exe /c build.cmd)
mkdir -p "$here/dist"
cp "$win_tmp/dist/ClassicForever.exe" "$here/dist/"
sha256sum "$here/dist/ClassicForever.exe"
