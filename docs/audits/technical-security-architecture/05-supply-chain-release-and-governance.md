# 05 — Supply Chain, Release, and Governance

## Review boundary

| Item | Value |
|---|---|
| Frozen application baseline | `7e64ed589e14eecc0e522e807d2e531a1095d19a` |
| Review date | 2026-07-28 |
| Scope | Dependency resolution, license inventory, vulnerability review, build/release gates, artifact integrity, SBOM/provenance, and release governance |
| Evidence boundary | Repository configuration and scripts were reviewed. A release transcript, dependency-audit output, signed artifact, SBOM, provenance attestation, and recipient-verifiable operating sample were not supplied. |

Source paths are internal traceability only. Repository implementation and test source are not assumed to be available to the recipient.

## Current posture at a glance

| Area | Baseline posture | Limitation |
|---|---|---|
| .NET versions | Central package version management with central transitive pinning | No committed NuGet `packages.lock.json` files |
| Frontend versions | Committed pnpm lockfile; release uses `pnpm install --frozen-lockfile` | Package manifest still contains semver ranges; the lockfile is the resolved source of truth |
| Tool versions | .NET SDK feature-band baseline, local dotnet tools, Velopack CLI, git-cliff archive, and dormant GitHub actions are version-pinned in their respective paths | `global.json` permits `latestFeature` roll-forward; pinning is not a signed toolchain attestation |
| License inventory | Generated manifest for direct production frontend and top-level backend packages, plus `NOTICE` pointers for other material | It is not a transitive package inventory or an explicit license allow/deny policy |
| Vulnerability review | Manual packager blocks on frontend high/critical production advisories and any NuGet vulnerable top-level/transitive package reported by the CLI | Runs at manual package time; no continuously operating scan is evidenced |
| Branch/merge validation | Tracked workflow definitions describe intended validation | GitHub Actions are dormant/disabled at the baseline; no repository-evidenced or GitHub Actions gate automatically gates a branch or merge |
| Tester release | `publish/package-tester-win.ps1` is the effective manual Windows gate and published tester-RC path | Execution depends on one packaging workstation/operator and supplied credentials |
| Artifact integrity | Clean-tree/tag checks, source commit/tag in a SHA-256 manifest, draft-first release, and hash verification before publication | Hashes and manifest are unsigned; integrity is not publisher authenticity |
| Code signing | Not wired at the baseline | Packaged artifacts remain unsigned |
| SBOM and signed provenance | No CycloneDX/SPDX SBOM or signed build attestation is produced by the effective release path | Dependency/license manifest and SHA-256 JSON are not substitutes for an SBOM or attestation |
| Formal governance | Scripted technical gates exist | Release authority, segregation of duties, risk acceptance, and evidence retention are not defined in repository-controlled policy |

## Dependency and toolchain control

### .NET

The solution uses central package management in `Directory.Packages.props`, including `CentralPackageTransitivePinningEnabled`. Direct and selected transitive versions are therefore visible in one file.

`global.json` requests SDK `10.0.100`, disallows prerelease SDKs, selects Microsoft.Testing.Platform, and permits `latestFeature` roll-forward. The root `dotnet-tools.json` pins the repository tools, including `dotnet-ef` and `nuget-license`, with tool roll-forward disabled.

There are no committed NuGet `packages.lock.json` files at the frozen baseline. Central pinning improves reviewability, but it is not a complete resolved-graph lock or a build-environment attestation.

Internal traceability:

- `Directory.Build.props`
- `Directory.Packages.props`
- `global.json`
- `dotnet-tools.json`

### Frontend

The React client commits `pnpm-lock.yaml` and the release packager uses `pnpm install --frozen-lockfile`. This fails rather than silently updating a stale lockfile during packaging.

Internal traceability:

- `XE-Local-AI-Engine.Client.React/package.json`
- `XE-Local-AI-Engine.Client.React/pnpm-lock.yaml`
- `publish/package-tester-win.ps1`

### Release tooling

The effective manual path pins:

- Velopack CLI as `dnx vpk@1.2.0`;
- git-cliff `2.13.1` and verifies the downloaded archive against a hard-coded SHA-256; and
- the project version through `Directory.Build.props`.

The dormant GitHub workflows also pin third-party actions to full commit SHAs. Those action pins are design controls only while the workflows remain disabled.

## License inventory

The repository includes an Apache-2.0 `LICENSE`, a `NOTICE`, and a generated third-party package manifest rendered in the application's About dialog.

At the frozen baseline, the generated manifest contains 96 entries:

- 37 direct production frontend dependencies; and
- 59 top-level backend NuGet packages.

No entry is labeled `Unknown` in that committed snapshot.

The generation process has important boundaries:

1. frontend collection intentionally narrows the resolved production tree to direct dependencies declared in `package.json`;
2. backend collection uses `nuget-license`, which reports top-level package references;
3. analyzer, test, fixture, and AppHost packages are excluded from the shipped-package view;
4. if backend license tooling or restore is unavailable, generation warns and reuses the previously committed backend entries; and
5. `licenses:check` regenerates the file and fails on a Git diff, but the generator contains no explicit license allow/deny evaluation.

`NOTICE` separately identifies the user-acquired llama.cpp and stable-diffusion.cpp runtimes, vendored agent templates with their license/provenance files, and user-selected model weights.

This is a useful direct-package disclosure and drift check. It is not:

- a full transitive dependency license inventory;
- a legal conclusion;
- a policy allowlist;
- proof that every downloaded runtime or model was reviewed; or
- a recipient-verifiable license report unless the manifest is separately supplied.

Internal traceability:

- `LICENSE`
- `NOTICE`
- `XE-Local-AI-Engine.Client.React/scripts/GenerateLicenses.mjs`
- `XE-Local-AI-Engine.Client.React/src/features/about/data/third-party-licenses.generated.json`
- `XE-Local-AI-Engine.Client.Application/Services/Agents/Templates/LICENSE-agency-agents`
- `XE-Local-AI-Engine.Client.Application/Services/Agents/Templates/PROVENANCE.md`

## Vulnerability review

The effective manual packager performs two dependency-advisory gates after restore/install:

| Ecosystem | Gate | Failure condition |
|---|---|---|
| Frontend production dependencies | `pnpm audit --prod --audit-level=high` | High or critical advisory reported by pnpm |
| .NET solution | `dotnet package list --vulnerable --include-transitive --format json --no-restore` | Any top-level or transitive package entry contains a reported vulnerability |

The NuGet JSON is parsed by the PowerShell script and a non-empty finding list stops packaging. The script's parsing helpers have Pester coverage in `publish/tests/package-tester-win.Tests.ps1`.

The release scripts also have a separate static-analysis entry point, `scripts/lint-release-scripts.sh`, which checks the packaging scripts and can run the Pester suite with `--pester`. The packaging script does not invoke that checker itself, the Pester leg is opt-in, and no CI service enforces either at the baseline. Their existence is test/design evidence, not evidence that the checks ran for a particular release.

Control boundaries:

- the scan runs when the manual packaging script is executed, not continuously;
- GitHub Actions do not provide a periodic or pull-request safety net at the baseline;
- advisory-database freshness depends on the package tools and network state used for that run;
- no archived audit output or baseline release transcript was supplied;
- the checks are dependency-advisory checks, not source-code SAST, binary malware scanning, runtime/model scanning, or penetration testing; and
- absence of a reported advisory is not a guarantee that a dependency is free of vulnerabilities.

## Effective tester-release gate

`publish/package-tester-win.ps1` is the effective quality and release gate for published Windows tester RCs. It runs manually on a Windows packaging machine.

### Gate sequence

| Stage | Enforced behavior |
|---|---|
| Repository precondition | Must run inside Git; working tree, including untracked files, must be clean |
| Target and version | Tester repository URL must equal the canonical target; project version must be valid SemVer; tag state must agree with `HEAD` |
| Credential/configuration guard | Upload requires the expected GitHub App client-id shape and `VPK_TOKEN`; placeholder values are rejected |
| Frontend | Reject local Vite overrides, materialize committed `.env.template`, frozen install, lint/type check, OpenAPI drift check, license-manifest drift check, coverage-gated tests, production audit, and production build |
| Backend | Restore, transitive vulnerability audit, Release build, serial solution tests, and a “Passed!/Failed!” hollow-gate check so a zero-suite run cannot pass silently |
| Publish | Self-contained Windows publish and explicit SPA-presence check |
| Release notes | Pinned git-cliff download with SHA-256 verification; empty notes are rejected |
| Package | Pinned Velopack CLI, canonical channel, clean output directory, and exactly one prior full package for delta generation |
| Integrity manifest | Records version, source commit, source tag, and SHA-256 for the expected five Velopack assets |
| Upload | Refuses to merge into an already-published release and leaves new upload as a draft |
| Publication | Requires an exact 64-hex Portable hash supplied after smoke testing, verifies the local manifest and every downloaded draft asset, confirms the remote source tag is `HEAD`, and publishes without rebuilding |

The publication ceremony binds a human smoke-test decision to an exact Portable zip hash. The script verifies the hash association; it does not record what was tested, by whom, on which environment, or the observed result. No completed packaging transcript, smoke-test record, or draft/publication evidence was supplied.

### Rehearsal boundary

`-SkipUpload` skips only the final upload and may omit the client id. A no-client-id rehearsal carries `REHEARSAL-DO-NOT-SHIP.txt` inside the package and leaves the updater inert. Build and test gates still run.

### Manual single-lane risk

The effective gate is substantial, but it remains a manually invoked lane. Repository evidence does not demonstrate configured enforcement for:

- who is authorized to package or publish;
- independent approval or segregation of duties;
- credential issuance, rotation, revocation, or access review;
- mandatory retention of build, audit, test, and smoke-test evidence;
- a release rollback decision procedure; or
- formal exception/risk-acceptance handling.

These may exist outside the repository, but no external control or evidence was supplied.

## Dormant GitHub Actions

Three workflow definitions are tracked:

- `.github/workflows/build-and-test.yml`
- `.github/workflows/e2e.yml`
- `.github/workflows/release.yml`

Their YAML expresses intended CI, E2E, and release behavior. It must not be described as an operating gate at this baseline.

Project-maintained external-state evidence, last reviewed 2026-07-24, records:

| Workflow | Recorded external state | Historical result |
|---|---|---|
| `build-and-test.yml` | `disabled_manually` | 3 runs, 3 failures |
| `release.yml` | `disabled_manually` | 3 runs, 3 failures; no release artifact |
| `e2e.yml` | Not registered as a workflow | No run |

That is 6 recorded runs, 6 failures, and 0 successes. The YAML files still declare triggers, but a tracked trigger declaration is not evidence that GitHub accepted, scheduled, or enforced it.

The external GitHub settings/API output supporting this observation is not bundled with the dossier. The claim is therefore internally traceable to the dated repository knowledge record, but recipient verification requires a separately captured GitHub settings/workflow export.

Internal traceability:

- `docs/agent-knowledge.md`
- `.opencode/context/project-intelligence/validation-matrix.md`
- `.github/workflows/build-and-test.yml`
- `.github/workflows/e2e.yml`
- `.github/workflows/release.yml`

## Artifact integrity, authenticity, and provenance

### Implemented integrity and traceability

The manual tester path:

- requires a clean tree;
- binds package version to `Directory.Build.props`;
- binds upload to the expected tag and `HEAD`;
- pins and hashes the downloaded git-cliff archive;
- pins the Velopack CLI version;
- records `SourceCommit` and `SourceTag` in a local asset manifest;
- records SHA-256 for all five Velopack assets;
- uploads as a draft;
- re-downloads and hashes the draft assets before publication; and
- refuses to rebuild between smoke test and publication.

These controls provide useful content-integrity and source-reference checks inside the release ceremony.

### Missing authenticity and signed provenance

At the frozen baseline:

- no code-signing option is passed to the effective Windows packager;
- the dormant release workflow explicitly records signing as deferred;
- the SHA-256 JSON manifest is not signed;
- no in-toto/SLSA-style attestation is generated;
- no trusted builder identity is bound to the package; and
- no recipient-verifiable signature or certificate chain was supplied.

An attacker able to replace both an artifact and its unsigned hash manifest can preserve internal hash consistency. SHA-256 detects accidental or uncoordinated modification; it does not by itself authenticate the publisher.

## SBOM status

No CycloneDX or SPDX SBOM generation was found in the effective release path. No SBOM artifact, SBOM signature, or attestation is produced by `publish/package-tester-win.ps1`.

The generated third-party-license JSON is not an SBOM because it intentionally lists only direct frontend and top-level backend packages and omits the transitive resolved graph, file/package relationships, supplier metadata, and signed provenance.

Current posture: **gap / not produced**. No owner, delivery requirement, retention rule, or formal acceptance of that gap is recorded.

## Governance and residual risk

1. **No repository-evidenced or GitHub Actions branch/merge gate operates at B1.** Quality checks can be deferred until a packaging operator runs the manual script; an external gate not represented in the reviewed evidence cannot be assessed here.
2. **Release execution is workstation- and operator-dependent.** Environment, credentials, and local tool/network state affect the evidence produced.
3. **License disclosure is partial.** It is direct/top-level only, can reuse stale backend entries when tooling fails, and does not enforce an allow/deny policy.
4. **Vulnerability review is point-in-time.** There is no evidenced continuous scan or archived baseline report.
5. **The release is unsigned.** Hashes bind content within the ceremony but do not provide publisher authenticity.
6. **No release SBOM or signed attestation exists.**
7. **Workflow hardening is dormant.** Full-SHA action pins and environment notes do not operate while GitHub workflows are disabled.
8. **Governance ownership is not assigned.** Repository artifacts do not identify release authority, independent approval, evidence custodian, or risk acceptor.

No certification, regulatory mapping, compliance conclusion, or formal risk acceptance is asserted by this dossier.
