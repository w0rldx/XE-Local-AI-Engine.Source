#!/usr/bin/env python3
from __future__ import annotations

import hashlib
import importlib.util
import json
import sys
import tempfile
import unittest
from pathlib import Path

MODULE_PATH = Path(__file__).resolve().parents[1] / "reconcile_payload_spdx.py"
sys.path.insert(0, str(MODULE_PATH.parent))
SPEC = importlib.util.spec_from_file_location("reconcile_payload_spdx", MODULE_PATH)
assert SPEC is not None
assert SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class PayloadSpdxReconciliationTests(unittest.TestCase):
    def fixture(self, root: Path, rid: str = "linux-x64") -> tuple[Path, Path, Path, Path, Path]:
        is_self_contained = rid == "linux-x64"
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
                            **(
                                {
                                    f"runtimepack.Microsoft.NETCore.App.Runtime.{rid}/10.0.10": {
                                        "runtime": {"System.Private.CoreLib.dll": {}}
                                    }
                                }
                                if is_self_contained
                                else {}
                            ),
                            "Meta.Package/3.0.0": {"dependencies": {"Example.Package": "2.0.0"}},
                        }
                    },
                    "libraries": {
                        "Product/1.0.0": {"type": "project"},
                        "Example.Package/2.0.0": {"type": "package"},
                        **(
                            {f"runtimepack.Microsoft.NETCore.App.Runtime.{rid}/10.0.10": {"type": "package"}}
                            if is_self_contained
                            else {}
                        ),
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
                            "licenseFiles": [{"path": "licenses/nuget/packages/Example.Package@2.0.0/LICENSE"}],
                        }
                    ],
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
                    "publishSingleFile": is_self_contained,
                    "selfContained": is_self_contained,
                    "inputs": [
                        {
                            "packageId": "Example.Package",
                            "packageVersion": "2.0.0",
                            "disposition": "bundle" if is_self_contained else "loose",
                            "origin": "nuget",
                            "relativePath": "Example.dll",
                            "sha256": "a" * 64,
                            "sourceType": "PackageReference",
                        },
                        *(
                            [
                                {
                                    "packageId": f"Microsoft.NETCore.App.Runtime.{rid}",
                                    "packageVersion": "10.0.10",
                                    "disposition": "bundle",
                                    "origin": "nuget",
                                    "relativePath": "System.Private.CoreLib.dll",
                                    "sha256": "b" * 64,
                                    "sourceType": "PackageReference",
                                }
                            ]
                            if is_self_contained
                            else []
                        ),
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
            self.assertEqual(
                3, len([item for item in document["relationships"] if item["relationshipType"] == "DEPENDS_ON"])
            )
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

    def test_windows_framework_dependent_payload_records_the_exact_mit_apphost_terms(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            spdx, deps, bundle, backend, frontend = self.fixture(root, "win-x64")
            with self.assertRaisesRegex(ValueError, "requires the Windows apphost version"):
                MODULE.reconcile(spdx, deps, bundle, backend, frontend, "win-x64", None)

            license_path = root / "DOTNET-APPHOST-LICENSE.txt"
            notices_path = root / "DOTNET-APPHOST-THIRD-PARTY-NOTICES.txt"
            license_path.write_text("apphost MIT terms", encoding="utf-8")
            notices_path.write_text("apphost notices", encoding="utf-8")
            MODULE.reconcile(
                spdx,
                deps,
                bundle,
                backend,
                frontend,
                "win-x64",
                None,
                windows_apphost_version="10.0.10",
                windows_apphost_license_path=license_path,
                windows_apphost_notices_path=notices_path,
            )
            document = json.loads(spdx.read_text())
            apphost = next(
                entry for entry in document["packages"] if entry["name"] == "Microsoft.NETCore.App.Host.win-x64"
            )
            self.assertEqual("MIT", apphost["licenseDeclared"])
            self.assertEqual(
                "pkg:nuget/Microsoft.NETCore.App.Host.win-x64@10.0.10",
                apphost["externalRefs"][0]["referenceLocator"],
            )
            self.assertIn(hashlib.sha256(b"apphost MIT terms").hexdigest(), apphost["licenseComments"])
            self.assertIn(hashlib.sha256(b"apphost notices").hexdigest(), apphost["licenseComments"])
            self.assertNotIn("hasExtractedLicensingInfos", document)

    def test_windows_framework_dependent_payload_rejects_a_runtime_pack(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            spdx, deps, bundle, backend, frontend = self.fixture(root, "win-x64")
            bundle_payload = json.loads(bundle.read_text())
            bundle_payload["inputs"].append(
                {
                    "packageId": "Microsoft.NETCore.App.Runtime.win-x64",
                    "packageVersion": "10.0.10",
                    "disposition": "loose",
                    "origin": "nuget",
                    "relativePath": "coreclr.dll",
                    "sha256": "b" * 64,
                    "sourceType": "PackageReference",
                }
            )
            bundle.write_text(json.dumps(bundle_payload), encoding="utf-8")
            backend_payload = json.loads(backend.read_text())
            backend_payload["shipmentEvidence"]["sha256"] = hashlib.sha256(bundle.read_bytes()).hexdigest()
            backend.write_text(json.dumps(backend_payload), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "must not include a .NET runtime pack"):
                MODULE.reconcile(
                    spdx,
                    deps,
                    bundle,
                    backend,
                    frontend,
                    "win-x64",
                    None,
                    windows_apphost_version="10.0.10",
                    windows_apphost_license_path=root / "missing-license",
                    windows_apphost_notices_path=root / "missing-notices",
                )


if __name__ == "__main__":
    unittest.main()
