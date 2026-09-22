#!/usr/bin/env python3
"""Multi-executable inventories must be backed by each executable's publish evidence."""

from __future__ import annotations

import importlib.util
import json
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).parents[1]
sys.path.insert(0, str(SCRIPTS))


def load(name: str):
    spec = importlib.util.spec_from_file_location(name, SCRIPTS / f"{name}.py")
    assert spec is not None
    assert spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


EVIDENCE = load("bundle_input_evidence")
CORPUS = load("generate_backend_license_corpus")
SPDX = load("reconcile_payload_spdx")


class MultiplePublishInputsTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.engine = self.write_pair("engine", {"Shared": "1", "Engine": "1"})
        self.desktop = self.write_pair("desktop", {"Shared": "1", "Desktop": "2"})

    def write_pair(self, name: str, packages: dict[str, str]) -> tuple[Path, Path]:
        deps = self.root / f"{name}.deps.json"
        evidence = self.root / f"{name}.inputs.json"
        libraries = {
            f"{package}/{version}": {"type": "package", "path": f"{package.lower()}/{version}"}
            for package, version in packages.items()
        }
        deps.write_text(json.dumps({"targets": {"net10.0/win-x64": {}}, "libraries": libraries}))
        evidence.write_text(
            json.dumps(
                {
                    "schemaVersion": 2,
                    "runtimeIdentifier": "win-x64",
                    "publishSingleFile": False,
                    "selfContained": False,
                    "inputs": [
                        {
                            "packageId": package,
                            "packageVersion": version,
                            "origin": "nuget",
                            "disposition": "loose",
                            "relativePath": f"{package}.dll",
                            "sha256": "a" * 64,
                        }
                        for package, version in packages.items()
                    ],
                }
            )
        )
        return deps, evidence

    def selected(self):
        return CORPUS.shipped_packages("win-x64", *self.engine, (self.desktop[0],), (self.desktop[1],))

    def test_includes_desktop_only_packages_and_deduplicates_shared_evidence(self) -> None:
        selected = self.selected()
        self.assertEqual({("shared", "1"), ("engine", "1"), ("desktop", "2")}, set(selected))
        self.assertEqual(1, len(selected[("shared", "1")]["bundleInputs"]))
        packages, _ = SPDX.detected_backend_packages(
            json.loads(self.engine[0].read_text()),
            self.engine[1],
            "win-x64",
            dict.fromkeys(selected, "MIT"),
            (json.loads(self.desktop[0].read_text()),),
            (self.desktop[1],),
        )
        self.assertEqual(["Desktop", "Engine", "Shared"], [package["name"] for package in packages])

    def test_rejects_conflicting_versions(self) -> None:
        self.desktop = self.write_pair("desktop", {"Shared": "2"})
        with self.assertRaisesRegex(ValueError, "conflicting shipped package versions"):
            self.selected()

    def test_rejects_conflicting_hashes_and_library_identity(self) -> None:
        path = self.desktop[1]
        document = json.loads(path.read_text())
        document["inputs"][0]["sha256"] = "b" * 64
        path.write_text(json.dumps(document))
        with self.assertRaisesRegex(ValueError, "conflicting publish input evidence"):
            self.selected()
        self.desktop = self.write_pair("desktop", {"Shared": "1"})
        path = self.desktop[0]
        document = json.loads(path.read_text())
        document["libraries"]["Shared/1"]["path"] = "wrong/1"
        path.write_text(json.dumps(document))
        with self.assertRaisesRegex(ValueError, "conflicting deps.json package evidence"):
            self.selected()

    def test_rejects_wrong_rid_in_either_additional_input(self) -> None:
        for position in (0, 1):
            with self.subTest(position=position):
                self.desktop = self.write_pair("desktop", {"Desktop": "2"})
                path = self.desktop[position]
                path.write_text(path.read_text().replace("win-x64", "linux-x64"))
                with self.assertRaisesRegex(ValueError, "win-x64 target|runtime identifier"):
                    self.selected()

    def test_requires_paired_evidence_and_package_in_its_own_deps(self) -> None:
        with self.assertRaisesRegex(ValueError, "requires its own"):
            CORPUS.shipped_packages("win-x64", *self.engine, (self.desktop[0],))
        path = self.desktop[0]
        document = json.loads(path.read_text())
        del document["libraries"]["Shared/1"]
        path.write_text(json.dumps(document))
        with self.assertRaisesRegex(ValueError, "absent from RID deps.json"):
            self.selected()
        self.desktop[1].unlink()
        with self.assertRaises(FileNotFoundError):
            self.selected()

    def test_inventory_binding_includes_additional_evidence(self) -> None:
        paths = [self.engine[1], self.desktop[1]]
        manifest = {
            "runtimeIdentifier": "win-x64",
            "packages": [],
            "shipmentEvidence": {"sha256": EVIDENCE.shipment_evidence_hash(paths)},
        }
        self.assertEqual({}, SPDX.backend_license_map(manifest, "win-x64", paths[0], (paths[1],)))
        with self.assertRaisesRegex(ValueError, "not bound"):
            SPDX.backend_license_map(manifest, "win-x64", paths[0])
        paths[1].write_text(paths[1].read_text() + "\n")
        with self.assertRaisesRegex(ValueError, "not bound"):
            SPDX.backend_license_map(manifest, "win-x64", paths[0], (paths[1],))

    def test_desktop_package_still_requires_license_and_rejects_stale_inventory(self) -> None:
        selected = self.selected()
        licenses = dict.fromkeys(selected, "MIT")
        del licenses[("desktop", "2")]
        args = (json.loads(self.engine[0].read_text()), self.engine[1], "win-x64")
        more = ((json.loads(self.desktop[0].read_text()),), (self.desktop[1],))
        with self.assertRaisesRegex(ValueError, "no approved license inventory"):
            SPDX.detected_backend_packages(*args, licenses, *more)
        licenses[("desktop", "2")] = "MIT"
        licenses[("stale", "1")] = "MIT"
        with self.assertRaisesRegex(ValueError, "stale backend license"):
            SPDX.detected_backend_packages(*args, licenses, *more)


if __name__ == "__main__":
    unittest.main()
