#!/usr/bin/env python3
from __future__ import annotations

import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
CENTRAL_PACKAGES = REPO_ROOT / "Directory.Packages.props"
APPHOST_PROJECT = REPO_ROOT / "XE-Local-AI-Engine.AppHost" / "XE-Local-AI-Engine.AppHost.csproj"

ASPIRE_RELEASE_VERSION = "13.5.3"
ASPIRE_BROWSERS_VERSION = "13.5.3-preview.1.26425.3"
SQLITE_TOOLKIT_VERSION = "13.5.0"


def central_package_versions() -> dict[str, str]:
    root = ET.parse(CENTRAL_PACKAGES).getroot()
    return {
        package.attrib["Include"]: package.attrib["Version"]
        for package in root.findall(".//PackageVersion")
        if "Include" in package.attrib and "Version" in package.attrib
    }


def apphost_sdk_version() -> str:
    root = ET.parse(APPHOST_PROJECT).getroot()
    sdk = root.find("Sdk")
    if sdk is None or sdk.attrib.get("Name") != "Aspire.AppHost.Sdk":
        raise AssertionError("AppHost project must declare Aspire.AppHost.Sdk")
    return sdk.attrib["Version"]


class AspireVersionTupleContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.packages = central_package_versions()

    def test_apphost_sdk_and_stable_hosting_packages_share_release_version(self) -> None:
        versions = {
            "Aspire.AppHost.Sdk": apphost_sdk_version(),
            "Aspire.Hosting.AppHost": self.packages["Aspire.Hosting.AppHost"],
            "Aspire.Hosting.JavaScript": self.packages["Aspire.Hosting.JavaScript"],
        }

        self.assertEqual(
            {ASPIRE_RELEASE_VERSION},
            set(versions.values()),
            f"Aspire stable release tuple drifted: {versions}",
        )

    def test_browsers_package_uses_authoritative_servicing_build(self) -> None:
        browsers_version = self.packages["Aspire.Hosting.Browsers"]

        self.assertEqual(ASPIRE_BROWSERS_VERSION, browsers_version)
        self.assertTrue(
            browsers_version.startswith(f"{ASPIRE_RELEASE_VERSION}-preview."),
            f"Aspire.Hosting.Browsers left the {ASPIRE_RELEASE_VERSION} servicing family",
        )

    def test_community_toolkit_sqlite_pin_is_deliberate(self) -> None:
        """CommunityToolkit now follows the Aspire release line (13.5.x), so only the exact pin is asserted.

        This used to also assert the version differed from the Aspire release version. That guard
        stopped meaning anything once the two started moving together, so it is gone deliberately.
        """
        sqlite_version = self.packages["CommunityToolkit.Aspire.Hosting.Sqlite"]

        self.assertEqual(SQLITE_TOOLKIT_VERSION, sqlite_version)


if __name__ == "__main__":
    unittest.main()
