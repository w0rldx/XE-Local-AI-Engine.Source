#!/usr/bin/env python3
"""Fail closed when the release runtime floor is below Microsoft's supported .NET 10 patch."""

from __future__ import annotations

import argparse
import json
import sys
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path

DEFAULT_METADATA_URL = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json"


def parse_version(value: str, label: str) -> tuple[int, int, int]:
    parts = value.strip().split(".")
    if len(parts) != 3 or any(not part.isdigit() for part in parts):
        raise ValueError(f"{label} must be a three-part numeric version, got {value!r}")
    return tuple(int(part) for part in parts)  # type: ignore[return-value]


def read_floor(props_path: Path) -> str:
    root = ET.parse(props_path).getroot()
    values = [element.text.strip() for element in root.iter("DotNetRuntimeVersion") if element.text]
    if len(values) != 1:
        raise ValueError(f"{props_path} must declare exactly one DotNetRuntimeVersion")
    return values[0]


def read_metadata(args: argparse.Namespace) -> dict[str, object]:
    if args.metadata_file:
        return json.loads(args.metadata_file.read_text(encoding="utf-8"))

    url = args.metadata_url
    if urllib.parse.urlsplit(url).scheme not in ("http", "https"):
        raise ValueError(f"release metadata URL must use http or https, got {url!r}")

    try:
        # Scheme restricted to http(s) directly above; file:/custom schemes are unreachable here.
        with urllib.request.urlopen(url, timeout=args.timeout_seconds) as response:  # noqa: S310  # nosec B310
            return json.load(response)
    except (OSError, urllib.error.URLError, json.JSONDecodeError) as error:
        raise RuntimeError(f"could not retrieve authoritative .NET release metadata: {error}") from error


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--props", type=Path, default=Path("eng/ReleaseVersion.props"))
    parser.add_argument("--metadata-file", type=Path)
    parser.add_argument("--metadata-url", default=DEFAULT_METADATA_URL)
    parser.add_argument("--timeout-seconds", type=float, default=30)
    args = parser.parse_args()

    try:
        floor_text = read_floor(args.props)
        metadata = read_metadata(args)
        latest_value = metadata.get("latest-release")
        if not isinstance(latest_value, str):
            raise ValueError("release metadata is missing the latest-release string")

        floor = parse_version(floor_text, "DotNetRuntimeVersion")
        latest = parse_version(latest_value, "latest-release")
        if floor[:2] != latest[:2]:
            raise ValueError(
                f"runtime floor {floor_text} and metadata release {latest_value} are not on the same feature band"
            )
        if floor < latest:
            raise ValueError(f"runtime floor {floor_text} is below latest supported servicing release {latest_value}")
    except (OSError, ET.ParseError, ValueError, RuntimeError) as error:
        print(f"runtime-floor verification failed: {error}", file=sys.stderr)
        return 1

    print(f"runtime floor {floor_text} satisfies official .NET 10 release metadata ({latest_value})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
