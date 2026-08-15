#!/usr/bin/env python3
"""Validation helpers for hashed embedded and loose MSBuild publish-input evidence."""

from __future__ import annotations

import json
from pathlib import Path

SCHEMA_VERSION = 2


def load_bundle_packages(path: Path, runtime_identifier: str) -> dict[tuple[str, str], dict]:
    with path.open(encoding="utf-8") as stream:
        document = json.load(stream)
    if not isinstance(document, dict) or document.get("schemaVersion") != SCHEMA_VERSION:
        raise ValueError(f"{path} is not a supported bundle-input evidence document")
    if document.get("runtimeIdentifier") != runtime_identifier:
        raise ValueError("bundle-input evidence runtime identifier does not match the payload")
    expected_modes = {"linux-x64": (True, True), "win-x64": (False, False)}
    try:
        expected_single_file, expected_self_contained = expected_modes[runtime_identifier]
    except KeyError as error:
        raise ValueError(f"unsupported runtime identifier: {runtime_identifier}") from error
    if (document.get("publishSingleFile"), document.get("selfContained")) != (
        expected_single_file,
        expected_self_contained,
    ):
        raise ValueError("bundle-input evidence publish mode does not match the RID contract")
    inputs = document.get("inputs")
    if not isinstance(inputs, list) or not inputs:
        raise ValueError("bundle-input evidence has no publish inputs")

    packages: dict[tuple[str, str], dict] = {}
    seen_inputs: set[tuple[str, str, str]] = set()
    for index, entry in enumerate(inputs):
        if not isinstance(entry, dict):
            raise ValueError(f"bundle-input evidence entry {index} is not an object")
        disposition = entry.get("disposition")
        origin = entry.get("origin")
        if disposition not in {"bundle", "loose"} or origin not in {"nuget", "other"}:
            raise ValueError(f"bundle-input evidence entry {index} has an invalid disposition/origin")
        package_id = entry.get("packageId")
        package_version = entry.get("packageVersion")
        source_type = entry.get("sourceType")
        relative_path = entry.get("relativePath")
        digest = entry.get("sha256")
        if package_id is None and package_version is None:
            if origin == "nuget":
                raise ValueError(f"NuGet-root bundle input entry {index} lacks package identity")
            continue
        if source_type is not None and not isinstance(source_type, str):
            raise ValueError(f"bundle-input evidence entry {index} has an invalid source type")
        if isinstance(source_type, str) and source_type.casefold() == "projectreference":
            continue
        if not (
            isinstance(package_id, str)
            and package_id.strip()
            and isinstance(package_version, str)
            and package_version.strip()
            and isinstance(relative_path, str)
            and relative_path.strip()
        ):
            raise ValueError(f"bundle-input evidence entry {index} has an incomplete package identity")
        if (
            not isinstance(digest, str)
            or len(digest) != 64
            or any(character not in "0123456789abcdef" for character in digest)
        ):
            raise ValueError(f"bundle-input evidence entry {index} has an invalid SHA-256")
        input_key = (package_id.casefold(), package_version, f"{disposition}:{relative_path}")
        if input_key in seen_inputs:
            raise ValueError(
                f"bundle-input evidence contains duplicate input {package_id}/{package_version}:{relative_path}"
            )
        seen_inputs.add(input_key)
        package_key = (package_id.casefold(), package_version)
        package = packages.setdefault(package_key, {"name": package_id, "inputs": []})
        if package["name"] != package_id:
            raise ValueError(f"bundle-input package casing is inconsistent for {package_id}/{package_version}")
        package["inputs"].append({"disposition": disposition, "relativePath": relative_path, "sha256": digest})
    if not packages:
        raise ValueError("bundle-input evidence contains no NuGet-owned inputs")
    for package in packages.values():
        package["inputs"].sort(key=lambda entry: (entry["relativePath"].casefold(), entry["relativePath"]))
    return packages
