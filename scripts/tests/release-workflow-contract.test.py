#!/usr/bin/env python3
from __future__ import annotations

import re
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = REPO_ROOT / ".github" / "workflows" / "release.yml"
PACKAGE_WORKFLOW = REPO_ROOT / ".github" / "workflows" / "package-velopack.yml"
DEV_WORKFLOW = REPO_ROOT / ".github" / "workflows" / "dev-build.yml"
PACKAGE_VERSIONS = REPO_ROOT / "Directory.Packages.props"
BUILD_WORKFLOW = REPO_ROOT / ".github" / "workflows" / "build-and-test.yml"
VERSION_READER = REPO_ROOT / "scripts" / "read-release-version.py"


class ReleaseWorkflowContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.source = WORKFLOW.read_text(encoding="utf-8")
        cls.package_source = PACKAGE_WORKFLOW.read_text(encoding="utf-8")
        cls.dev_source = DEV_WORKFLOW.read_text(encoding="utf-8")
        cls.package_versions = PACKAGE_VERSIONS.read_text(encoding="utf-8")
        # release.yml plus the packaging workflow it calls: the surface a tag release renders.
        cls.packaging = cls.source + "\n" + cls.package_source
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

    def test_frontend_ci_uses_the_shared_acceptance_gate(self) -> None:
        client_job = re.search(r"\n  client-react:\n(?P<body>.*)", self.build_source, re.DOTALL)
        self.assertIsNotNone(client_job)
        body = client_job.group("body")
        self.assertEqual(1, body.count("run: pnpm run acceptance"))
        self.assertNotIn("run: pnpm run build", body)
        self.assertNotIn("run: pnpm run validate", body)

    def test_cross_platform_release_job_pins_python_and_uses_python_command(self) -> None:
        build_job = re.search(r"\n  build-pack:\n(?P<body>.*)", self.package_source, re.DOTALL)
        self.assertIsNotNone(build_job)
        body = build_job.group("body")
        setup = "uses: actions/setup-python@5fda3b95a4ea91299a34e894583c3862153e4b97 # v7.0.0"
        self.assertIn(setup, body)
        self.assertIn("python-version: '3.13'", body)
        self.assertNotIn("python3", body)
        self.assertIn("python scripts/compliance/create_bundle_input_manifest.py", body)

    def test_release_serialization_is_repository_wide(self) -> None:
        self.assertIn("group: official-release-${{ github.repository }}", self.source)
        self.assertNotIn("group: release-${{ github.ref }}", self.source)

    def test_windows_is_portable_only_and_linux_is_appimage(self) -> None:
        self.assertIn('pack-args: "--noInst"', self.package_source)
        self.assertIn('--runtime "${{ matrix.rid }}"', self.package_source)
        self.assertNotIn("--noPortable", self.package_source)
        self.assertRegex(self.package_source, r"icon-args: .+\.png")

    def test_windows_publishes_csharp_launcher_for_framework_dependent_payload(self) -> None:
        build_job = re.search(r"\n  build-pack:\n(?P<body>.*)", self.package_source, re.DOTALL)
        self.assertIsNotNone(build_job)
        body = build_job.group("body")
        publish = body.index("name: Publish (${{ matrix.rid }})")
        launcher = body.index("name: Build and test Windows framework launcher")
        compliance = body.index("name: Generate exact backend NuGet legal corpus")
        self.assertLess(publish, launcher)
        self.assertLess(launcher, compliance)
        self.assertIn("XE-Local-AI-Engine.WindowsLauncher/XE-Local-AI-Engine.WindowsLauncher.csproj", body)
        self.assertIn("scripts/tests/windows-framework-launcher-smoke.ps1", body)
        self.assertIn("main-exe: XE-Local-AI-Engine.WindowsLauncher.exe", body)
        self.assertIn('--mainExe "${{ matrix.main-exe }}"', body)
        self.assertNotIn("--framework", body)
        self.assertIn("scripts/read-release-version.py --dotnet-runtime", body)
        self.assertIn("--windows-apphost-version", body)
        self.assertIn("--windows-apphost-license", body)
        self.assertIn("--windows-apphost-notices", body)

    def test_native_shell_is_published_with_separate_compliance_evidence(self) -> None:
        shell = self.package_source.index("name: Publish native shell (${{ matrix.rid }})")
        launcher = self.package_source.index("name: Build and test Windows framework launcher")
        self.assertLess(shell, launcher)
        self.assertIn("main-exe: XE-Local-AI-Engine.Desktop", self.package_source)
        self.assertIn("XE-Local-AI-Engine.Desktop/XE-Local-AI-Engine.Desktop.csproj", self.package_source)
        self.assertEqual(
            2,
            self.package_source.count(
                '--additional-deps-json "XE-Local-AI-Engine.Desktop/bin/Release/net10.0/'
                '${{ matrix.rid }}/XE-Local-AI-Engine.Desktop.deps.json"'
            ),
        )
        self.assertEqual(
            2,
            self.package_source.count(
                '--additional-bundle-input-manifest "${{ matrix.publish-dir }}/desktop-bundle-inputs.json"'
            ),
        )
        self.assertIn('--output "${{ matrix.publish-dir }}/desktop-bundle-inputs.json"', self.package_source)

    def test_windows_payload_does_not_claim_the_dotnet_library_license(self) -> None:
        self.assertNotIn("--dotnet-library-license", self.packaging)
        self.assertNotIn("DOTNET-LIBRARY-LICENSE.html", self.packaging)

    def test_prerelease_seed_download_is_explicit(self) -> None:
        download_block = re.search(
            r"name: Download previous release.*?(?=\n\s+- name:)", self.package_source, re.DOTALL
        )
        self.assertIsNotNone(download_block)
        body = download_block.group(0)
        self.assertIn('if [[ "${{ inputs.is-prerelease }}" == "true" ]]', body)
        self.assertIn("INCLUDE_PRERELEASE=true", body)
        self.assertIn('SEED_DIR="ReleaseSeed-${{ matrix.rid }}"', body)

    def test_the_delta_seed_never_goes_through_the_ten_release_vpk_window(self) -> None:
        # `vpk download github` wraps the same Velopack GithubSource that reads only the 10 newest releases,
        # so after 10 Development prereleases a tag release would find no win/linux predecessor.
        self.assertNotIn("vpk download", self.packaging)
        download_block = re.search(
            r"name: Download previous release.*?(?=\n\s+- name:)", self.package_source, re.DOTALL
        )
        self.assertIsNotNone(download_block)
        body = download_block.group(0)
        self.assertIn('FEED_NAME="releases.$VPK_CHANNEL.json"', body)
        self.assertIn("Accept: application/octet-stream", body)
        self.assertIn('select(.Type == "Full")', body)
        self.assertIn("expected exactly one previous full package", body)

    def test_the_delta_seed_is_verified_against_the_feed_hash(self) -> None:
        # The vpk CLI hash-checked its own seed. The pack step deletes this one before upload, so no later
        # gate ever sees these bytes and a corrupt base would ship a delta that cannot apply.
        download_block = re.search(
            r"name: Download previous release.*?(?=\n\s+- name:)", self.package_source, re.DOTALL
        )
        self.assertIsNotNone(download_block)
        body = download_block.group(0)
        self.assertIn("select(.FileName == $name) | .SHA256", body)
        self.assertIn("sha256sum --check --strict", body)
        self.assertIn("^[0-9a-f]{64}$", body)

    def test_previous_release_probe_matches_the_current_release_track(self) -> None:
        download_block = re.search(
            r"name: Download previous release.*?(?=\n\s+- name:)", self.package_source, re.DOTALL
        )
        self.assertIsNotNone(download_block)
        body = download_block.group(0)
        self.assertIn("--argjson include_pre", body)
        self.assertIn("$include_pre or (.prerelease == false)", body)

    def test_previous_full_package_is_removed_after_pack(self) -> None:
        pack_block = re.search(r"name: Pack portable artifact.*?(?=\n\s+- name:)", self.package_source, re.DOTALL)
        self.assertIsNotNone(pack_block)
        self.assertIn('rm -- "Releases-${{ matrix.rid }}/$PREVIOUS_NAME"', pack_block.group(0))

    def test_release_track_selects_the_matching_publish_flavor(self) -> None:
        self.assertIn("update-channel=tester", self.source)
        self.assertIn("update-channel=main", self.source)
        self.assertIn("-p:UpdateChannel=${{ inputs.update-channel }}", self.package_source)
        self.assertIn("Verify packaged update release track", self.package_source)

    def test_matrix_builds_but_does_not_upload_releases(self) -> None:
        build_job = re.search(r"\n  build-pack:\n(?P<body>.*)", self.package_source, re.DOTALL)
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
        self.assertIn("sha256sum protected-remote/CHECKSUMS.sha256", body)
        self.assertIn('if [[ "$ACTUAL_CHECKSUM_DIGEST" != "$EXPECTED_CHECKSUM_DIGEST" ]]', body)
        self.assertLess(
            body.index("Verify anonymous repository availability before promotion"),
            body.index("Publish the already verified draft without replacing assets"),
        )
        for forbidden in ("vpk upload", "gh release upload", "dotnet publish", "vpk pack"):
            self.assertNotIn(forbidden, body)

    def test_compliance_and_authority_gates_run_before_publication(self) -> None:
        self.assertIn("scripts/compliance/sbom-tool.sh", self.packaging)
        self.assertEqual(1, self.source.count("scripts/release/verify-release-authority.py"))
        self.assertIn("RELEASE.spdx.json", self.source)
        self.assertIn("RELEASE-MANIFEST.json", self.source)
        self.assertIn("CHECKSUMS.sha256", self.source)
        self.assertIn("scripts/compliance/verify_remote_velopack_assets.py", self.source)
        self.assertEqual(2, self.source.count("scripts/release/verify-release-envelope.py"))

    def test_each_rid_generates_its_exact_backend_legal_corpus_before_spdx(self) -> None:
        corpus_command = "scripts/compliance/generate_backend_license_corpus.py"
        sbom_command = "scripts/compliance/sbom-tool.sh Generate"
        self.assertEqual(1, self.package_source.count(corpus_command))
        self.assertLess(self.package_source.index(corpus_command), self.package_source.index(sbom_command))
        self.assertIn("dotnet nuget-license", self.package_source)
        self.assertIn("--include-transitive", self.package_source)
        self.assertIn('--rid "${{ matrix.rid }}"', self.package_source)
        self.assertIn('--output-directory "${{ matrix.publish-dir }}"', self.package_source)
        self.assertEqual(
            2,
            self.package_source.count('--bundle-input-manifest "${{ matrix.publish-dir }}/bundle-inputs.json"'),
        )
        self.assertIn('--backend-manifest "${{ matrix.publish-dir }}/backend-components.json"', self.package_source)
        self.assertNotIn("--about-manifest", self.packaging)

    def test_publish_captures_embedded_and_loose_inputs_before_compliance_generation(self) -> None:
        publish_block = re.search(
            r"name: Publish \(\$\{\{ matrix\.rid \}\}\).*?(?=\n\s+- name:)", self.package_source, re.DOTALL
        )
        self.assertIsNotNone(publish_block)
        body = publish_block.group(0)
        self.assertIn("scripts/compliance/capture_bundle_inputs.targets", body)
        self.assertIn("-p:XeBundleInputEvidenceRaw=", body)
        self.assertIn("scripts/compliance/create_bundle_input_manifest.py", body)
        self.assertIn('--output "${{ matrix.publish-dir }}/bundle-inputs.json"', body)
        self.assertLess(body.index("dotnet publish"), body.index("create_bundle_input_manifest.py"))

    def test_sbom_tool_runs_on_net8_and_validation_is_explicit(self) -> None:
        self.assertGreaterEqual(self.packaging.count("dotnet-version: 8.0.x"), 2)
        self.assertEqual(2, self.packaging.count("scripts/compliance/sbom-tool.sh Validate"))
        self.assertGreaterEqual(self.packaging.count("-mi SPDX:2.2"), 2)
        self.assertIn("payload-sbom-validation-${{ matrix.rid }}.json", self.package_source)
        self.assertIn("release-envelope-validation.json", self.source)
        self.assertEqual(1, self.packaging.count("scripts/compliance/reconcile_payload_spdx.py"))
        self.assertIn('-bc "${{ matrix.publish-dir }}"', self.package_source)
        self.assertIn("-bc remote-primary", self.source)
        self.assertEqual(2, self.packaging.count('-ps "XE Local AI Engine contributors"'))
        self.assertNotIn('-ps "Organization: XE Local AI Engine contributors"', self.packaging)

    def test_version_job_does_not_depend_on_an_unconfigured_dotnet_runner(self) -> None:
        version_job = re.search(r"\n  version:\n(?P<body>.*?)(?=\n  build-pack:)", self.source, re.DOTALL)
        self.assertIsNotNone(version_job)
        self.assertNotIn("dotnet tool restore", version_job.group("body"))

    def test_release_delegates_packaging_to_the_shared_workflow(self) -> None:
        self.assertIn("uses: ./.github/workflows/package-velopack.yml", self.source)
        build_job = re.search(r"\n  build-pack:\n(?P<body>.*?)(?=\n  prepare-release-draft:)", self.source, re.DOTALL)
        self.assertIsNotNone(build_job)
        body = build_job.group("body")
        self.assertNotIn("vpk pack", body)
        self.assertNotIn("strategy:", body)
        self.assertIn("workflow_call:", self.package_source)
        for name in (
            "pack-version:",
            "update-channel:",
            "is-prerelease:",
            "source-sha:",
            "velopack-channel-suffix:",
            "require-tag-binding:",
            "release-notes-artifact:",
            "vpk-version:",
        ):
            self.assertIn(f"      {name}", self.package_source)

    def test_packaging_workflow_binds_the_tag_only_when_asked(self) -> None:
        guard = 'if [[ "${{ inputs.require-tag-binding }}" == "true" ]]'
        self.assertIn(guard, self.package_source)
        unconditional = 'test "$GITHUB_SHA" = "${{ inputs.source-sha }}"'
        self.assertIn(unconditional, self.package_source)
        self.assertLess(self.package_source.index(unconditional), self.package_source.index(guard))
        self.assertIn("require-tag-binding: true", self.source)

    def test_packaging_workflow_accepts_only_the_three_baked_channels(self) -> None:
        self.assertIn(
            'EXPECTED_DEFAULT_CHANNELS = {"main": "Stable", "tester": "Preview", "dev": "Development"}',
            self.package_source,
        )
        self.assertIn("unsupported update channel", self.package_source)
        self.assertIn('actual.get("DefaultChannel") != expected_default', self.package_source)
        self.assertNotIn("ReleaseTrack", self.package_source)

    def test_packaging_cross_checks_the_prerelease_flag_against_the_flavour(self) -> None:
        self.assertIn(
            'EXPECTED_PRERELEASE = {"main": "false", "tester": "true", "dev": "true"}',
            self.package_source,
        )
        self.assertIn('actual_prerelease = "${{ inputs.is-prerelease }}"', self.package_source)
        self.assertIn("packaged prerelease flag mismatch", self.package_source)

    def test_velopack_channel_is_the_os_name_plus_the_suffix(self) -> None:
        self.assertEqual(
            1,
            self.package_source.count("VPK_CHANNEL: ${{ matrix.os-short }}${{ inputs.velopack-channel-suffix }}"),
        )
        self.assertNotIn("matrix.vpk-channel", self.packaging)
        self.assertIn('--channel "$VPK_CHANNEL"', self.package_source)
        # One call site: `vpk pack`. The delta seed selects its release through the releases API instead.
        self.assertEqual(1, self.package_source.count('--channel "$VPK_CHANNEL"'))
        self.assertIn('FEED_NAME="releases.$VPK_CHANNEL.json"', self.package_source)
        self.assertIn("name: velopack-${{ matrix.os-short }}", self.package_source)
        self.assertIn('case "${{ inputs.velopack-channel-suffix }}" in', self.package_source)
        self.assertIn('""|-dev) ;;', self.package_source)

    def test_no_vpk_pack_invocation_carries_pre(self) -> None:
        blocks = re.findall(r"vpk pack \\\n(?:.*\\\n)*.*\n", self.packaging)
        self.assertGreaterEqual(len(blocks), 1)
        for block in blocks:
            self.assertNotIn(" --pre", block)
        # `--pre` is an upload-time flag, and release.yml still uses it there.
        self.assertIn("--pre", self.source)

    def test_both_release_paths_call_the_shared_packaging_workflow(self) -> None:
        call = "uses: ./.github/workflows/package-velopack.yml"
        self.assertEqual(1, self.source.count(call))
        self.assertEqual(1, self.dev_source.count(call))

    def test_dev_build_runs_the_shared_validation_gate(self) -> None:
        self.assertIn("uses: ./.github/workflows/build-and-test.yml", self.dev_source)
        validate = re.search(r"\n  validate:\n(?P<body>.*?)(?=\n  package:)", self.dev_source, re.DOTALL)
        self.assertIsNotNone(validate)
        body = validate.group("body")
        self.assertIn("needs: decide", body)
        self.assertIn("if: needs.decide.outputs.skip != 'true'", body)

    def test_dev_build_packs_the_dev_flavour_on_suffixed_channels(self) -> None:
        package = re.search(r"\n  package:\n(?P<body>.*?)(?=\n  publish:)", self.dev_source, re.DOTALL)
        self.assertIsNotNone(package)
        body = package.group("body")
        for value in (
            "update-channel: dev",
            "velopack-channel-suffix: -dev",
            "is-prerelease: 'true'",
            "require-tag-binding: false",
        ):
            self.assertIn(value, body)

    def test_dev_build_never_uploads_to_the_default_channels(self) -> None:
        self.assertEqual(0, self.dev_source.count("--channel win\n"))
        self.assertEqual(0, self.dev_source.count("--channel linux\n"))
        self.assertEqual(0, self.dev_source.count('--tag "v'))
        self.assertEqual(0, self.dev_source.count("refs/tags/v"))
        self.assertEqual(1, self.dev_source.count("--channel win-dev"))
        self.assertEqual(1, self.dev_source.count("--channel linux-dev"))

    def test_dev_build_joins_the_release_concurrency_lane(self) -> None:
        self.assertIn("group: official-release-${{ github.repository }}", self.dev_source)
        self.assertIn("cancel-in-progress: false", self.dev_source)

    def test_dev_build_has_no_environment_gate_and_writes_only_where_needed(self) -> None:
        self.assertEqual(0, self.dev_source.count("environment:"))
        self.assertEqual(2, self.dev_source.count("contents: write"))
        self.assertEqual(4, self.dev_source.count("contents: read"))

    def test_dev_build_clears_a_stale_draft_before_uploading(self) -> None:
        # A failed earlier run on the same UTC date leaves a draft under this tag; `vpk upload` then refuses the
        # name and the "exactly one release for tag" assertion fails, wedging every retry until the date rolls.
        self.assertIn("name: Remove stale drafts for this tag", self.dev_source)
        self.assertIn("select(.draft == true and .tag_name == $tag)", self.dev_source)
        self.assertIn('gh api --method DELETE "repos/$GITHUB_REPOSITORY/releases/$release_id"', self.dev_source)
        self.assertLess(
            self.dev_source.index("name: Remove stale drafts for this tag"),
            self.dev_source.index("vpk upload github"),
        )

    def test_dev_build_publishes_a_draft_before_promoting_it(self) -> None:
        self.assertEqual(2, self.dev_source.count("vpk upload github"))
        self.assertEqual(0, self.dev_source.count("--publish"))
        self.assertLess(
            self.dev_source.index("scripts/release/verify-release-envelope.py"),
            self.dev_source.index("draft=false"),
        )

    def test_dev_build_prune_never_deletes_tags_or_official_releases(self) -> None:
        self.assertIn("gh release delete", self.dev_source)
        self.assertEqual(0, self.dev_source.count("--cleanup-tag"))
        self.assertIn("dev/*) ;;", self.dev_source)
        self.assertIn('startswith("dev/")', self.dev_source)
        self.assertEqual(0, self.dev_source.count("--keep"))
        # Paginated: `gh release list --limit 200` hides the oldest dev/ rows once the v* line is long enough.
        self.assertNotIn("gh release list", self.dev_source)
        self.assertEqual(3, self.dev_source.count('gh api --paginate "repos/$GITHUB_REPOSITORY/releases?per_page=100"'))

    def test_dev_build_never_expands_a_computed_string_into_a_run_body(self) -> None:
        # Those values are refnames; `$`, backticks and parentheses in one would execute in the publish lane.
        bodies = re.findall(r"\n        run: \|\n(?P<body>(?:.*\n)*?)(?=\n?\s{6}- name:|\n\s{2}\w|\Z)", self.dev_source)
        self.assertGreaterEqual(len(bodies), 8)
        for body in bodies:
            self.assertNotIn("steps.identity.outputs", body)
            self.assertNotIn("needs.decide.outputs", body)

    def test_dev_build_is_scheduled_and_dispatchable_from_develop_only(self) -> None:
        self.assertIn("schedule:", self.dev_source)
        self.assertIn("cron:", self.dev_source)
        self.assertIn("workflow_dispatch:", self.dev_source)
        self.assertIn('test "$GITHUB_REF" = "refs/heads/develop"', self.dev_source)

    def test_dev_build_verifies_both_dev_feeds_after_publication(self) -> None:
        self.assertIn("for channel in win-dev linux-dev; do", self.dev_source)
        self.assertIn('--arg name "releases.$channel.json"', self.dev_source)
        self.assertIn('grep -F "$DEV_VERSION"', self.dev_source)

    def test_vpk_version_pin_is_consistent_across_every_release_surface(self) -> None:
        pins = [
            re.search(r'VPK_VERSION: "([^"]+)"', self.source),
            re.search(r'VPK_VERSION: "([^"]+)"', self.dev_source),
            re.search(r'vpk-version:.*?default: "([^"]+)"', self.package_source, re.DOTALL),
            re.search(r'<PackageVersion Include="Velopack" Version="([^"]+)"', self.package_versions),
        ]
        self.assertEqual(4, len([pin for pin in pins if pin is not None]))
        values = {pin.group(1) for pin in pins if pin is not None}
        self.assertEqual(1, len(values), f"Velopack pins drifted: {sorted(values)}")

    def test_git_cliff_pin_is_consistent_across_both_note_producers(self) -> None:
        pattern = r'GIT_CLIFF_VERSION: "([^"]+)"\n\s+GIT_CLIFF_SHA256: "([^"]+)"'
        release_pin = re.search(pattern, self.source)
        dev_pin = re.search(pattern, self.dev_source)
        self.assertIsNotNone(release_pin)
        self.assertIsNotNone(dev_pin)
        self.assertEqual(release_pin.groups(), dev_pin.groups())

    def test_ci_compares_the_live_backend_openapi_document(self) -> None:
        self.assertIn("pnpm install --frozen-lockfile", self.build_source)
        self.assertIn("scripts/openapi-live-check.sh", self.build_source)


if __name__ == "__main__":
    unittest.main()
