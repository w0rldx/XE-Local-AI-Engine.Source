#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import re
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

MODULE_PATH = Path(__file__).resolve().parents[1] / "read-release-version.py"
PACKAGE_SCRIPT = Path(__file__).resolve().parents[2] / "publish" / "package-rc.sh"
SPEC = importlib.util.spec_from_file_location("read_release_version", MODULE_PATH)
assert SPEC is not None
assert SPEC.loader is not None
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
            "<Project><PropertyGroup><VersionPrefix>1.2.3</VersionPrefix><VersionSuffix /></PropertyGroup></Project>"
        )
        self.assertEqual("1.2.3", MODULE.read_version(path))

    def test_invalid_semver_fails_closed(self) -> None:
        path = self.manifest("<Project><PropertyGroup><VersionPrefix>1.2</VersionPrefix></PropertyGroup></Project>")
        with self.assertRaisesRegex(ValueError, "invalid SemVer"):
            MODULE.read_version(path)

    def test_dotnet_runtime_version_comes_from_the_same_manifest(self) -> None:
        path = self.manifest(
            "<Project><PropertyGroup><VersionPrefix>1.2.3</VersionPrefix>"
            "<DotNetRuntimeVersion>10.0.10</DotNetRuntimeVersion></PropertyGroup></Project>"
        )
        self.assertEqual("10.0.10", MODULE.read_dotnet_runtime_version(path))

    def test_invalid_dotnet_runtime_version_fails_closed(self) -> None:
        path = self.manifest(
            "<Project><PropertyGroup><VersionPrefix>1.2.3</VersionPrefix>"
            "<DotNetRuntimeVersion>10.0</DotNetRuntimeVersion></PropertyGroup></Project>"
        )
        with self.assertRaisesRegex(ValueError, "invalid DotNetRuntimeVersion"):
            MODULE.read_dotnet_runtime_version(path)

    def test_deprecated_package_script_reads_the_canonical_manifest(self) -> None:
        package_source = PACKAGE_SCRIPT.read_text(encoding="utf-8")
        function = re.search(r"^read_version\(\) \{\n(?P<body>.*?)^\}\n", package_source, re.MULTILINE | re.DOTALL)
        self.assertIsNotNone(function)

        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        repository = Path(directory.name)
        (repository / "eng").mkdir()
        (repository / "scripts").mkdir()
        (repository / "eng" / "ReleaseVersion.props").write_text(
            "<Project><PropertyGroup><VersionPrefix>9.8.7</VersionPrefix>"
            "<VersionSuffix>rc.6</VersionSuffix></PropertyGroup></Project>",
            encoding="utf-8",
        )
        (repository / "Directory.Build.props").write_text(
            "<Project><PropertyGroup><VersionPrefix>1.2.3</VersionPrefix>"
            "<VersionSuffix>legacy.1</VersionSuffix></PropertyGroup></Project>",
            encoding="utf-8",
        )
        shutil.copy2(MODULE_PATH, repository / "scripts" / "read-release-version.py")

        shell = f"set -euo pipefail\nREPO_ROOT=$1\nread_version() {{\n{function.group('body')}}}\nread_version\n"
        result = subprocess.run(
            ["bash", "-c", shell, "read-version-test", str(repository)],
            check=True,
            capture_output=True,
            text=True,
        )
        self.assertEqual("9.8.7-rc.6", result.stdout.strip())


if __name__ == "__main__":
    unittest.main()
