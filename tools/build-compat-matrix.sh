#!/usr/bin/env bash
# Build a flattened STS2 Android compatibility family zip from one checkout.
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
elif [[ -f "$ROOT/.env" ]]; then
  set -a
  # shellcheck disable=SC1091
  source "$ROOT/.env"
  set +a
fi

DOTNET_BIN="${DOTNET_BIN:-dotnet}"
PROJECT="$ROOT/STS2AndroidPortCompat/STS2Mobile.csproj"
TARGET_ROOT="${COMPAT_TARGET_ROOT:-$ROOT/targets/active}"
ARCHIVED_TARGET_ROOT="${COMPAT_ARCHIVED_TARGET_ROOT:-$ROOT/targets/archived}"
OUT_ROOT="${COMPAT_MATRIX_OUT_ROOT:-$ROOT/dist/compat-matrix}"
PACK_ID="${COMPAT_MATRIX_PACK_ID:-sts2-android-compat}"
DISPLAY_NAME="${COMPAT_MATRIX_DISPLAY_NAME:-STS2 Android Compatibility}"
COMPAT_VERSION="${COMPAT_MATRIX_VERSION:-0.4.0-dev}"
CHANNEL="${COMPAT_MATRIX_CHANNEL:-mixed}"
TARGET_FILTER=""
INCLUDE_ARCHIVED="${BUILD_ARCHIVED_TARGETS:-0}"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --target)
      TARGET_FILTER="${2:-}"
      shift 2
      ;;
    --include-archived)
      INCLUDE_ARCHIVED=1
      shift
      ;;
    --out)
      OUT_ROOT="${2:-}"
      shift 2
      ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 1
      ;;
  esac
done

if [[ "$DOTNET_BIN" != "dotnet" && ! -x "$DOTNET_BIN" ]]; then
  echo "Missing dotnet: $DOTNET_BIN" >&2
  exit 1
fi
if [[ ! -f "$PROJECT" ]]; then
  echo "Missing project: $PROJECT" >&2
  exit 1
fi

resolve_path() {
  local value="$1"
  [[ -n "$value" ]] || return 0
  if command -v sts2_resolve_path >/dev/null 2>&1; then
    sts2_resolve_path "$value"
    return
  fi
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

compat_reference_dir_for_flavor() {
  local flavor="$1"
  if command -v sts2_compat_reference_dir_for_flavor >/dev/null 2>&1; then
    sts2_compat_reference_dir_for_flavor "$flavor"
    return
  fi
  case "$flavor" in
    original)
      resolve_path "${STS2_ORIGINAL_V103_REFERENCE_DIR:-$ROOT/refs/original}"
      ;;
    original-v0.106.1)
      resolve_path "${STS2_ORIGINAL_V1061_REFERENCE_DIR:-$ROOT/refs/original-v0.106.1}"
      ;;
    original-v0.107.0)
      resolve_path "${STS2_ORIGINAL_V1070_REFERENCE_DIR:-$ROOT/refs/original-v0.107.0}"
      ;;
    original-v0.107.1)
      resolve_path "${STS2_ORIGINAL_V1071_REFERENCE_DIR:-$ROOT/refs/original-v0.107.1}"
      ;;
    original-v0.108.0)
      resolve_path "${STS2_ORIGINAL_V1080_REFERENCE_DIR:-$ROOT/refs/original-v0.108.0}"
      ;;
    runtime|*)
      resolve_path "${STS2_RUNTIME_REFERENCE_DIR:-}"
      ;;
  esac
}

json_value() {
  python3 - "$1" "$2" <<'PY'
import json, sys
path, key = sys.argv[1:]
data = json.load(open(path, encoding="utf-8"))
value = data
for part in key.split("."):
    value = value.get(part) if isinstance(value, dict) else None
if isinstance(value, list):
    print(";".join(str(item) for item in value))
elif value is None:
    print("")
else:
    print(value)
PY
}

collect_target_files() {
  if [[ -d "$TARGET_ROOT" ]]; then
    find "$TARGET_ROOT" -mindepth 2 -maxdepth 2 -name target.json -print
  fi
  if [[ "$INCLUDE_ARCHIVED" == "1" && -d "$ARCHIVED_TARGET_ROOT" ]]; then
    find "$ARCHIVED_TARGET_ROOT" -mindepth 2 -maxdepth 2 -name target.json -print
  fi
}

mapfile -t TARGET_FILES < <(collect_target_files | sort)
if [[ ${#TARGET_FILES[@]} -eq 0 ]]; then
  echo "No compat targets found under $TARGET_ROOT" >&2
  exit 1
fi

rm -rf "$OUT_ROOT"
mkdir -p "$OUT_ROOT/$PACK_ID/variants"
TARGETS_JSONL="$OUT_ROOT/targets.jsonl"
: > "$TARGETS_JSONL"

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

for target_file in "${TARGET_FILES[@]}"; do
  target_id="$(json_value "$target_file" target_id)"
  if [[ -n "$TARGET_FILTER" && "$target_id" != "$TARGET_FILTER" ]]; then
    continue
  fi
  reference_flavor="$(json_value "$target_file" reference_flavor)"
  compile_constants="$(json_value "$target_file" compile_constants)"
  reference_dir="$(compat_reference_dir_for_flavor "$reference_flavor")"
  if [[ -z "$reference_dir" || ! -f "$reference_dir/sts2.dll" ]]; then
    echo "Missing compat reference sts2.dll for target=$target_id ReferenceFlavor=$reference_flavor: $reference_dir" >&2
    exit 1
  fi

  echo "Building compat target $target_id (ReferenceFlavor=$reference_flavor)"
  msbuild_args=(
    "$PROJECT"
    "-p:ReferenceFlavor=$reference_flavor"
    "-p:CompatReferenceDir=$reference_dir"
    "-p:_CompatGitBranch=$GIT_BRANCH"
    "-p:_CompatGitCommit=$GIT_COMMIT"
    "-p:_CompatGitCommitSubject=$GIT_SUBJECT"
    "-p:_CompatGitDirty=$GIT_DIRTY"
    "-p:_CompatBuildTimestampUtc=$BUILD_TIMESTAMP_UTC"
    "-v:q"
  )
  if [[ -n "$compile_constants" ]]; then
    msbuild_args+=("-p:DefineConstants=$compile_constants")
  fi
  "$DOTNET_BIN" build "${msbuild_args[@]}"

  target_out="$OUT_ROOT/$PACK_ID/variants/$target_id"
  mkdir -p "$target_out"
  cp -f "$ROOT/STS2AndroidPortCompat/bin/Debug/net9.0/STS2Mobile.dll" "$target_out/STS2Mobile.dll"
  "$ROOT/tools/make-port-overlay-pck.py" "$target_out/port_compat.pck" >/dev/null

  python3 - "$target_file" "$target_id" <<'PY' >> "$TARGETS_JSONL"
import json, sys
path, target_id = sys.argv[1:]
data = json.load(open(path, encoding="utf-8"))
versions = data.get("versions") or []
entry = {
    "target_id": target_id,
    "status": data.get("status", "active"),
    "display_name": data.get("display_name", target_id),
    "version": versions[0] if versions else data.get("version", ""),
    "versions": versions,
    "channel": data.get("channel", ""),
    "source": data.get("source", ""),
    "sts2_dll_sha256": data.get("sts2_dll_sha256", ""),
    "reference_flavor": data.get("reference_flavor", ""),
    "artifacts": {
        "dll": f"variants/{target_id}/STS2Mobile.dll",
        "overlay_pck": f"variants/{target_id}/port_compat.pck",
    },
    "notes": data.get("notes", []),
}
print(json.dumps(entry, ensure_ascii=False, separators=(",", ":")))
PY
done

if [[ ! -s "$TARGETS_JSONL" ]]; then
  echo "No compat targets matched filter: ${TARGET_FILTER:-<none>}" >&2
  exit 1
fi

python3 - "$OUT_ROOT/$PACK_ID/compat_manifest.json" "$TARGETS_JSONL" "$PACK_ID" "$DISPLAY_NAME" "$COMPAT_VERSION" "$CHANNEL" "$GIT_BRANCH" "$GIT_COMMIT" "$GIT_SUBJECT" "$GIT_DIRTY" "$BUILD_TIMESTAMP_UTC" <<'PY'
import json, sys
manifest_path, targets_path, pack_id, display_name, compat_version, channel, branch, commit, subject, dirty, built_at = sys.argv[1:]
targets = [json.loads(line) for line in open(targets_path, encoding="utf-8") if line.strip()]
data = {
    "schema": 2,
    "pack_id": pack_id,
    "display_name": display_name,
    "compat_version": compat_version,
    "channel": channel,
    "targets": targets,
    "runtime": {
        "entry_assembly": "STS2Mobile.dll",
        "entry_type": "STS2Mobile.ModEntry",
        "entry_method": "Apply",
    },
    "resources": {
        "overlay_pck": "port_compat.pck",
    },
    "build_info": {
        "branch": branch,
        "commit": commit,
        "subject": subject,
        "dirty": dirty,
        "built_utc": built_at,
        "layout": "flat-matrix",
    },
    "notes": [
        "Schema 2 family compatibility pack built from a single source checkout.",
        "Each targets[] entry points at its own compiled DLL and overlay variant.",
    ],
}
with open(manifest_path, "w", encoding="utf-8") as fh:
    json.dump(data, fh, ensure_ascii=False, indent=2)
    fh.write("\n")
PY

(
  cd "$OUT_ROOT/$PACK_ID"
  find compat_manifest.json variants -type f -print | sort | xargs sha256sum > SHA256SUMS
)
(
  cd "$OUT_ROOT"
  zip -qr "$PACK_ID.zip" "$PACK_ID"
)
sha256sum "$OUT_ROOT/$PACK_ID.zip"
echo "Compat family pack: $OUT_ROOT/$PACK_ID.zip"
