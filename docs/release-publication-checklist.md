# Official portable release publication checklist

This checklist applies to the current portable artifacts published through Velopack. It does not assert that an
installer, signing certificate, or signed stable artifact exists.

## Authority gate

- [ ] Replace every `unresolved` decision in `docs/compliance/release-authority-register.json` with durable,
      named, dated, evidenced approval.
- [ ] Keep author identity entries public-safe: use the two established public identities or aliases; do not publish
      new private identity data.
- [ ] Document permissions or non-applicability for employer, contractor, predecessor-project, and C0re material.
- [ ] Account for copied/adapted schemas, migrations, golden vectors, templates, logos/media/branding, and vendored
      agency-agents.
- [ ] Bind the third-party/native/WASM/.NET/Velopack redistribution decision to the release's license inventory and
      artifact evidence.
- [ ] Name who is authorized to create the canonical tag and publish the corresponding portable binaries.
- [ ] Record the unsigned-build risk decision. A future certificate does not retroactively validate an earlier
      artifact; update the decision and evidence when signing is introduced.
- [ ] Run `python3 scripts/release/verify-release-authority.py` and require a pass.

## Repository protection prerequisite

- [ ] Have a repository administrator create and configure the GitHub environment `open-source-release` externally
      in the repository settings before publication. Referencing it in workflow YAML does not create its protection.
- [ ] Configure the environment with required reviewers and other appropriate protection rules; enable prevention of
      self-review where GitHub makes that setting available.
- [ ] Verify the protection in the repository settings and in a rehearsal: both `prepare-release-draft` and
      `publish-release` must pause for separate approvals before either receives repository write access.
- [ ] Treat a missing or misconfigured environment as an external publication blocker. No workflow job may create,
      replace, delete, or publish release assets before the environment authorizes its write-capable job.

## Publication binding

- [ ] Confirm a supported .NET 8 runtime is installed for the pinned SBOM tool; never satisfy that prerequisite by
      rolling the tool forward to .NET 10.
- [ ] Confirm the release is built from the canonical, immutable `v<version>` tag.
- [ ] Confirm the verified artifacts are the exact portable assets handed to Velopack for publication.
- [ ] Confirm checksums, SBOM, license inventory, `LICENSE`, and `NOTICE` refer to those exact artifact bytes.
- [ ] Preserve the authority-register result and release evidence with the release record.

## Target-OS and public-equivalent rehearsal

- [ ] Retain a real Windows transcript for the exact generated Velopack `Portable.zip`: extract to a writable local
      directory, launch the top-level executable, authenticate, verify the displayed version and runtime-license links,
      apply an anonymous RC.1 → RC.2 update, confirm relaunch, then remove the extracted app and user data as documented.
- [ ] Retain a real Linux transcript for the exact generated AppImage: verify checksum, mark executable, launch from a
      writable local directory, authenticate, verify the displayed version and runtime-license links, apply an anonymous
      RC.1 → RC.2 update, confirm in-place replacement/relaunch, then remove the AppImage and user data as documented.
- [ ] Run and retain the non-vacuous Playwright E2E result from `scripts/run-e2e-local.sh`; exit 75 is contamination,
      not a pass.
- [ ] Run and retain `scripts/run-gpu-smoke-local.sh` on an NVIDIA GPU host. A correct answer without the GPU-work
      assertion is not release evidence; exit 5 is infrastructure/no verdict, not a pass.
- [ ] Retain the clean `scripts/lint-release-scripts.sh`, Release restore/build/test, frontend frozen-install/lint/test/
      build, live OpenAPI, license-corpus, SPDX, artifact-content, and checksum results for the tagged source.
- [ ] Rehearse two published RCs in a disposable public-equivalent repository and prove anonymous Windows/Linux
      discovery, full/delta download, apply, relaunch, and release-track/OS-channel selection.
- [ ] Confirm the canonical `prepare-release-draft` run re-downloaded and verified all primary assets plus detached
      evidence, and that protected `publish-release` re-verified the trusted checksum digest before promotion.

The register automation validates structure, completeness, evidence paths, and freshness only. It is not legal advice
or certification. Publication remains blocked while the committed register is unresolved.
