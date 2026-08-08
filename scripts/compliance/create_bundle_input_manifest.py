#!/usr/bin/env python3
"""Convert the MSBuild FilesToBundle capture into deterministic hashed JSON evidence."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


def create_manifest(raw_path: Path, runtime_identifier: str, output_path: Path) -> int:
    lines = raw_path.read_text(encoding="utf-8-sig").splitlines()
    header = lines[0].split("|", 4) if lines else []
    expected_modes = {"linux-x64": (True, True), "win-x64": (False, False)}
    expected_single_file, expected_self_contained = expected_modes[runtime_identifier]
    expected_header = [
        "XE-BUNDLE-INPUTS-V2",
        runtime_identifier,
        str(expected_single_file).lower(),
        str(expected_self_contained).lower(),
    ]
    if len(header) != 5 or [value.casefold() for value in header[:4]] != [value.casefold() for value in expected_header]:
        raise ValueError(f"publish-input capture header does not match {'|'.join(expected_header)}|<NuGetPackageRoot>")
    nuget_package_root = Path(header[4]).resolve()
    if not nuget_package_root.is_dir():
        raise ValueError(f"bundle-input capture NuGetPackageRoot is missing: {nuget_package_root}")
    inputs: list[dict] = []
    seen: dict[tuple[str | None, str | None, str], tuple[str, ...]] = {}
    for number, line in enumerate(lines[1:], 2):
        fields = line.split("|", 5)
        if len(fields) != 6:
            raise ValueError(f"bundle-input capture line {number} is malformed")
        disposition, package_id, package_version, source_type, relative_path, source_value = fields
        if disposition not in {"bundle", "loose"}:
            raise ValueError(f"bundle-input capture line {number} has an invalid disposition")
        source = Path(source_value)
        if not relative_path or not source.is_file():
            raise ValueError(f"bundle-input capture line {number} references a missing input: {source}")
        if bool(package_id) != bool(package_version):
            raise ValueError(f"bundle-input capture line {number} has an incomplete NuGet identity")
        try:
            source.absolute().relative_to(nuget_package_root.absolute())
            origin = "nuget"
        except ValueError:
            origin = "other"
        if origin == "nuget" and not package_id:
            raise ValueError(f"NuGet-root bundle input lacks package identity: {source}")
        key = (package_id.casefold() if package_id else None, package_version or None, f"{disposition}:{relative_path}")
        capture_identity = (
            disposition,
            package_id,
            package_version,
            source_type,
            relative_path,
            str(source.resolve()),
        )
        if key in seen:
            if seen[key] == capture_identity:
                continue
            raise ValueError(f"bundle-input capture contains duplicate relative input: {relative_path}")
        seen[key] = capture_identity
        inputs.append(
            {
                "packageId": package_id or None,
                "packageVersion": package_version or None,
                "disposition": disposition,
                "origin": origin,
                "relativePath": relative_path,
                "sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
                "sourceType": source_type or None,
            }
        )
    inputs.sort(
        key=lambda entry: (
            (entry["packageId"] or "").casefold(),
            entry["packageVersion"] or "",
            entry["disposition"],
            entry["relativePath"].casefold(),
            entry["relativePath"],
        )
    )
    document = {
        "$generated": "Captured from MSBuild FilesToBundle and loose ResolvedFileToPublish inputs at the RID-specific publish boundary.",
        "inputs": inputs,
        "publishSingleFile": expected_single_file,
        "runtimeIdentifier": runtime_identifier,
        "schemaVersion": 2,
        "selfContained": expected_self_contained,
    }
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(document, indent=2, sort_keys=True) + "\n", encoding="utf-8", newline="\n")
    return len(inputs)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--rid", choices=("linux-x64", "win-x64"), required=True)
    parser.add_argument("--raw", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    count = create_manifest(args.raw, args.rid, args.output)
    print(f"created {args.rid} publish-input evidence for {count} files")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
