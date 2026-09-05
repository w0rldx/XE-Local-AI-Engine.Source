#!/usr/bin/env python3

from __future__ import annotations

import importlib.util
import json
import tempfile
import unittest
from pathlib import Path
from typing import Any


def load(name: str):
    path = Path(__file__).parents[1] / f"{name}.py"
    spec = importlib.util.spec_from_file_location(name, path)
    assert spec is not None
    assert spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


CREATE = load("create_bundle_input_manifest")
EVIDENCE = load("bundle_input_evidence")


class BundleInputEvidenceTests(unittest.TestCase):
    def test_framework_dependent_windows_payload_uses_loose_publish_inputs(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            nuget_root = root / "packages"
            package_asset = nuget_root / "example.package" / "1.0.0" / "lib" / "net10.0" / "Example.dll"
            package_asset.parent.mkdir(parents=True)
            package_asset.write_bytes(b"managed package")
            app_dll = root / "XE-Local-AI-Engine.Client.dll"
            app_dll.write_bytes(b"managed app")
            raw = root / "inputs.txt"
            raw.write_text(
                f"XE-BUNDLE-INPUTS-V2|win-x64|false|false|{nuget_root}\n"
                f"loose||||XE-Local-AI-Engine.Client.dll|{app_dll}\n"
                f"loose|Example.Package|1.0.0|PackageReference|Example.dll|{package_asset}\n",
                encoding="utf-8",
            )

            output = root / "bundle-inputs.json"
            self.assertEqual(2, CREATE.create_manifest(raw, "win-x64", output))
            document = json.loads(output.read_text(encoding="utf-8"))
            self.assertFalse(document["publishSingleFile"])
            self.assertFalse(document["selfContained"])
            packages = EVIDENCE.load_bundle_packages(output, "win-x64")
            self.assertEqual(
                ["loose"], [entry["disposition"] for entry in packages[("example.package", "1.0.0")]["inputs"]]
            )

    def test_collapses_only_byte_identical_duplicate_capture_rows(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            nuget_root = root / "packages"
            nuget_root.mkdir()
            asset = root / "wwwroot" / "asset.js"
            asset.parent.mkdir()
            asset.write_bytes(b"asset")
            raw = root / "inputs.txt"
            row = f"loose||||wwwroot/asset.js|{asset}\n"
            raw.write_text(
                f"XE-BUNDLE-INPUTS-V2|linux-x64|true|true|{nuget_root}\n{row}{row}",
                encoding="utf-8",
            )

            output = root / "bundle-inputs.json"
            self.assertEqual(1, CREATE.create_manifest(raw, "linux-x64", output))
            inputs = json.loads(output.read_text(encoding="utf-8"))["inputs"]
            self.assertEqual(["wwwroot/asset.js"], [entry["relativePath"] for entry in inputs])

            other_asset = root / "other" / "asset.js"
            other_asset.parent.mkdir()
            other_asset.write_bytes(b"different")
            raw.write_text(
                f"XE-BUNDLE-INPUTS-V2|linux-x64|true|true|{nuget_root}\n{row}loose||||wwwroot/asset.js|{other_asset}\n",
                encoding="utf-8",
            )
            with self.assertRaisesRegex(ValueError, "duplicate relative input"):
                CREATE.create_manifest(raw, "linux-x64", output)

    def test_hashes_real_pre_bundle_inputs_and_preserves_embedded_package_identity(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            app = root / "app.dll"
            nuget_root = root / "packages"
            embedded = nuget_root / "devui/1.0.0/Microsoft.Agents.AI.DevUI.dll"
            loose = nuget_root / "devui/1.0.0/content/devui.json"
            project = root / "Project.dll"
            app.write_bytes(b"app")
            embedded.parent.mkdir(parents=True)
            embedded.write_bytes(b"embedded dev ui")
            loose.parent.mkdir(parents=True)
            loose.write_bytes(b"loose package content")
            project.write_bytes(b"project")
            raw = root / "inputs.txt"
            raw.write_text(
                f"XE-BUNDLE-INPUTS-V2|linux-x64|true|true|{nuget_root}\n"
                f"bundle||||app.dll|{app}\n"
                f"bundle|Microsoft.Agents.AI.DevUI|1.15.0-preview|PackageReference|Microsoft.Agents.AI.DevUI.dll|{embedded}\n"
                f"loose|Microsoft.Agents.AI.DevUI|1.15.0-preview|PackageReference|content/devui.json|{loose}\n"
                f"bundle|Local.Project|1.0.0|ProjectReference|Project.dll|{project}\n",
                encoding="utf-8",
            )
            output = root / "bundle-inputs.json"
            self.assertEqual(4, CREATE.create_manifest(raw, "linux-x64", output))
            packages = EVIDENCE.load_bundle_packages(output, "linux-x64")
            package = packages[("microsoft.agents.ai.devui", "1.15.0-preview")]
            self.assertEqual("Microsoft.Agents.AI.DevUI", package["name"])
            self.assertEqual({"bundle", "loose"}, {entry["disposition"] for entry in package["inputs"]})
            self.assertTrue(all(len(entry["sha256"]) == 64 for entry in package["inputs"]))
            self.assertNotIn(("local.project", "1.0.0"), packages)

            missing_identity = root / "missing-identity.txt"
            missing_identity.write_text(
                f"XE-BUNDLE-INPUTS-V2|linux-x64|true|true|{nuget_root}\nloose||||content/devui.json|{loose}\n",
                encoding="utf-8",
            )
            with self.assertRaisesRegex(ValueError, "NuGet-root bundle input lacks package identity"):
                CREATE.create_manifest(missing_identity, "linux-x64", root / "invalid.json")

    def test_rejects_wrong_rid_incomplete_identity_and_duplicate_inputs(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            evidence = root / "evidence.json"
            baseline: dict[str, Any] = {
                "schemaVersion": 2,
                "runtimeIdentifier": "linux-x64",
                "publishSingleFile": True,
                "selfContained": True,
                "inputs": [
                    {
                        "packageId": "Example",
                        "packageVersion": "1.0.0",
                        "disposition": "bundle",
                        "origin": "nuget",
                        "relativePath": "Example.dll",
                        "sha256": "a" * 64,
                        "sourceType": "PackageReference",
                    }
                ],
            }
            evidence.write_text(json.dumps(baseline))
            with self.assertRaisesRegex(ValueError, "runtime identifier"):
                EVIDENCE.load_bundle_packages(evidence, "win-x64")
            baseline["inputs"][0]["packageVersion"] = None
            evidence.write_text(json.dumps(baseline))
            with self.assertRaisesRegex(ValueError, "incomplete package identity"):
                EVIDENCE.load_bundle_packages(evidence, "linux-x64")
            baseline["inputs"][0]["packageVersion"] = "1.0.0"
            baseline["inputs"].append(dict(baseline["inputs"][0]))
            evidence.write_text(json.dumps(baseline))
            with self.assertRaisesRegex(ValueError, "duplicate input"):
                EVIDENCE.load_bundle_packages(evidence, "linux-x64")

    def test_rejects_publish_mode_that_does_not_match_the_rid_contract(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            evidence = Path(temporary_directory) / "evidence.json"
            baseline: dict[str, Any] = {
                "schemaVersion": 2,
                "runtimeIdentifier": "win-x64",
                "publishSingleFile": True,
                "selfContained": True,
                "inputs": [
                    {
                        "packageId": "Example",
                        "packageVersion": "1.0.0",
                        "disposition": "bundle",
                        "origin": "nuget",
                        "relativePath": "Example.dll",
                        "sha256": "a" * 64,
                        "sourceType": "PackageReference",
                    }
                ],
            }
            evidence.write_text(json.dumps(baseline))
            with self.assertRaisesRegex(ValueError, "publish mode"):
                EVIDENCE.load_bundle_packages(evidence, "win-x64")
            baseline["publishSingleFile"] = False
            baseline["selfContained"] = False
            baseline["inputs"] = [
                {
                    "packageId": None,
                    "packageVersion": None,
                    "disposition": "loose",
                    "origin": "nuget",
                    "relativePath": "content/unidentified.dat",
                    "sha256": "b" * 64,
                    "sourceType": None,
                }
            ]
            evidence.write_text(json.dumps(baseline))
            with self.assertRaisesRegex(ValueError, "NuGet-root bundle input.*lacks package identity"):
                EVIDENCE.load_bundle_packages(evidence, "win-x64")

    def test_capture_target_accounts_for_embedded_and_loose_publish_inputs(self) -> None:
        source = (Path(__file__).parents[1] / "capture_bundle_inputs.targets").read_text(encoding="utf-8")
        self.assertIn("@(FilesToBundle->'bundle|", source)
        self.assertIn("@(ResolvedFileToPublish->'loose|", source)
        self.assertIn("$(NuGetPackageRoot)", source)
        self.assertIn("requires a resolved NuGetPackageRoot", source)


if __name__ == "__main__":
    unittest.main()
