#!/usr/bin/env python3
from __future__ import annotations

import json
import subprocess
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[3]
VERIFIER = REPO_ROOT / "scripts" / "compliance" / "verify-dotnet-runtime-floor.py"


class RuntimeFloorVerifierTests(unittest.TestCase):
    def run_verifier(self, floor: str, latest: str) -> subprocess.CompletedProcess[str]:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            props = root / "ReleaseVersion.props"
            metadata = root / "releases.json"
            props.write_text(
                f"<Project><PropertyGroup><DotNetRuntimeVersion>{floor}</DotNetRuntimeVersion></PropertyGroup></Project>",
                encoding="utf-8",
            )
            metadata.write_text(json.dumps({"latest-release": latest}), encoding="utf-8")
            return subprocess.run(
                [str(VERIFIER), "--props", str(props), "--metadata-file", str(metadata)],
                check=False,
                capture_output=True,
                text=True,
            )

    def test_current_floor_passes(self) -> None:
        result = self.run_verifier("10.0.10", "10.0.10")
        self.assertEqual(0, result.returncode, result.stderr)

    def test_superseded_floor_fails_closed(self) -> None:
        result = self.run_verifier("10.0.9", "10.0.10")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("below latest supported servicing release", result.stderr)

    def test_newer_floor_is_not_downgraded(self) -> None:
        result = self.run_verifier("10.0.11", "10.0.10")
        self.assertEqual(0, result.returncode, result.stderr)


if __name__ == "__main__":
    unittest.main()
