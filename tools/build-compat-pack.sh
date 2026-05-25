#!/usr/bin/env bash
# Build a standalone STS2 Android compatibility package zip.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
DOTNET_BIN="${DOTNET_BIN:-$ROOT/../s2/.local/dotnet/dotnet}"
if [[ ! -x "$DOTNET_BIN" && -x "/mnt/datas/agent_workspace/s2/.local/dotnet/dotnet" ]]; then
  DOTNET_BIN="/mnt/datas/agent_workspace/s2/.local/dotnet/dotnet"
fi
REFERENCE_FLAVOR="${REFERENCE_FLAVOR:-original-v0.106.1}"
MANIFEST="${COMPAT_MANIFEST:-$ROOT/compat_manifest.v0.106.1-beta.json}"
PROJECT="$ROOT/STS2AndroidPortCompat/STS2Mobile.csproj"
OUT_ROOT="$ROOT/dist/compat-pack"
PACK_ID="$(python3 - <<'PY' "$MANIFEST"
import json, sys
print(json.load(open(sys.argv[1], encoding='utf-8'))['pack_id'])
PY
)"
if [[ ! -x "$DOTNET_BIN" ]]; then
  echo "Missing dotnet: $DOTNET_BIN" >&2
  exit 1
fi
rm -rf "$OUT_ROOT"
mkdir -p "$OUT_ROOT/$PACK_ID"
"$DOTNET_BIN" build "$PROJECT" -p:ReferenceFlavor="$REFERENCE_FLAVOR" -v:q
cp -f "$ROOT/STS2AndroidPortCompat/bin/Debug/net9.0/STS2Mobile.dll" "$OUT_ROOT/$PACK_ID/STS2Mobile.dll"
"$ROOT/tools/make-port-overlay-pck.py" "$OUT_ROOT/$PACK_ID/port_compat.pck"
cp -f "$MANIFEST" "$OUT_ROOT/$PACK_ID/compat_manifest.json"
(
  cd "$OUT_ROOT/$PACK_ID"
  sha256sum STS2Mobile.dll port_compat.pck > SHA256SUMS
)
(
  cd "$OUT_ROOT"
  zip -qr "$PACK_ID.zip" "$PACK_ID"
)
sha256sum "$OUT_ROOT/$PACK_ID.zip"
echo "Compat pack: $OUT_ROOT/$PACK_ID.zip"
