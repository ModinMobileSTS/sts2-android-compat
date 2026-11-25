#!/usr/bin/env python3
"""Offline signing CLI tests; fixtures contain only synthetic manifest/DLL/PCK bytes."""

import base64
from copy import deepcopy
from datetime import datetime, timedelta, timezone
import hashlib
import json
from pathlib import Path
import stat
import subprocess
import sys
import tempfile
import unittest
import zipfile

from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PublicKey


CLI = Path(__file__).with_name("compat-release-signing.py")
PACK = "sts2-android-compat"
ZIP_NAME = PACK + ".zip"
RELEASE_BASE = "https://github.com/ModinMobileSTS/sts2-android-compat/releases"
KEY_ID = "sts2-compat-release-v1"
DLL = "variants/v0.114.0/STS2Mobile.dll"
PCK = "variants/v0.114.0/port_compat.pck"


class CompatReleaseSigningTest(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory(prefix="compat-release-signing-")
        self.addCleanup(tmp.cleanup)
        self.root = Path(tmp.name)
        self.key = self.root / "compat-release-ed25519.pem"
        self.index = self.root / "compat-index.json"
        self.sig = self.root / "compat-index.json.sig"
        self.zip = self.root / ZIP_NAME
        self.manifest = self.root / "compat_manifest.json"
        self.manifest_data = {
            "schema": 2,
            "pack_id": PACK,
            "display_name": "STS2 Android Compatibility",
            "compat_version": "0.8.0",
            "compat_revision": 800,
            "min_launcher_version_code": 115,
            "min_launcher_version_name": "v0.1.13",
            "channel": "mixed",
            "build_info": {"branch": "main", "commit": "fixture", "layout": "flat-matrix", "dirty": False},
            "targets": [{
                "target_id": "v0.114.0",
                "status": "active",
                "display_name": "测试目标",
                "version": "v0.114.0",
                "versions": ["v0.114.0", "v0.114.1"],
                "channel": "beta",
                "steam_branch": "public-beta",
                "sts2_dll_sha256": ["1" * 64, "2" * 64],
                "artifacts": {"dll": DLL, "overlay_pck": PCK},
            }],
        }
        self.manifest.write_text(json.dumps(self.manifest_data), encoding="utf-8")
        self.write_zip()
        self.keygen = self.cli("keygen", "--out-private", self.key, "--key-id", KEY_ID)
        self.public = self.cli("keygen", "--from-private", self.key).stdout.strip()

    def cli(self, *args, error=None):
        result = subprocess.run([sys.executable, str(CLI), *map(str, args)], capture_output=True, text=True, check=False)
        if error is None:
            self.assertEqual(result.returncode, 0, result.stderr)
        else:
            self.assertNotEqual(result.returncode, 0, result.stdout)
            self.assertIn(error, result.stderr)
        return result

    def write_zip(self, *, changed_dll=None, bad_manifest=None, omit_from_sums=None):
        files = {"compat_manifest.json": self.manifest.read_bytes(), DLL: b"fixture DLL", PCK: b"fixture PCK"}
        if bad_manifest is not None:
            files["compat_manifest.json"] = json.dumps(bad_manifest).encode("utf-8")
        sums = "".join(f"{hashlib.sha256(raw).hexdigest()}  {name}\n"
                       for name, raw in sorted(files.items()) if name != omit_from_sums)
        if changed_dll is not None:
            files[DLL] = changed_dll
        with zipfile.ZipFile(self.zip, "w", zipfile.ZIP_DEFLATED) as archive:
            for name, raw in files.items():
                archive.writestr(f"{PACK}/{name}", raw)
            archive.writestr(f"{PACK}/SHA256SUMS", sums)

    def build(self, *args, error=None):
        self.cli("build-index", "--manifest", self.manifest, "--zip", self.zip,
                 "--tag", "v0.8.0", "--release-name", "Fixture v0.8.0", "--changelog", "新增功能",
                 "--compat-revision", "800", "--min-launcher-version-code", "115",
                 "--out-index", self.index, *args, error=error)

    def sign(self, index=None, sig=None):
        self.cli("sign", "--index", index or self.index, "--private-key", self.key,
                 "--key-id", KEY_ID, "--out-sig", sig or self.sig)

    def verify(self, *, with_zip=True, error=None):
        args = ("--zip", self.zip, "--manifest", self.manifest) if with_zip else ()
        self.cli("verify", "--index", self.index, "--sig", self.sig,
                 "--public-key-base64", self.public, "--expected-key-id", KEY_ID, *args, error=error)

    def test_keygen_build_sign_verify_round_trip(self):
        self.assertIn(f'"{KEY_ID}": "{self.public}"', self.keygen.stdout)
        self.assertEqual(stat.S_IMODE(self.key.stat().st_mode), 0o600)
        before = self.key.read_bytes()
        self.cli("keygen", "--out-private", self.key, error="File exists")
        self.assertEqual(self.key.read_bytes(), before)
        self.build()
        self.sign()
        self.verify()
        raw = self.index.read_bytes()
        self.assertIn("新增功能".encode(), raw)
        release = json.loads(raw)["releases"][0]
        self.assertEqual(release["zip_download_url"], f"{RELEASE_BASE}/download/v0.8.0/{ZIP_NAME}")
        self.assertNotIn("artifacts", release["targets"][0])
        self.assertEqual(release["targets"][0]["sts2_dll_sha256"], ["1" * 64, "2" * 64])
        envelope = json.loads(self.sig.read_bytes())
        Ed25519PublicKey.from_public_bytes(base64.b64decode(self.public)).verify(
            base64.b64decode(envelope["signature_base64"]), b"sts2-android-compat-index-v1\n" + raw)
        self.cli("keygen", "--out-private", self.key, "--force")
        self.assertNotEqual(self.key.read_bytes(), before)
        self.assertEqual(stat.S_IMODE(self.key.stat().st_mode), 0o600)

    def test_version_only_target_has_no_fingerprint(self):
        self.manifest_data["targets"][0]["sts2_dll_sha256"] = ""
        self.manifest.write_text(json.dumps(self.manifest_data), encoding="utf-8")
        self.write_zip()
        self.build()
        self.sign()
        self.verify()
        release = json.loads(self.index.read_bytes())["releases"][0]
        self.assertNotIn("sts2_dll_sha256", release["targets"][0])

    def test_repo_keys_rejected_in_both_symlink_directions(self):
        repo = self.root / "checkout"
        repo.mkdir()
        (repo / ".git").mkdir()
        inner = repo / "inner.pem"
        self.cli("keygen", "--out-private", inner, error="outside Git repositories")
        inner.write_bytes(self.key.read_bytes())
        outward = repo / "outward.pem"
        outward.symlink_to(self.key)
        inward = self.root / "inward.pem"
        inward.symlink_to(inner)
        hop = self.root / "hop.pem"
        hop.symlink_to(outward)
        for alias in (inner, outward, inward, hop):
            with self.subTest(alias=alias.name):
                self.cli("keygen", "--from-private", alias, error="outside Git repositories")

    def test_merges_authenticated_history_sorts_deduplicates_limits_and_rejects_tampering(self):
        self.build()
        previous = self.root / "previous.json"
        previous_sig = self.root / "previous.json.sig"
        history = json.loads(self.index.read_bytes())
        prototype = deepcopy(history["releases"][0])
        old = datetime(2025, 1, 1, tzinfo=timezone.utc)

        def entry(version, revision, timestamp, name):
            tag = "v" + version
            return {**prototype, "tag_name": tag, "compat_version": version, "compat_revision": revision,
                    "release_name": name, "published_utc": timestamp.strftime("%Y-%m-%dT%H:%M:%SZ"),
                    "release_url": f"{RELEASE_BASE}/tag/{tag}",
                    "zip_download_url": f"{RELEASE_BASE}/download/{tag}/{ZIP_NAME}"}

        history["releases"] = [entry(f"0.7.{n}", 700 + n, old + timedelta(days=n), f"Release {n}") for n in range(1, 18)]
        history["releases"].insert(0, entry("0.7.17", 716, old, "Stale duplicate"))
        history["releases"].append(entry("0.8.0", 800, old, "Replace this release"))
        raw = json.dumps(history).encode()
        previous.write_bytes(raw)
        self.sign(previous, previous_sig)
        args = ("--previous-index", previous, "--previous-public-key-base64", self.public,
                "--expected-previous-key-id", KEY_ID)
        self.build(*args)
        releases = json.loads(self.index.read_bytes())["releases"]
        self.assertEqual([release["tag_name"] for release in releases], ["v0.8.0"] + [f"v0.7.{n}" for n in range(17, 3, -1)])
        self.assertEqual(releases[0]["release_name"], "Fixture v0.8.0")
        self.assertEqual(releases[1]["release_name"], "Release 17")
        self.sign()
        self.verify()
        self.build(*args, "--require-new-revision", error="already in published history")
        self.build(*args, "--previous-tag", "v0.7.17", error="latest_tag does not match")
        previous.write_bytes(raw + b" ")
        self.build(*args, error="payload SHA-256 mismatch")
        previous.write_bytes(raw)
        previous_sig.unlink()
        self.build(*args, error="No such file")
        self.build("--previous-index", previous, error="requires --previous-public-key-base64")

    def test_tampered_index_envelope_and_signed_foreign_url(self):
        self.build()
        self.sign()
        raw = self.index.read_bytes()
        self.index.write_bytes(raw.replace(b"Fixture", b"fixture", 1))
        self.verify(error="payload SHA-256 mismatch")
        self.index.write_bytes(raw)
        envelope = json.loads(self.sig.read_bytes())
        signature = bytearray(base64.b64decode(envelope["signature_base64"]))
        signature[0] ^= 1
        envelope["signature_base64"] = base64.b64encode(signature).decode()
        self.sig.write_text(json.dumps(envelope), encoding="utf-8")
        self.verify(error="Invalid Ed25519 signature")
        self.sign()
        envelope = json.loads(self.sig.read_bytes())
        envelope["key_id"] = "unknown"
        self.sig.write_text(json.dumps(envelope), encoding="utf-8")
        self.verify(error="Unexpected signature key_id")
        index = json.loads(raw)
        index["releases"][0]["zip_download_url"] = f"https://github.com.example/{ZIP_NAME}"
        self.index.write_text(json.dumps(index), encoding="utf-8")
        self.sign()
        self.verify(with_zip=False, error="Invalid GitHub release asset URL")

    def test_tampered_zip_and_uncovered_or_corrupt_inner_file(self):
        self.build()
        self.sign()
        raw = self.zip.read_bytes()
        self.zip.write_bytes(raw[:-1] + bytes([raw[-1] ^ 1]))
        self.verify(error="ZIP SHA-256 does not match")
        self.write_zip(changed_dll=b"replacement DLL")
        self.build()
        self.sign()
        self.verify(error=f"ZIP member SHA-256 mismatch: {DLL}")
        self.write_zip(omit_from_sums=PCK)
        self.build()
        self.sign()
        self.verify(error="SHA256SUMS does not cover the exact ZIP file set")

    def test_signed_zip_manifest_must_match_index_and_standalone(self):
        self.write_zip(bad_manifest={**self.manifest_data, "compat_revision": 801})
        self.build()
        self.sign()
        self.verify(error="Standalone manifest does not match ZIP manifest")
        self.cli("verify", "--index", self.index, "--sig", self.sig, "--public-key-base64", self.public,
                 "--zip", self.zip, error="ZIP manifest compat_revision does not match signed index")


if __name__ == "__main__":
    unittest.main()
