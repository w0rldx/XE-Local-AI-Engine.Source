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
assert SPEC is not None
assert SPEC.loader is not None
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
                # vpk writes FULL packages only into the legacy Squirrel feed -- a legacy client cannot consume a
                # Velopack delta. Mirrors deltaPackLegacyFeedFileNames in the pinned 1.2.0 capture.
                if name.endswith("-full.nupkg"):
                    legacy_lines.append(
                        f"{hashlib.sha1(package.read_bytes(), usedforsecurity=False).hexdigest()} "
                        f"{name} {package.stat().st_size}"
                    )
            (local / policy.feed).write_text(json.dumps({"Assets": assets}), encoding="utf-8")
            (remote / policy.feed).write_text(json.dumps({"Assets": assets}), encoding="utf-8")
            (local / policy.legacy_feed).write_text("\n".join(legacy_lines) + "\n", encoding="utf-8")
            # vpk only publishes the legacy feed for the default (win) channel; the linux legacy feed is retained
            # locally but never uploaded, so it must not appear among the remote assets.
            if policy.legacy_feed_published:
                (remote / policy.legacy_feed).write_text("\n".join(legacy_lines) + "\n", encoding="utf-8")
            (local / f"assets.{channel}.json").write_text("[]", encoding="utf-8")
            (local / "CHECKSUMS.sha256").write_text("retained evidence", encoding="utf-8")
        return root, local_roots, remote

    def verified_legacy_feeds(self, local_roots: dict[str, Path], remote: Path) -> dict[str, Path]:
        """The legacy feed each channel is actually reconciled against: win's from the release, linux's retained."""
        return {
            channel: (remote if policy.legacy_feed_published else local_roots[channel]) / policy.legacy_feed
            for channel, policy in MODULE.POLICIES.items()
        }

    @staticmethod
    def append_legacy_line(feed: Path, file_name: str, size: int = 4096) -> None:
        line = f"{'A' * 40} {file_name} {size}"
        feed.write_text(feed.read_text(encoding="utf-8") + line + "\n", encoding="utf-8")

    def drop_delta(self, channel: str, local_roots: dict[str, Path], remote: Path) -> str:
        """Turn a channel into a delta-less release, the way the first release of a line packs."""
        name = f"XE-Local-AI-Engine-{self.VERSION}-{channel}-delta.nupkg"
        (local_roots[channel] / name).unlink()
        (remote / name).unlink()
        for root in (local_roots[channel], remote):
            feed = root / MODULE.POLICIES[channel].feed
            payload = json.loads(feed.read_text(encoding="utf-8"))
            payload["Assets"] = [asset for asset in payload["Assets"] if asset["FileName"] != name]
            feed.write_text(json.dumps(payload), encoding="utf-8")
        return name

    def test_internal_local_manifests_are_not_expected_remote_assets(self) -> None:
        _, local_roots, remote = self.make_fixture()
        MODULE.verify(self.VERSION, local_roots, remote)

    def test_delta_is_published_but_absent_from_the_legacy_feed(self) -> None:
        # Regression for the rc.2 draft failure: the first release with a predecessor attaches a delta, and vpk
        # lists only full packages in the legacy feed. Requiring the delta there reported it as spuriously missing.
        _, local_roots, remote = self.make_fixture()
        for channel, feed in self.verified_legacy_feeds(local_roots, remote).items():
            delta = f"XE-Local-AI-Engine-{self.VERSION}-{channel}-delta.nupkg"
            self.assertTrue((remote / delta).is_file())
            self.assertNotIn(delta, feed.read_text(encoding="utf-8"))
        MODULE.verify(self.VERSION, local_roots, remote)

    def test_legacy_feed_may_carry_earlier_releases_full_packages(self) -> None:
        # The legacy feed accumulates so a Squirrel client can walk back a version. Those packages belong to their
        # own release and cannot be hashed here, but they must not make this release's reconciliation fail.
        _, local_roots, remote = self.make_fixture()
        for channel, feed in self.verified_legacy_feeds(local_roots, remote).items():
            self.append_legacy_line(feed, f"XE-Local-AI-Engine-1.2.2-{channel}-full.nupkg")
        MODULE.verify(self.VERSION, local_roots, remote)

    def test_legacy_feed_omitting_the_attached_full_package_fails_closed(self) -> None:
        _, local_roots, remote = self.make_fixture()
        feed = self.verified_legacy_feeds(local_roots, remote)["win"]
        full = f"XE-Local-AI-Engine-{self.VERSION}-win-full.nupkg"
        kept = [line for line in feed.read_text(encoding="utf-8").splitlines() if full not in line]
        feed.write_text("\n".join(kept) + "\n", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "does not describe attached full package"):
            MODULE.verify(self.VERSION, local_roots, remote)

    def test_legacy_feed_listing_an_unattached_delta_fails_closed(self) -> None:
        # The inverse of the regression: a delta named by the feed but missing from the release must still fail.
        _, local_roots, remote = self.make_fixture()
        delta = self.drop_delta("win", local_roots, remote)
        self.append_legacy_line(self.verified_legacy_feeds(local_roots, remote)["win"], delta)
        with self.assertRaisesRegex(ValueError, "references unattached package"):
            MODULE.verify(self.VERSION, local_roots, remote)

    def test_legacy_feed_listing_an_unattached_current_version_package_fails_closed(self) -> None:
        _, local_roots, remote = self.make_fixture()
        self.append_legacy_line(
            self.verified_legacy_feeds(local_roots, remote)["win"],
            f"XE-Local-AI-Engine-{self.VERSION}-linux-full.nupkg",
        )
        with self.assertRaisesRegex(ValueError, "references unattached package"):
            MODULE.verify(self.VERSION, local_roots, remote)

    def test_legacy_feed_must_match_attached_package_hash(self) -> None:
        _, local_roots, remote = self.make_fixture()
        feed = self.verified_legacy_feeds(local_roots, remote)["win"]
        original = feed.read_text(encoding="utf-8")
        feed.write_text(original.replace(original[:40], "B" * 40), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "SHA-1"):
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

    def test_only_the_win_legacy_feed_is_published_remotely(self) -> None:
        # Baseline passes with the linux legacy feed retained locally but absent from the release
        # (vpk never uploads it).
        _, local_roots, remote = self.make_fixture()
        MODULE.verify(self.VERSION, local_roots, remote)
        # A linux legacy feed appearing in the release would be an unexpected remote asset.
        (remote / MODULE.POLICIES["linux"].legacy_feed).write_bytes(b"unexpected legacy feed")
        with self.assertRaisesRegex(ValueError, "remote asset set mismatch"):
            MODULE.verify(self.VERSION, local_roots, remote)
        # The win legacy feed, by contrast, IS published and required in the release.
        _, local_roots, remote = self.make_fixture()
        (remote / MODULE.POLICIES["win"].legacy_feed).unlink()
        with self.assertRaisesRegex(ValueError, "remote asset set mismatch"):
            MODULE.verify(self.VERSION, local_roots, remote)

    def test_pinned_velopack_1_2_fixture_excludes_internal_manifest_and_prior_full_package(self) -> None:
        fixture = json.loads(PINNED_FIXTURE.read_text(encoding="utf-8"))
        self.assertEqual("1.2.0", fixture["toolVersion"])
        self.assertNotIn(
            "assets.linux.json", {entry["RelativeFileName"] for entry in fixture["firstPackUploadManifest"]}
        )
        self.assertNotIn(
            "XE-Local-AI-Engine-1.2.3-rc.1-linux-full.nupkg",
            {entry["RelativeFileName"] for entry in fixture["deltaPackUploadManifest"]},
        )

        # The legacy Squirrel feed carries full packages only, and keeps the previous release's full package.
        legacy_feed = fixture["deltaPackLegacyFeedFileNames"]
        self.assertEqual([name for name in legacy_feed if name.endswith("-full.nupkg")], legacy_feed)
        self.assertIn("XE-Local-AI-Engine-1.2.3-rc.1-linux-full.nupkg", legacy_feed)
        self.assertNotIn(
            "XE-Local-AI-Engine-1.2.3-rc.2-linux-delta.nupkg",
            legacy_feed,
            "a Velopack delta is not consumable by a legacy Squirrel client and is never listed there",
        )

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
