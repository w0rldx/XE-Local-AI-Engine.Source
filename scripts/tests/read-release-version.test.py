#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import tempfile
import unittest
from pathlib import Path


MODULE_PATH = Path(__file__).resolve().parents[1] / "read-release-version.py"
SPEC = importlib.util.spec_from_file_location("read_release_version", MODULE_PATH)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class ReleaseVersionReaderTests(unittest.TestCase):
    def manifest(self, content: str) -> Path:
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        path = Path(directory.name) / "ReleaseVersion.props"
        path.write_text(content, encoding="utf-8")
        return path

    def test_prerelease_suffix_is_composed(self) -> None:
        path = self.manifest(
            "<Project><PropertyGroup><VersionPrefix>1.2.3</VersionPrefix>"
            "<VersionSuffix>rc.4</VersionSuffix></PropertyGroup></Project>"
        )
        self.assertEqual("1.2.3-rc.4", MODULE.read_version(path))

    def test_stable_version_allows_absent_suffix(self) -> None:
        path = self.manifest("<Project><PropertyGroup><VersionPrefix>1.2.3</VersionPrefix></PropertyGroup></Project>")
        self.assertEqual("1.2.3", MODULE.read_version(path))

    def test_stable_version_allows_empty_suffix(self) -> None:
        path = self.manifest(
            "<Project><PropertyGroup><VersionPrefix>1.2.3</VersionPrefix>"
            "<VersionSuffix /></PropertyGroup></Project>"
        )
        self.assertEqual("1.2.3", MODULE.read_version(path))

    def test_invalid_semver_fails_closed(self) -> None:
        path = self.manifest("<Project><PropertyGroup><VersionPrefix>1.2</VersionPrefix></PropertyGroup></Project>")
        with self.assertRaisesRegex(ValueError, "invalid SemVer"):
            MODULE.read_version(path)


if __name__ == "__main__":
    unittest.main()
