#!/usr/bin/env python3
"""Merge Cobertura line coverage without double-counting repeated source lines."""

from __future__ import annotations

import argparse
from pathlib import Path
import xml.etree.ElementTree as ET


def merge_reports(paths: list[Path]) -> tuple[int, int]:
    lines: dict[tuple[str, int], bool] = {}
    for path in paths:
        root = ET.parse(path).getroot()
        for class_node in root.findall(".//class"):
            filename = class_node.attrib.get("filename", "").replace("\\", "/")
            for line in class_node.findall("./lines/line"):
                number = int(line.attrib["number"])
                covered = int(line.attrib.get("hits", "0")) > 0
                key = (filename, number)
                lines[key] = lines.get(key, False) or covered
    return sum(lines.values()), len(lines)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--minimum-file", type=Path, required=True)
    parser.add_argument("reports", nargs="+", type=Path)
    args = parser.parse_args()
    if any(not path.is_file() for path in args.reports):
        parser.error("every Cobertura report must exist")
    covered, valid = merge_reports(args.reports)
    if valid == 0:
        raise SystemExit("[coverage] FAIL: current Cobertura reports contain zero source lines")
    minimum = float(args.minimum_file.read_text(encoding="utf-8").strip())
    percent = covered * 100.0 / valid
    print(
        f"[coverage] backend line coverage: {percent:.2f}% "
        f"({covered}/{valid} unique source lines across {len(args.reports)} current report(s))"
    )
    print(f"[coverage] committed minimum: {minimum:.2f}%")
    if percent + 1e-9 < minimum:
        print(f"[coverage] FAIL: {percent:.2f}% is below {minimum:.2f}%")
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
