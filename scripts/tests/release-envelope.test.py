#!/usr/bin/env python3
from __future__ import annotations

import hashlib
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

MODULE_PATH = Path(__file__).resolve().parents[1] / "release" / "verify-release-envelope.py"
SPEC = importlib.util.spec_from_file_location("verify_release_envelope", MODULE_PATH)
assert SPEC is not None
assert SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class ReleaseEnvelopeTests(unittest.TestCase):
    TAG = "v1.2.3-rc.1"
    SOURCE_SHA = "a" * 40

    def make_fixture(self) -> Path:
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        for name, payload in (("app.zip", b"application"), ("releases.win.json", b"feed")):
            (root / name).write_bytes(payload)

        spdx = {
            "spdxVersion": "SPDX-2.2",
            "files": [
                {
                    "fileName": f"./{path.name}",
                    "checksums": [{"algorithm": "SHA256", "checksumValue": self.sha256(path)}],
                }
                for path in sorted(root.iterdir())
            ],
        }
        (root / "RELEASE.spdx.json").write_text(json.dumps(spdx), encoding="utf-8")

        assets = [
            {"name": path.name, "size": path.stat().st_size, "sha256": self.sha256(path)}
            for path in sorted(root.iterdir())
        ]
        manifest = {
            "schemaVersion": 1,
            "tag": self.TAG,
            "sourceSha": self.SOURCE_SHA,
            "assets": assets,
            "signing": {"state": "unsigned", "decisionGate": "signing-risk-decision"},
            "selfExclusions": ["RELEASE-MANIFEST.json", "CHECKSUMS.sha256"],
        }
        (root / "RELEASE-MANIFEST.json").write_text(json.dumps(manifest), encoding="utf-8")
        checksums = [f"{self.sha256(path)}  {path.name}" for path in sorted(root.iterdir())]
        (root / "CHECKSUMS.sha256").write_text("\n".join(checksums) + "\n", encoding="utf-8")
        return root

    @staticmethod
    def sha256(path: Path) -> str:
        return hashlib.sha256(path.read_bytes()).hexdigest()

    def test_valid_verified_remote_envelope_passes(self) -> None:
        root = self.make_fixture()
        MODULE.verify(root, self.TAG, self.SOURCE_SHA)

    def test_gnu_find_checksum_names_with_dot_slash_pass(self) -> None:
        root = self.make_fixture()
        path = root / "CHECKSUMS.sha256"
        lines = [
            f"{digest}  ./{name}"
            for digest, name in (line.split("  ", 1) for line in path.read_text(encoding="utf-8").splitlines())
        ]
        path.write_text("\n".join(lines) + "\n", encoding="utf-8")

        MODULE.verify(root, self.TAG, self.SOURCE_SHA)

    def test_extra_asset_not_declared_by_checksum_fails(self) -> None:
        root = self.make_fixture()
        (root / "unexpected.bin").write_bytes(b"unexpected")
        with self.assertRaisesRegex(ValueError, "checksum asset set mismatch"):
            MODULE.verify(root, self.TAG, self.SOURCE_SHA)

    def test_tampered_asset_fails(self) -> None:
        root = self.make_fixture()
        (root / "app.zip").write_bytes(b"tampered")
        with self.assertRaisesRegex(ValueError, "checksum mismatch"):
            MODULE.verify(root, self.TAG, self.SOURCE_SHA)

    def test_spdx_must_cover_every_primary_asset(self) -> None:
        root = self.make_fixture()
        path = root / "RELEASE.spdx.json"
        payload = json.loads(path.read_text(encoding="utf-8"))
        payload["files"] = payload["files"][1:]
        path.write_text(json.dumps(payload), encoding="utf-8")
        self.reseal_metadata(root)
        with self.assertRaisesRegex(ValueError, "SPDX primary asset set mismatch"):
            MODULE.verify(root, self.TAG, self.SOURCE_SHA)

    def test_tag_and_source_sha_are_bound(self) -> None:
        root = self.make_fixture()
        with self.assertRaisesRegex(ValueError, "release identity mismatch"):
            MODULE.verify(root, "v9.9.9", self.SOURCE_SHA)

    def reseal_metadata(self, root: Path) -> None:
        manifest_path = root / "RELEASE-MANIFEST.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        manifest["assets"] = [
            {"name": path.name, "size": path.stat().st_size, "sha256": self.sha256(path)}
            for path in sorted(root.iterdir())
            if path.name not in {"RELEASE-MANIFEST.json", "CHECKSUMS.sha256"}
        ]
        manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
        checksums = [
            f"{self.sha256(path)}  {path.name}" for path in sorted(root.iterdir()) if path.name != "CHECKSUMS.sha256"
        ]
        (root / "CHECKSUMS.sha256").write_text("\n".join(checksums) + "\n", encoding="utf-8")


if __name__ == "__main__":
    unittest.main()
