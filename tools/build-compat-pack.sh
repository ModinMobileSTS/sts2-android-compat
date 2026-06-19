#!/usr/bin/env bash
# Build a standalone STS2 Android compatibility package zip.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
REPO_ROOT="$ROOT"
if [[ -f "$ROOT/../tools/env/load-local-config.sh" ]]; then
  REPO_ROOT="$(cd "$ROOT/.." && pwd -P)"
  STS2_RE_ROOT="$REPO_ROOT"
  export STS2_RE_ROOT
  # shellcheck disable=SC1091
  source "$REPO_ROOT/tools/env/load-local-config.sh"
  sts2_init_env
else
  if [[ -f "$ROOT/.env" ]]; then
    set -a
    # shellcheck disable=SC1091
    source "$ROOT/.env"
    set +a
  fi
  _sts2_abs() {
    local value="$1"
    [[ -n "$value" ]] || return 0
    python3 - "$ROOT" "$value" <<'PY'
from pathlib import Path
import os, sys
root = Path(sys.argv[1])
value = os.path.expandvars(os.path.expanduser(sys.argv[2]))
path = Path(value)
if not path.is_absolute():
    path = root / path
print(path.resolve(strict=False))
PY
  }
  if [[ -n "${STS2_REFERENCE_ROOT:-}" ]]; then
    STS2_REFERENCE_ROOT="$(_sts2_abs "$STS2_REFERENCE_ROOT")"
    DOTNET_BIN="${DOTNET_BIN:-$STS2_REFERENCE_ROOT/.local/dotnet/dotnet}"
    STS2_RUNTIME_REFERENCE_DIR="${STS2_RUNTIME_REFERENCE_DIR:-$STS2_REFERENCE_ROOT/.cache/StS2-Launcher_Mod_Manager/upstream/godot-export/.godot/mono/publish/arm64}"
  fi
  if [[ -n "${DOTNET_BIN:-}" ]]; then DOTNET_BIN="$(_sts2_abs "$DOTNET_BIN")"; fi
  if [[ -n "${STS2_RUNTIME_REFERENCE_DIR:-}" ]]; then STS2_RUNTIME_REFERENCE_DIR="$(_sts2_abs "$STS2_RUNTIME_REFERENCE_DIR")"; fi
  if [[ -n "${STS2_ORIGINAL_V103_ROOT:-}" ]]; then
    STS2_ORIGINAL_V103_ROOT="$(_sts2_abs "$STS2_ORIGINAL_V103_ROOT")"
    STS2_ORIGINAL_V103_REFERENCE_DIR="${STS2_ORIGINAL_V103_REFERENCE_DIR:-$STS2_ORIGINAL_V103_ROOT/.godot/mono/temp/bin/Debug}"
  fi
  if [[ -n "${STS2_ORIGINAL_V1061_ROOT:-}" ]]; then
    STS2_ORIGINAL_V1061_ROOT="$(_sts2_abs "$STS2_ORIGINAL_V1061_ROOT")"
    STS2_ORIGINAL_V1061_REFERENCE_DIR="${STS2_ORIGINAL_V1061_REFERENCE_DIR:-$STS2_ORIGINAL_V1061_ROOT/.godot/mono/temp/bin/Debug}"
  fi
  if [[ -n "${STS2_ORIGINAL_V1070_ROOT:-}" ]]; then
    STS2_ORIGINAL_V1070_ROOT="$(_sts2_abs "$STS2_ORIGINAL_V1070_ROOT")"
    STS2_ORIGINAL_V1070_REFERENCE_DIR="${STS2_ORIGINAL_V1070_REFERENCE_DIR:-$STS2_ORIGINAL_V1070_ROOT/.godot/mono/temp/bin/Debug}"
  fi
  if [[ -n "${STS2_ORIGINAL_V1071_ROOT:-}" ]]; then
    STS2_ORIGINAL_V1071_ROOT="$(_sts2_abs "$STS2_ORIGINAL_V1071_ROOT")"
    STS2_ORIGINAL_V1071_REFERENCE_DIR="${STS2_ORIGINAL_V1071_REFERENCE_DIR:-$STS2_ORIGINAL_V1071_ROOT/.godot/mono/temp/bin/Debug}"
  fi
  if [[ -n "${STS2_ORIGINAL_V103_REFERENCE_DIR:-}" ]]; then STS2_ORIGINAL_V103_REFERENCE_DIR="$(_sts2_abs "$STS2_ORIGINAL_V103_REFERENCE_DIR")"; fi
  if [[ -n "${STS2_ORIGINAL_V1061_REFERENCE_DIR:-}" ]]; then STS2_ORIGINAL_V1061_REFERENCE_DIR="$(_sts2_abs "$STS2_ORIGINAL_V1061_REFERENCE_DIR")"; fi
  if [[ -n "${STS2_ORIGINAL_V1070_REFERENCE_DIR:-}" ]]; then STS2_ORIGINAL_V1070_REFERENCE_DIR="$(_sts2_abs "$STS2_ORIGINAL_V1070_REFERENCE_DIR")"; fi
  if [[ -n "${STS2_ORIGINAL_V1071_REFERENCE_DIR:-}" ]]; then STS2_ORIGINAL_V1071_REFERENCE_DIR="$(_sts2_abs "$STS2_ORIGINAL_V1071_REFERENCE_DIR")"; fi
fi

DOTNET_BIN="${DOTNET_BIN:-dotnet}"
REFERENCE_FLAVOR="${REFERENCE_FLAVOR:-original-v0.107.0}"
if [[ -z "${COMPAT_REFERENCE_DIR:-}" ]]; then
  case "$REFERENCE_FLAVOR" in
    original)
      COMPAT_REFERENCE_DIR="${STS2_ORIGINAL_V103_REFERENCE_DIR:-$ROOT/refs/original}"
      ;;
    original-v0.106.1)
      COMPAT_REFERENCE_DIR="${STS2_ORIGINAL_V1061_REFERENCE_DIR:-$ROOT/refs/original-v0.106.1}"
      ;;
    original-v0.107.0)
      COMPAT_REFERENCE_DIR="${STS2_ORIGINAL_V1070_REFERENCE_DIR:-$ROOT/refs/original-v0.107.0}"
      ;;
    original-v0.107.1)
      COMPAT_REFERENCE_DIR="${STS2_ORIGINAL_V1071_REFERENCE_DIR:-$ROOT/refs/original-v0.107.1}"
      ;;
    runtime|*)
      COMPAT_REFERENCE_DIR="${STS2_RUNTIME_REFERENCE_DIR:-}"
      ;;
  esac
fi
if command -v sts2_resolve_path >/dev/null 2>&1; then
  COMPAT_REFERENCE_DIR="$(sts2_resolve_path "$COMPAT_REFERENCE_DIR")"
elif [[ -n "$COMPAT_REFERENCE_DIR" && "$COMPAT_REFERENCE_DIR" != /* ]]; then
  COMPAT_REFERENCE_DIR="$(cd "$ROOT" && python3 - "$COMPAT_REFERENCE_DIR" <<'PY'
from pathlib import Path
import os, sys
print(Path(os.path.expandvars(os.path.expanduser(sys.argv[1]))).resolve(strict=False))
PY
)"
fi

MANIFEST="${COMPAT_MANIFEST:-$ROOT/compat_manifest.v0.107.0-beta.json}"
PROJECT="$ROOT/STS2AndroidPortCompat/STS2Mobile.csproj"
OUT_ROOT="$ROOT/dist/compat-pack"
PACK_ID="$(python3 - <<'PY' "$MANIFEST"
import json, sys
print(json.load(open(sys.argv[1], encoding='utf-8'))['pack_id'])
PY
)"
if [[ "$DOTNET_BIN" != "dotnet" && ! -x "$DOTNET_BIN" ]]; then
  echo "Missing dotnet: $DOTNET_BIN" >&2
  exit 1
fi
if [[ -z "$COMPAT_REFERENCE_DIR" || ! -f "$COMPAT_REFERENCE_DIR/sts2.dll" ]]; then
  echo "Missing compat reference sts2.dll for ReferenceFlavor=$REFERENCE_FLAVOR: $COMPAT_REFERENCE_DIR" >&2
  exit 1
fi
rm -rf "$OUT_ROOT"
mkdir -p "$OUT_ROOT/$PACK_ID"
GIT_BRANCH="${COMPAT_BUILD_GIT_BRANCH:-$(git -C "$ROOT" branch --show-current 2>/dev/null || true)}"
if [[ -z "$GIT_BRANCH" ]]; then
  GIT_BRANCH="$(git -C "$ROOT" rev-parse --abbrev-ref HEAD 2>/dev/null || echo unknown)"
fi
GIT_COMMIT="${COMPAT_BUILD_GIT_COMMIT:-$(git -C "$ROOT" rev-parse --short=12 HEAD 2>/dev/null || echo unknown)}"
GIT_SUBJECT="${COMPAT_BUILD_GIT_SUBJECT:-$(git -C "$ROOT" log -1 --pretty=%s 2>/dev/null || echo unknown)}"
GIT_DIRTY="${COMPAT_BUILD_GIT_DIRTY:-false}"
if [[ "${COMPAT_BUILD_GIT_DIRTY:-}" == "" ]]; then
  if ! git -C "$ROOT" diff --quiet --ignore-submodules -- 2>/dev/null || ! git -C "$ROOT" diff --cached --quiet --ignore-submodules -- 2>/dev/null || [[ -n "$(git -C "$ROOT" ls-files --others --exclude-standard 2>/dev/null)" ]]; then
    GIT_DIRTY="true"
  fi
fi
BUILD_TIMESTAMP_UTC="${COMPAT_BUILD_TIMESTAMP_UTC:-$(date -u +%Y-%m-%dT%H:%M:%SZ)}"
"$DOTNET_BIN" build "$PROJECT" -p:ReferenceFlavor="$REFERENCE_FLAVOR" -p:CompatReferenceDir="$COMPAT_REFERENCE_DIR" -p:_CompatGitBranch="$GIT_BRANCH" -p:_CompatGitCommit="$GIT_COMMIT" -p:_CompatGitCommitSubject="$GIT_SUBJECT" -p:_CompatGitDirty="$GIT_DIRTY" -p:_CompatBuildTimestampUtc="$BUILD_TIMESTAMP_UTC" -v:q
cp -f "$ROOT/STS2AndroidPortCompat/bin/Debug/net9.0/STS2Mobile.dll" "$OUT_ROOT/$PACK_ID/STS2Mobile.dll"
"$ROOT/tools/make-port-overlay-pck.py" "$OUT_ROOT/$PACK_ID/port_compat.pck"
cp -f "$MANIFEST" "$OUT_ROOT/$PACK_ID/compat_manifest.json"
python3 - <<'PYMANIFEST' "$OUT_ROOT/$PACK_ID/compat_manifest.json" "$GIT_BRANCH" "$GIT_COMMIT" "$GIT_SUBJECT" "$GIT_DIRTY" "$BUILD_TIMESTAMP_UTC"
import json, sys
path, branch, commit, subject, dirty, built_at = sys.argv[1:]
with open(path, encoding="utf-8") as fh:
    data = json.load(fh)
data["build_info"] = {
    "branch": branch,
    "commit": commit,
    "subject": subject,
    "dirty": dirty,
    "built_utc": built_at,
}
with open(path, "w", encoding="utf-8") as fh:
    json.dump(data, fh, ensure_ascii=False, indent=2)
    fh.write("\n")
PYMANIFEST
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
