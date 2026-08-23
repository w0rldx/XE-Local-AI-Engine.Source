#!/usr/bin/env python3
from __future__ import annotations

import fnmatch
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Any

REPO_ROOT = Path(__file__).resolve().parents[2]
CENTRAL_PACKAGES = REPO_ROOT / "Directory.Packages.props"
DEPENDABOT = REPO_ROOT / ".github" / "dependabot.yml"

MCP_PACKAGES = ("ModelContextProtocol", "ModelContextProtocol.AspNetCore")


def central_package_versions() -> dict[str, str]:
    root = ET.parse(CENTRAL_PACKAGES).getroot()
    return {
        package.attrib["Include"]: package.attrib["Version"]
        for package in root.findall(".//PackageVersion")
        if "Include" in package.attrib and "Version" in package.attrib
    }


def nuget_groups() -> dict[str, dict[str, Any]]:
    # dependabot-groups.test.py owns the general YAML contract; this only needs the
    # nuget group patterns, which are a flat two-level block.
    groups: dict[str, dict[str, list[str]]] = {}
    ecosystem: str | None = None
    group: str | None = None
    key: str | None = None
    for raw in DEPENDABOT.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        indent = len(raw) - len(raw.lstrip(" "))
        if line.startswith("- package-ecosystem:"):
            ecosystem = line.split(":", 1)[1].strip()
            group = key = None
            continue
        if ecosystem != "nuget":
            continue
        if indent == 6 and line.endswith(":"):
            group = line[:-1]
            key = None
            groups.setdefault(group, {})
        elif indent == 8 and line.endswith(":") and group is not None:
            key = line[:-1]
            groups[group][key] = []
        elif line.startswith("- ") and group is not None and key is not None:
            groups[group][key].append(line[2:].strip().strip("\"'"))
    return groups


def matches(group: dict[str, Any], dependency: str) -> bool:
    patterns = group.get("patterns", ["*"])
    excluded = group.get("exclude-patterns", [])
    return any(fnmatch.fnmatchcase(dependency, pattern) for pattern in patterns) and not any(
        fnmatch.fnmatchcase(dependency, pattern) for pattern in excluded
    )


class McpVersionTupleContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.packages = central_package_versions()
        cls.groups = nuget_groups()

    def test_mcp_packages_share_one_version(self) -> None:
        versions = {name: self.packages[name] for name in MCP_PACKAGES}

        self.assertEqual(
            1,
            len(set(versions.values())),
            "ModelContextProtocol.AspNetCore pins ModelContextProtocol exactly; "
            f"a split version fails restore: {versions}",
        )

    def test_mcp_pair_is_isolated_from_the_nuget_catch_all(self) -> None:
        catch_all = self.groups["nuget-remaining"]
        coupled = self.groups["mcp-coupled"]

        for package in MCP_PACKAGES:
            self.assertTrue(matches(coupled, package), f"{package} left the mcp-coupled group")
            self.assertFalse(
                matches(catch_all, package),
                f"{package} can be bumped alone by nuget-remaining",
            )


if __name__ == "__main__":
    unittest.main()
