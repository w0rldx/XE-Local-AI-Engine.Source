# Testing & Validation

> Baseline: `7e64ed589e14eecc0e522e807d2e531a1095d19a` · Reviewed: 2026-07-28 · Code-grounded.

This page is the contributor map of how XE Local AI Engine is tested and what counts as "validated". It covers the test-project topology (backend integration, AI/agent, persistence + migration, Playwright E2E, plus the FakeOllama and Client.Testing support libraries), the validation commands and standalone runners, the state of the (disabled) GitHub Actions workflows and where the gates actually run instead, and the RC evidence bar a maintainer must clear before claiming release/doc work is done. For *what each suite asserts about a subsystem*, follow the per-subsystem links — this page owns the harness, not the features.

Repository tests and scripts are evidence that controls can be exercised; their presence is not proof
that a particular deployment or release ran them, passed them, retained the output, or made that output
available to an auditor. Operational evidence must be identified separately rather than inferred.

Test stack at a glance: **TUnit 1.58.0** on **Microsoft.Testing.Platform (MTP)** for all .NET suites, **NSubstitute 5.3.0** for mocks, **Microsoft.Playwright 1.61.0 + TUnit.Playwright** for browser E2E, and **Vitest (v8 coverage)** for the React client. `global.json` sets a `10.0.100` feature-band baseline (`rollForward: latestFeature`, so it rolls forward to the highest installed 10.0 feature band and patch at or above `10.0.100` rather than pinning an exact version) and `"test": { "runner": "Microsoft.Testing.Platform" }`, so the whole repo runs under MTP, not VSTest.

> ⚠️ MTP gotcha (repo-wide): filter by `--treenode-filter`, NOT the legacy VSTest `--filter`. The repository's TUnit/MTP runners and examples support only the tree-node form.

## Test topology

| Project | Kind | Framework | What it covers | Subsystem page |
|---|---|---|---|---|
| `XE-Local-AI-Engine.Tests` | Backend integration (in-process host via `WebApplicationFactory<Program>`) | TUnit + NSubstitute | The whole node host: endpoints, hubs, auth, chat, agents, scheduler, model-fit, capacity, MCP, providers, memory, shutdown, and development mode | [Architecture](01-architecture-overview.md), [API & Hubs](09-api-and-hubs.md) |
| `XE-Local-AI-Engine.AI.Agent.Tests` | Unit/component for the agent runtime | TUnit + NSubstitute | MAF/MEAI wiring: `Chat/`, `Eval/`, `Invocation/`, `Tools/`, `PreviewWorkflows/`, plus project smoke tests | [Agent Mode](04-agent-mode.md), [Chat](05-chat.md) |
| `XE-Local-AI-Engine.Client.Persistence.Tests` | Persistence + EF migration tests | TUnit | SQLite stores with selected encrypted fields, AEAD cipher, schema-focused migration tests (23 `*MigrationTests.cs` files vs 45 migration implementations on disk, not strictly 1:1), and the `NegativeFence/` compile-fence | [Data & Persistence](08-data-and-persistence.md) |
| `XE-Local-AI-Engine.Tests.E2ETests` | Browser E2E | Playwright + TUnit.Playwright | Real Chromium against the in-process host serving the real built React SPA (19 `*E2ETests.cs`: Chat, Agents, Scheduler, Models, NodeSettings, Dashboard, smoke, viewport, …) | [React Client](10-react-client.md), [Hosting](11-hosting-and-deployment.md) |
| `XE-Local-AI-Engine.Testing.FakeOllama` | Support library (not a test suite) | — | In-memory fake Ollama HTTP server + deterministic embeddings, so backend tests never need a real model runtime | [Local Runtime & Providers](03-local-runtime-and-providers.md) |
| `XE-Local-AI-Engine.Client.Testing` | Support library | — | Outbound-event recorders + `RecordingHubMessageSender` to assert what the node *would* send over WorkerHub without a real platform | [API & Hubs](09-api-and-hubs.md), [Security & Privacy](12-security-and-privacy.md) |

React unit/component tests live **inside** the client tree (`XE-Local-AI-Engine.Client.React/src/**/*.test.{ts,tsx}`), colocated with source per the repo convention, and run under Vitest. See [React Client](10-react-client.md).

> Test-file totals change frequently and are not a validation result. Run the solution-level command below under MTP with `--max-parallel-test-modules 1`; for a targeted run, use `--treenode-filter`.

### Suites added since the last review

These suites landed with the 2026-06-24…27 subsystems and are confirmed present in the tree (counts left qualitative on purpose):

- **Inference optimizer / per-machine tuning** (`XE-Local-AI-Engine.Tests`): `Inference/InferenceProfileResolverTests.cs`, `Inference/InferenceProfileServiceTests.cs`, `Inference/MachineKeyProviderTests.cs`, and the provider-side `Providers/LlamaServer/LlamaListDevicesProcessVramBudgetProbeTests.cs` and `Providers/LlamaServer/LlamaDeviceInventoryProbeTests.cs` (the two `--list-devices` consumers: the process VRAM-budget figure and the structured device inventory). See [Local Runtime & Providers](03-local-runtime-and-providers.md).
- **Inference profile operator endpoints**: `Endpoints/ModelFit/V1/InferenceProfileEndpointTests.cs` (explore / benchmark / freeze). See [API & Hubs](09-api-and-hubs.md).
- **GGUF quant recommendation / quant ladder**: `ModelFit/GgufVariantRecommenderTests.cs`, `ModelFit/QuantLadderTests.cs` (quality-tier + hardware-fit + recommended-variant logic). See [Model Fit](07-model-fit.md).
- **Client voice runtime**: `Voice/VoiceManifestEndpointTests.cs` (config-only backend voice manifest; the TTS engine itself is browser-side). See [React Client](10-react-client.md).
- **Desktop hosting**: `Hosting/DesktopPortStoreTests.cs` (loopback port persistence across launches). See [Hosting & Deployment](11-hosting-and-deployment.md).
- **Persistence** (`XE-Local-AI-Engine.Client.Persistence.Tests`): `ConversationUploadedFileStoreTests.cs` (encrypted chat file-upload store). See [Data & Persistence](08-data-and-persistence.md).

### `XE-Local-AI-Engine.Tests` — backend integration

This is the heaviest suite and the heart of validation. `TestingWebAppFactory.cs` spins up the real node host in-process:

- It is a `WebApplicationFactory<Program>` that runs under environment `Testing`, serialises host startup behind a static `HostStartupLock` (TUnit runs classes in parallel; the host bootstrap is not re-entrant), and exposes helpers `CreateNodeAccessToken()` / `AddNodeBearerToken(request)` to mint an admin JWT for the loopback admin API (`TestingWebAppFactory.cs:68-86`).
- Unless `RUN_LOCAL_INTEGRATION=true`, the constructor starts a `FakeOllamaServer` seeded with `["qwen3.5:0.8b", "qwen3-embedding:0.6b"]` and wires the host's provider HTTP base to it — so the suite exercises the real provider/abstraction seam with a fake backend instead of a live model (`TestingWebAppFactory.cs:33-41`). Setting `RUN_LOCAL_INTEGRATION=true` opts into a real local runtime for fidelity runs.
- `Fixtures/FakeWorkerNodeFixture.cs` plus the recorded-events helpers stand in for the platform side of the WorkerHub connection.

`Integration/ApplicationStartupTests.cs` and `Integration/EmbeddingSmokeTests.cs` are the boot/smoke anchors — if these fail, nothing else is trustworthy.

### `XE-Local-AI-Engine.Client.Persistence.Tests` — migrations & the negative fence

Schema-focused EF migrations have dedicated `*MigrationTests.cs` files that apply the migration to a
fresh SQLite DB and assert the resulting shape — 23 such files for 45 migration implementations
(43 timestamped + 2 untimestamped), so coverage is intentionally not 1:1. The file follows the migration
name rather than always taking an `Add*` prefix (for example, `NodeChatOriginMigrationTests.cs` and
`NodeMessageLifecycleMigrationTests.cs`). The AEAD cipher (`INodeAeadCipher` → `AesGcmNodeAeadCipher`)
and persistence encryption are tested directly. The `NegativeFence/` folder is a separate **compile-only** project
(`XE-Local-AI-Engine.Client.Persistence.NegativeFence`) whose `Program.cs` constructs a `NodeMessage`;
it guards a compile-time visibility/constructibility contract rather than runtime behavior. See
[Data & Persistence](08-data-and-persistence.md).

### `XE-Local-AI-Engine.Tests.E2ETests` — Playwright

The E2E harness is the highest-fidelity path: a real browser drives the real SPA served by the real host.

- `Infrastructure/XEReactClientFixture.cs` runs `pnpm install --frozen-lockfile` then **`pnpm run build:e2e`** — a bare `vite build` that deliberately does **not** typecheck — of the actual React client (serialising across fixtures behind a `BuildLock`, one retry on transient pnpm contention), then copies `dist/` into a temp web-root that the host serves at `/` via `UseWebRoot` — same-origin, no `/app` prefix. This is why E2E is slow and ask-gated: it builds the frontend. **It is not `pnpm run build`, so a green E2E run does not prove the frontend typechecks** — `pnpm run lint` is the only typecheck; see the runner note below.
- `Infrastructure/XENodeE2EWebApplicationFactory.cs` boots the host and seeds a single admin (`AdminEmail`/`AdminPassword`); `StubTokenStore.cs` stands in for credential storage.
- `Common/XEE2ETestBase.cs` is the per-test base: headless Chromium (set `HEADED=true` for a visible browser), `--ignore-certificate-errors`, Playwright tracing that is **saved only on failure** to `test-results/traces/*.zip`, real password login in `[Before(Test)]` so the context holds the HttpOnly refresh cookie, and `ResetWorkerEventDispatcher()` before each test to stop a completed invocation leaking into another test's empty-state assertion (`XEE2ETestBase.cs:50-133`).
- `Common/BrowserParallelLimit.cs` (`[ParallelLimiter<BrowserParallelLimit>]`) bounds concurrent browsers so CI/WSL2 runners don't thrash.

### Support libraries

- **FakeOllama** (`XE-Local-AI-Engine.Testing.FakeOllama`): an in-memory HTTP server (`FakeOllamaServer.StartAsync`) implementing the Ollama API surface the provider calls — `Endpoints/`: `Chat`, `Generate`, `Embed`, `Show`, `Tags`, `Ps`, `Pull`, `Delete`, plus `TestControlEndpoints` for scripting responses/failures. `Determinism/EmbeddingDeterminism.cs` produces a stable SHA256-seeded vector for any input so embedding/RAG tests are deterministic. `FakeOllamaOptions`, `FakeOllamaScriptRequest`, and `FakeOllamaFailure*` let a test pre-program model lists, scripted turns, and induced failures.
- **Client.Testing** (`XE-Local-AI-Engine.Client.Testing`): `RecordingHubMessageSender` decorates the real `IHubMessageSender`, recording every outbound WorkerHub call (chunks, tool-call requests, approval requests, completed/failed envelopes) with a monotonic sequence number before delegating, so tests assert the node's *outbound contract* without a live platform. `IOutboundEventRecorder` has `HttpForwardingOutboundEventRecorder` (forward to a sink) and `NoOpOutboundEventRecorder` implementations; `AddHubMessageRecording(...)` wires it. This seam exercises the [security invariant](12-security-and-privacy.md) at the application boundary; it is not network observation or operating-effectiveness evidence.

## Validation commands

### Raw commands (from repo root)

```bash
# Backend — restore, build Release, test (whole solution)
scripts/with-build-lock.sh -- dotnet restore XE-Local-AI-Engine.slnx
scripts/with-build-lock.sh -- dotnet build XE-Local-AI-Engine.slnx --configuration Release --no-restore
scripts/with-build-lock.sh -- scripts/assembly-guard.sh guard --test-bins -- \
  dotnet test XE-Local-AI-Engine.slnx --configuration Release --no-build --max-parallel-test-modules 1
```

Never run a build concurrently with `dotnet test --no-build`: the build can rewrite assemblies while
the test host reads them, producing a phantom red or phantom green. `with-build-lock.sh` prevents
collisions between cooperating processes; `assembly-guard.sh` detects an unwrapped build. Exit `69`
means the lock timed out and nothing ran. Exit `75` means **CONTAMINATED, result void, rerun required**.

```bash
# React client
cd XE-Local-AI-Engine.Client.React
pnpm install --frozen-lockfile
pnpm run lint     # tsc --noEmit + CheckEventCurrentTargetInUpdaters.mjs + biome lint + stylelint
pnpm test         # vitest run
pnpm run build    # lint chain + vite build
```

Notable React scripts (from `package.json`): `test:coverage` / `test:coverage:check` (the latter sets `VITEST_COVERAGE_CHECK=true` to enforce thresholds), `openapi:check` (regenerate the hey-api client from the committed spec and fail on drift — see below), `validate` (lint + knip + depcruise), `knip`, `depcruise`, `spellCheck`. The lint chain is strict: type-check, a custom `currentTarget`-in-updaters guard, Biome, and Stylelint all run before the build.

### Internal RC tooling

Release readiness passes used to route through an internal `project-validate.sh` scope runner
(`.opencode/scripts/`). That tooling was internal-only and is not part of this repository — it never
shipped here and there is no in-repo replacement scope runner. Treat the raw commands above, plus the
[root `AGENTS.md`](../../AGENTS.md#validation) and the standalone runners below, as the canonical
validation path for a fresh clone. A successful pre-merge/pre-packaging pass also needs a live
desktop-backend OpenAPI comparison (`pnpm openapi:check:live` against a running desktop backend) and
the coverage gate described below.

### The three standalone runners

All three exist because GitHub Actions is disabled, so anything not reachable from a local command is not reachable at all.

- **[`scripts/run-e2e-local.sh`](../../scripts/run-e2e-local.sh)** — opt-in local runner for the Playwright suite. Nothing invokes it automatically; run it by hand before cutting a tester RC. It performs a frozen frontend install plus `pnpm run lint` before the fixture's intentionally bare Vite `build:e2e`, installs Playwright browsers (via `playwright.ps1`, so it needs `pwsh`), and rejects zero-test or missing-summary runs. `--list` enumerates without running; `--filter` accepts a TUnit/MTP tree-node expression. Exit codes: `0` pass, `1` tests failed or the run was vacuous, `2` a prerequisite/usage error, `75` build contamination (**void; rerun**). No external services needed — FakeOllama plus the in-process host, so no `llama-server` and no `scripts/dev-stop.sh`.
- **[`scripts/lint-release-scripts.sh`](../../scripts/lint-release-scripts.sh)** — shellcheck + PSScriptAnalyzer over the packaging scripts. With Actions disabled, `publish/package-tester-win.ps1` is the entire release path, so it gets static analysis and its [`publish/tests/package-tester-win.Tests.ps1`](../../publish/tests/package-tester-win.Tests.ps1) Pester suite on every default run. A **missing linter or test module exits 2** rather than passing silently.
- **[`scripts/run-gpu-smoke-local.sh`](../../scripts/run-gpu-smoke-local.sh)** — opt-in **live GPU smoke** against a real, locally started node. Nothing invokes it automatically; run it by hand before cutting a tester RC or after touching the inference/runtime path. It owns the AppHost lifecycle (`dev-start.sh` → `aspire wait app` → `dev-stop.sh`), discovers the port from `dev-status.sh --json` (it changes on every restart), and asserts in order: the installed llama.cpp identity, the `IRuntimeDeviceAudit` verdict, a real streamed chat turn, **that the GPU actually did the work** (nvidia-smi utilisation during generation plus a VRAM rise over a pre-host baseline), a real tool call, optionally image generation (`--images`), and that eject returns VRAM to baseline. Every step must record a verdict, so "nothing ran" can never read as green. **This is the only gate that proves the GPU did the work** — a correct reply proves nothing, because a CPU fallback answers correctly, just slowly (measured on the dev box: GPU 72% / +1199 MiB VRAM versus CPU-fallback 11% / +0 MiB, identical correct answer). Exit codes: `0` pass, `1` the product failed a judged step (always with a `=== Summary ===`), **`5` an infrastructure abort where nothing was judged and no summary prints** (AppHost never became healthy, base URL undiscoverable, auth failed) — so a wrapper can treat 1 as "product says no" and 5 as "fix the machine and re-run"; `3` an instance is already running, `4` could not tell, `2` a missing prerequisite (including "this box has no NVIDIA GPU"), `75` contamination (**void; rerun**), `130` interrupted. Its refuse-to-pass logic is itself tested without a GPU by `scripts/tests/gpu-smoke.test.sh`.

> **A zero-test run is a failure, not a pass.** All three runners enforce this directly, and so does the E2E project itself: the GPU smoke's equivalent is that a step which records no verdict fails the run. The E2E project sets `IsTestProject=false`/`OutputType=Library` unless `-p:RunE2ETests=true` is passed, so without it `dotnet test` discovers nothing and exits **0**. Never read a green E2E run without checking that a non-zero number of tests actually ran.

> **The frontend prerequisite is explicit.** The standalone runner executes `pnpm run lint` before
> E2E because the fixture uses bare `pnpm run build:e2e`; do not infer type/lint correctness from a
> green Vite build alone. Use `--list` for the current discovered-test count.

## Continuous integration — dormant

> **GitHub Actions is disabled on this repository and has never produced a successful run.** Nothing in `.github/workflows/` gates a merge or a release at the baseline. External GitHub state was last checked 2026-07-24 via `gh workflow list --all` and `gh run list`; it was not recaptured for the 2026-07-28 documentation review.

| Workflow file | Registered state | Run history |
|---|---|---|
| `build-and-test.yml` | `disabled_manually` | 3 runs, **3 failures**, last attempt 2026-04-20 |
| `release.yml` | `disabled_manually` | 3 runs, **3 failures**, all 2026-06-27, each dead in ~40 s |
| `e2e.yml` | **not a registered workflow** | never run; its nightly cron has never fired |

Six runs, six failures, zero successes, in the repository's whole history. Two further reasons `build-and-test.yml` could not have gated the current RC even if it were enabled: it triggers only on `pull_request`/`push` to `develop`/`main`, the RC branch is `feature/agent-mode-foundation`, and **`main` does not exist** in this repo.

The workflow files are still tracked and are the design of record — read them for intent, and keep them accurate if you change the validation commands. But treat every gate below as **what would run if the workflows were re-enabled**, not as something protecting the branch you are on.

**Where the release-time gates live:** when manually run,
[`publish/package-tester-win.ps1`](../../publish/package-tester-win.ps1) is the canonical Windows
tester packaging path. It runs the frontend gate set (frozen install, lint, OpenAPI drift check,
third-party license check, coverage-gated tests, production dependency audit, production build) and
the backend gate set (restore, transitive NuGet vulnerability audit, Release build, solution-wide
serial tests with a hollow-gate guard) on the packaging machine. It became canonical in
`0.1.0-rc.4.0`; earlier tester releases predate it and are not evidence that they passed today's gates.
A gate added only to a disabled workflow enforces nothing; release-script lint remains a separate
local gate. The script is a control definition, not evidence of a successful run.

Between releases, the enforcement is you: run the raw commands above (and the root [`AGENTS.md`](../../AGENTS.md#validation) Validation section) before you call a change done.

### What the dormant workflows describe

**`build-and-test.yml`** — designed to run on `pull_request`/`push` to `develop`/`main` plus `workflow_dispatch`, and to be `workflow_call`-reusable (`release.yml` calls it so the exact tagged commit re-runs these gates before packaging). Two jobs:

- **`build-and-test` (ubuntu-latest)** — SDK from `global.json`, restore + `build -c Release --no-restore`, then a single auto-enrolled solution-wide `dotnet test XE-Local-AI-Engine.slnx -c Release --no-build --max-parallel-test-modules 1`. Solution-level `dotnet test` runs every MTP test project (name ends `.Tests` / contains `.Tests.`), so a new suite needs no workflow edit; output is piped through `tee` and a **hollow-gate guard** greps for a `Passed!`/`Failed!` summary, failing if none is found (catching a silent green where zero suites enrolled). `package-tester-win.ps1` implements the same hollow-gate guard, which is why that one is load-bearing today.
- **`client-react` (ubuntu-latest)** — pnpm + Node 22, `install --frozen-lockfile`, then in order `openapi:check`, `licenses:check`, `lint`, `test:coverage:check`, `build`, and `pnpm audit --prod --audit-level=high`. The same set now runs in the packaging script's frontend leg.

Two design choices worth preserving in the file: **`--max-parallel-test-modules 1`** (concurrent modules time out / exhaust the WSL `inotify` watch limit on shared runners) and **`TZ=Europe/Berlin`** on the test step (a non-UTC zone deliberately exposes time-zone bugs; the comment cites `CapabilityReporterTests`). Note that `TZ` is a **Unix-only** mechanism in .NET (`TimeZoneInfo.Unix.NonAndroid.cs` reads it; the Windows implementation resolves the zone from `kernel32!GetDynamicTimeZoneInformation` and reads no environment variable). It therefore cannot be reproduced on a Windows packaging machine by setting a variable — `package-tester-win.ps1` instead **requires** the machine's own time zone to be non-UTC, throwing before the test leg if the current offset is `+00:00` and pointing at `tzutil /s`, with `-AllowUtcTestTimeZone` to accept the reduced coverage.

**`e2e.yml`** is written for a nightly cron, manual dispatch, and PRs labelled `run-e2e`, building the SPA and installing Playwright browsers. It has never executed. E2E is a manual lane only: [`scripts/run-e2e-local.sh`](../../scripts/run-e2e-local.sh), or the raw commands with `-p:RunE2ETests=true`.

### Release-path gates

Two gates ride the packaging path rather than any test suite:

- **Release-notes generation** — git-cliff renders `RELEASE_NOTES.md` from conventional commits between the previous `v`-prefixed tag and HEAD (config `cliff.toml`), and the notes are fed to `vpk pack --releaseNotes`. `package-tester-win.ps1` downloads a **checksum-pinned** git-cliff and invokes it directly; it does **not** call `scripts/generate-release-notes.sh`. See [Hosting & Deployment](11-hosting-and-deployment.md).
- **SPA-build-required publish gate** — the `GuardNodeReactBuildPresentOnPublish` MSBuild target **fails a publish whose React `dist/` build is missing**, so a packaged build can never ship a blank page. This one is enforced by MSBuild, so it holds on every publish path including a hand-run `dotnet publish`. Build the SPA (`pnpm run build`) first. See [Hosting & Deployment](11-hosting-and-deployment.md).

## Coverage gates

- **Backend**: MTP-native `--coverage --coverage-output-format cobertura`, with a unique current-run directory and one output subdirectory per non-E2E test project. The validator requires every run to execute tests, merges all reports by unique source line, and fails below the committed 90.50% baseline.
- **Frontend**: Vitest v8 coverage. Thresholds are **only** enforced when `VITEST_COVERAGE_CHECK=true` (the `test:coverage:check` script). Current bar in `vite.config.ts`: branches 35, functions 34, lines 39, statements 38. Generated/locale/test/route-tree files are excluded from coverage (`vite.config.ts:13-20, 101-115`).

## RC evidence requirements

The README's "RC readiness status" section is the contract: **do not mark release or documentation work complete until matching validation evidence is available.** Required evidence:

- the canonical packager's frontend, backend, vulnerability, and package-gate transcript,
- a clean default `scripts/lint-release-scripts.sh` result, including the mandatory Pester suite,
- a non-vacuous `scripts/run-e2e-local.sh` result with no exit-75 contamination,
- a passing `scripts/run-gpu-smoke-local.sh` run on a GPU box — the only evidence that the GPU actually
  did the work rather than a silent CPU fallback; an exit 5 is an infrastructure abort in which nothing
  was judged, so it is not evidence either way and the run must be repeated,
- generated schema/sample-manifest validation, including a clean `openapi:check`,
- pinned runtime binary and package checksums (llama.cpp release pins; see [Local Runtime & Providers](03-local-runtime-and-providers.md)),
- the matching `v<version>` source tag on the exact packaged commit,
- a real-Windows smoke transcript for the exact generated `Portable.zip`,
- the generated five-asset SHA-256 manifest, printed Portable hash, pushed source-tag verification,
  and successful verification of all five remote assets during `-PublishDraft`, and
- confirmation that the unchanged draft was published in `w0rldx/XE-Local-AI-Engine.Tester-App`.

This baseline documentation review does not assert that those release artifacts or transcripts exist,
were retained, or are available to the recipient.

Two things that **cannot** be proven in WSL2 or on a headless runner and require target-OS evidence
for an RC claim: the no-orphan design (terminal/console close reaps the `llama-server` child) and the
Windows Job Object hard-kill path. Both require a real desktop with a model loaded; without the matching
retained transcript their operating status is unknown. See [Hosting & Deployment](11-hosting-and-deployment.md).

## Maintainer checklist

- Use `--treenode-filter`, never `--filter`, when targeting individual MTP tests.
- New EF migration → add a `<Name>MigrationTests.cs` in `Client.Persistence.Tests`. Coverage is not strictly 1:1 today (23 test files for 45 migration implementations), but any migration that changes table/column/index shape should ship its test.
- New persistence entity surface change → re-check the `NegativeFence` compile fence still builds.
- New WorkerHub outbound call → assert it through `RecordingHubMessageSender` and confirm no secret crosses the boundary ([Security & Privacy](12-security-and-privacy.md)).
- New backend behavior that touches a model → drive it through `FakeOllama` (script the response) rather than a live runtime; only flip `RUN_LOCAL_INTEGRATION=true` for fidelity runs.
- React change → run `pnpm run lint` + `pnpm test`; if you touched API calls, run the fast snapshot-only `pnpm run openapi:check`. The full validator additionally compares against a freshly launched desktop backend.
- Before claiming done: backend + frontend transcripts green and uncontaminated, `openapi:check` clean, and (for RC) the complete draft/hash/desktop smoke evidence captured.

## Related pages

- [Architecture Overview](01-architecture-overview.md)
- [Project Layout](02-project-layout.md)
- [Local Runtime & Providers](03-local-runtime-and-providers.md)
- [Agent Mode](04-agent-mode.md)
- [Chat](05-chat.md)
- [Scheduler](06-scheduler.md)
- [Model Fit](07-model-fit.md)
- [Data & Persistence](08-data-and-persistence.md)
- [API & Hubs](09-api-and-hubs.md)
- [React Client](10-react-client.md)
- [Hosting & Deployment](11-hosting-and-deployment.md)
- [Security & Privacy](12-security-and-privacy.md)
- [Technical/Security Architecture Dossier](../audits/technical-security-architecture/README.md)
- [Home](Home.md)
