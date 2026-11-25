#!/usr/bin/env bash
# Build and verify the compatibility family locally; upload only the release assets.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
TOOLS="$ROOT/tools"
PARENT="$(dirname "$ROOT")"
REPO="ModinMobileSTS/sts2-android-compat"
PACK="sts2-android-compat"
ASSETS=("$PACK.zip" compat-index.json compat-index.json.sig compat_manifest.json SHA256SUMS)

usage() {
  cat <<'USAGE'
Usage: publish-compat-release.sh --tag vX.Y.Z --revision N [options]
  --version X.Y.Z                     Must match --tag; either may be inferred from the other.
                                      Prerelease suffixes such as -rc.1 are supported.
  --min-launcher-version-code N       COMPAT_MIN_LAUNCHER_VERSION_CODE or 115 by default.
  --min-launcher-version-name NAME    Optional; defaults to COMPAT_MIN_LAUNCHER_VERSION_NAME.
  --notes-file PATH                    Use this UTF-8 file for both index changelog and GitHub notes.
  --previous-index PATH                Merge a verified local history instead of fetching history.
  --previous-index-sig PATH            Defaults to the --previous-index path followed by .sig.
  --signing-key-file PATH              Override COMPAT_RELEASE_SIGNING_KEY_FILE (external Ed25519 PEM).
  --signing-key-id ID                  Override COMPAT_RELEASE_SIGNING_KEY_ID (sts2-compat-release-v1).
  --prerelease                         Never replace GitHub's stable /releases/latest endpoint.
  --dry-run                            Local build and self-verification only; no GitHub calls.
  --skip-tests                         Explicitly skip the existing pre-build regression scripts.
  --allow-dirty                        Permit dirty checkouts (only with --dry-run).
USAGE
}

die() {
  printf 'publish-compat-release: %s\n' "$*" >&2
  exit 1
}

tag=""
version=""
revision=""
min_code=""
min_name=""
notes_file=""
previous_index=""
previous_sig=""
key_file=""
key_id=""
prerelease=0
dry_run=0
skip_tests=0
allow_dirty=0
while (( $# )); do
  case "$1" in
    --tag|--version|--revision|--min-launcher-version-code|--min-launcher-version-name|--notes-file|--previous-index|--previous-index-sig|--signing-key-file|--signing-key-id)
      [[ $# -ge 2 && -n "$2" && "$2" != --* ]] || die "Missing value for $1"
      case "$1" in
        --tag) tag="$2" ;;
        --version) version="$2" ;;
        --revision) revision="$2" ;;
        --min-launcher-version-code) min_code="$2" ;;
        --min-launcher-version-name) min_name="$2" ;;
        --notes-file) notes_file="$2" ;;
        --previous-index) previous_index="$2" ;;
        --previous-index-sig) previous_sig="$2" ;;
        --signing-key-file) key_file="$2" ;;
        --signing-key-id) key_id="$2" ;;
      esac
      shift 2 ;;
    --prerelease) prerelease=1; shift ;;
    --dry-run) dry_run=1; shift ;;
    --skip-tests) skip_tests=1; shift ;;
    --allow-dirty) allow_dirty=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) die "Unknown option: $1" ;;
  esac
done

(( !allow_dirty || dry_run )) || die '--allow-dirty is only valid with --dry-run'
[[ -z "$previous_sig" || -n "$previous_index" ]] || die '--previous-index-sig requires --previous-index'
[[ -z "$notes_file" || -f "$notes_file" ]] || die "Missing release notes file: $notes_file"
[[ -n "$version" || -n "$tag" ]] || die 'Specify --tag or --version'
version="${version:-${tag#v}}"
tag="${tag:-v$version}"
[[ "$tag" == "v$version" && "$tag" =~ ^v[0-9]+\.[0-9]+\.[0-9]+(-[a-zA-Z0-9-]+(\.[a-zA-Z0-9-]+)*)?$ ]] \
  || die 'Tag must be vX.Y.Z[-suffix] and match --version'
[[ "$version" != *-* || "$prerelease" == 1 ]] || die 'A version with a suffix requires --prerelease'

loader="$PARENT/tools/env/load-local-config.sh"
if [[ -f "$loader" ]]; then
  export STS2_RE_ROOT="$PARENT"
  # shellcheck disable=SC1090
  source "$loader"
  sts2_init_env
  if [[ -z "${DOTNET_BIN:-}" ]]; then
    DOTNET_BIN="$(sts2_config_path DOTNET_BIN build.dotnet)"
    export DOTNET_BIN
  fi
  if [[ -z "$key_file" ]]; then
    key_file="$(sts2_compat_release_signing_key_file)"
  fi
  if [[ -z "$key_id" ]]; then
    key_id="$(sts2_compat_release_signing_key_id)"
  fi
else
  if [[ "${STS2_DOTENV_LOADED:-0}" != 1 && -f "$ROOT/.env" ]]; then
    set -a
    # shellcheck disable=SC1091
    source "$ROOT/.env"
    set +a
  fi
  export STS2_DOTENV_LOADED=1
  if [[ -z "$key_file" && -n "${COMPAT_RELEASE_SIGNING_KEY_FILE:-}" ]]; then
    # Config paths in a standalone checkout are relative to that checkout.
    key_file="$(cd "$ROOT" && python3 -c 'import os,sys; print(os.path.expanduser(os.path.expandvars(sys.argv[1])))' "$COMPAT_RELEASE_SIGNING_KEY_FILE")"
    [[ "$key_file" == /* ]] || key_file="$ROOT/$key_file"
  fi
  key_id="${key_id:-${COMPAT_RELEASE_SIGNING_KEY_ID:-sts2-compat-release-v1}}"
fi

min_code="${min_code:-${COMPAT_MIN_LAUNCHER_VERSION_CODE:-115}}"
min_name="${min_name:-${COMPAT_MIN_LAUNCHER_VERSION_NAME:-}}"
for n in "$revision" "$min_code"; do
  [[ "$n" =~ ^[1-9][0-9]{0,9}$ ]] && (( n <= 2147483647 )) \
    || die '--revision and --min-launcher-version-code must be positive 32-bit integers'
done
[[ -n "$key_file" ]] || die 'Configure COMPAT_RELEASE_SIGNING_KEY_FILE or pass --signing-key-file'
[[ "$key_id" =~ ^[a-zA-Z0-9][a-zA-Z0-9_.-]*$ ]] || die 'Invalid signing key ID'

check_clean() {
  local repo="$1" status
  status="$(git -C "$repo" status --porcelain --untracked-files=all --ignore-submodules=none)" || return 1
  [[ -z "$status" ]] || die "Git checkout is dirty: $repo (only --dry-run --allow-dirty bypasses this)"
}
if (( !allow_dirty )); then
  check_clean "$ROOT"
  if [[ -f "$loader" ]]; then
    check_clean "$PARENT"
  fi
fi
commit="$(git -C "$ROOT" rev-parse --verify HEAD)"
public_key="$("$TOOLS/compat-release-signing.py" keygen --from-private "$key_file")"

out_base="$ROOT/dist/compat-release"
mkdir -p "$out_base"
work="$(mktemp -d "$out_base/.work-XXXXXXXX")"
trap 'rm -rf -- "$work"' EXIT
release="$work/release"
mkdir -- "$release"

history_tag=""
if (( !dry_run )); then
  command -v gh >/dev/null || die 'GitHub CLI (gh) is required to publish'
  export GH_HOST=github.com GH_PROMPT_DISABLED=1 GH_PAGER=cat
fi
if [[ -z "$previous_index" ]] && (( !dry_run )); then
  # Query successfully before treating absence as first release. A failed API
  # call or missing asset on an existing published release is never "empty".
  # Include prereleases in history even though they do not become GitHub latest.
  history_result="$(gh release list --repo "$REPO" --exclude-drafts --limit 1 --json tagName \
    --jq 'if length == 0 then "empty" else "published:" + .[0].tagName end')" \
    || die 'Could not list previous compatibility releases'
  case "$history_result" in
    empty) echo 'No previous published releases; starting a new index.' ;;
    published:?*)
      history_tag="${history_result#published:}"
      mkdir -- "$work/history"
      gh release download --repo "$REPO" --dir "$work/history" \
        --pattern compat-index.json --pattern compat-index.json.sig -- "$history_tag" \
        || die "Could not download both signed history files for $history_tag"
      previous_index="$work/history/compat-index.json"
      previous_sig="$work/history/compat-index.json.sig"
      ;;
    *) die 'Invalid response while looking for published compatibility releases' ;;
  esac
fi

history_args=()
if [[ -n "$previous_index" ]]; then
  previous_sig="${previous_sig:-$previous_index.sig}"
  [[ -f "$previous_index" && -f "$previous_sig" ]] || die 'Previous index and its detached signature are both required'
  history_args+=(--previous-index "$previous_index" --previous-index-sig "$previous_sig"
    --previous-public-key-base64 "$public_key" --expected-previous-key-id "$key_id")
  if [[ -n "$history_tag" ]]; then
    history_args+=(--previous-tag "$history_tag")
  fi
fi

if (( !skip_tests )); then
  for test in deferred-mod-patch-queue harmony-method-reference-importer modeldb-shadow-placeholder \
    extended-multiplayer-rooms resource-safety; do
    ( cd "$ROOT" && "$TOOLS/test-$test.sh" )
  done
fi

# Release builds always use the checkout's complete active target matrix.
(
  cd "$ROOT"
  COMPAT_TARGET_ROOT="$ROOT/targets/active" COMPAT_MATRIX_PACK_ID="$PACK" BUILD_ARCHIVED_TARGETS=0 \
    "$TOOLS/build-compat-matrix.sh" --out "$work/matrix" --version "$version" --revision "$revision" \
      --min-launcher-version-code "$min_code" --min-launcher-version-name "$min_name"
)
cp -- "$work/matrix/$PACK.zip" "$work/matrix/$PACK/compat_manifest.json" "$release/"
cp -- "$work/matrix/$PACK/SHA256SUMS" "$release/SHA256SUMS"
( cd "$release" && sha256sum -- "$PACK.zip" >> SHA256SUMS )

index_args=(--manifest "$release/compat_manifest.json" --zip "$release/$PACK.zip"
  --tag "$tag" --release-name "STS2 Android Compatibility $tag" --out-index "$release/compat-index.json"
  --compat-revision "$revision" --min-launcher-version-code "$min_code")
gh_args=(--title "STS2 Android Compatibility $tag")
if [[ -n "$min_name" ]]; then
  index_args+=(--min-launcher-version-name "$min_name")
fi
if [[ -n "$notes_file" ]]; then
  cp -- "$notes_file" "$work/notes"
  index_args+=(--changelog-file "$work/notes")
  gh_args+=(--notes-file "$work/notes")
else
  gh_args+=(--notes '')
fi
if (( prerelease )); then
  index_args+=(--prerelease)
  gh_args+=(--prerelease --latest=false)
else
  gh_args+=(--latest)
fi
if (( !dry_run )); then
  index_args+=(--require-new-revision)
fi

"$TOOLS/compat-release-signing.py" build-index "${index_args[@]}" "${history_args[@]}"
"$TOOLS/compat-release-signing.py" sign --index "$release/compat-index.json" \
  --private-key "$key_file" --key-id "$key_id" --out-sig "$release/compat-index.json.sig"
"$TOOLS/compat-release-signing.py" verify --index "$release/compat-index.json" --sig "$release/compat-index.json.sig" \
  --public-key-base64 "$public_key" --expected-key-id "$key_id" --zip "$release/$PACK.zip" \
  --manifest "$release/compat_manifest.json"

output="$out_base/$tag"
[[ ! -L "$output" ]] || die "Refusing to archive to a symlink: $output"
mkdir -p -- "$output"
for asset in "${ASSETS[@]}"; do
  # Keep the verified staging paths for the upload and do not follow old output links.
  cp --remove-destination -- "$release/$asset" "$output/$asset"
done
printf 'Verified release assets: %s\n' "$output"
if (( dry_run )); then
  echo 'Dry run: no GitHub release created.'
  exit 0
fi

export GH_HOST=github.com GH_PROMPT_DISABLED=1 GH_PAGER=cat
( cd "$release" && gh release create "$tag" "${ASSETS[@]}" --repo "$REPO" --target "$commit" "${gh_args[@]}" )
