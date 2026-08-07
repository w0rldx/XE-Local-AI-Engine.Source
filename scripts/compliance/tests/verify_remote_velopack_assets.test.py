#!/usr/bin/env python3
from __future__ import annotations

import hashlib
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path


MODULE_PATH = Path(__file__).resolve().parents[1] / "verify_remote_velopack_assets.py"
SPEC = importlib.util.spec_from_file_location("verify_remote_velopack_assets", MODULE_PATH)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
PINNED_FIXTURE = Path(__file__).resolve().parent / "fixtures" / "velopack-1.2.0-linux-output.json"


class RemoteVelopackAssetTests(unittest.TestCase):
    VERSION = "1.2.3-rc.1"

    def make_fixture(self) -> tuple[Path, dict[str, Path], Path]:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        root = Path(directory.name)
        remote = root / "remote"
        remote.mkdir()
        local_roots: dict[str, Path] = {}
        for channel, portable_name in (
            ("win", "XE-Local-AI-Engine-win-Portable.zip"),
            ("linux", f"XE-Local-AI-Engine-{self.VERSION}-linux.AppImage"),
        ):
            local = root / channel
            local.mkdir()
            local_roots[channel] = local
            package_names = [
                f"XE-Local-AI-Engine-{self.VERSION}-{channel}-full.nupkg",
                f"XE-Local-AI-Engine-{self.VERSION}-{channel}-delta.nupkg",
            ]
            for name in [portable_name, *package_names]:
                (local / name).write_bytes(f"{channel}:{name}".encode())
                (remote / name).write_bytes((local / name).read_bytes())
            policy = MODULE.POLICIES[channel]
            assets = []
            legacy_lines = []
            for name in package_names:
                package = remote / name
                assets.append(
                    {
                        "Version": self.VERSION,
                        "FileName": name,
                        "SHA256": hashlib.sha256(package.read_bytes()).hexdigest(),
                        "Size": package.stat().st_size,
                    }
                )
                legacy_lines.append(
                    f"{hashlib.sha1(package.read_bytes(), usedforsecurity=False).hexdigest()} "
                    f"{name} {package.stat().st_size}"
                )
            (local / policy.feed).write_text(json.dumps({"Assets": assets}), encoding="utf-8")
            (remote / policy.feed).write_text(json.dumps({"Assets": assets}), encoding="utf-8")
            (local / policy.legacy_feed).write_text("\n".join(legacy_lines) + "\n", encoding="utf-8")
            (remote / policy.legacy_feed).write_text("\n".join(legacy_lines) + "\n", encoding="utf-8")
            (local / f"assets.{channel}.json").write_text("[]", encoding="utf-8")
            (local / "CHECKSUMS.sha256").write_text("retained evidence", encoding="utf-8")
        return root, local_roots, remote

    def test_internal_local_manifests_are_not_expected_remote_assets(self) -> None:
        _, local_roots, remote = self.make_fixture()
        MODULE.verify(self.VERSION, local_roots, remote)

    def test_previous_full_package_must_be_removed_after_pack(self) -> None:
        _, local_roots, remote = self.make_fixture()
        (local_roots["win"] / "XE-Local-AI-Engine-1.2.2-win-full.nupkg").write_bytes(b"prior")
        with self.assertRaisesRegex(ValueError, "previous full package"):
            MODULE.verify(self.VERSION, local_roots, remote)

    def test_remote_content_package_must_match_retained_bytes(self) -> None:
        _, local_roots, remote = self.make_fixture()
        next(remote.glob("*Portable.zip")).write_bytes(b"changed")
        with self.assertRaisesRegex(ValueError, "bytes differ"):
            MODULE.verify(self.VERSION, local_roots, remote)

    def test_remote_feed_must_match_attached_package_hash(self) -> None:
        _, local_roots, remote = self.make_fixture()
        feed = remote / "releases.win.json"
        payload = json.loads(feed.read_text(encoding="utf-8"))
        payload["Assets"][0]["SHA256"] = "0" * 64
        feed.write_text(json.dumps(payload), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "SHA-256"):
            MODULE.verify(self.VERSION, local_roots, remote)

    def test_unexpected_remote_asset_fails_closed(self) -> None:
        _, local_roots, remote = self.make_fixture()
        (remote / "unexpected.zip").write_bytes(b"unexpected")
        with self.assertRaisesRegex(ValueError, "remote asset set mismatch"):
            MODULE.verify(self.VERSION, local_roots, remote)

    def test_pinned_velopack_1_2_fixture_excludes_internal_manifest_and_prior_full_package(self) -> None:
        fixture = json.loads(PINNED_FIXTURE.read_text(encoding="utf-8"))
        self.assertEqual("1.2.0", fixture["toolVersion"])
        self.assertNotIn("assets.linux.json", {
            entry["RelativeFileName"] for entry in fixture["firstPackUploadManifest"]
        })
        self.assertNotIn("XE-Local-AI-Engine-1.2.3-rc.1-linux-full.nupkg", {
            entry["RelativeFileName"] for entry in fixture["deltaPackUploadManifest"]
        })

        with tempfile.TemporaryDirectory() as temporary_directory:
            output = Path(temporary_directory)
            for name in fixture["deltaPackFilesBeforeCleanup"]:
                (output / name).write_bytes(b"fixture")
            with self.assertRaisesRegex(ValueError, "previous full package"):
                MODULE.expected_channel_assets("linux", output, "1.2.3-rc.2")
            (output / "XE-Local-AI-Engine-1.2.3-rc.1-linux-full.nupkg").unlink()
            retained = MODULE.expected_channel_assets("linux", output, "1.2.3-rc.2")
            self.assertEqual(
                set(fixture["deltaPackUploadManifest"][index]["RelativeFileName"] for index in range(3))
                | {"releases.linux.json", "RELEASES-linux"},
                set(retained),
            )


if __name__ == "__main__":
    unittest.main()
