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
GIT_BRANCH="$(git -C "$ROOT" branch --show-current 2>/dev/null || true)"
if [[ -z "$GIT_BRANCH" ]]; then
  GIT_BRANCH="$(git -C "$ROOT" rev-parse --abbrev-ref HEAD 2>/dev/null || echo unknown)"
fi
GIT_COMMIT="$(git -C "$ROOT" rev-parse --short=12 HEAD 2>/dev/null || echo unknown)"
GIT_SUBJECT="$(git -C "$ROOT" log -1 --pretty=%s 2>/dev/null || echo unknown)"
GIT_DIRTY="false"
if ! git -C "$ROOT" diff --quiet --ignore-submodules -- 2>/dev/null || ! git -C "$ROOT" diff --cached --quiet --ignore-submodules -- 2>/dev/null || [[ -n "$(git -C "$ROOT" ls-files --others --exclude-standard 2>/dev/null)" ]]; then
  GIT_DIRTY="true"
fi
BUILD_TIMESTAMP_UTC="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
"$DOTNET_BIN" build "$PROJECT" -p:ReferenceFlavor="$REFERENCE_FLAVOR" -p:_CompatGitBranch="$GIT_BRANCH" -p:_CompatGitCommit="$GIT_COMMIT" -p:_CompatGitCommitSubject="$GIT_SUBJECT" -p:_CompatGitDirty="$GIT_DIRTY" -p:_CompatBuildTimestampUtc="$BUILD_TIMESTAMP_UTC" -v:q
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
