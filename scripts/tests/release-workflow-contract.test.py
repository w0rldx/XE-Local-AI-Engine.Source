#!/usr/bin/env python3
from __future__ import annotations

import re
import unittest
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = REPO_ROOT / ".github" / "workflows" / "release.yml"
BUILD_WORKFLOW = REPO_ROOT / ".github" / "workflows" / "build-and-test.yml"
VERSION_READER = REPO_ROOT / "scripts" / "read-release-version.py"


class ReleaseWorkflowContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.source = WORKFLOW.read_text(encoding="utf-8")
        cls.build_source = BUILD_WORKFLOW.read_text(encoding="utf-8")
        cls.version_reader = VERSION_READER.read_text(encoding="utf-8")

    def test_release_identity_comes_from_single_manifest(self) -> None:
        self.assertNotRegex(self.source, r"grep.+Directory\.Build\.props")
        self.assertIn("scripts/read-release-version.py", self.source)
        self.assertIn('Path("eng/ReleaseVersion.props")', self.version_reader)

    def test_frontend_license_gate_installs_sdk_runtime_and_pinned_dotnet_tools(self) -> None:
        client_job = re.search(r"\n  client-react:\n(?P<body>.*)", self.build_source, re.DOTALL)
        self.assertIsNotNone(client_job)
        body = client_job.group("body")
        license_gate = body.index("name: Check dependency licenses")
        for prerequisite in (
            "dotnet-version: 8.0.x",
            "global-json-file: global.json",
            "dotnet tool restore --tool-manifest ../dotnet-tools.json",
        ):
            self.assertIn(prerequisite, body)
            self.assertLess(body.index(prerequisite), license_gate)

    def test_frontend_ci_runs_tooling_regressions_before_build(self) -> None:
        client_job = re.search(r"\n  client-react:\n(?P<body>.*)", self.build_source, re.DOTALL)
        self.assertIsNotNone(client_job)
        body = client_job.group("body")
        self.assertIn("pnpm run test:tooling", body)
        self.assertLess(body.index("pnpm run test:tooling"), body.index("pnpm run build"))

    def test_cross_platform_release_job_pins_python_and_uses_python_command(self) -> None:
        build_job = re.search(
            r"\n  build-pack:\n(?P<body>.*?)(?=\n  prepare-release-draft:)", self.source, re.DOTALL
        )
        self.assertIsNotNone(build_job)
        body = build_job.group("body")
        setup = "uses: actions/setup-python@ece7cb06caefa5fff74198d8649806c4678c61a1 # v6.3.0"
        self.assertIn(setup, body)
        self.assertIn("python-version: '3.13'", body)
        self.assertNotIn("python3", body)
        self.assertIn("python scripts/compliance/create_bundle_input_manifest.py", body)

    def test_release_serialization_is_repository_wide(self) -> None:
        self.assertIn("group: official-release-${{ github.repository }}", self.source)
        self.assertNotIn("group: release-${{ github.ref }}", self.source)

    def test_windows_is_portable_only_and_linux_is_appimage(self) -> None:
        self.assertIn('pack-args: "--noInst"', self.source)
        self.assertIn("--runtime \"${{ matrix.rid }}\"", self.source)
        self.assertNotIn("--noPortable", self.source)
        self.assertRegex(self.source, r"icon-args: .+\.png")

    def test_windows_publishes_csharp_launcher_for_framework_dependent_payload(self) -> None:
        build_job = re.search(
            r"\n  build-pack:\n(?P<body>.*?)(?=\n  prepare-release-draft:)", self.source, re.DOTALL
        )
        self.assertIsNotNone(build_job)
        body = build_job.group("body")
        publish = body.index("name: Publish (${{ matrix.rid }})")
        launcher = body.index("name: Build and test Windows framework launcher")
        compliance = body.index("name: Generate exact backend NuGet legal corpus")
        self.assertLess(publish, launcher)
        self.assertLess(launcher, compliance)
        self.assertIn("XE-Local-AI-Engine.WindowsLauncher/XE-Local-AI-Engine.WindowsLauncher.csproj", body)
        self.assertIn("scripts/tests/windows-framework-launcher-smoke.ps1", body)
        self.assertIn('main-exe: XE-Local-AI-Engine.WindowsLauncher.exe', body)
        self.assertIn('--mainExe "${{ matrix.main-exe }}"', body)
        self.assertNotIn("--framework", body)
        self.assertIn("scripts/read-release-version.py --dotnet-runtime", body)
        self.assertIn("--windows-apphost-version", body)
        self.assertIn("--windows-apphost-license", body)
        self.assertIn("--windows-apphost-notices", body)

    def test_windows_payload_does_not_claim_the_dotnet_library_license(self) -> None:
        self.assertNotIn("--dotnet-library-license", self.source)
        self.assertNotIn("DOTNET-LIBRARY-LICENSE.html", self.source)

    def test_prerelease_seed_download_is_explicit(self) -> None:
        download_block = re.search(
            r"name: Download previous release.*?(?=\n\s+- name:)", self.source, re.DOTALL
        )
        self.assertIsNotNone(download_block)
        self.assertIn("--pre", download_block.group(0))
        self.assertIn('SEED_DIR="ReleaseSeed-${{ matrix.rid }}"', download_block.group(0))

    def test_previous_release_probe_matches_the_current_release_track(self) -> None:
        download_block = re.search(
            r"name: Download previous release.*?(?=\n\s+- name:)", self.source, re.DOTALL
        )
        self.assertIsNotNone(download_block)
        body = download_block.group(0)
        self.assertIn("--argjson include_pre", body)
        self.assertIn("$include_pre or (.prerelease == false)", body)

    def test_previous_full_package_is_removed_after_pack(self) -> None:
        pack_block = re.search(
            r"name: Pack portable artifact.*?(?=\n\s+- name:)", self.source, re.DOTALL
        )
        self.assertIsNotNone(pack_block)
        self.assertIn('rm -- "Releases-${{ matrix.rid }}/$PREVIOUS_NAME"', pack_block.group(0))

    def test_release_track_selects_the_matching_publish_flavor(self) -> None:
        self.assertIn("update-channel=tester", self.source)
        self.assertIn("update-channel=main", self.source)
        self.assertIn("-p:UpdateChannel=${{ needs.version.outputs.update-channel }}", self.source)
        self.assertIn("Verify packaged update release track", self.source)

    def test_matrix_builds_but_does_not_upload_releases(self) -> None:
        build_job = re.search(
            r"\n  build-pack:\n(?P<body>.*?)(?=\n  prepare-release-draft:)", self.source, re.DOTALL
        )
        self.assertIsNotNone(build_job)
        self.assertNotIn("vpk upload github", build_job.group("body"))
        self.assertIn("actions/upload-artifact", build_job.group("body"))

    def test_one_protected_serial_job_owns_explicit_bound_draft_creation(self) -> None:
        prepare_job = re.search(
            r"\n  prepare-release-draft:\n(?P<body>.*?)(?=\n  publish-release:)", self.source, re.DOTALL
        )
        self.assertIsNotNone(prepare_job)
        body = prepare_job.group("body")
        self.assertEqual(2, body.count("vpk upload github"))
        self.assertGreaterEqual(body.count('--tag "v$PACK_VERSION"'), 2)
        self.assertGreaterEqual(body.count('--targetCommitish "$GITHUB_SHA"'), 2)
        self.assertIn("--merge", body)
        self.assertNotIn("--publish", body)
        self.assertGreaterEqual(body.count("Download and verify complete remote draft"), 2)
        self.assertIn("gh release upload", body)
        self.assertNotIn("draft=false", body)
        self.assertIn("environment: open-source-release", body)
        self.assertIn("checksum-digest: ${{ steps.final.outputs.checksum-digest }}", body)
        self.assertIn('echo "checksum-digest=$CHECKSUM_DIGEST" >> "$GITHUB_OUTPUT"', body)

    def test_protected_publication_only_verifies_and_promotes_the_existing_draft(self) -> None:
        publish_job = re.search(r"\n  publish-release:\n(?P<body>.*)", self.source, re.DOTALL)
        self.assertIsNotNone(publish_job)
        body = publish_job.group("body")
        self.assertIn("environment: open-source-release", body)
        self.assertIn("needs: [version, prepare-release-draft]", body)
        self.assertIn("needs.prepare-release-draft.outputs.release-id", body)
        self.assertIn("needs.prepare-release-draft.outputs.checksum-digest", body)
        self.assertIn("draft=false", body)
        self.assertIn("scripts/release/verify-release-authority.py", body)
        self.assertIn("Verify anonymous repository availability before promotion", body)
        self.assertIn('sha256sum protected-remote/CHECKSUMS.sha256', body)
        self.assertIn('if [[ "$ACTUAL_CHECKSUM_DIGEST" != "$EXPECTED_CHECKSUM_DIGEST" ]]', body)
        self.assertLess(
            body.index("Verify anonymous repository availability before promotion"),
            body.index("Publish the already verified draft without replacing assets"),
        )
        for forbidden in ("vpk upload", "gh release upload", "dotnet publish", "vpk pack"):
            self.assertNotIn(forbidden, body)

    def test_compliance_and_authority_gates_run_before_publication(self) -> None:
        self.assertIn("scripts/compliance/sbom-tool.sh", self.source)
        self.assertEqual(1, self.source.count("scripts/release/verify-release-authority.py"))
        self.assertIn("RELEASE.spdx.json", self.source)
        self.assertIn("RELEASE-MANIFEST.json", self.source)
        self.assertIn("CHECKSUMS.sha256", self.source)
        self.assertIn("scripts/compliance/verify_remote_velopack_assets.py", self.source)
        self.assertEqual(2, self.source.count("scripts/release/verify-release-envelope.py"))

    def test_each_rid_generates_its_exact_backend_legal_corpus_before_spdx(self) -> None:
        corpus_command = "scripts/compliance/generate_backend_license_corpus.py"
        sbom_command = "scripts/compliance/sbom-tool.sh Generate"
        self.assertEqual(1, self.source.count(corpus_command))
        self.assertLess(self.source.index(corpus_command), self.source.index(sbom_command))
        self.assertIn("dotnet nuget-license", self.source)
        self.assertIn("--include-transitive", self.source)
        self.assertIn('--rid "${{ matrix.rid }}"', self.source)
        self.assertIn('--output-directory "${{ matrix.publish-dir }}"', self.source)
        self.assertEqual(
            2,
            self.source.count('--bundle-input-manifest "${{ matrix.publish-dir }}/bundle-inputs.json"'),
        )
        self.assertIn('--backend-manifest "${{ matrix.publish-dir }}/backend-components.json"', self.source)
        self.assertNotIn("--about-manifest", self.source)

    def test_publish_captures_embedded_and_loose_inputs_before_compliance_generation(self) -> None:
        publish_block = re.search(
            r"name: Publish \(\$\{\{ matrix\.rid \}\}\).*?(?=\n\s+- name:)", self.source, re.DOTALL
        )
        self.assertIsNotNone(publish_block)
        body = publish_block.group(0)
        self.assertIn("scripts/compliance/capture_bundle_inputs.targets", body)
        self.assertIn("-p:XeBundleInputEvidenceRaw=", body)
        self.assertIn("scripts/compliance/create_bundle_input_manifest.py", body)
        self.assertIn('--output "${{ matrix.publish-dir }}/bundle-inputs.json"', body)
        self.assertLess(body.index("dotnet publish"), body.index("create_bundle_input_manifest.py"))

    def test_sbom_tool_runs_on_net8_and_validation_is_explicit(self) -> None:
        self.assertGreaterEqual(self.source.count("dotnet-version: 8.0.x"), 2)
        self.assertEqual(2, self.source.count("scripts/compliance/sbom-tool.sh Validate"))
        self.assertGreaterEqual(self.source.count("-mi SPDX:2.2"), 2)
        self.assertIn("payload-sbom-validation-${{ matrix.rid }}.json", self.source)
        self.assertIn("release-envelope-validation.json", self.source)
        self.assertEqual(1, self.source.count("scripts/compliance/reconcile_payload_spdx.py"))
        self.assertIn('-bc "${{ matrix.publish-dir }}"', self.source)
        self.assertIn("-bc remote-primary", self.source)
        self.assertEqual(2, self.source.count('-ps "XE Local AI Engine contributors"'))
        self.assertNotIn('-ps "Organization: XE Local AI Engine contributors"', self.source)

    def test_version_job_does_not_depend_on_an_unconfigured_dotnet_runner(self) -> None:
        version_job = re.search(
            r"\n  version:\n(?P<body>.*?)(?=\n  build-pack:)", self.source, re.DOTALL
        )
        self.assertIsNotNone(version_job)
        self.assertNotIn("dotnet tool restore", version_job.group("body"))

    def test_ci_compares_the_live_backend_openapi_document(self) -> None:
        self.assertIn("pnpm install --frozen-lockfile", self.build_source)
        self.assertIn("scripts/openapi-live-check.sh", self.build_source)


if __name__ == "__main__":
    unittest.main()
