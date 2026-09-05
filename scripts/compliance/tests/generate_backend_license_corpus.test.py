#!/usr/bin/env python3

from __future__ import annotations

import hashlib
import importlib.util
import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

SCRIPT = Path(__file__).parents[1] / "generate_backend_license_corpus.py"
REPOSITORY_ROOT = Path(__file__).parents[3]
sys.path.insert(0, str(SCRIPT.parent))
SPEC = importlib.util.spec_from_file_location("generate_backend_license_corpus", SCRIPT)
assert SPEC is not None
assert SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def deps(rid: str, entries: dict[str, dict], projects: set[str] | None = None) -> dict:
    projects = projects or set()
    return {
        "targets": {f".NETCoreApp,Version=v10.0/{rid}": entries},
        "libraries": {
            identity: {
                "type": "project" if identity in projects else "package",
                "path": identity.casefold(),
            }
            for identity in entries
        },
    }


def package(
    name: str,
    version: str,
    license_expression: str = "MIT",
    authors: str = "Example contributors",
    copyright_text: str = "Copyright Example contributors",
) -> dict:
    return {
        "PackageId": name,
        "PackageVersion": version,
        "Authors": authors,
        "Copyright": copyright_text,
        "License": license_expression,
        "LicenseUrl": f"https://licenses.nuget.org/{license_expression}",
        "PackageProjectUrl": f"https://example.test/{name}",
    }


class BackendLicenseCorpusTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.license_root = self.root / "third-party"
        self.packages_root = self.root / "packages"
        standard = self.license_root / "nuget" / "standard"
        standard.mkdir(parents=True)
        for name in ("MIT", "Apache-2.0", "BSD-3-Clause", "ISC"):
            text_path = standard / f"{name}.txt"
            contents = {
                "MIT": "MIT License\n\nCopyright (c) <year> <copyright holders>\n\nPermission text\n",
                "BSD-3-Clause": "Copyright (c) <year> <owner>. \n\nBSD terms\n",
            }.get(name, f"exact {name} text\n")
            text_path.write_text(contents, encoding="utf-8")
            self.write_provenance(standard / f"{name}.source.txt", text_path)
        nuget = self.license_root / "nuget"
        for filename, contents in (
            ("SQLite-3.50.3-public-domain.html", "SQLite blessing\n"),
            ("UTF.Unknown-2.7.0-MPL-1.1.txt", "MPL 1.1\n"),
            (
                "UTF.Unknown-2.7.0-SOURCE-AVAILABILITY.txt",
                "UTF.Unknown 2.7.0 covered source is available at immutable commit 404ca51.\n",
            ),
        ):
            text_path = nuget / filename
            text_path.write_text(contents, encoding="utf-8")
            self.write_provenance(nuget / f"{filename}.source.txt", text_path)
        upstream = nuget / "upstream"
        upstream.mkdir()
        for filename, contents in (
            ("FastEndpoints-8.3.0-LICENSE.md", "Copyright (c) 2021 FastEndpoints upstream\nMIT terms\n"),
            ("Scalar.AspNetCore-2.16.10-LICENSE", "Copyright (c) 2023-present Scalar\nMIT terms\n"),
            ("Scrutor-7.0.0-LICENSE", "Copyright (c) 2015 Kristian Hellang\nMIT terms\n"),
            ("TimeZoneConverter-7.2.0-LICENSE.txt", "Copyright (c) 2017 Matt Johnson-Pint\nMIT terms\n"),
        ):
            text_path = upstream / filename
            text_path.write_text(contents, encoding="utf-8")
            self.write_provenance(upstream / f"{filename}.source.txt", text_path)
        assets = self.license_root / "assets"
        assets.mkdir()
        for source_path, _ in (asset["notice"] for asset in MODULE.EMBEDDED_ASSETS):
            text_path = self.license_root / source_path
            text_path.write_text(f"synthetic {source_path.name}\n", encoding="utf-8")
            self.write_provenance(self.license_root / f"{source_path.as_posix()}.source.txt", text_path)
        self.manifest = self.write_json(
            "dotnet-tools.json",
            {"tools": {"nuget-license": {"version": "4.0.14", "commands": ["nuget-license"]}}},
        )

    def tearDown(self) -> None:
        self.temp.cleanup()

    @staticmethod
    def write_provenance(provenance: Path, text_path: Path) -> None:
        provenance.write_text(
            f"Source: https://example.test/{text_path.name}\n"
            f"SHA-256: {hashlib.sha256(text_path.read_bytes()).hexdigest()}\n",
            encoding="utf-8",
        )

    def write_json(self, name: str, value: object) -> Path:
        path = self.root / name
        path.write_text(json.dumps(value), encoding="utf-8")
        return path

    def ensure_package_roots(self, document: dict) -> None:
        for library in document["libraries"].values():
            if library["type"] == "package":
                (self.packages_root / library["path"]).mkdir(parents=True, exist_ok=True)

    def generate(
        self,
        metadata_value: object,
        document: dict | None = None,
        rid: str = "linux-x64",
        output_name: str = "output",
        bundle_identities: list[str] | None = None,
    ) -> Path:
        document = document or deps(
            rid,
            {
                "Zeta/2.0.0": {"native": {f"runtimes/{rid}/native/zeta": {}}},
                "Alpha/1.0.0": {"runtime": {"lib/net10.0/Alpha.dll": {}}},
                f"runtimepack.Microsoft.NETCore.App.Runtime.{rid}/10.0.10": {
                    "runtime": {"System.Private.CoreLib.dll": {}}
                },
                "Local.Project/1.0.0": {"runtime": {"Local.Project.dll": {}}},
                "Build.Only/1.0.0": {"compile": {"lib/net10.0/Build.Only.dll": {}}},
            },
            {"Local.Project/1.0.0"},
        )
        self.ensure_package_roots(document)
        if bundle_identities is None:
            target = document["targets"][next(iter(document["targets"]))]
            bundle_identities = [
                identity
                for identity, assets in target.items()
                if document["libraries"][identity]["type"] == "package"
                and any(assets.get(group) for group in ("runtime", "native", "runtimeTargets", "resources"))
            ]
        bundle_inputs = []
        for identity in bundle_identities:
            name, version = identity.rsplit("/", 1)
            bundle_inputs.append(
                {
                    "packageId": name.removeprefix("runtimepack."),
                    "packageVersion": version,
                    "disposition": "bundle",
                    "origin": "nuget",
                    "relativePath": f"{name}.dll",
                    "sha256": "a" * 64,
                    "sourceType": "PackageReference",
                }
            )
        bundle_manifest = self.write_json(
            f"{rid}.bundle-inputs.json",
            {
                "schemaVersion": 2,
                "runtimeIdentifier": rid,
                "publishSingleFile": rid == "linux-x64",
                "selfContained": rid == "linux-x64",
                "inputs": bundle_inputs,
            },
        )
        output = self.root / output_name
        MODULE.generate_corpus(
            rid,
            self.write_json(f"{rid}.deps.json", document),
            bundle_manifest,
            self.write_json("metadata.json", metadata_value),
            self.manifest,
            self.license_root,
            self.packages_root,
            output,
        )
        return output

    def test_generates_one_exact_rid_with_attributed_fallback_and_no_authorship_inference(self) -> None:
        output = self.generate([package("Zeta", "2.0.0", "Apache-2.0"), package("Alpha", "1.0.0")])
        components = json.loads((output / "backend-components.json").read_text(encoding="utf-8"))
        self.assertEqual("linux-x64", components["runtimeIdentifier"])
        self.assertEqual(["Alpha", "Zeta"], [entry["name"] for entry in components["packages"]])
        alpha = components["packages"][0]
        self.assertEqual("licenses/nuget/packages/Alpha@1.0.0/MIT.txt", alpha["licenseTextPath"])
        fallback = (output / alpha["licenseTextPath"]).read_text(encoding="utf-8")
        self.assertIn("Copyright Example contributors", fallback)
        self.assertNotIn("<year>", fallback)
        self.assertNotIn("<copyright holders>", fallback)

        authors_only = [package("Alpha", "1.0.0", copyright_text=""), package("Zeta", "2.0.0")]
        with self.assertRaisesRegex(ValueError, "author-only MIT metadata.*no exact upstream"):
            self.generate(authors_only, output_name="authors-only-generic")

        scalar_document = deps("linux-x64", {"Scalar.AspNetCore/2.16.10": {"runtime": {"Scalar.dll": {}}}})
        authors_output = self.generate(
            [package("Scalar.AspNetCore", "2.16.10", copyright_text="")],
            scalar_document,
            output_name="authors-only-exact",
        )
        authors_component = json.loads((authors_output / "backend-components.json").read_text())["packages"][0]
        self.assertEqual("Example contributors", authors_component["authors"])
        self.assertIsNone(authors_component["copyright"])
        self.assertIn("not inferred", authors_component["attributionPolicy"])
        self.assertIn(
            "Copyright (c) 2023-present Scalar", (authors_output / authors_component["licenseTextPath"]).read_text()
        )
        self.assertIn("Authors are not inferred", (authors_output / "THIRD-PARTY-NOTICES.md").read_text())

        wrong_version = deps("linux-x64", {"Scalar.AspNetCore/2.16.11": {"runtime": {"Scalar.dll": {}}}})
        with self.assertRaisesRegex(ValueError, "author-only MIT metadata.*no exact upstream"):
            self.generate(
                [package("Scalar.AspNetCore", "2.16.11", copyright_text="")],
                wrong_version,
                output_name="authors-only-wrong-version",
            )

    def test_attribution_bearing_standard_fallbacks_never_ship_placeholders_or_misattribution(self) -> None:
        for expression, expected_placeholder in (
            ("MIT", "<copyright holders>"),
            ("BSD-3-Clause", "<owner>"),
        ):
            name = expression.replace("-", "")
            document = deps("linux-x64", {f"{name}/1.0.0": {"runtime": {f"{name}.dll": {}}}})
            output = self.generate(
                [package(name, "1.0.0", expression, copyright_text=f"Copyright 2026 {name} Owner")],
                document,
                output_name=f"attributed-{name}",
            )
            component = json.loads((output / "backend-components.json").read_text())["packages"][0]
            rendered = (output / component["licenseTextPath"]).read_text(encoding="utf-8")
            self.assertIn(f"Copyright 2026 {name} Owner", rendered)
            self.assertNotIn("<year>", rendered)
            self.assertNotIn(expected_placeholder, rendered)

        isc_document = deps("linux-x64", {"Some.Isc.Package/1.0.0": {"runtime": {"Isc.dll": {}}}})
        with self.assertRaisesRegex(ValueError, "ISC.*requires an exact package-owned or reviewed upstream license"):
            self.generate(
                [package("Some.Isc.Package", "1.0.0", "ISC", copyright_text="Copyright 2026 Actual Owner")],
                isc_document,
                output_name="isc-needs-exact-terms",
            )

    def test_rid_payloads_can_diverge_without_unioning_packages(self) -> None:
        metadata = [package("Linux.Only", "1.0.0"), package("Windows.Only", "1.0.0")]
        linux = deps("linux-x64", {"Linux.Only/1.0.0": {"native": {"linux.so": {}}}})
        windows = deps("win-x64", {"Windows.Only/1.0.0": {"native": {"windows.dll": {}}}})
        linux_output = self.generate(metadata, linux, "linux-x64", "linux-output")
        windows_output = self.generate(metadata, windows, "win-x64", "windows-output")
        linux_packages = json.loads((linux_output / "backend-components.json").read_text())["packages"]
        windows_packages = json.loads((windows_output / "backend-components.json").read_text())["packages"]
        self.assertEqual(["Linux.Only"], [entry["name"] for entry in linux_packages])
        self.assertEqual(["Windows.Only"], [entry["name"] for entry in windows_packages])

    def test_bundle_evidence_includes_embedded_package_even_without_deps_runtime_group(self) -> None:
        document = deps(
            "linux-x64",
            {
                "Embedded.DevUI/1.0.0": {"dependencies": {"Example": "1.0.0"}},
                "Deps.Only/1.0.0": {"runtime": {"Deps.Only.dll": {}}},
            },
        )
        output = self.generate(
            [package("Embedded.DevUI", "1.0.0"), package("Deps.Only", "1.0.0")],
            document,
            bundle_identities=["Embedded.DevUI/1.0.0"],
        )
        packages = json.loads((output / "backend-components.json").read_text())["packages"]
        self.assertEqual(["Embedded.DevUI"], [entry["name"] for entry in packages])

    def test_copies_all_package_owned_license_and_notice_files_and_detects_omission(self) -> None:
        document = deps("linux-x64", {"NSec.Cryptography/26.4.0": {"runtime": {"NSec.dll": {}}}})
        self.ensure_package_roots(document)
        package_root = self.packages_root / "nsec.cryptography/26.4.0"
        (package_root / "LICENSE").write_bytes(b"package MIT terms\n")
        (package_root / "NOTICE").write_bytes(b"required attribution notice\n")
        output = self.generate([package("NSec.Cryptography", "26.4.0")], document)
        component = json.loads((output / "backend-components.json").read_text())["packages"][0]
        copied = {entry["sourceFile"]: entry for entry in component["licenseFiles"]}
        self.assertEqual({"LICENSE", "NOTICE"}, set(copied))
        self.assertEqual(b"required attribution notice\n", (output / copied["NOTICE"]["path"]).read_bytes())
        self.assertIn("NOTICE", (output / "THIRD-PARTY-NOTICES.md").read_text())

        real_copy = MODULE.shutil.copyfile

        def omit_notice(source: Path, destination: Path) -> None:
            if source.name != "NOTICE":
                real_copy(source, destination)

        with (
            patch.object(MODULE.shutil, "copyfile", side_effect=omit_notice),
            self.assertRaisesRegex(ValueError, "omitted expected package term.*NOTICE"),
        ):
            self.generate([package("NSec.Cryptography", "26.4.0")], document, output_name="omitted")

    def test_maps_special_licenses_only_to_exact_reviewed_packages(self) -> None:
        document = deps(
            "linux-x64",
            {
                "SQLitePCLRaw.lib.e_sqlite3/3.50.3": {"native": {"libe_sqlite3.so": {}}},
                "UTF.Unknown/2.7.0": {"runtime": {"UtfUnknown.dll": {}}},
            },
        )
        output = self.generate(
            [
                package("SQLitePCLRaw.lib.e_sqlite3", "3.50.3", "blessing"),
                package("UTF.Unknown", "2.7.0", "MPL-1.1"),
            ],
            document,
        )
        packages = json.loads((output / "backend-components.json").read_text())["packages"]
        paths = {entry["name"]: entry["licenseTextPath"] for entry in packages}
        self.assertIn("SQLite-3.50.3-public-domain.html", paths["SQLitePCLRaw.lib.e_sqlite3"])
        self.assertIn("UTF.Unknown-2.7.0-MPL-1.1.txt", paths["UTF.Unknown"])
        utf_unknown = next(entry for entry in packages if entry["name"] == "UTF.Unknown")
        self.assertEqual(
            {
                "licenseBasis": "MPL-1.1",
                "sourceArchive": "https://github.com/CharsetDetector/UTF-unknown/archive/404ca51e057ff299934cabc485ae80122410f56b.tar.gz",
                "sourceCommit": "404ca51e057ff299934cabc485ae80122410f56b",
                "sourceRepository": "https://github.com/CharsetDetector/UTF-unknown",
                "upstreamTag": "v2.7",
            },
            utf_unknown["sourceAvailability"],
        )
        source_notice = next(
            entry for entry in utf_unknown["licenseFiles"] if entry["sourceFile"].endswith("SOURCE-AVAILABILITY.txt")
        )
        self.assertEqual("notice", source_notice["role"])
        notices = (output / "THIRD-PARTY-NOTICES.md").read_text()
        self.assertIn("Covered source availability", notices)
        self.assertIn("404ca51e057ff299934cabc485ae80122410f56b", notices)

        wrong = deps("linux-x64", {"Not.UTF.Unknown/1.0.0": {"runtime": {"Other.dll": {}}}})
        with self.assertRaisesRegex(ValueError, "unsupported special license mapping"):
            self.generate([package("Not.UTF.Unknown", "1.0.0", "MPL-1.1")], wrong, output_name="wrong")

    def test_fails_closed_on_identity_license_attribution_tool_and_text_errors(self) -> None:
        baseline = [package("Alpha", "1.0.0"), package("Zeta", "2.0.0")]
        cases = (
            ([package("Alpha", "2.0.0"), package("Zeta", "2.0.0")], "stale.*Alpha/1.0.0"),
            ([package("Alpha", "1.0.0"), package("Alpha", "1.0.0"), package("Zeta", "2.0.0")], "duplicate"),
            ([package("Alpha", "1.0.0", "NOASSERTION"), package("Zeta", "2.0.0")], "unreviewable license"),
            ([package("Alpha", "1.0.0", "GPL-3.0-only"), package("Zeta", "2.0.0")], "unsupported license"),
            (
                [package("Alpha", "1.0.0", authors="", copyright_text=""), package("Zeta", "2.0.0")],
                "missing attribution",
            ),
        )
        for value, message in cases:
            with self.subTest(message=message), self.assertRaisesRegex(ValueError, message):
                self.generate(value, output_name=f"invalid-{len(message)}")

        self.manifest.write_text(json.dumps({"tools": {"nuget-license": {"version": "4.0.15"}}}))
        with self.assertRaisesRegex(ValueError, "expected pinned nuget-license 4.0.14"):
            self.generate(baseline, output_name="wrong-tool")

        self.manifest.write_text(json.dumps({"tools": {"nuget-license": {"version": "4.0.14"}}}))
        (self.license_root / "nuget/standard/MIT.txt").write_text("tampered\n")
        with self.assertRaisesRegex(ValueError, "SHA-256 mismatch"):
            self.generate(baseline, output_name="tampered")

    def test_lists_embedded_non_nuget_assets_with_their_terms_and_provenance(self) -> None:
        """The corpus is generated from NuGet metadata, so an asset vendored into our own source is invisible to it.

        Nothing here is discoverable: if this stops being emitted, the payload ships a third-party file with no
        attribution and every other check still passes.
        """
        output = self.generate([package("Alpha", "1.0.0"), package("Zeta", "2.0.0")], output_name="assets")
        payload = json.loads((output / "backend-components.json").read_text())

        self.assertEqual(len(MODULE.EMBEDDED_ASSETS), len(payload["embeddedAssets"]))
        seccomp = next(entry for entry in payload["embeddedAssets"] if "seccomp" in entry["name"])
        self.assertEqual("Apache-2.0", seccomp["licenseExpression"])
        self.assertEqual("seccomp/v0.2.3", seccomp["version"])
        self.assertEqual("836ae4d37ef2ec995c77c99fc55f5b5f3af3a897", seccomp["sourceCommit"])
        self.assertEqual(
            "536529b665dd0972c37bfb569f5d4ac8a53592e7b00752bc39ff063ca9864c74",
            seccomp["sha256"],
        )
        self.assertEqual({"license", "notice"}, {term["role"] for term in seccomp["licenseFiles"]})

        # Every listed term is really in the payload, at the stated path and the stated hash.
        for term in seccomp["licenseFiles"]:
            copied = output / term["path"]
            self.assertTrue(copied.is_file(), term["path"])
            self.assertEqual(term["sha256"], hashlib.sha256(copied.read_bytes()).hexdigest())

        notices = (output / "THIRD-PARTY-NOTICES.md").read_text()
        self.assertIn("Embedded third-party assets", notices)
        self.assertIn("836ae4d37ef2ec995c77c99fc55f5b5f3af3a897", notices)
        self.assertIn("536529b665dd0972c37bfb569f5d4ac8a53592e7b00752bc39ff063ca9864c74", notices)

    def test_embedded_asset_attribution_matches_the_bytes_checked_into_this_repository(self) -> None:
        """The half that makes the attribution honest rather than merely present.

        Run against the REAL third-party tree and the REAL source tree: the curated notice must match its own
        provenance hash, and the vendored file must still hash to what the attribution claims. Copy a newer upstream
        profile in without updating the notice and this fails, which is the only way a stated commit and SHA-256 can
        be kept from describing a file that no longer exists.
        """
        with tempfile.TemporaryDirectory() as staging:
            assets = MODULE.build_embedded_assets(REPOSITORY_ROOT / "third-party", REPOSITORY_ROOT, Path(staging))

        self.assertEqual(len(MODULE.EMBEDDED_ASSETS), len(assets))
        for asset in MODULE.EMBEDDED_ASSETS:
            vendored = REPOSITORY_ROOT / asset["repositoryPath"]
            self.assertTrue(vendored.is_file(), asset["repositoryPath"])
            self.assertEqual(asset["sha256"], hashlib.sha256(vendored.read_bytes()).hexdigest())

    def test_fails_closed_when_an_embedded_asset_no_longer_matches_its_attribution(self) -> None:
        asset = MODULE.EMBEDDED_ASSETS[0]
        with tempfile.TemporaryDirectory() as fake_repository, tempfile.TemporaryDirectory() as staging:
            vendored = Path(fake_repository) / asset["repositoryPath"]
            vendored.parent.mkdir(parents=True, exist_ok=True)
            vendored.write_bytes(b"a newer upstream profile nobody re-attributed\n")
            with self.assertRaisesRegex(ValueError, "attribution claims"):
                MODULE.build_embedded_assets(self.license_root, Path(fake_repository), Path(staging))

            vendored.unlink()
            with self.assertRaisesRegex(ValueError, "missing from the source tree"):
                MODULE.build_embedded_assets(self.license_root, Path(fake_repository), Path(staging))


if __name__ == "__main__":
    unittest.main()
