#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
PARENT_ROOT="$(cd "$ROOT/.." && pwd -P)"

if [[ -f "$PARENT_ROOT/tools/env/load-local-config.sh" ]]; then
  # shellcheck disable=SC1091
  source "$PARENT_ROOT/tools/env/load-local-config.sh"
  sts2_load_dotenv
fi

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
  --project "$ROOT/tests/ExtendedMultiplayerRoom.Tests/Runner/Runner.csproj" \
  --configuration Release
