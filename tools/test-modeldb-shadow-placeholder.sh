#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
PARENT_ROOT="$(cd "$ROOT/.." && pwd -P)"
REFERENCE_DIR="${HARMONY_REFERENCE_DIR:-}"

usage() {
  cat <<'USAGE'
Usage: tools/test-modeldb-shadow-placeholder.sh [--reference-dir DIR]

Verifies that early vanilla model placeholders resolve through shadow lookup
methods while ModelDb's canonical content dictionary remains unchanged until
phase 1 publication.
USAGE
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --reference-dir)
      [[ $# -ge 2 ]] || { usage >&2; exit 2; }
      REFERENCE_DIR="$2"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

if [[ -f "$PARENT_ROOT/tools/env/load-local-config.sh" ]]; then
  # shellcheck disable=SC1091
  source "$PARENT_ROOT/tools/env/load-local-config.sh"
  sts2_load_dotenv
fi

if [[ -z "$REFERENCE_DIR" ]]; then
  for candidate in \
    "${STS2_ORIGINAL_V1071_REFERENCE_DIR:-}" \
    "${STS2_ORIGINAL_V1080_REFERENCE_DIR:-}" \
    "${STS2_ORIGINAL_V1090_REFERENCE_DIR:-}" \
    "${STS2_ORIGINAL_V1100_REFERENCE_DIR:-}" \
    "${STS2_ORIGINAL_V1110_REFERENCE_DIR:-}" \
    "$PARENT_ROOT/android/assets/dotnet_bcl"; do
    if [[ -n "$candidate" && -f "$candidate/0Harmony.dll" && -f "$candidate/sts2.dll" ]]; then
      REFERENCE_DIR="$candidate"
      break
    fi
  done
fi

if [[ -z "$REFERENCE_DIR" || ! -f "$REFERENCE_DIR/0Harmony.dll" || ! -f "$REFERENCE_DIR/sts2.dll" ]]; then
  echo "Unable to find 0Harmony.dll and sts2.dll; pass --reference-dir DIR." >&2
  exit 1
fi
REFERENCE_DIR="$(cd "$REFERENCE_DIR" && pwd -P)"

DOTNET="${DOTNET_BIN:-}"
if [[ -z "$DOTNET" && -x "$PARENT_ROOT/.agent/dotnet/dotnet" ]]; then
  DOTNET="$PARENT_ROOT/.agent/dotnet/dotnet"
fi
if [[ -z "$DOTNET" ]]; then
  DOTNET="$(command -v dotnet || true)"
fi
if [[ -z "$DOTNET" || ! -x "$DOTNET" ]]; then
  echo "Unable to find a .NET 9 SDK; set DOTNET_BIN." >&2
  exit 1
fi

"$DOTNET" run \
  --project "$ROOT/tests/ModelDbShadowPlaceholder.Tests/Runner/Runner.csproj" \
  --configuration Release \
  "-p:CompatReferenceDir=$REFERENCE_DIR"
