# Open-source release readiness and compatibility review

- **Date:** 2026-08-07
- **Source revision:** `444d0c2ec770a18f3f6cb929e13f876be92d33b7`
- **Intended project license:** Apache License 2.0 (`LICENSE`)
- **Scope:** source publication, Git history, dependency and asset licensing, binary redistribution, secrets/privacy, repository hygiene, contributor onboarding, clean build/test/run, CI, packaging, updates, and public-project governance
- **Decision:** **NO-GO for public source publication or public binary release today**
- **Nature of review:** engineering and repository audit, not legal advice; the items marked **legal verification required** need a qualified owner or counsel before distribution

## Executive assessment

The repository has a substantially better engineering baseline than many pre-public projects: `LICENSE` contains the standard complete Apache-2.0 text, a NOTICE file exists, dependencies are centrally pinned, pnpm is locked, NuGet sources are restricted to nuget.org, GitHub Actions are pinned by full commit SHA, generated OpenAPI drift is checked, and the current tree does not contain an obvious live credential. A clean temporary clone restored, built, ran 5,651 backend tests (5,638 passed and 13 skipped), and passed 1,882 frontend tests.

That is not enough to publish safely. The current branch history contains deleted runtime-generated encrypted documents/images and workspace state; those objects remain reachable and appear to have been pushed. The application also cannot presently produce a defensible binary license bundle: its license generator omits shipped transitive/native/browser-runtime components, can silently reuse stale backend data, and the Windows self-contained single-file package includes .NET runtime material governed by terms not represented in the package. A bundled text-to-speech path also presents a strong, unresolved indication that GPL-licensed eSpeak-derived worker/data material is being shipped under an upstream package's Apache-only declaration. Those are release blockers, not documentation polish.

The canonical release path is also not operational as documented. Its Velopack upload is draft-only, parallel platform jobs can race the same release, installer/output claims do not match the CLI flags, prerelease delta discovery is incomplete, and shipped update configuration is intentionally rejected as unconfigured. The source repository was still private on the audit date, had no registered release workflow run and no source-repository release, so there is no end-to-end evidence that the described public release path has ever worked.

### Priority summary

| Priority | Count | Meaning |
|---|---:|---|
| **Release blockers** | 7 | Each blocks source publication, binary release, or both; the `Applies to` field identifies the affected gate. |
| **Strongly recommended** | 20 | Should be completed before the first public release candidate; deferral creates material contributor, security, maintenance, or release risk. |
| **Optional improvements** | 10 | Valuable hardening and community improvements that do not independently block publication. |

### Evidence labels

- **Observed:** directly verified in repository contents, reachable history, command output, or current repository state.
- **Inference:** a likely consequence supported by observed facts, but not independently proven at runtime or by the third-party copyright owner.
- **Unknown:** evidence needed from an owner, upstream archive, repository setting, or legal review was unavailable.

## Release blockers

### B1. Purge deleted runtime/user artifacts from reachable history before visibility changes

**Category:** security, privacy, repository hygiene

**Applies to:** source publication

**Confidence:** high (objects and commits observed); sensitivity of encrypted payloads is unknown

Reachable branch history contains runtime-generated material that was later deleted but was not purged: AgentHome workspace state, one encrypted knowledge-base document, and six encrypted generated images. Exact paths, object IDs, and commit IDs are intentionally omitted from this public-intended report because they would make retrieval/correlation easier. Create and retain that manifest only in a restricted remediation record before rewriting history.

The encrypted payloads have near-random entropy. No corresponding `node.key` path or common live credential signature was found, but encryption does not make accidental publication acceptable: payload ownership, plaintext sensitivity, and key exposure are unknown. The local remote-tracking public feature ref resolved to the audited HEAD, providing evidence that this ancestry was intended for or already reached a remote.

**Required before public visibility:** inventory all affected refs (including remote branches/tags), obtain the data owner's decision, rewrite history to remove the objects, force-update the approved refs, expire cached release artifacts/forks where applicable, and verify from a fresh remote clone that the blobs are unreachable. Rotate the originating encryption/operator secret if there is any possibility it or decrypted content escaped. Do not publish first and attempt cleanup later; GitHub secret scanning examines full history, and public clones/forks are not retractable in practice.

### B2. Resolve the likely GPL eSpeak payload in the browser TTS bundle

**Category:** licensing and redistribution

**Applies to:** official browser/binary distributions; source dependency policy also needs correction

**Confidence:** medium-high inference; exact byte provenance remains unknown

**Legal verification required**

`XE-Local-AI-Engine.Client.React/src/core/runtime/TtsWorker.ts:1-8` statically imports `kokoro-js`. `XE-Local-AI-Engine.Client.React/package.json` pins `kokoro-js@1.2.1`; the locked graph brings `phonemizer@1.2.1` (`XE-Local-AI-Engine.Client.React/pnpm-lock.yaml:7808-7813`). The phonemizer package declares Apache-2.0 but describes itself as using eSpeak and publishes large `espeakng.worker.js` / `espeakng.worker.data` artifacts. The official eSpeak NG project is GPL-3.0-or-later. The ASF's own distribution policy treats GPLv3 material as unsuitable for inclusion in ASF Apache-licensed products; that policy is informative evidence of the compatibility risk, not a rule that automatically governs every non-ASF Apache-licensed repository.

The audit did not establish byte-for-byte identity between the packed phonemizer files and a specific official eSpeak Emscripten build, so this is not presented as a final legal conclusion. It is nevertheless a strong enough provenance conflict to block distribution. The feature being disabled by default does not remove the issue: the clean production build emitted a 2.226 MB `TtsWorker` chunk, demonstrating that the imported path is bundled.

**Required:** remove/replace the phonemizer/eSpeak path with material whose redistribution terms are compatible with the intended product, or obtain authoritative provenance, copyright permission, source-delivery compliance, notices, and a counsel-approved distribution model. Do not “fix” this only by adding a GPL label to the existing Apache-only manifest.

Relevant upstream references: [eSpeak NG license](https://github.com/espeak-ng/espeak-ng), [phonemizer.js metadata](https://github.com/xenova/phonemizer.js), and [ASF GPL compatibility guidance](https://www.apache.org/licenses/GPL-compatibility).

### B3. Choose and implement a compliant .NET runtime distribution model

**Category:** licensing and packaging

**Applies to:** Windows and Linux public binaries

**Confidence:** high

**Legal verification required**

The publish profiles build self-contained, single-file applications (`XE-Local-AI-Engine.Client/Properties/PublishProfiles/win-x64.pubxml:3-18,28-31` and the corresponding Linux profile), and `.github/workflows/release.yml:239-288` uses those outputs. `XE-Local-AI-Engine.Client/XE-Local-AI-Engine.Client.csproj:120-131` copies only this project's Apache `LICENSE` and `NOTICE`.

Microsoft's current Windows licensing statement says that `coreclr.dll` and .NET runtimes embedded in single-file Windows binaries are governed by the **.NET Library License**, while other .NET files are MIT. Microsoft's asset guidance separately requires every .NET binary distribution to carry its applicable license and third-party notice. The repository's packaging logic explicitly copies only this project's Apache files and contains no step that assembles or displays the applicable .NET license/third-party-notice corpus. Exact final archives were not produced or inspected, so their actual contents remain unknown. Linux self-contained output still requires the .NET MIT license and third-party notices.

**Required:** either switch official releases to a documented framework-dependent model, or implement a counsel-approved self-contained distribution that includes and displays the correct per-RID .NET license and third-party notices. Validate the exact produced archive/installer contents and applicable .NET Library License terms rather than inferring compliance from project references.

Authoritative sources: [.NET Windows license information](https://github.com/dotnet/core/blob/main/license-information-windows.md), [.NET Library License](https://dotnet.microsoft.com/en-us/dotnet_library_license.htm), and [.NET asset licensing model](https://github.com/dotnet/runtime/blob/main/docs/project/licensing-assets.md).

### B4. Replace the fail-open, direct-dependency license manifest with artifact-derived compliance output

**Category:** dependency attribution, legal reproducibility

**Applies to:** source and binary releases

**Confidence:** high

`NOTICE:10-18` describes the generated third-party list as the authoritative inventory of compiled/shipped packages. It is not.

- Frontend collection traverses direct declared packages rather than the shipped transitive graph (`XE-Local-AI-Engine.Client.React/scripts/GenerateLicenses.mjs:68-128`).
- Backend generation invokes `nuget-license` without `--include-transitive`, `--allowed-license-types`, or `--exclude-publish-false` (`XE-Local-AI-Engine.Client.React/scripts/GenerateLicenses.mjs:170-180`).
- On backend-tool failure it reuses committed backend entries (`XE-Local-AI-Engine.Client.React/scripts/GenerateLicenses.mjs:215-270`). In a fresh clone with a normal fresh pnpm store, `pnpm run licenses:check` printed that `nuget-license` was unavailable, reused 61 backend entries, wrote the same 97-package file, and exited successfully. The gate therefore certifies stale data.
- The committed list contains `Microsoft.EntityFrameworkCore.Design` and `.Tools` even though both are `PrivateAssets=all` (`XE-Local-AI-Engine.Client.Persistence/XE-Local-AI-Engine.Client.Persistence.csproj:12-20`), while omitting transitive shipped/runtime material including `OpenAI`, `SQLitePCLRaw.lib.e_sqlite3`, phonemizer/Transformers, ONNX Runtime Web, and libsodium.
- `XE-Local-AI-Engine.Client.React/vite.config.ts:16-36,112-116` explicitly copies ONNX Runtime Web WASM into the distributable, but its MIT license and extensive ThirdPartyNotices are not carried in the current output.
- The JSON records normalized license labels/URLs, not the exact license texts, required copyright statements, or upstream NOTICE files.

Apache-2.0 section 4 requires retaining applicable copyright, patent, trademark, attribution, and upstream NOTICE material when its conditions apply; each bundled component's own license may add further notice/source obligations. ASF third-party-work guidance illustrates a conservative distribution-specific inventory practice but does not govern this non-ASF project.

**Required:** generate SBOM and license/notice output from each actual publish artifact/RID (including copied WASM, native assets, self-contained runtime, and package transitives); fail closed if collection cannot run; preserve exact license/NOTICE texts; distinguish build-only/private dependencies from shipped files; and test the generated bundle contents in CI. Remove the claim of authority until the generator proves it.

### B5. Repair and dry-run the canonical release workflow before publishing release claims

**Category:** release engineering, reproducibility, user trust

**Applies to:** public binary release

**Confidence:** high

The tag workflow conflicts with Velopack's current CLI contract and with this repository's documentation:

1. `.github/workflows/release.yml:290-302` invokes `vpk upload github` without `--publish`; Velopack uploads are drafts by default, so they are not the published releases described by `README.md:194-199` and `docs/velopack-release-install-guide.md:230-253`.
2. Windows and Linux matrix legs independently upload the same version/release (`.github/workflows/release.yml:159-179,290-302`) without `--merge`, creating a race or second-leg failure.
3. `vpk pack` omits `--noInst` (`.github/workflows/release.yml:274-288`) while documentation claims installer-less/portable output (`docs/velopack-release-install-guide.md:116-122`). Windows normally produces setup output and Linux's pack flow produces AppImage-oriented output, contrary to the documented archive-only contract.
4. Previous-release download omits `--pre` (`.github/workflows/release.yml:266-272`) although the current version line is prerelease/RC, so delta discovery is not established.
5. `workflow_dispatch` permits an arbitrary selected ref (`.github/workflows/release.yml:27-31`), but tag/version equality is checked only for `refs/tags/*` (`.github/workflows/release.yml:101-114`). Upload also omits an explicit tag/target commit. A branch-dispatched release can therefore be unbound from the advertised source tag.
6. The workflow does not enforce all release evidence required by `README.md:218-242`: release-script lint/Pester, E2E/GPU smoke evidence, schema/OpenAPI checks, checksums, exact-tag proof, Windows smoke, expected assets, and publication. In addition, it produces no SBOM, provenance/attestation, or retained cross-platform smoke record.

Velopack's current GitHub Actions example uses `--publish` and an explicit `--tag` in the upload command. See [Velopack's official workflow](https://docs.velopack.io/distributing/github-actions).

**Required:** redesign the workflow so one coordinated job owns a deterministic release, bind artifacts to an immutable tag/commit, merge all platform assets, accurately select the intended artifact formats, publish intentionally, generate checksums/SBOM/attestations, and run a release-candidate dry run in a disposable public-equivalent repository. Update documentation only after retained run evidence proves the path.

### B6. Make the shipped update channel real or remove the self-update promise

**Category:** product/release compatibility

**Applies to:** Windows public binary release

**Confidence:** high

Both `XE-Local-AI-Engine.Client/appsettings.AppUpdate.main.json:1-6` and `XE-Local-AI-Engine.Client/appsettings.AppUpdate.tester.json:1-6` leave `GitHubAppClientId` empty. `XE-Local-AI-Engine.Client.Application/Services/AppUpdate/AppUpdateChannelOptions.cs:25-36,78-83` defines a configured channel as a valid GitHub URL **and** a valid `Iv...` client ID, and updater paths gate on that predicate. The canonical release workflow selects `-p:UpdateChannel=main` (`.github/workflows/release.yml:239-247`) but never injects a client ID. The produced artifact is therefore intentionally `notConfigured`/inert while `docs/velopack-release-install-guide.md:177-204`, `docs/user-guide/docs/updating.md:19-69`, and `docs/user-guide/README.md:201-206` promise Windows self-update.

**Required:** either provision and safely inject the supported GitHub App client ID and verify an actual update from N to N+1, redesign the update/auth source if unauthenticated public releases or another OAuth mechanism are intended, or remove/disable the feature and correct all user-facing claims for the first release.

### B7. Confirm authority to apply Apache-2.0 to all project and predecessor material

**Category:** ownership and source licensing

**Applies to:** source publication and binary release

**Confidence:** ownership authority unknown; private/predecessor provenance references observed

**Legal-owner verification required**

The complete Apache-2.0 text in `LICENSE` grants rights only to the extent the named licensor/contributors have authority over the work. The repository repeatedly identifies a “C0re”/central-platform compatibility contract (`README.md:3-6`), private predecessor/release infrastructure (`docs/velopack-release-install-guide.md:128-139`; `publish/README.md:8-21,128-132`), copied/adapted schemas, golden vectors, branded media, and historical material. Those references do not prove infringement, but the repository contains no consolidated ownership/provenance sign-off establishing that all project-authored, copied, adapted, employee/contractor, and predecessor material can be publicly licensed by the current owner.

**Required before source publication:** have the copyright owner inventory authorship and predecessor transfers, confirm contributor/contractor authority, resolve any third-party or private-company rights, and record approval to license the covered source/docs/tests/assets under Apache-2.0. Keep third-party components under their actual licenses rather than treating the root license as relicensing them.

## Licensing and redistribution concerns requiring verification

### L1. Select and comply with UTF.Unknown's applicable license

`UTF.Unknown@2.6.0` is published under a multi-license expression involving MPL-1.1/GPL/LGPL. If MPL-1.1 is the selected basis, preserve the exact license and notices and evaluate executable/source-availability obligations for modifications. The current normalized manifest entry is insufficient. **Strongly recommended; legal verification required.**

### L2. Inspect exact CUDA companion archives and preserve their terms

The llama.cpp and stable-diffusion.cpp runtime managers download pinned Windows CUDA 12.4 companion archives (`XE-Local-AI-Engine.Providers.LlamaServer/LlamaCppReleasePins.cs:73-79`; `XE-Local-AI-Engine.Providers.StableDiffusionCpp/StableDiffusionReleasePins.cs:67-72`), flatten-copy DLLs, and delete the archive/staging tree (`XE-Local-AI-Engine.Providers.LlamaServer/Implementation/LlamaCppBinaryManager.cs:557-631`; `XE-Local-AI-Engine.Providers.StableDiffusionCpp/Implementation/StableDiffusionCppBinaryManager.cs:326-382`). That DLL-only copy path would not preserve archive license/notice files if present; exact archives were not inspected.

NVIDIA's [CUDA 12.4 EULA](https://docs.nvidia.com/cuda/archive/12.4.1/eula/index.html#distribution-requirements) limits redistribution to listed Attachment A components incorporated into a materially functional application and requires consistent downstream terms. It is unknown whether this project is legally acting as distributor when its runtime manager retrieves a third-party GitHub archive, and the exact archive contents were not audited. **Before release:** inspect hashes and contents of every pinned archive, establish provenance/redistribution authority, preserve license notices, and define any acceptance/downstream terms. **Strongly recommended; legal verification required.**

### L3. Confirm ownership/provenance for copied, adapted, and project-branded material

- Vendored agent templates have comparatively strong source and license annotations and appear ready.
- A Quartz-derived scheduler schema is identified in `XE-Local-AI-Engine.Client.Persistence/Migrations/20260601195214_AddSchedulerTables.cs:117-123`; retain precise upstream provenance and any required notice. The audit could not verify the exact upstream version/file, so any additional obligation is unknown.
- Project logos, screenshots, videos, performance captures, and user-guide media lack a single ownership/provenance register. Existing metadata did not expose personal fields, but that does not establish copyright/subject consent.

After the core authority decision in B7, create `THIRD_PARTY_NOTICES`, a per-file/asset source register, and a release-specific provenance checklist. **Strongly recommended; legal-owner verification required.**

### L4. Disclose model/runtime downloads and their independent terms

The root NOTICE says models are user-selected, but first run specifies a fixed Qwen model in `XE-Local-AI-Engine.Client/appsettings.json:34-42`, and voice uses a fixed Kokoro path. Weights are not committed, which is positive, but first-run/runtime download UI and docs should identify exact upstreams, hashes/revisions where possible, licenses/acceptable-use terms, disk/network impact, and whether acceptance is required. **Strongly recommended.**

## Security, privacy, and repository hygiene

### S1. Decide whether personal commit metadata may be public

Reachable history contains roughly 1,490 commits using a personal mailbox domain in author/committer metadata. A `.mailmap` changes display/attribution but does not purge raw objects. Obtain owner consent or rewrite to an approved public/noreply identity before publication. **Strongly recommended; privacy-owner decision required.**

### S2. Remove private operational disclosures and obsolete infrastructure references

Current tracked text says some material is internal/not open sourced (`.gitignore:454-458`) and documents absent private validator/evaluation layouts ([The `.opencode/` agent eval foundation](../agent-knowledge.md#the-opencode-agent-eval-foundation); `docs/wiki/13-testing-and-validation.md:108`; `docs/wiki/16-code-conventions.md:208-210`). Retired private tester repository names, URLs, collaborator/download flows, and obsolete source URLs remain in `docs/velopack-release-install-guide.md:128-139`, `publish/README.md:8-21,37,68-69,128-132`, and `publish/package-tester-win.ps1:456-457`.

A deleted historical `opencode.jsonc` remains reachable. Exact object/commit locators belong in the restricted remediation manifest, not this public-intended report. No common secret signature was found in it, but private workflow disclosure and stale maintainer instructions should be reviewed alongside B1's history rewrite. **Strongly recommended.**

### S3. Add deny-by-default ignore patterns for common local secrets and crash artifacts

`git check-ignore --no-index` showed that `.env`, `.env.production`, `appsettings.Development.json`, `appsettings.Production.json`, `secrets.json`, `*.pem`, `*.key`, `*.dmp`, `*.dump`, and `core` are not ignored at repository root. None was tracked at HEAD. Add broad protections with narrow allowlists for committed templates/test fixtures. Pair this with pre-commit and CI secret scanning. **Strongly recommended.**

### S4. Verify every host-process execution path is gated and documented

`XE-Local-AI-Engine.Client/appsettings.json:54-75` enables AgentHome/development and chooses the `process` execution provider. `README.md:30-38` and the development UI correctly warn that commands run as the host user with filesystem/network access and no OS sandbox boundary. An acknowledgement gate exists in the UI, but this audit did not exhaustively prove that every API, scheduler, restored-agent, migration, and automation path enforces equivalent authorization and consent. Add authorization/negative-path tests and threat-model documentation before inviting untrusted third-party agents/templates. **Strongly recommended.**

### S5. Configure public-repository security settings at cutover

The repository file set cannot prove GitHub settings. As part of the visibility cutover, enable and verify secret scanning/push protection (including non-provider/custom patterns where appropriate), Dependabot alerts/updates, CodeQL/code scanning, branch/ruleset protections, signed/tag protection policy, least-privilege Actions permissions, and private vulnerability reporting. `SECURITY.md:7-12` currently offers only GitHub's “Report a vulnerability” path; that button exists only after the owner enables the public-repository setting. Add a private fallback contact before cutover if the setting cannot be guaranteed immediately. GitHub documents [full-history secret scanning](https://docs.github.com/en/code-security/concepts/secret-security/secret-scanning) and [private vulnerability reporting](https://docs.github.com/en/code-security/how-tos/report-and-fix-vulnerabilities/configure-vulnerability-reporting/configure-for-a-repository). **Strongly recommended.**

### S6. Positive security/hygiene evidence

- No tracked certificate, private key, database, dump, archive, executable, native library, symlink, submodule, or runtime-state path was found at HEAD.
- Common secret-format hits in current/reachable history resolved to scanner/test fixtures; an additional high-entropy credential-assignment scan found no candidates.
- No private NuGet feed or non-example internal domain was found; `nuget.config:3-12` clears sources and permits nuget.org only.
- `AllowedHosts` is loopback-only (`XE-Local-AI-Engine.Client/appsettings.json:110`).
- No reachable blob was at least 10 MiB; the Git object pack was about 30.5 MiB. The largest current tracked artifact was a 3.7 MiB performance JSON.
- Six privacy-sensitive screenshots and PNG metadata across 28 PNGs were inspected without finding visible secrets or personal metadata. Remaining video frames were not exhaustively reviewed.

These checks reduce risk but do not replace a final server-side secret scan after history remediation. `gitleaks`/`trufflehog` were not installed, and remote-only or unreachable objects were outside the local scan.

## Documentation and contributor experience

### D1. Make contributor commands match actual CI

`CONTRIBUTING.md:16-43` and `.github/PULL_REQUEST_TEMPLATE.md:7-20` describe plain `pnpm install`, lint, plain tests, and build as the real gates, with OpenAPI checking conditional. `.github/workflows/build-and-test.yml:75-111` instead uses a frozen install, unconditional OpenAPI and license drift checks, coverage-gated tests, build, and a high-severity production audit. Publish one authoritative bootstrap/verification script or exact command list and make README, CONTRIBUTING, PR template, and CI agree. **Strongly recommended.**

The documented React build path also does not materialize `.env.template`, while `XE-Local-AI-Engine.Client.React/index.html:11` interpolates `%VITE_APP_TITLE%`. The clean build warned that the variable was undefined; release CI separately creates `.env` (`.github/workflows/release.yml:211-218`). Align local/CI/release environment creation or provide a safe default, and assert the final HTML title so a clean contributor build cannot ship an unresolved placeholder. **Strongly recommended.**

### D2. Document and pin all bootstrap prerequisites

The README lists .NET, Node, pnpm, optional GPU support, and optional Docker (`README.md:79-85`) but its advertised Aspire path calls wrappers that require Aspire CLI and Python (`scripts/dev-aspire-common.sh:91-99`) and `setsid` (`scripts/dev-start.sh:25-26`). `XE-Local-AI-Engine.Client.React/package.json:7` declares `>=20.19 || >=22.12`, whose first arm unintentionally admits Node 21/23; locked Vite 8 requires `^20.19.0 || >=22.12.0` (`XE-Local-AI-Engine.Client.React/pnpm-lock.yaml:4605-4610`). There is no `.nvmrc`, `.node-version`, mise file, devcontainer, or equivalent contributor environment.

Correct the Node range, document Aspire/Python/setsid/platform requirements, add a machine-readable environment pin/bootstrap check, and explain Windows-native versus WSL expectations. **Strongly recommended.**

### D3. Fix fresh-profile Aspire startup and document certificate trust

Two isolated `scripts/aspire-readiness-smoke.sh` runs from a fresh temporary HOME failed before the AppHost built. The first exhausted Aspire's 120-second start timeout during first-use bundle/template discovery. A warmed retry with `ASPIRE_CLI_START_TIMEOUT=240` still timed out; the detached log reached “Trusting certificates,” emitted the Linux `SSL_CERT_DIR` guidance, then made no AppHost progress before termination. Cleanup confirmed no running instance.

This does not prove the application itself cannot run on a configured developer machine; it proves the documented clone-to-Aspire path is not validated from a fresh profile. Add an explicit non-interactive prerequisite/certificate bootstrap, surface the relevant timeout, produce actionable failure output, and retain a clean-profile CI/smoke lane. **Strongly recommended.**

### D4. Provide a complete public-project documentation set

Present: README, LICENSE, NOTICE, CONTRIBUTING, SECURITY, CODEOWNERS, Dependabot configuration, issue templates, PR template, wiki/user guide, platform limitations, and generated-code instructions.

Missing or insufficient:

- `CODE_OF_CONDUCT.md`
- `SUPPORT.md` with support boundaries and discussions/issues routing
- maintainer/governance and release-authority documentation
- public roadmap/status or an explicit statement that none is promised
- architecture explanation for “C0re”/central-platform compatibility versus standalone operation
- accurate first-run configuration/example files and environment-variable reference
- verified public release/checksum/signature instructions

GitHub's [community profile guidance](https://docs.github.com/en/communities/setting-up-your-project-for-healthy-contributions/about-community-profiles-for-public-repositories) explicitly treats README, Code of Conduct, LICENSE, CONTRIBUTING, issue templates, and security policy as recommended public-project surfaces. **Strongly recommended.**

### D5. Remove release/version documentation drift

`Directory.Build.props:4-7` identifies `0.1.0-rc.5.1`, but `CHANGELOG.md:35-40,52-60` still names 5.0 as current and says there is no unreleased work, contrary to its own maintenance rule (`CHANGELOG.md:17-19`) and `publish/README.md:82-89`. This is observed release drift rather than merely unreleased source: the source remote has tag `v0.1.0-rc.5.1`, and the private tester repository reported a published 5.1 prerelease dated 2026-08-05, while the `.Source` repository still had no release. `scripts/lint-release-scripts.sh:4-8` also claims GitHub Actions are disabled and the manual packager is the only path; `publish/README.md:31-37,67` points to an obsolete repository and calls `release.yml` disabled. Reconcile or delete deprecated guidance before external maintainers rely on it. **Strongly recommended.**

### D6. Pin repository-invoked development tools and fix portable browser discovery

`.mcp.json:8-12` invokes `npx -y chrome-devtools-mcp@latest`. A tracked repository configuration that downloads and executes a moving package on a contributor machine is a supply-chain risk even when the tool is optional; pin an exact reviewed version/integrity or remove automatic execution. **Strongly recommended.**

`XE-Local-AI-Engine.AppHost/AppHost.cs:86-87` and `.mcp.json:8-12` also hard-code `/usr/bin/google-chrome`. Make browser discovery/configuration platform-aware. **Optional improvement** unless Chrome tooling is a supported prerequisite.

## Build, test, packaging, and reproducibility

### Clean-clone validation results

A local temporary clone was exercised with a new HOME and NuGet package directory rather than the already configured worktree. It was not a fully hermetic container or a second operating system.

| Check | Result | Evidence/limitation |
|---|---|---|
| `dotnet restore XE-Local-AI-Engine.slnx` | **Pass** | Restored all solution projects using nuget.org. |
| Release build with `--no-restore` | **Pass** | 0 warnings, 0 errors. |
| Guarded Release backend test run | **Pass** | 5,651 total; 5,638 passed; 13 skipped; 0 failed; assemblies unchanged. Skips were platform/live-runtime/hardware gated. |
| `pnpm install --frozen-lockfile` with pinned pnpm | **Pass** | 901 packages; lock policy passed. |
| `pnpm run lint` | **Pass** | 798 files. |
| `pnpm test` | **Pass** | 224 files; 1,882 tests. |
| `pnpm run build` | **Pass with warnings** | Undefined `%VITE_APP_TITLE%` in a documented clean clone because no env template was copied; large chunks; TTS worker 2.226 MB; ORT WASM asset 21.596 MB. Bundle-budget gate passed. |
| `pnpm openapi:check` | **Pass** | Generated client/snapshot did not drift. |
| NuGet transitive vulnerability audit | **Pass (CLI JSON)** | Command exited 0 and returned no vulnerable package rows; the production PowerShell parser was reviewed but not executed. |
| `pnpm audit --prod --audit-level=high` | **Pass threshold, findings present** | Two moderate advisories: `protobufjs@7.6.4` (patched at 7.6.5) and `tar@7.5.19` (patched at 7.5.21) through the Kokoro/Transformers/ONNX graph. |
| `pnpm run licenses:check` | **False-green** | Backend tool unavailable; reused stale entries and exited successfully. See B4. |
| Aspire clean-profile readiness smoke | **Fail** | 120s and 240s startup timeouts before AppHost startup; see D3. |

### Validation reproduction and evidence retention

The primary clone was `/tmp/xe-open-source-readiness-clean.pl0AoF/repo` at the audited revision, with `CLEAN=/tmp/xe-open-source-readiness-clean.pl0AoF`, `CLEAN_HOME=$CLEAN/home`, and `CLEAN_NUGET=$CLEAN/nuget`. The host was Linux in the Europe/Berlin timezone, using .NET SDK 10.0.302, Aspire CLI 13.4.6, and pnpm 11.2.2 selected by the repository's `packageManager` field. Commands were run sequentially; build and test were not concurrent.

The command forms used were:

```sh
HOME="$CLEAN_HOME" NUGET_PACKAGES="$CLEAN_NUGET" \
  dotnet restore XE-Local-AI-Engine.slnx
HOME="$CLEAN_HOME" NUGET_PACKAGES="$CLEAN_NUGET" \
  scripts/with-build-lock.sh -- \
  dotnet build XE-Local-AI-Engine.slnx --configuration Release --no-restore
HOME="$CLEAN_HOME" NUGET_PACKAGES="$CLEAN_NUGET" \
  dotnet package list --project XE-Local-AI-Engine.slnx --vulnerable \
    --include-transitive --format json --no-restore > "$CLEAN/nuget-vulnerable.json"
HOME="$CLEAN_HOME" NUGET_PACKAGES="$CLEAN_NUGET" \
  scripts/with-build-lock.sh -- scripts/assembly-guard.sh guard --test-bins -- \
  dotnet test XE-Local-AI-Engine.slnx --configuration Release --no-build \
    --max-parallel-test-modules 1

cd XE-Local-AI-Engine.Client.React
HOME="$CLEAN_HOME" pnpm install --frozen-lockfile
HOME="$CLEAN_HOME" pnpm run lint
HOME="$CLEAN_HOME" pnpm test
HOME="$CLEAN_HOME" pnpm run build
HOME="$CLEAN_HOME" pnpm openapi:check
HOME="$CLEAN_HOME" pnpm audit --prod --audit-level=high

cd ..
HOME="$CLEAN_HOME" scripts/aspire-readiness-smoke.sh
HOME="$CLEAN_HOME" ASPIRE_CLI_START_TIMEOUT=240 \
  XE_ASPIRE_SMOKE_TIMEOUT_SECONDS=240 scripts/aspire-readiness-smoke.sh
```

The normal-store license reproduction used a second clone at `/tmp/xe-oss-license-check.Hg9JmN/repo` and:

```sh
cd XE-Local-AI-Engine.Client.React
HOME="$SECOND_CLEAN_HOME" pnpm install --frozen-lockfile
HOME="$SECOND_CLEAN_HOME" pnpm run licenses:check
```

All core/frontend commands above exited 0. The NuGet JSON contained no top-level or transitive package with a vulnerability row. The audit used the same CLI invocation as `publish/package-tester-win.ps1:630-635`, but its JSON was inspected directly; the production PowerShell `ConvertFrom-Json`/enumeration path at `publish/package-tester-win.ps1:637-663` was reviewed rather than executed in this clean-clone lane. `licenses:check` also exited 0 despite its backend warning, which is the defect. Both Aspire smoke invocations exited 1 and their scoped CLI logs showed 120-second and 240-second start timeouts. Raw command transcripts, audit JSON, and temporary HOME/package stores were not committed as repository artifacts; only the counts, warning/error excerpts, command/environment record, and audit conclusion are retained here. This limits independent replay of the exact output and is why a future release process should retain machine-readable test, SBOM, scan, and smoke artifacts.

Chrome UI validation was not pursued after Aspire failed to start, and the task did not modify UI behavior. E2E and live GPU/tool-grammar smokes were not run because they are explicitly opt-in/ask-gated release lanes and require suitable local runtime/model/GPU infrastructure. Before a public release candidate, run them and retain their summaries as release evidence.

### R1. Lock or record transitive NuGet resolution

Direct NuGet versions are centrally pinned, but no `packages.lock.json`/locked restore contract was found. A future clean restore can therefore select different transitives under compatible ranges. Add per-solution/project NuGet lock files with locked-mode CI, or generate and attest an equivalent resolved dependency snapshot/SBOM for every release. **Strongly recommended.**

### R2. Tighten SDK/toolchain reproducibility

`global.json:1-9` sets a .NET 10 baseline with `latestFeature` roll-forward; the audit machine used 10.0.302, not exactly 10.0.100. This is reasonable for patch adoption but is not bit-reproducible. State the policy explicitly, pin exact release builders, and record SDK/tool versions in provenance. Keep pnpm's exact `packageManager` pin and frozen CI install. **Optional improvement.**

### R3. Add release SBOM, checksums, provenance, and artifact-content tests

No canonical release step produces a per-RID SBOM, cryptographic checksum file, signature, or artifact attestation, and no test opens the final archives to verify licenses/notices/native assets/configuration. Add CycloneDX or SPDX SBOMs, SHA-256 checksums, GitHub artifact attestations/SLSA-compatible provenance, and deterministic archive-content assertions. **Strongly recommended.**

### R4. Address moderate production dependency advisories

Update or constrain the transitive `protobufjs` and `tar` versions after compatibility testing. Determine whether ONNX Runtime Node/tar is present in the actual browser distribution or merely installed through package metadata, and teach the artifact-derived inventory to make that distinction. **Strongly recommended for the first RC; not a current high-severity blocker.**

### R5. Separate public development workflows from private/release infrastructure

Public CI should build/test entirely from public sources with least privileges and no private credentials. Publication, signing, GitHub App configuration, tester distribution, and hardware qualification should be separate protected environments with explicit approvals and artifact promotion rather than rebuilds. Remove deprecated scripts or place them under clearly marked archival documentation so public contributors do not mistake private/manual flows for supported paths. **Strongly recommended.**

## Repository cleanup and optional improvements

1. **Optional:** move large performance JSON/media evidence to release artifacts or a dedicated evidence repository if clone growth becomes material; current size is acceptable.
2. **Optional:** add `.editorconfig`/format-on-contribution documentation for non-C# assets if current tooling does not cover them uniformly.
3. **Optional:** publish a compatibility matrix for Windows/Linux distributions, GPU backends, driver ranges, CPU fallback, unsupported macOS/ARM, and tested Node/.NET/Aspire versions.
4. **Optional:** add a devcontainer or reproducible bootstrap script after the native Windows/WSL policy is settled.
5. **Optional:** add a public extension-authoring guide for agents/tools/providers and clearly label internal compatibility seams that are not supported APIs.
6. **Optional:** create `MAINTAINERS.md`/`GOVERNANCE.md`, a triage policy, and release-support window even if the initial project is single-maintainer.
7. **Optional:** define DCO or CLA policy. Apache-2.0 section 5 supplies a default inbound=outbound contribution rule, but the project should state whether Signed-off-by is required.
8. **Optional:** add a public `CHANGELOG` automation/check that rejects a version release when the matching section is absent.

## Items checked and considered ready

- Root `LICENSE` contains the complete standard Apache License 2.0 text, an OSI-approved license. Authority to apply it to all covered work remains unresolved under B7.
- Root `NOTICE` exists and is copied into packages; its inventory content must be repaired under B4.
- README, CONTRIBUTING, SECURITY, CODEOWNERS, issue/PR templates, Dependabot, extensive wiki/user documentation, and honest unsigned/platform limitation notes are present.
- NuGet source configuration is public-only; no external/private `ProjectReference`, submodule, or private package feed was found.
- Central package pins, exact pnpm version, frozen installs, pnpm supply-chain policy, committed OpenAPI snapshot/client, and full-SHA GitHub Action pins are strong controls.
- Vendored agent templates have identifiable upstream provenance/license annotations.
- Downloaded native/runtime artifacts use explicit pins and hashes; notice/redistribution handling still needs work.
- Current tree secret/media/size scans found no immediate live credential or oversized binary blocker.
- Clean-clone core restore, Release build, backend tests, frontend lint/tests/build, and OpenAPI drift checks passed.
- Loopback host configuration and visible warnings about unsandboxed process execution are positive defaults/disclosures.

## Public-release action plan

### Gate 1 — source visibility

1. Freeze the candidate refs and inventory all remote branches/tags/releases.
2. Complete B7's licensing-authority sign-off and decide privacy/ownership treatment for runtime artifacts, personal email metadata, predecessor/private material, and branded assets.
3. Rewrite/purge B1/S2 history; rotate any uncertain secrets; verify from a fresh remote clone and server-side secret scan.
4. Remove B4's false authoritative-inventory claim/fail-open source check or clearly scope it until artifact-derived binary compliance is implemented.
5. Remove private/internal operational documentation or explicitly publish it by owner decision.
6. Prepare the security, branch/ruleset, vulnerability-reporting, and community settings that must be enabled/verified at cutover.

After Gate 1, the repository may be made public as a **source-only prerelease** while B2/B3/B5/B6 and the binary-specific part of B4 remain unresolved, provided no official binaries are published and the README/release pages state that limitation clearly.

### Gate 2 — binary release candidate

1. Resolve B3/L1/L2 and select the legal distribution model per platform.
2. Replace B4 with artifact-derived SBOM/license/NOTICE output and archive-content tests.
3. Repair B5/B6; bind release to tag/commit; add checksums/attestations and a single coordinated publication job.
4. Fix clean-profile Aspire/bootstrap documentation and make contributor commands match CI.
5. Run Release build/test, frontend gates, release-script lint/Pester, E2E, live GPU smoke, tool-grammar smoke, Windows/Linux install/start/update/uninstall smoke, and a vulnerability scan against exact final artifacts.
6. Publish to a disposable public-equivalent repository first; verify download, update, deltas, notices, checksums, SBOM, provenance, and source-tag correspondence.

### Gate 3 — public binary launch

1. Update CHANGELOG/version/release notes and supported-platform matrix.
2. Add Code of Conduct, support/governance/maintainer guidance, and a reliable private security contact.
3. Publish official assets only after Gate 2 is signed off; then re-run community-profile, secret-scanning, link, clone/bootstrap, artifact-license, and release-download checks as an unauthenticated user.
4. Monitor first-public-day issues, dependency/security alerts, workflow permissions, and release telemetry without collecting undocumented user data.

## Current external/repository state observed on 2026-08-07

- GitHub reported the source repository as **private** with default branch `develop`.
- Only `build-and-test` was registered; the queried API returned no workflow runs and no source-repository release.
- The reviewed release workflow lived on the audited feature/ref rather than the remote default branch.
- GitHub's community-profile API reported 14% completeness in the current private state.
- Existing user links to `.Source/releases/latest` therefore did not resolve to a source-repository release for an external user.

These are point-in-time operational observations, not permanent repository facts. Recheck immediately before cutover.

## Important limitations

- This is not legal advice and did not obtain licenses/permissions from copyright owners.
- Exact final Windows/Linux release archives were not produced or inspected because the release workflow is unproven and the task was an audit, not a publication.
- The phonemizer/eSpeak conclusion is a strong provenance inference, not a byte-identity finding.
- Exact CUDA companion archive contents and redistribution status were not inspected.
- Server-side repository settings, remote-only refs, forks, caches, Actions logs/secrets, and unreachable Git objects were not fully accessible.
- Workflow YAML was syntax-parsed, but `actionlint` was unavailable, no GitHub schema validation was run, and no source-repository workflow execution existed to validate behavior empirically.
- No exhaustive manual review of every video frame or every historical prose blob was performed.
- Aspire did not reach application startup in the fresh profile, so Chrome/UI/authentication validation was not possible.
- E2E/live GPU/tool-grammar/release packaging lanes were not run.

## Authoritative references consulted

- [Apache License 2.0, especially redistribution/NOTICE conditions](https://www.apache.org/licenses/LICENSE-2.0.html)
- [ASF treatment of third-party works and notices](https://www.apache.org/legal/src-headers.html)
- [ASF GPL compatibility guidance](https://www.apache.org/licenses/GPL-compatibility)
- [.NET license information for Windows](https://github.com/dotnet/core/blob/main/license-information-windows.md)
- [.NET binary asset licensing model](https://github.com/dotnet/runtime/blob/main/docs/project/licensing-assets.md)
- [.NET Library License](https://dotnet.microsoft.com/en-us/dotnet_library_license.htm)
- [Velopack GitHub Actions guidance](https://docs.velopack.io/distributing/github-actions)
- [NVIDIA CUDA 12.4 distribution terms](https://docs.nvidia.com/cuda/archive/12.4.1/eula/index.html#distribution-requirements)
- [GitHub public-repository community profiles](https://docs.github.com/en/communities/setting-up-your-project-for-healthy-contributions/about-community-profiles-for-public-repositories)
- [GitHub secret scanning](https://docs.github.com/en/code-security/concepts/secret-security/secret-scanning)
- [GitHub private vulnerability reporting](https://docs.github.com/en/code-security/how-tos/report-and-fix-vulnerabilities/configure-vulnerability-reporting/configure-for-a-repository)
