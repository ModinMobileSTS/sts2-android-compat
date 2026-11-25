#!/usr/bin/env python3
"""Build, sign, and verify the official STS2 Android compatibility release index."""

import argparse
import base64
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import sys
import tempfile
import zipfile

from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey, Ed25519PublicKey


REPOSITORY = "ModinMobileSTS/sts2-android-compat"
PACK_ID = "sts2-android-compat"
ZIP_NAME = f"{PACK_ID}.zip"
RELEASE_BASE = f"https://github.com/{REPOSITORY}/releases"
KEY_ID = "sts2-compat-release-v1"
SIGNATURE_DOMAIN = b"sts2-android-compat-index-v1\n"
RELEASE_LIMIT = 15
MAX_INDEX_BYTES = 512 * 1024
MAX_SIGNATURE_BYTES = 8 * 1024
MAX_ZIP_BYTES = 64 * 1024**2
MAX_UNPACKED_BYTES = 256 * 1024**2
MAX_ZIP_ENTRIES = 4096
MAX_METADATA_BYTES = 1024 * 1024
TAG = re.compile(r"v[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*)?")
IDENTIFIER = re.compile(r"[A-Za-z0-9][A-Za-z0-9_.-]*")
TARGET_ID = re.compile(r"[a-z0-9](?:[a-z0-9._-]*[a-z0-9])?")
SHA256 = re.compile(r"[0-9a-fA-F]{64}")
TARGET_FIELDS = ("target_id", "status", "display_name", "version", "versions", "channel",
                 "steam_branch", "sts2_dll_sha256")


def require(condition, message):
    if not condition:
        raise ValueError(message)


def private_key_path(value):
    # Check the supplied spelling AND each resolved ancestor. In particular, do
    # not accept either a repo-contained symlink to an external key or the inverse.
    path = Path(os.path.expandvars(value)).expanduser().absolute()
    pending = [path]
    checked = set()
    while pending:
        current = pending.pop()
        for candidate in (*current.parents, current):
            if candidate in checked:
                continue
            checked.add(candidate)
            real = candidate.resolve()
            require(all(not os.path.lexists(parent / ".git") and parent.name != ".git"
                        for parent in (candidate, real) if parent.is_dir()),
                    "Signing key must be outside Git repositories (including symlink paths)")
            if candidate.is_symlink():
                pending.append(candidate.parent / candidate.readlink())
    return path.resolve()


def load_private_key(value):
    path = private_key_path(value)
    require(path.is_file(), f"Signing key is not a regular file: {path}")
    key = serialization.load_pem_private_key(path.read_bytes(), password=None)
    require(isinstance(key, Ed25519PrivateKey), "Signing key must be an Ed25519 private PEM")
    return key


def raw_public_key(key):
    return key.public_key().public_bytes(serialization.Encoding.Raw, serialization.PublicFormat.Raw)


def keygen(args):
    if args.from_private:
        require(not args.force, "--force requires --out-private")
        print(base64.b64encode(raw_public_key(load_private_key(args.from_private))).decode("ascii"))
        return

    require(IDENTIFIER.fullmatch(args.key_id), "Invalid key ID")
    supplied = Path(os.path.expandvars(args.out_private)).expanduser()
    require(not supplied.is_symlink(), "Refusing to write a private key through a symlink")
    path = private_key_path(args.out_private)
    path.parent.mkdir(parents=True, exist_ok=True)
    key = Ed25519PrivateKey.generate()
    pem = key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8,
                            serialization.NoEncryption())
    # Stage a mode-0600 inode first; never truncate an existing key or follow a symlink.
    fd, temp = tempfile.mkstemp(dir=path.parent)
    try:
        with os.fdopen(fd, "wb") as stream:
            os.fchmod(stream.fileno(), 0o600)
            stream.write(pem)
        if args.force:
            os.replace(temp, path)
        else:
            os.link(temp, path)
    finally:
        Path(temp).unlink(missing_ok=True)
    public = base64.b64encode(raw_public_key(key)).decode("ascii")
    print(f"key_id: {args.key_id}\npublic_key_base64: {public}")
    print("Public key table entry:")
    print(json.dumps({args.key_id: public}, indent=2))


def reject_duplicate_fields(pairs):
    obj = dict(pairs)
    require(len(obj) == len(pairs), "Duplicate JSON object field")
    return obj


def json_object(raw):
    obj = json.loads(raw.decode("utf-8"), object_pairs_hook=reject_duplicate_fields)
    require(isinstance(obj, dict), "Expected a JSON object")
    return obj


def write_json(path, obj):
    Path(path).write_bytes((json.dumps(obj, ensure_ascii=False, indent=2, allow_nan=False) + "\n").encode("utf-8"))


def positive_int(value, label, maximum=2_147_483_647):
    require(type(value) is int and 0 < value <= maximum, f"Invalid {label}: expected a positive integer")
    return value


def string_field(obj, field):
    value = obj.get(field)
    require(isinstance(value, str) and value.strip(), f"Missing or invalid {field}")
    return value


def timestamp(value):
    require(re.fullmatch(r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]+)?Z", value),
            "Expected a UTC timestamp")
    return datetime.fromisoformat(value[:-1] + "+00:00")


def stream_sha256(stream):
    digest = hashlib.sha256()
    for chunk in iter(lambda: stream.read(1024 * 1024), b""):
        digest.update(chunk)
    return digest.hexdigest()


def sha256_string(value):
    return isinstance(value, str) and SHA256.fullmatch(value) is not None


def target_summary(targets):
    require(isinstance(targets, list) and targets, "Expected at least one target")
    summary = []
    seen = set()
    for target in targets:
        require(isinstance(target, dict), "Invalid target")
        target_id = string_field(target, "target_id")
        require(TARGET_ID.fullmatch(target_id) and target_id not in seen, f"Invalid or duplicate target: {target_id}")
        seen.add(target_id)
        version = string_field(target, "version")
        versions = target.get("versions")
        require(isinstance(versions, list) and "*" not in version and all(
            isinstance(v, str) and v.strip() and "*" not in v for v in versions), f"Invalid versions for {target_id}")
        shas = target.get("sts2_dll_sha256")
        # Legacy v0.103.x is a version-only match, with no original DLL fingerprint.
        require(shas in (None, "") or sha256_string(shas)
                or (isinstance(shas, list) and shas and all(map(sha256_string, shas))),
                f"Invalid original DLL SHA-256 for {target_id}")
        summary.append({field: target[field] for field in TARGET_FIELDS
                        if field in target and (field != "sts2_dll_sha256" or shas)})
    return summary


def release_urls(tag):
    require(TAG.fullmatch(tag), f"Invalid release tag: {tag!r}")
    return f"{RELEASE_BASE}/tag/{tag}", f"{RELEASE_BASE}/download/{tag}/{ZIP_NAME}"


def manifest_metadata(manifest):
    require(type(manifest.get("schema")) is int and manifest["schema"] == 2
            and manifest.get("pack_id") == PACK_ID and manifest.get("pack_kind") != "offline-bootstrap",
            "Expected a schema 2 full compatibility pack manifest")
    build_info = manifest.get("build_info", {})
    require(isinstance(build_info, dict), "Invalid manifest build_info")
    info = {
        "pack_id": PACK_ID,
        "display_name": string_field(manifest, "display_name"),
        "manifest_schema": 2,
        "compat_version": string_field(manifest, "compat_version"),
        "compat_revision": positive_int(manifest.get("compat_revision"), "compat_revision"),
        "min_launcher_version_code": positive_int(manifest.get("min_launcher_version_code"),
                                                  "min_launcher_version_code"),
        "channel": string_field(manifest, "channel"),
        "build_info": {field: build_info[field] for field in ("branch", "commit", "built_utc", "layout")
                       if field in build_info},
        "targets": target_summary(manifest.get("targets")),
    }
    if "min_launcher_version_name" in manifest:
        info["min_launcher_version_name"] = string_field(manifest, "min_launcher_version_name")
    return info


def validate_index(index, *, allow_duplicate_tags=False):
    require(type(index.get("schema")) is int and index["schema"] == 1
            and index.get("repository") == REPOSITORY and index.get("pack_id") == PACK_ID,
            "Invalid index schema, repository, or pack ID")
    timestamp(string_field(index, "generated_utc"))
    releases = index.get("releases")
    require(isinstance(releases, list) and releases, "Index contains no releases")
    require(allow_duplicate_tags or len(releases) <= RELEASE_LIMIT, "Too many signed releases")
    tags = set()
    for release in releases:
        require(isinstance(release, dict), "Invalid release entry")
        tag = string_field(release, "tag_name")
        release_url, download_url = release_urls(tag)
        require(allow_duplicate_tags or tag not in tags, f"Duplicate release tag: {tag}")
        tags.add(tag)
        require(release.get("release_url") == release_url and release.get("zip_download_url") == download_url
                and release.get("zip_asset_name") == ZIP_NAME, f"Invalid GitHub release asset URL or name: {tag}")
        require(type(release.get("manifest_schema")) is int and release["manifest_schema"] == 2
                and release.get("pack_id") == PACK_ID, f"Invalid release manifest schema or pack ID: {tag}")
        require(type(release.get("prerelease")) is bool, f"Invalid prerelease marker: {tag}")
        timestamp(string_field(release, "published_utc"))
        require(tag == "v" + string_field(release, "compat_version"), f"Version does not match tag: {tag}")
        positive_int(release.get("compat_revision"), "compat_revision")
        positive_int(release.get("min_launcher_version_code"), "min_launcher_version_code")
        positive_int(release.get("zip_size_bytes"), "zip_size_bytes", MAX_ZIP_BYTES)
        require(sha256_string(release.get("zip_sha256")), f"Invalid release ZIP SHA-256: {tag}")
        target_summary(release.get("targets"))
    require(string_field(index, "latest_tag") in tags, "latest_tag does not identify a release")


def verify_index(index_path, sig_path, public_key, expected_key_id, *, allow_duplicate_tags=False):
    index_file, signature_file = Path(index_path), Path(sig_path)
    require(0 < index_file.stat().st_size <= MAX_INDEX_BYTES
            and 0 < signature_file.stat().st_size <= MAX_SIGNATURE_BYTES, "Invalid index or signature size")
    raw = index_file.read_bytes()
    envelope = json_object(signature_file.read_bytes())
    require(type(envelope.get("schema")) is int and envelope["schema"] == 1
            and envelope.get("algorithm") == "ed25519", "Invalid Ed25519 signature envelope")
    key_id = string_field(envelope, "key_id")
    require(IDENTIFIER.fullmatch(key_id) and (expected_key_id is None or key_id == expected_key_id),
            f"Unexpected signature key_id: {key_id}")
    require(envelope.get("payload_sha256") == hashlib.sha256(raw).hexdigest(), "Index payload SHA-256 mismatch")
    signature = base64.b64decode(string_field(envelope, "signature_base64"), validate=True)
    require(len(signature) == 64, "Invalid Ed25519 signature size")
    try:
        Ed25519PublicKey.from_public_bytes(base64.b64decode(public_key, validate=True)).verify(
            signature, SIGNATURE_DOMAIN + raw)
    except InvalidSignature as exc:
        raise ValueError("Invalid Ed25519 signature") from exc
    index = json_object(raw)
    validate_index(index, allow_duplicate_tags=allow_duplicate_tags)
    return index


def build_index(args):
    manifest = json_object(Path(args.manifest).read_bytes())
    metadata = manifest_metadata(manifest)
    for field in ("compat_revision", "min_launcher_version_code", "min_launcher_version_name"):
        value = getattr(args, field)
        if value is not None:
            require(metadata.get(field) == value, f"{field} does not match built manifest")
    release_url, download_url = release_urls(args.tag)
    require(args.tag == "v" + metadata["compat_version"], "Tag does not match manifest compat_version")
    history = []
    if args.previous_index:
        require(args.previous_public_key_base64 and args.expected_previous_key_id,
                "--previous-index requires --previous-public-key-base64 and --expected-previous-key-id")
        previous = verify_index(args.previous_index, args.previous_index_sig or args.previous_index + ".sig",
                                args.previous_public_key_base64, args.expected_previous_key_id,
                                allow_duplicate_tags=True)
        if args.previous_tag:
            require(previous["latest_tag"] == args.previous_tag, "History index latest_tag does not match its release")
        history = previous["releases"]
    else:
        require(not any((args.previous_index_sig, args.previous_public_key_base64, args.expected_previous_key_id,
                         args.previous_tag)), "History verification options require --previous-index")
    if args.require_new_revision and history:
        require(all(entry["tag_name"] != args.tag for entry in history), "Tag is already in published history")
        require(metadata["compat_revision"] > max(entry["compat_revision"] for entry in history),
                "Release revision must be greater than all verified history revisions")
    now = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    if history:
        require(all(timestamp(entry["published_utc"]) <= timestamp(now) for entry in history),
                "Release history contains a future publication timestamp")
    with open(args.zip, "rb") as stream:
        release = {
            "tag_name": args.tag,
            "release_name": args.release_name or f"{metadata['display_name']} {args.tag}",
            "published_utc": now,
            "prerelease": args.prerelease,
            **metadata,
            "changelog": Path(args.changelog_file).read_text(encoding="utf-8") if args.changelog_file else args.changelog,
            "release_url": release_url,
            "zip_asset_name": ZIP_NAME,
            "zip_download_url": download_url,
            "zip_size_bytes": os.fstat(stream.fileno()).st_size,
            "zip_sha256": stream_sha256(stream),
        }
    # Prefer the new release over its old entry, and the newest record of any
    # previously duplicated tag. Keep current first even for same-second releases.
    releases = {args.tag: release}
    for entry in sorted(history, key=lambda entry: (timestamp(entry["published_utc"]),
                                                     entry["compat_revision"], entry["tag_name"]), reverse=True):
        releases.setdefault(entry["tag_name"], entry)
    index = {
        "schema": 1,
        "repository": REPOSITORY,
        "pack_id": PACK_ID,
        "generated_utc": now,
        "latest_tag": args.tag,
        "releases": list(releases.values())[:RELEASE_LIMIT],
    }
    validate_index(index)
    write_json(args.out_index, index)


def sign(args):
    require(IDENTIFIER.fullmatch(args.key_id), "Invalid key ID")
    key = load_private_key(args.private_key)
    raw = Path(args.index).read_bytes()
    require(Path(args.out_sig).resolve() != Path(args.private_key).expanduser().resolve(),
            "Signature output must not overwrite the signing key")
    write_json(args.out_sig, {
        "schema": 1,
        "algorithm": "ed25519",
        "key_id": args.key_id,
        "payload_sha256": hashlib.sha256(raw).hexdigest(),
        "signature_base64": base64.b64encode(key.sign(SIGNATURE_DOMAIN + raw)).decode("ascii"),
    })


def verify_archive(archive, release, manifest_path):
    files = {}
    entries = archive.infolist()
    require(len(entries) <= MAX_ZIP_ENTRIES and sum(info.file_size for info in entries) <= MAX_UNPACKED_BYTES,
            "ZIP exceeds the launcher extraction limits")
    seen = set()
    prefix = PACK_ID + "/" if any(info.filename == f"{PACK_ID}/compat_manifest.json" for info in entries) else ""
    for info in entries:
        name = info.filename
        require(name.rstrip("/") not in seen, f"Duplicate ZIP member: {name!r}")
        seen.add(name.rstrip("/"))
        if prefix and name == prefix and info.is_dir():
            continue
        require(name.startswith(prefix) and "\\" not in name and "\x00" not in info.orig_filename
                and not stat.S_ISLNK(info.external_attr >> 16), f"Unsafe ZIP member: {name!r}")
        name = name[len(prefix):].removesuffix("/")
        parts = name.split("/")
        require(all(part and part not in (".", "..") and ":" not in part for part in parts)
                and not any(ord(ch) < 32 or 127 <= ord(ch) <= 159 for ch in name), f"Unsafe ZIP member: {name!r}")
        require(name == "variants" or name.startswith("variants/")
                or (not info.is_dir() and name in ("compat_manifest.json", "SHA256SUMS")),
                f"Unexpected ZIP member: {name!r}")
        if not info.is_dir():
            files[name] = info
    require({"compat_manifest.json", "SHA256SUMS"} <= files.keys(), "ZIP is missing compat_manifest.json or SHA256SUMS")
    require(all(0 < files[name].file_size <= MAX_METADATA_BYTES for name in ("compat_manifest.json", "SHA256SUMS")),
            "Invalid ZIP manifest or SHA256SUMS size")
    raw_manifest = archive.read(files["compat_manifest.json"])
    if manifest_path:
        require(Path(manifest_path).read_bytes() == raw_manifest, "Standalone manifest does not match ZIP manifest")
    manifest = json_object(raw_manifest)
    metadata = manifest_metadata(manifest)
    for field, value in metadata.items():
        require(release.get(field) == value, f"ZIP manifest {field} does not match signed index")
    require("min_launcher_version_name" in metadata or "min_launcher_version_name" not in release,
            "ZIP manifest min_launcher_version_name does not match signed index")
    for target in manifest["targets"]:
        variant = f"variants/{target['target_id']}"
        expected = {"dll": f"{variant}/STS2Mobile.dll", "overlay_pck": f"{variant}/port_compat.pck"}
        require(target.get("artifacts") == expected and all(name in files and files[name].file_size > 0 for name in expected.values()),
                f"Missing or invalid artifacts for target {target['target_id']}")
    checksums = {}
    for line in archive.read(files["SHA256SUMS"]).decode("utf-8").splitlines():
        match = re.fullmatch(r"([0-9A-Fa-f]{64})  (.+)", line)
        require(match, f"Invalid SHA256SUMS line: {line!r}")
        digest, name = match.groups()
        require(name not in checksums, f"Duplicate SHA256SUMS entry: {name!r}")
        checksums[name] = digest.lower()
    require(checksums.keys() == files.keys() - {"SHA256SUMS"}, "SHA256SUMS does not cover the exact ZIP file set")
    for name, digest in checksums.items():
        with archive.open(files[name]) as stream:
            require(stream_sha256(stream) == digest, f"ZIP member SHA-256 mismatch: {name}")


def verify(args):
    index = verify_index(args.index, args.sig, args.public_key_base64, args.expected_key_id)
    require(not args.manifest or args.zip, "--manifest requires --zip")
    if args.zip:
        release = next(item for item in index["releases"] if item["tag_name"] == index["latest_tag"])
        with open(args.zip, "rb") as stream:
            require(os.fstat(stream.fileno()).st_size == release["zip_size_bytes"], "ZIP size does not match signed index")
            require(stream_sha256(stream) == release["zip_sha256"].lower(), "ZIP SHA-256 does not match signed index")
            with zipfile.ZipFile(stream) as archive:
                verify_archive(archive, release, args.manifest)
    print(f"Verified {index['latest_tag']} index" + (" and ZIP" if args.zip else ""))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    generate = commands.add_parser("keygen", help="generate a private key or derive its public key")
    mode = generate.add_mutually_exclusive_group(required=True)
    mode.add_argument("--out-private", help="write a new Ed25519 PEM outside Git repositories")
    mode.add_argument("--from-private", help="print only the raw public key Base64 for an existing PEM")
    generate.add_argument("--key-id", default=KEY_ID)
    generate.add_argument("--force", action="store_true", help="replace existing PEM with --out-private")
    generate.set_defaults(run=keygen)

    build = commands.add_parser("build-index", help="build an index from a manifest and ZIP")
    for name in ("manifest", "zip", "tag", "out-index"):
        build.add_argument(f"--{name}", required=True)
    build.add_argument("--release-name")
    notes = build.add_mutually_exclusive_group()
    notes.add_argument("--changelog", default="")
    notes.add_argument("--changelog-file")
    build.add_argument("--compat-revision", type=int)
    build.add_argument("--min-launcher-version-code", type=int)
    build.add_argument("--min-launcher-version-name")
    build.add_argument("--prerelease", action="store_true")
    build.add_argument("--previous-index")
    build.add_argument("--previous-index-sig", help="default: --previous-index path followed by .sig")
    build.add_argument("--previous-public-key-base64")
    build.add_argument("--expected-previous-key-id")
    build.add_argument("--previous-tag", help="require that history latest_tag matches this release tag")
    build.add_argument("--require-new-revision", action="store_true", help="reject a reused tag or non-increasing revision")
    build.set_defaults(run=build_index)

    signer = commands.add_parser("sign", help="sign the exact bytes of an index")
    for name in ("index", "private-key", "key-id", "out-sig"):
        signer.add_argument(f"--{name}", required=True)
    signer.set_defaults(run=sign)

    verifier = commands.add_parser("verify", help="verify a signed index and optionally its latest ZIP")
    for name in ("index", "sig", "public-key-base64"):
        verifier.add_argument(f"--{name}", required=True)
    verifier.add_argument("--expected-key-id")
    verifier.add_argument("--zip")
    verifier.add_argument("--manifest", help="require that this standalone manifest matches the ZIP member")
    verifier.set_defaults(run=verify)

    args = parser.parse_args()
    try:
        args.run(args)
    except (ValueError, OSError, RuntimeError, TypeError, zipfile.BadZipFile) as exc:
        parser.exit(1, f"{parser.prog}: {exc}\n")


if __name__ == "__main__":
    main()
