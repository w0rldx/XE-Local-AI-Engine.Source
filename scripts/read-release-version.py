#!/usr/bin/env python3
"""Read and validate the application version from the canonical release manifest."""

from __future__ import annotations

import argparse
import re
import xml.etree.ElementTree as ET
from pathlib import Path


SEMVER = re.compile(
    r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)"
    r"(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$"
)
DOTNET_RUNTIME_VERSION = re.compile(r"^[0-9]+\.[0-9]+\.[0-9]+$")


def optional_property(root: ET.Element, name: str) -> str:
    values = [(element.text or "").strip() for element in root.iter(name)]
    if len(values) > 1:
        raise ValueError(f"release manifest contains more than one {name}")
    return values[0] if values else ""


def read_version(path: Path) -> str:
    root = ET.parse(path).getroot()
    prefix = optional_property(root, "VersionPrefix")
    suffix = optional_property(root, "VersionSuffix")
    if not prefix:
        raise ValueError("release manifest VersionPrefix is missing or empty")
    version = f"{prefix}-{suffix}" if suffix else prefix
    if SEMVER.fullmatch(version) is None:
        raise ValueError(f"release manifest produced invalid SemVer '{version}'")
    return version


def read_dotnet_runtime_version(path: Path) -> str:
    root = ET.parse(path).getroot()
    version = optional_property(root, "DotNetRuntimeVersion")
    if DOTNET_RUNTIME_VERSION.fullmatch(version) is None:
        raise ValueError(f"release manifest produced invalid DotNetRuntimeVersion '{version}'")
    return version


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--props", type=Path, default=Path("eng/ReleaseVersion.props"))
    parser.add_argument("--dotnet-runtime", action="store_true")
    args = parser.parse_args()
    print(read_dotnet_runtime_version(args.props) if args.dotnet_runtime else read_version(args.props))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
