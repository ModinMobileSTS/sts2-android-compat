#!/usr/bin/env bash
set -euo pipefail

# Obtain the official Spine 4.2 / Godot 4.5.1 GDExtension separately, subject to
# https://esotericsoftware.com/spine-runtimes-license. Do not commit its binaries.
# Required: .NET 9 SDK, Godot 4.5.1 mono, real OpenGL rendering (Mesa works),
# and Xvfb when no DISPLAY is available. No commercial game files are used.
: "${GODOT:?Set GODOT to the Godot 4.5.1 mono executable}"
: "${DOTNET:?Set DOTNET to the .NET SDK executable}"
: "${SPINE_EXTENSION_DIR:?Set SPINE_EXTENSION_DIR to the official archive bin directory}"

export DOTNET_ROOT="$(dirname "$(realpath "$DOTNET")")"
export PATH="$DOTNET_ROOT:$PATH"
project="$(dirname "$(realpath "$0")")"
"$DOTNET" build "$project/SpineWarmup.Tests.csproj" "/p:SpineExtensionDir=$SPINE_EXTENSION_DIR"
# Importing discovers the staged GDExtension and GDScript sentinels. This is not
# the regression run: --headless must never substitute for actual pixel checks.
"$GODOT" --headless --editor --path "$project" --import
args=("$GODOT" --path "$project" --rendering-method gl_compatibility --rendering-driver opengl3 --audio-driver Dummy -- "$@")
if [[ -n "${DISPLAY:-}" ]]; then
    "${args[@]}"
else
    LIBGL_ALWAYS_SOFTWARE=1 xvfb-run -a -s '-screen 0 1024x768x24' "${args[@]}"
fi
