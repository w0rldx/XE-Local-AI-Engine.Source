#!/usr/bin/env python3
from __future__ import annotations

import hashlib
import importlib.util
import io
import json
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest import mock

MODULE_PATH = Path(__file__).resolve().parents[1] / "verify_release_artifacts.py"
REPOSITORY_ROOT = MODULE_PATH.parents[2]
SPEC = importlib.util.spec_from_file_location("verify_release_artifacts", MODULE_PATH)
assert SPEC is not None
assert SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class ReleaseArtifactVerifierTests(unittest.TestCase):
    @staticmethod
    def windows_compliance_files() -> dict[str, bytes]:
        license_bytes = b"Example package terms"
        asset_notice_bytes = b"Embedded asset notice"
        backend_manifest = {
            "runtimeIdentifier": "win-x64",
            "packages": [
                {
                    "name": "Example.Package",
                    "version": "1.0.0",
                    "licenseExpression": "MIT",
                    "licenseFiles": [
                        {
                            "path": "licenses/nuget/packages/Example.Package@1.0.0/LICENSE",
                            "sha256": hashlib.sha256(license_bytes).hexdigest(),
                        }
                    ],
                }
            ],
            # A non-NuGet asset vendored into our own source. Held to the same contract as a package's terms: listed
            # here, and its bytes really present in the payload below.
            "embeddedAssets": [
                {
                    "name": "example/profiles embedded asset",
                    "version": "v1.2.3",
                    "licenseExpression": "Apache-2.0",
                    "licenseFiles": [
                        {
                            "path": "licenses/assets/example-asset/NOTICE.txt",
                            "sha256": hashlib.sha256(asset_notice_bytes).hexdigest(),
                        }
                    ],
                }
            ],
        }
        files = {
            "current/LICENSE": (REPOSITORY_ROOT / "LICENSE").read_bytes(),
            "current/NOTICE": (REPOSITORY_ROOT / "NOTICE").read_bytes(),
            "current/XE-Local-AI-Engine.WindowsLauncher.exe": b"Microsoft MIT apphost",
            "current/XE-Local-AI-Engine.WindowsLauncher.dll": b"repository-owned C# launcher",
            "current/XE-Local-AI-Engine.WindowsLauncher.deps.json": b"{}",
            "current/XE-Local-AI-Engine.WindowsLauncher.runtimeconfig.json": b"{}",
            "current/XE-Local-AI-Engine.Client.dll": b"managed application",
            "current/XE-Local-AI-Engine.Client.deps.json": b"{}",
            "current/XE-Local-AI-Engine.Client.runtimeconfig.json": b"{}",
            "current/licenses/dotnet/DOTNET-APPHOST-LICENSE.txt": b"MIT terms",
            "current/licenses/dotnet/DOTNET-APPHOST-THIRD-PARTY-NOTICES.txt": b"apphost notices",
            "current/wwwroot/licenses/dotnet/DOTNET-APPHOST-LICENSE.txt": b"MIT terms",
            "current/wwwroot/licenses/dotnet/DOTNET-APPHOST-THIRD-PARTY-NOTICES.txt": b"apphost notices",
            "current/_manifest/spdx_2.2/manifest.spdx.json": b"{}",
            "current/wwwroot/component-manifest.json": b'{"components":[{"purl":"pkg:npm/react@19.2.7"}]}',
            "current/wwwroot/licenses/frontend/FRONTEND-COMPONENTS.json": json.dumps(
                {
                    "components": [
                        {
                            "purl": "pkg:npm/react@19.2.7",
                            "licenseFiles": [
                                {
                                    "path": "react@19.2.7/LICENSE",
                                    "sha256": hashlib.sha256(b"React license").hexdigest(),
                                }
                            ],
                        }
                    ]
                }
            ).encode(),
            "current/wwwroot/licenses/frontend/THIRD-PARTY-NOTICES.md": b"React notices",
            "current/wwwroot/licenses/frontend/react@19.2.7/LICENSE": b"React license",
            "current/backend-components.json": json.dumps(backend_manifest).encode(),
            "current/THIRD-PARTY-NOTICES.md": b"Backend notices",
            "current/licenses/nuget/packages/Example.Package@1.0.0/LICENSE": license_bytes,
            "current/licenses/assets/example-asset/NOTICE.txt": asset_notice_bytes,
        }
        return files

    def make_archive(self, root: Path, files: dict[str, bytes]) -> Path:
        archive = root / "XE-Local-AI-Engine-win-Portable.zip"
        with zipfile.ZipFile(archive, "w") as output:
            for name, content in files.items():
                output.writestr(name, content)
        return archive

    def test_an_embedded_asset_term_the_payload_does_not_carry_is_rejected(self) -> None:
        """The listing is not the attribution; the bytes in the payload are.

        An embedded asset is hand-listed in the generator, so nothing upstream can catch a notice that was declared and
        then not shipped. This is where that becomes a failure rather than a footnote.
        """
        files = self.windows_compliance_files()
        del files["current/licenses/assets/example-asset/NOTICE.txt"]
        manifest = json.loads(files["current/backend-components.json"])
        terms = MODULE.backend_legal_terms(files["current/backend-components.json"], "win-x64", "payload")

        self.assertIn("licenses/assets/example-asset/NOTICE.txt", terms)
        self.assertIn("embeddedAssets", manifest)
        with self.assertRaisesRegex(ValueError, "missing backend legal term"):
            MODULE.assert_backend_terms(
                terms,
                {name.removeprefix("current/"): content for name, content in files.items()},
                "payload",
            )

    def test_an_embedded_asset_term_outside_the_assets_tree_is_rejected(self) -> None:
        manifest = {
            "runtimeIdentifier": "win-x64",
            "packages": [
                {
                    "licenseFiles": [
                        {"path": "licenses/nuget/packages/A@1.0.0/LICENSE", "sha256": "a" * 64},
                    ]
                }
            ],
            "embeddedAssets": [
                {"licenseFiles": [{"path": "licenses/nuget/packages/A@1.0.0/SNEAK", "sha256": "b" * 64}]}
            ],
        }
        with self.assertRaisesRegex(ValueError, "invalid legal term"):
            MODULE.backend_legal_terms(json.dumps(manifest).encode(), "win-x64", "payload")

    def test_linux_requires_the_split_runtime_license_corpus(self) -> None:
        paths = [
            "LICENSE",
            "NOTICE",
            "licenses/dotnet/DOTNET-RUNTIME-LICENSE.txt",
            "licenses/dotnet/DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt",
            "licenses/dotnet/ASPNETCORE-RUNTIME-LICENSE.txt",
            "licenses/dotnet/ASPNETCORE-RUNTIME-THIRD-PARTY-NOTICES.txt",
            "_manifest/spdx_2.2/manifest.spdx.json",
            "wwwroot/component-manifest.json",
            "wwwroot/licenses/frontend/frontend-components.json",
            "wwwroot/licenses/frontend/third-party-notices.md",
            "backend-components.json",
            "third-party-notices.md",
            "wwwroot/licenses/dotnet/DOTNET-RUNTIME-LICENSE.txt",
            "wwwroot/licenses/dotnet/DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt",
            "wwwroot/licenses/dotnet/ASPNETCORE-RUNTIME-LICENSE.txt",
            "wwwroot/licenses/dotnet/ASPNETCORE-RUNTIME-THIRD-PARTY-NOTICES.txt",
        ]

        MODULE.assert_required_paths(paths, "linux-x64", "linux payload")

    def test_windows_portable_with_required_compliance_material_passes(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            archive = self.make_archive(
                Path(temporary_directory),
                {
                    **self.windows_compliance_files(),
                    "current/wwwroot/app.js": b"console.log('clean')",
                },
            )
            MODULE.verify_zip_archive(archive, "win-x64")

    def test_nested_portable_tree_resolves_backend_terms_from_the_application_root(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            for relative, content in self.windows_compliance_files().items():
                path = root / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(content)
            MODULE.verify_tree(root, "win-x64", "nested portable tree")

    def test_compressed_removed_tts_payload_fails(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            archive = self.make_archive(
                Path(temporary_directory),
                {
                    **self.windows_compliance_files(),
                    "current/assets/worker.js": b"load phonemizer and espeakng.worker.data",
                },
            )
            with self.assertRaisesRegex(ValueError, "forbidden release payload"):
                MODULE.verify_zip_archive(archive, "win-x64")

    def test_preview_dev_ui_payload_fails(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            archive = self.make_archive(
                Path(temporary_directory),
                {
                    **self.windows_compliance_files(),
                    "current/XE-Local-AI-Engine.Client.exe": b"Microsoft.Agents.AI.DevUI.dll",
                },
            )
            with self.assertRaisesRegex(ValueError, "forbidden release payload"):
                MODULE.verify_zip_archive(archive, "win-x64")

    def test_preview_agent_hosting_payload_fails(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            archive = self.make_archive(
                Path(temporary_directory),
                {
                    **self.windows_compliance_files(),
                    "current/XE-Local-AI-Engine.Client.exe": b"Microsoft.Agents.AI.Hosting.dll",
                },
            )
            with self.assertRaisesRegex(ValueError, "forbidden release payload"):
                MODULE.verify_zip_archive(archive, "win-x64")

    def test_hosting_unit_test_friend_assembly_name_is_not_a_payload_match(self) -> None:
        MODULE.assert_stream_has_no_forbidden_markers(
            "single-file",
            io.BytesIO(b"Microsoft.Agents.AI.Hosting.UnitTests, PublicKey=00"),
            "payload",
        )

    def test_forbidden_marker_split_across_stream_chunks_fails(self) -> None:
        prefix = b"x" * (1024 * 1024 - 10)
        stream = io.BytesIO(prefix + b"Microsoft.Agents.AI.DevUI.dll")

        with self.assertRaisesRegex(ValueError, "forbidden release payload"):
            MODULE.assert_stream_has_no_forbidden_markers("large-single-file", stream, "payload")

    def test_windows_framework_dependent_payload_rejects_embedded_runtime_or_library_terms(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            for path in (
                "current/coreclr.dll",
                "current/hostfxr.dll",
                "current/licenses/dotnet/DOTNET-LIBRARY-LICENSE.html",
            ):
                with self.subTest(path=path):
                    files = self.windows_compliance_files()
                    files[path] = b"must not ship"
                    archive = self.make_archive(Path(temporary_directory), files)
                    with self.assertRaisesRegex(ValueError, "framework-dependent Windows payload"):
                        MODULE.verify_zip_archive(archive, "win-x64")

    def test_windows_framework_dependent_payload_requires_launcher_and_managed_entrypoint(self) -> None:
        for path in (
            "current/XE-Local-AI-Engine.WindowsLauncher.exe",
            "current/XE-Local-AI-Engine.WindowsLauncher.dll",
            "current/XE-Local-AI-Engine.WindowsLauncher.deps.json",
            "current/XE-Local-AI-Engine.WindowsLauncher.runtimeconfig.json",
            "current/XE-Local-AI-Engine.Client.dll",
            "current/XE-Local-AI-Engine.Client.deps.json",
            "current/XE-Local-AI-Engine.Client.runtimeconfig.json",
        ):
            with self.subTest(path=path), tempfile.TemporaryDirectory() as temporary_directory:
                files = self.windows_compliance_files()
                del files[path]
                archive = self.make_archive(Path(temporary_directory), files)
                with self.assertRaisesRegex(ValueError, "missing required"):
                    MODULE.verify_zip_archive(archive, "win-x64")

    def test_backend_corpus_file_hash_must_match_manifest(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            files = self.windows_compliance_files()
            files["current/licenses/nuget/packages/Example.Package@1.0.0/LICENSE"] = b"tampered"
            archive = self.make_archive(Path(temporary_directory), files)
            with self.assertRaisesRegex(ValueError, "backend legal term checksum mismatch"):
                MODULE.verify_zip_archive(archive, "win-x64")

    def test_frontend_corpus_requires_every_referenced_license_file(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            files = self.windows_compliance_files()
            del files["current/wwwroot/licenses/frontend/react@19.2.7/LICENSE"]
            archive = self.make_archive(Path(temporary_directory), files)
            with self.assertRaisesRegex(ValueError, "missing frontend legal term"):
                MODULE.verify_zip_archive(archive, "win-x64")

    def test_frontend_corpus_file_hash_must_match_manifest(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            files = self.windows_compliance_files()
            files["current/wwwroot/licenses/frontend/react@19.2.7/LICENSE"] = b"tampered"
            archive = self.make_archive(Path(temporary_directory), files)
            with self.assertRaisesRegex(ValueError, "frontend legal term checksum mismatch"):
                MODULE.verify_zip_archive(archive, "win-x64")

    def test_project_license_and_notice_are_required(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            files = self.windows_compliance_files()
            del files["current/LICENSE"]
            archive = self.make_archive(Path(temporary_directory), files)
            with self.assertRaisesRegex(ValueError, "LICENSE"):
                MODULE.verify_zip_archive(archive, "win-x64")

    def test_project_license_and_notice_must_match_the_checked_out_source(self) -> None:
        for document in ("LICENSE", "NOTICE"):
            with self.subTest(document=document), tempfile.TemporaryDirectory() as temporary_directory:
                files = self.windows_compliance_files()
                files[f"current/{document}"] = b"tampered project terms"
                archive = self.make_archive(Path(temporary_directory), files)
                with self.assertRaisesRegex(ValueError, f"project {document} differs"):
                    MODULE.verify_zip_archive(archive, "win-x64")

    def test_served_linux_dotnet_document_must_match_the_bundled_corpus(self) -> None:
        contents = {}
        for name in MODULE.DOTNET_RUNTIME_DOCUMENT_NAMES:
            contents[f"licenses/dotnet/{name}"] = b"same"
            contents[f"wwwroot/licenses/dotnet/{name}"] = b"same"
        contents["wwwroot/licenses/dotnet/DOTNET-RUNTIME-LICENSE.txt"] = b"different"
        with self.assertRaisesRegex(ValueError, "differs from bundled corpus"):
            MODULE.assert_dotnet_documents_are_served(contents, "linux-x64", "linux payload")

    def test_empty_frontend_component_detection_fails(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            archive = self.make_archive(
                Path(temporary_directory),
                {
                    **self.windows_compliance_files(),
                    "current/wwwroot/component-manifest.json": b'{"components":[]}',
                    "current/wwwroot/licenses/frontend/FRONTEND-COMPONENTS.json": b'{"components":[]}',
                },
            )
            with self.assertRaisesRegex(ValueError, "no detected components"):
                MODULE.verify_zip_archive(archive, "win-x64")

    def test_frontend_corpus_must_match_detected_components(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            archive = self.make_archive(
                Path(temporary_directory),
                {
                    **self.windows_compliance_files(),
                    "current/wwwroot/component-manifest.json": (
                        b'{"components":[{"purl":"pkg:npm/react@19.2.7"},{"purl":"pkg:npm/zod@4.4.3"}]}'
                    ),
                    "current/wwwroot/licenses/frontend/FRONTEND-COMPONENTS.json": (
                        b'{"components":[{"purl":"pkg:npm/react@19.2.7"}]}'
                    ),
                },
            )
            with self.assertRaisesRegex(ValueError, "does not match detected components"):
                MODULE.verify_zip_archive(archive, "win-x64")

    def test_previous_full_package_in_pack_output_fails(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            (root / "XE-Local-AI-Engine-1.2.3-win-full.nupkg").write_bytes(b"current")
            (root / "XE-Local-AI-Engine-1.2.2-win-full.nupkg").write_bytes(b"previous")
            with self.assertRaisesRegex(ValueError, "previous full package"):
                MODULE.select_current_full_package(root, "1.2.3")

    def test_linux_full_package_verifies_its_embedded_appimage(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            standalone = root / "XE-Local-AI-Engine.AppImage"
            standalone.write_bytes(b"appimage-bytes")
            full_package = root / "XE-Local-AI-Engine-1.2.3-linux-full.nupkg"
            with zipfile.ZipFile(full_package, "w") as output:
                output.writestr("lib/app/XE-Local-AI-Engine.AppImage", b"appimage-bytes")

            inspected: list[bytes] = []

            def inspect_embedded(path: Path, runtime_identifier: str) -> None:
                self.assertEqual("linux-x64", runtime_identifier)
                inspected.append(path.read_bytes())

            with mock.patch.object(MODULE, "verify_appimage", side_effect=inspect_embedded) as verifier:
                MODULE.verify_linux_full_package(full_package, standalone)

            verifier.assert_called_once()
            self.assertEqual([b"appimage-bytes"], inspected)

    def test_linux_full_package_rejects_an_ambiguous_embedded_appimage(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            standalone = root / "XE-Local-AI-Engine.AppImage"
            standalone.write_bytes(b"appimage-bytes")
            full_package = root / "XE-Local-AI-Engine-1.2.3-linux-full.nupkg"
            with zipfile.ZipFile(full_package, "w") as output:
                output.writestr("lib/app/one.AppImage", b"appimage-bytes")
                output.writestr("lib/app/two.AppImage", b"appimage-bytes")

            with self.assertRaisesRegex(ValueError, "exactly one embedded AppImage"):
                MODULE.verify_linux_full_package(full_package, standalone)

    def test_linux_full_package_must_match_the_standalone_appimage(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            standalone = root / "XE-Local-AI-Engine.AppImage"
            standalone.write_bytes(b"standalone")
            full_package = root / "XE-Local-AI-Engine-1.2.3-linux-full.nupkg"
            with zipfile.ZipFile(full_package, "w") as output:
                output.writestr("lib/app/XE-Local-AI-Engine.AppImage", b"different")

            with self.assertRaisesRegex(ValueError, "does not match the standalone AppImage"):
                MODULE.verify_linux_full_package(full_package, standalone)


if __name__ == "__main__":
    unittest.main()
