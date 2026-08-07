#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import hashlib
import json
import tempfile
import unittest
from pathlib import Path
import sys


MODULE_PATH = Path(__file__).resolve().parents[1] / "reconcile_payload_spdx.py"
sys.path.insert(0, str(MODULE_PATH.parent))
SPEC = importlib.util.spec_from_file_location("reconcile_payload_spdx", MODULE_PATH)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class PayloadSpdxReconciliationTests(unittest.TestCase):
    def fixture(self, root: Path, rid: str = "linux-x64") -> tuple[Path, Path, Path, Path, Path]:
        spdx = root / "manifest.spdx.json"
        spdx.write_text(
            json.dumps(
                {
                    "documentDescribes": ["SPDXRef-RootPackage"],
                    "externalDocumentRefs": [{"externalDocumentId": "DocumentRef-stale"}],
                    "packages": [
                        {"name": "product", "SPDXID": "SPDXRef-RootPackage"},
                        {"name": "dev-only", "SPDXID": "SPDXRef-Package-stale"},
                    ],
                    "relationships": [
                        {
                            "relationshipType": "DEPENDS_ON",
                            "relatedSpdxElement": "SPDXRef-Package-stale",
                            "spdxElementId": "SPDXRef-RootPackage",
                        }
                    ],
                }
            ),
            encoding="utf-8",
        )
        deps = root / "app.deps.json"
        deps.write_text(
            json.dumps(
                {
                    "targets": {
                        f".NETCoreApp,Version=v10.0/{rid}": {
                            "Product/1.0.0": {"runtime": {"Product.dll": {}}},
                            "Example.Package/2.0.0": {"runtime": {"Example.dll": {}}},
                            f"runtimepack.Microsoft.NETCore.App.Runtime.{rid}/10.0.10": {
                                "runtime": {"System.Private.CoreLib.dll": {}}
                            },
                            "Meta.Package/3.0.0": {"dependencies": {"Example.Package": "2.0.0"}},
                        }
                    },
                    "libraries": {
                        "Product/1.0.0": {"type": "project"},
                        "Example.Package/2.0.0": {"type": "package"},
                        f"runtimepack.Microsoft.NETCore.App.Runtime.{rid}/10.0.10": {"type": "package"},
                        "Meta.Package/3.0.0": {"type": "package"},
                    },
                }
            ),
            encoding="utf-8",
        )
        backend = root / "backend-components.json"
        backend.write_text(
            json.dumps(
                {
                    "runtimeIdentifier": rid,
                    "packages": [
                        {
                            "name": "Example.Package",
                            "version": "2.0.0",
                            "licenseExpression": "MIT",
                            "licenseFiles": [
                                {"path": "licenses/nuget/packages/Example.Package@2.0.0/LICENSE"}
                            ],
                        }
                    ]
                }
            ),
            encoding="utf-8",
        )
        bundle = root / "bundle-inputs.json"
        bundle.write_text(
            json.dumps(
                {
                    "schemaVersion": 2,
                    "runtimeIdentifier": rid,
                    "publishSingleFile": True,
                    "selfContained": True,
                    "inputs": [
                        {
                            "packageId": "Example.Package",
                            "packageVersion": "2.0.0",
                            "disposition": "bundle",
                            "origin": "nuget",
                            "relativePath": "Example.dll",
                            "sha256": "a" * 64,
                            "sourceType": "PackageReference",
                        },
                        {
                            "packageId": f"Microsoft.NETCore.App.Runtime.{rid}",
                            "packageVersion": "10.0.10",
                            "disposition": "bundle",
                            "origin": "nuget",
                            "relativePath": "System.Private.CoreLib.dll",
                            "sha256": "b" * 64,
                            "sourceType": "PackageReference",
                        },
                    ],
                }
            ),
            encoding="utf-8",
        )
        backend_payload = json.loads(backend.read_text())
        backend_payload["shipmentEvidence"] = {"sha256": hashlib.sha256(bundle.read_bytes()).hexdigest()}
        backend.write_text(json.dumps(backend_payload), encoding="utf-8")
        frontend = root / "frontend.json"
        frontend.write_text(
            json.dumps(
                {
                    "components": [
                        {
                            "name": "react",
                            "version": "19.2.7",
                            "license": "MIT",
                            "purl": "pkg:npm/react@19.2.7",
                        }
                    ]
                }
            ),
            encoding="utf-8",
        )
        return spdx, deps, bundle, backend, frontend

    def test_replaces_build_tree_guesses_with_exact_runtime_and_frontend_components(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            spdx, deps, bundle, backend, frontend = self.fixture(root)
            counts = MODULE.reconcile(spdx, deps, bundle, backend, frontend, "linux-x64", None)
            document = json.loads(spdx.read_text())

            self.assertEqual((2, 1), counts)
            self.assertEqual(
                {"product", "Example.Package", "Microsoft.NETCore.App.Runtime.linux-x64", "react"},
                {entry["name"] for entry in document["packages"]},
            )
            self.assertNotIn("externalDocumentRefs", document)
            self.assertEqual("Apache-2.0", document["packages"][0]["licenseDeclared"])
            self.assertEqual("Copyright 2026 w0rldx", document["packages"][0]["copyrightText"])
            self.assertEqual(3, len([item for item in document["relationships"] if item["relationshipType"] == "DEPENDS_ON"]))
            self.assertEqual(
                MODULE.hashlib.sha256(spdx.read_bytes()).hexdigest(),
                spdx.with_name("manifest.spdx.json.sha256").read_text(),
            )

    def test_missing_shipped_backend_license_fails_closed(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            spdx, deps, bundle, backend, frontend = self.fixture(root)
            payload = json.loads(backend.read_text())
            payload["packages"] = []
            backend.write_text(json.dumps(payload), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "has no approved license inventory entry"):
                MODULE.reconcile(spdx, deps, bundle, backend, frontend, "linux-x64", None)

    def test_stale_backend_corpus_package_fails_closed(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            spdx, deps, bundle, backend, frontend = self.fixture(root)
            payload = json.loads(backend.read_text())
            payload["packages"].append(
                {
                    "name": "Stale.Package",
                    "version": "9.9.9",
                    "licenseExpression": "MIT",
                    "licenseFiles": [{"path": "licenses/nuget/stale/LICENSE"}],
                }
            )
            backend.write_text(json.dumps(payload), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "stale backend license inventory"):
                MODULE.reconcile(spdx, deps, bundle, backend, frontend, "linux-x64", None)

    def test_bundle_evidence_is_authoritative_for_embedded_package_without_runtime_asset_group(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            spdx, deps, bundle, backend, frontend = self.fixture(root)
            bundle_payload = json.loads(bundle.read_text())
            bundle_payload["inputs"][0].update(
                packageId="Meta.Package",
                packageVersion="3.0.0",
                relativePath="embedded/Meta.Package.dll",
            )
            bundle.write_text(json.dumps(bundle_payload))
            backend_payload = json.loads(backend.read_text())
            backend_payload["packages"][0].update(name="Meta.Package", version="3.0.0")
            backend_payload["shipmentEvidence"]["sha256"] = hashlib.sha256(bundle.read_bytes()).hexdigest()
            backend.write_text(json.dumps(backend_payload))
            MODULE.reconcile(spdx, deps, bundle, backend, frontend, "linux-x64", None)
            document = json.loads(spdx.read_text())
            self.assertIn("Meta.Package", {entry["name"] for entry in document["packages"]})
            self.assertNotIn("Example.Package", {entry["name"] for entry in document["packages"]})

    def test_unknown_frontend_license_fails_closed(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            spdx, deps, bundle, backend, frontend = self.fixture(root)
            payload = json.loads(frontend.read_text())
            payload["components"][0]["license"] = "NOASSERTION"
            frontend.write_text(json.dumps(payload), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "has no approved license expression"):
                MODULE.reconcile(spdx, deps, bundle, backend, frontend, "linux-x64", None)

    def test_backend_inventory_must_be_bound_to_exact_bundle_evidence(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            spdx, deps, bundle, backend, frontend = self.fixture(root)
            payload = json.loads(bundle.read_text())
            payload["inputs"][0]["sha256"] = "f" * 64
            bundle.write_text(json.dumps(payload))
            with self.assertRaisesRegex(ValueError, "not bound to the supplied bundle-input evidence"):
                MODULE.reconcile(spdx, deps, bundle, backend, frontend, "linux-x64", None)

    def test_windows_runtime_requires_and_records_library_license(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            spdx, deps, bundle, backend, frontend = self.fixture(root, "win-x64")
            with self.assertRaisesRegex(ValueError, "requires the .NET Library License"):
                MODULE.reconcile(spdx, deps, bundle, backend, frontend, "win-x64", None)

            license_path = root / "DOTNET-LIBRARY-LICENSE.html"
            license_path.write_text("library terms", encoding="utf-8")
            MODULE.reconcile(spdx, deps, bundle, backend, frontend, "win-x64", license_path)
            document = json.loads(spdx.read_text())
            runtime = next(
                entry for entry in document["packages"] if entry["name"].startswith("Microsoft.NETCore.App.Runtime.")
            )
            self.assertEqual("MIT AND LicenseRef-DotNet-Library", runtime["licenseDeclared"])
            self.assertEqual("library terms", document["hasExtractedLicensingInfos"][0]["extractedText"])

    def test_windows_library_license_preserves_windows_1252_legal_text(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            license_path = Path(temporary_directory) / "DOTNET-LIBRARY-LICENSE.html"
            license_path.write_bytes('Microsoft\u2019s \u201cAS-IS\u201d terms'.encode("windows-1252"))

            expression, extracted = MODULE.runtime_license(
                "runtimepack.Microsoft.NETCore.App.Runtime.win-x64",
                "win-x64",
                license_path,
            )

            self.assertEqual("MIT AND LicenseRef-DotNet-Library", expression)
            self.assertIsNotNone(extracted)
            self.assertEqual('Microsoft\u2019s \u201cAS-IS\u201d terms', extracted["extractedText"])
            self.assertNotIn("\ufffd", extracted["extractedText"])


if __name__ == "__main__":
    unittest.main()
