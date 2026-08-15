#!/usr/bin/env python3
"""Verify a downloaded GitHub release draft's detached compliance envelope."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path

CHECKSUM_NAME = "CHECKSUMS.sha256"
MANIFEST_NAME = "RELEASE-MANIFEST.json"
SPDX_NAME = "RELEASE.spdx.json"
METADATA_NAMES = {CHECKSUM_NAME, MANIFEST_NAME, SPDX_NAME}
SELF_EXCLUSIONS = [MANIFEST_NAME, CHECKSUM_NAME]


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def load_object(path: Path) -> dict[str, object]:
    payload = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(payload, dict):
        raise ValueError(f"{path.name} must contain a JSON object")
    return payload


def top_level_files(root: Path) -> dict[str, Path]:
    files = {path.name: path for path in root.iterdir() if path.is_file()}
    missing = METADATA_NAMES - set(files)
    if missing:
        raise ValueError(f"release envelope is missing metadata: {sorted(missing)}")
    return files


def parse_checksums(path: Path) -> dict[str, str]:
    checksums: dict[str, str] = {}
    for line_number, raw_line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
        if not raw_line.strip():
            continue
        match = re.fullmatch(r"([0-9A-Fa-f]{64})[ \t]+\*?(.+)", raw_line)
        if match is None:
            raise ValueError(f"invalid checksum line {line_number}")
        digest, raw_name = match.groups()
        name = raw_name.removeprefix("./")
        if name in checksums or Path(name).name != name or name == CHECKSUM_NAME:
            raise ValueError(f"invalid or duplicate checksum asset name: {raw_name}")
        checksums[name] = digest.lower()
    return checksums


def parse_manifest_assets(payload: dict[str, object]) -> dict[str, tuple[int, str]]:
    raw_assets = payload.get("assets")
    if not isinstance(raw_assets, list):
        raise ValueError("release manifest assets must be an array")
    assets: dict[str, tuple[int, str]] = {}
    for raw_asset in raw_assets:
        if not isinstance(raw_asset, dict):
            raise ValueError("release manifest contains a non-object asset")
        name = raw_asset.get("name")
        size = raw_asset.get("size")
        digest = raw_asset.get("sha256")
        if (
            not isinstance(name, str)
            or Path(name).name != name
            or name in assets
            or not isinstance(size, int)
            or size < 0
            or not isinstance(digest, str)
            or re.fullmatch(r"[0-9A-Fa-f]{64}", digest) is None
        ):
            raise ValueError(f"invalid or duplicate release manifest asset: {name!r}")
        assets[name] = (size, digest.lower())
    return assets


def parse_spdx_files(payload: dict[str, object]) -> dict[str, str]:
    if payload.get("spdxVersion") != "SPDX-2.2":
        raise ValueError("release SPDX must use SPDX-2.2")
    raw_files = payload.get("files")
    if not isinstance(raw_files, list):
        raise ValueError("release SPDX files must be an array")
    files: dict[str, str] = {}
    for raw_file in raw_files:
        if not isinstance(raw_file, dict):
            raise ValueError("release SPDX contains a non-object file")
        raw_name = raw_file.get("fileName")
        name = raw_name.removeprefix("./") if isinstance(raw_name, str) else ""
        checksums = raw_file.get("checksums")
        digest = None
        if isinstance(checksums, list):
            for checksum in checksums:
                if isinstance(checksum, dict) and str(checksum.get("algorithm", "")).upper() == "SHA256":
                    digest = checksum.get("checksumValue")
                    break
        if (
            not name
            or Path(name).name != name
            or name in files
            or not isinstance(digest, str)
            or re.fullmatch(r"[0-9A-Fa-f]{64}", digest) is None
        ):
            raise ValueError(f"invalid or duplicate SPDX file entry: {raw_name!r}")
        files[name] = digest.lower()
    return files


def verify(root: Path, expected_tag: str, expected_source_sha: str) -> None:
    files = top_level_files(root)
    actual_names = set(files)

    checksums = parse_checksums(files[CHECKSUM_NAME])
    expected_checksum_names = actual_names - {CHECKSUM_NAME}
    if set(checksums) != expected_checksum_names:
        raise ValueError(
            "checksum asset set mismatch: "
            f"missing={sorted(expected_checksum_names - set(checksums))} "
            f"extra={sorted(set(checksums) - expected_checksum_names)}"
        )
    for name, digest in checksums.items():
        if sha256(files[name]) != digest:
            raise ValueError(f"checksum mismatch for {name}")

    manifest = load_object(files[MANIFEST_NAME])
    if (
        manifest.get("schemaVersion") != 1
        or manifest.get("tag") != expected_tag
        or manifest.get("sourceSha") != expected_source_sha
    ):
        raise ValueError("release identity mismatch in release manifest")
    if manifest.get("selfExclusions") != SELF_EXCLUSIONS:
        raise ValueError("release manifest self-exclusions do not match the versioned contract")
    signing = manifest.get("signing")
    if not isinstance(signing, dict) or signing.get("state") not in {"signed", "unsigned"}:
        raise ValueError("release manifest signing state is missing or invalid")
    if signing.get("state") == "unsigned" and signing.get("decisionGate") != "signing-risk-decision":
        raise ValueError("unsigned release manifest is missing its risk-decision gate")

    manifest_assets = parse_manifest_assets(manifest)
    expected_manifest_names = actual_names - {MANIFEST_NAME, CHECKSUM_NAME}
    if set(manifest_assets) != expected_manifest_names:
        raise ValueError(
            "manifest asset set mismatch: "
            f"missing={sorted(expected_manifest_names - set(manifest_assets))} "
            f"extra={sorted(set(manifest_assets) - expected_manifest_names)}"
        )
    for name, (size, digest) in manifest_assets.items():
        if files[name].stat().st_size != size or sha256(files[name]) != digest:
            raise ValueError(f"manifest size or checksum mismatch for {name}")

    spdx_files = parse_spdx_files(load_object(files[SPDX_NAME]))
    primary_names = actual_names - METADATA_NAMES
    if set(spdx_files) != primary_names:
        raise ValueError(
            "SPDX primary asset set mismatch: "
            f"missing={sorted(primary_names - set(spdx_files))} "
            f"extra={sorted(set(spdx_files) - primary_names)}"
        )
    for name, digest in spdx_files.items():
        if sha256(files[name]) != digest:
            raise ValueError(f"SPDX checksum mismatch for {name}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--directory", required=True, type=Path)
    parser.add_argument("--tag", required=True)
    parser.add_argument("--source-sha", required=True)
    args = parser.parse_args()
    try:
        verify(args.directory, args.tag, args.source_sha)
    except (OSError, ValueError, json.JSONDecodeError) as exception:
        print(f"ERROR: {exception}", file=sys.stderr)
        return 1
    print(f"verified release envelope for {args.tag} ({len(top_level_files(args.directory))} assets)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
