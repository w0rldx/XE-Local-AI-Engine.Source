#!/usr/bin/env python3
"""Reconcile retained Velopack outputs with the exact assets downloaded from a GitHub draft."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
from collections.abc import Callable
from pathlib import Path
from typing import NamedTuple

INTERNAL_LOCAL_FILES = {"CHECKSUMS.sha256", "assets.win.json", "assets.linux.json"}
FEED_FILES = {"releases.win.json", "releases.linux.json", "RELEASES", "RELEASES-linux"}


class ChannelPolicy(NamedTuple):
    feed: str
    legacy_feed: str
    legacy_feed_published: bool
    portable_suffix: str


# vpk publishes the modern per-channel JSON feed for every channel, but only uploads the legacy Squirrel `RELEASES`
# feed for the default (win) channel. The non-default channel's legacy feed (RELEASES-linux) is retained locally as
# build evidence and never lands in the GitHub release (Linux has no legacy Squirrel client). Confirmed against the
# pinned velopack-1.2.0 upload manifest and live vpk upload output.
POLICIES = {
    "win": ChannelPolicy("releases.win.json", "RELEASES", True, "Portable.zip"),
    "linux": ChannelPolicy("releases.linux.json", "RELEASES-linux", False, ".AppImage"),
}


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def files_by_name(root: Path, *, ignore_internal: bool) -> dict[str, Path]:
    files: dict[str, Path] = {}
    for path in root.iterdir():
        if not path.is_file() or (ignore_internal and path.name in INTERNAL_LOCAL_FILES):
            continue
        if path.name in files:
            raise ValueError(f"duplicate asset name in {root}: {path.name}")
        files[path.name] = path
    return files


def require_one(names: set[str], predicate: Callable[[str], bool], label: str) -> str:
    matches = [name for name in names if predicate(name)]
    if len(matches) != 1:
        raise ValueError(f"expected exactly one {label}, found {sorted(matches)}")
    return matches[0]


def expected_channel_assets(channel: str, root: Path, version: str) -> dict[str, Path]:
    policy = POLICIES[channel]
    files = files_by_name(root, ignore_internal=True)
    names = set(files)
    version_marker = f"-{version}-"
    require_one(
        names,
        lambda name: name.endswith("-full.nupkg") and version_marker in name,
        f"{channel} full package for {version}",
    )
    stale_full = [name for name in names if name.endswith("-full.nupkg") and version_marker not in name]
    if stale_full:
        raise ValueError(f"{channel} output still contains previous full package(s): {sorted(stale_full)}")
    deltas = [name for name in names if name.endswith("-delta.nupkg")]
    if len(deltas) > 1 or any(version_marker not in name for name in deltas):
        raise ValueError(f"{channel} output has an invalid delta package set: {sorted(deltas)}")
    require_one(names, lambda name: name.endswith(policy.portable_suffix), f"{channel} portable artifact")
    for required in (policy.feed, policy.legacy_feed):
        if required not in names:
            raise ValueError(f"{channel} output is missing {required}")

    expected_count = 4 + len(deltas)
    if len(files) != expected_count:
        expected_kinds = "portable, full, optional delta, JSON feed, and legacy feed"
        raise ValueError(
            f"{channel} output contains unexpected assets; expected only {expected_kinds}: {sorted(names)}"
        )
    return files


def get_field(payload: dict[str, object], name: str) -> object | None:
    lowered = name.lower()
    return next((value for key, value in payload.items() if key.lower() == lowered), None)


def verify_json_feed(feed_path: Path, version: str, packages: dict[str, Path]) -> None:
    payload = json.loads(feed_path.read_text(encoding="utf-8-sig"))
    assets = get_field(payload, "assets") if isinstance(payload, dict) else None
    if not isinstance(assets, list) or not assets:
        raise ValueError(f"{feed_path.name} lists no assets")
    described: set[str] = set()
    for entry in assets:
        if not isinstance(entry, dict):
            raise ValueError(f"{feed_path.name} contains a non-object asset entry")
        entry_version = str(get_field(entry, "version") or "")
        file_name = str(get_field(entry, "fileName") or "")
        digest = str(get_field(entry, "sha256") or "")
        size = get_field(entry, "size")
        if entry_version != version:
            raise ValueError(f"{feed_path.name} lists version '{entry_version}', expected '{version}'")
        package = packages.get(file_name)
        if package is None:
            raise ValueError(f"{feed_path.name} references unattached package '{file_name}'")
        if not re.fullmatch(r"[0-9A-Fa-f]{64}", digest) or sha256(package).lower() != digest.lower():
            raise ValueError(f"{feed_path.name} SHA-256 does not match attached package '{file_name}'")
        if not isinstance(size, int) or package.stat().st_size != size:
            raise ValueError(f"{feed_path.name} size does not match attached package '{file_name}'")
        described.add(file_name)
    if described != set(packages):
        raise ValueError(
            f"{feed_path.name} package set mismatch: missing={sorted(set(packages) - described)} "
            f"extra={sorted(described - set(packages))}"
        )


def verify_legacy_feed(feed_path: Path, packages: dict[str, Path]) -> None:
    described: set[str] = set()
    for raw_line in feed_path.read_text(encoding="utf-8-sig").splitlines():
        line = raw_line.strip()
        if not line:
            continue
        match = re.fullmatch(r"([0-9A-Fa-f]{40})\s+(\S+)\s+([0-9]+)", line)
        if match is None:
            raise ValueError(f"{feed_path.name} has an unparseable line: {line}")
        digest, file_name, size_text = match.groups()
        package = packages.get(file_name)
        if package is None:
            raise ValueError(f"{feed_path.name} references unattached package '{file_name}'")
        if hashlib.sha1(package.read_bytes(), usedforsecurity=False).hexdigest().lower() != digest.lower():
            raise ValueError(f"{feed_path.name} SHA-1 does not match attached package '{file_name}'")
        if package.stat().st_size != int(size_text):
            raise ValueError(f"{feed_path.name} size does not match attached package '{file_name}'")
        described.add(file_name)
    if described != set(packages):
        raise ValueError(
            f"{feed_path.name} package set mismatch: missing={sorted(set(packages) - described)} "
            f"extra={sorted(described - set(packages))}"
        )


def verify(version: str, local_roots: dict[str, Path], remote_root: Path) -> None:
    expected: dict[str, Path] = {}
    per_channel_files: dict[str, dict[str, Path]] = {}
    for channel, root in local_roots.items():
        channel_files = expected_channel_assets(channel, root, version)
        policy = POLICIES[channel]
        # Reconcile the remote against only the assets vpk actually publishes: drop the unpublished legacy feed
        # (retained locally, never uploaded) so its absence from the release is not flagged as a mismatch.
        published = {
            name: path
            for name, path in channel_files.items()
            if policy.legacy_feed_published or name != policy.legacy_feed
        }
        duplicates = set(expected).intersection(published)
        if duplicates:
            raise ValueError(f"channel outputs contain duplicate remote asset names: {sorted(duplicates)}")
        expected.update(published)
        per_channel_files[channel] = channel_files

    remote = files_by_name(remote_root, ignore_internal=False)
    if set(expected) != set(remote):
        raise ValueError(
            f"remote asset set mismatch: missing={sorted(set(expected) - set(remote))} "
            f"extra={sorted(set(remote) - set(expected))}"
        )
    for name, local_path in expected.items():
        if name not in FEED_FILES and sha256(local_path) != sha256(remote[name]):
            raise ValueError(f"remote asset bytes differ from retained build: {name}")

    for channel, channel_files in per_channel_files.items():
        policy = POLICIES[channel]
        packages = {name: remote[name] for name in channel_files if name.endswith(".nupkg")}
        verify_json_feed(remote[policy.feed], version, packages)
        # Verify the published legacy feed from the release; the unpublished one from its retained local copy.
        legacy_feed_path = (
            remote[policy.legacy_feed] if policy.legacy_feed_published else channel_files[policy.legacy_feed]
        )
        verify_legacy_feed(legacy_feed_path, packages)


def parse_local(value: str) -> tuple[str, Path]:
    channel, separator, path = value.partition("=")
    if not separator or channel not in POLICIES or not path:
        raise argparse.ArgumentTypeError("--local must be win=<path> or linux=<path>")
    return channel, Path(path)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", required=True)
    parser.add_argument("--local", action="append", type=parse_local, required=True)
    parser.add_argument("--remote-dir", type=Path, required=True)
    args = parser.parse_args()
    local_roots = dict(args.local)
    if set(local_roots) != set(POLICIES):
        raise ValueError("both --local win=<path> and --local linux=<path> are required")
    verify(args.version, local_roots, args.remote_dir)
    print(f"verified {len(files_by_name(args.remote_dir, ignore_internal=False))} remote Velopack primary assets")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
