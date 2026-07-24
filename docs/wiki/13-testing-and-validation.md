# Testing & Validation

> Last reviewed: 2026-07-24 · Code-grounded.

This page is the contributor map of how XE Local AI Engine is tested and what counts as "validated". It covers the test-project topology (backend integration, AI/agent, persistence + migration, Playwright E2E, plus the FakeOllama and Client.Testing support libraries), the validation commands and the `project-validate.sh` scope runner, the state of the (disabled) GitHub Actions workflows and where the gates actually run instead, and the RC evidence bar a maintainer must clear before claiming release/doc work is done. For *what each suite asserts about a subsystem*, follow the per-subsystem links — this page owns the harness, not the features.

Test stack at a glance: **TUnit 1.58.0** on **Microsoft.Testing.Platform (MTP)** for all .NET suites, **NSubstitute 5.3.0** for mocks, **Microsoft.Playwright 1.61.0 + TUnit.Playwright** for browser E2E, and **Vitest (v8 coverage)** for the React client. `global.json` sets a `10.0.100` feature-band baseline (`rollForward: latestFeature`, so it rolls forward to the highest installed 10.0 feature band and patch at or above `10.0.100` rather than pinning an exact version) and `"test": { "runner": "Microsoft.Testing.Platform" }`, so the whole repo runs under MTP, not VSTest.

> ⚠️ MTP gotcha (repo-wide): filter by `--treenode-filter`, NOT `--filter`. The legacy VSTest `--filter` silently matches nothing under MTP and gives a false "0 tests, all green" result.

## Test topology

| Project | Kind | Framework | What it covers | Subsystem page |
|---|---|---|---|---|
| `XE-Local-AI-Engine.Tests` | Backend integration (in-process host via `WebApplicationFactory<Program>`) | TUnit + NSubstitute | The whole node host: endpoints, hubs, auth, chat, agents, scheduler, model-fit, capacity, MCP, providers, memory, shutdown, and development mode | [Architecture](01-architecture-overview.md), [API & Hubs](09-api-and-hubs.md) |
| `XE-Local-AI-Engine.AI.Agent.Tests` | Unit/component for the agent runtime | TUnit + NSubstitute | MAF/MEAI wiring: `Chat/`, `Eval/`, `Invocation/`, `Tools/`, `PreviewWorkflows/`, plus project smoke tests | [Agent Mode](04-agent-mode.md), [Chat](05-chat.md) |
| `XE-Local-AI-Engine.Client.Persistence.Tests` | Persistence + EF migration tests | TUnit | Encrypted SQLite stores, AEAD cipher, schema-focused migration tests (22 `*MigrationTests.cs` files vs 40 migrations on disk, not strictly 1:1), and the `NegativeFence/` compile-fence | [Data & Persistence](08-data-and-persistence.md) |
| `XE-Local-AI-Engine.Tests.E2ETests` | Browser E2E | Playwright + TUnit.Playwright | Real Chromium against the in-process host serving the real built React SPA (19 `*E2ETests.cs`: Chat, Agents, Scheduler, Models, NodeSettings, Dashboard, smoke, viewport, …) | [React Client](10-react-client.md), [Hosting](11-hosting-and-deployment.md) |
| `XE-Local-AI-Engine.Testing.FakeOllama` | Support library (not a test suite) | — | In-memory fake Ollama HTTP server + deterministic embeddings, so backend tests never need a real model runtime | [Local Runtime & Providers](03-local-runtime-and-providers.md) |
| `XE-Local-AI-Engine.Client.Testing` | Support library | — | Outbound-event recorders + `RecordingHubMessageSender` to assert what the node *would* send over WorkerHub without a real platform | [API & Hubs](09-api-and-hubs.md), [Security & Privacy](12-security-and-privacy.md) |

React unit/component tests live **inside** the client tree (`XE-Local-AI-Engine.Client.React/src/**/*.test.{ts,tsx}`), colocated with source per the repo convention, and run under Vitest. See [React Client](10-react-client.md).

> Test-file totals change frequently and are not a validation result. Run the solution-level command below under MTP with `--max-parallel-test-modules 1`; for a targeted run, use `--treenode-filter`.

### Suites added since the last review

These suites landed with the 2026-06-24…27 subsystems and are confirmed present in the tree (counts left qualitative on purpose):

- **Inference optimizer / per-machine tuning** (`XE-Local-AI-Engine.Tests`): `Inference/InferenceProfileResolverTests.cs`, `Inference/InferenceProfileServiceTests.cs`, `Inference/MachineKeyProviderTests.cs`, and the provider-side `Providers/LlamaServer/LlamaListDevicesVramProbeTests.cs` (the real `--list-devices` VRAM probe). See [Local Runtime & Providers](03-local-runtime-and-providers.md).
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

Schema-focused EF migrations have dedicated `*MigrationTests.cs` files that apply the migration to a fresh encrypted SQLite DB and assert the resulting shape — 22 such files for 40 migrations on disk, so coverage is intentionally not 1:1. The file follows the migration name rather than always taking an `Add*` prefix (for example, `NodeChatOriginMigrationTests.cs` and `NodeMessageLifecycleMigrationTests.cs`). `NodeAeadCipher` and persistence encryption are tested directly. The `NegativeFence/` folder is a separate **compile-only** project (`XE-Local-AI-Engine.Client.Persistence.NegativeFence`) whose `Program.cs` constructs a `NodeMessage`; it guards a compile-time visibility/constructibility contract rather than runtime behavior. See [Data & Persistence](08-data-and-persistence.md).

### `XE-Local-AI-Engine.Tests.E2ETests` — Playwright

The E2E harness is the highest-fidelity path: a real browser drives the real SPA served by the real host.

- `Infrastructure/XEReactClientFixture.cs` runs `pnpm install --frozen-lockfile` then `pnpm run build` of the actual React client (serialising across fixtures behind a `BuildLock`, one retry on transient pnpm contention), then copies `dist/` into a temp web-root that the host serves at `/` via `UseWebRoot` — same-origin, no `/app` prefix (`XEReactClientFixture.cs:60-115`). This is why E2E is slow and ask-gated: it builds the frontend.
- `Infrastructure/XENodeE2EWebApplicationFactory.cs` boots the host and seeds a single admin (`AdminEmail`/`AdminPassword`); `StubTokenStore.cs` stands in for credential storage.
- `Common/XEE2ETestBase.cs` is the per-test base: headless Chromium (set `HEADED=true` for a visible browser), `--ignore-certificate-errors`, Playwright tracing that is **saved only on failure** to `test-results/traces/*.zip`, real password login in `[Before(Test)]` so the context holds the HttpOnly refresh cookie, and `ResetWorkerEventDispatcher()` before each test to stop a completed invocation leaking into another test's empty-state assertion (`XEE2ETestBase.cs:50-133`).
- `Common/BrowserParallelLimit.cs` (`[ParallelLimiter<BrowserParallelLimit>]`) bounds concurrent browsers so CI/WSL2 runners don't thrash.

### Support libraries

- **FakeOllama** (`XE-Local-AI-Engine.Testing.FakeOllama`): an in-memory HTTP server (`FakeOllamaServer.StartAsync`) implementing the Ollama API surface the provider calls — `Endpoints/`: `Chat`, `Generate`, `Embed`, `Show`, `Tags`, `Ps`, `Pull`, `Delete`, plus `TestControlEndpoints` for scripting responses/failures. `Determinism/EmbeddingDeterminism.cs` produces a stable SHA256-seeded vector for any input so embedding/RAG tests are deterministic. `FakeOllamaOptions`, `FakeOllamaScriptRequest`, and `FakeOllamaFailure*` let a test pre-program model lists, scripted turns, and induced failures.
- **Client.Testing** (`XE-Local-AI-Engine.Client.Testing`): `RecordingHubMessageSender` decorates the real `IHubMessageSender`, recording every outbound WorkerHub call (chunks, tool-call requests, approval requests, completed/failed envelopes) with a monotonic sequence number before delegating, so tests assert the node's *outbound contract* without a live platform. `IOutboundEventRecorder` has `HttpForwardingOutboundEventRecorder` (forward to a sink) and `NoOpOutboundEventRecorder` implementations; `AddHubMessageRecording(...)` wires it. This is the seam that lets tests prove the [security invariant](12-security-and-privacy.md) that only the node talks to the platform and creds never cross the boundary.

## Validation commands

### Raw commands (from repo root)

```bash
# Backend — restore, build Release, test (whole solution)
dotnet restore XE-Local-AI-Engine.slnx
dotnet build   XE-Local-AI-Engine.slnx --configuration Release --no-restore
dotnet test    XE-Local-AI-Engine.slnx --configuration Release --no-build --max-parallel-test-modules 1  # serial modules: WSL inotify limit
```

```bash
# React client
cd XE-Local-AI-Engine.Client.React
pnpm install --frozen-lockfile
pnpm run lint     # tsc --noEmit + CheckEventCurrentTargetInUpdaters.mjs + biome lint + stylelint
pnpm test         # vitest run
pnpm run build    # lint chain + vite build
```

Notable React scripts (from `package.json`): `test:coverage` / `test:coverage:check` (the latter sets `VITEST_COVERAGE_CHECK=true` to enforce thresholds), `openapi:check` (regenerate the hey-api client from the committed spec and fail on drift — see below), `validate` (lint + knip + depcruise), `knip`, `depcruise`, `spellCheck`. The lint chain is strict: type-check, a custom `currentTarget`-in-updaters guard, Biome, and Stylelint all run before the build.

### `.opencode/scripts/project-validate.sh` — the scope runner

The wrapper mirrors the raw commands and parallelises independent trees. Invoke as `project-validate.sh --scope <scope> [--confirm-e2e] [--base <branch>] [--serial]`. Per-tree output is redirected to timestamped logs under `.tmp/validate-logs/`; on failure it prints the last 80 lines of the failing log. Backend and frontend run in parallel by default (`--serial` to debug); E2E waits for both to pass first.

| Scope | What it runs |
|---|---|
| `backend` | restore + `build -c Release` + `test` on `XE-Local-AI-Engine.slnx` |
| `frontend` | `pnpm install --frozen-lockfile` + `lint` + `test` + `build` |
| `e2e` | **ask-gated**: pre-gate backend+frontend, then build + `dotnet test` the E2E project **with `-p:RunE2ETests=true`** (required — without it the csproj is a plain library and zero tests are discovered; the runner also fails the lane if zero tests run). Refuses to run without `--confirm-e2e` |
| `smoke` | fast backend+frontend subset; adds E2E smoke only with `--confirm-e2e` |
| `coverage` | backend `--collect:"XPlat Code Coverage"` + frontend `test:coverage:check` (threshold gate) |
| `changed` | auto-detects backend/frontend from `git diff` vs `--base` (default `main`); `*.cs/*.csproj/*.slnx/Directory.*/global.json` → backend, frontend dir → frontend |
| `full` | backend + frontend in parallel, + E2E if `--confirm-e2e` |
| `setup` | OpenCode meta-validation (`validate-no-legacy.sh`, `validate-opencode.ts`, JSON config sanity) — project-agnostic |

The everyday loop is `--scope changed --serial`; E2E is deliberately behind `--confirm-e2e` because it builds the React client and launches browsers and so may need browser/runtime setup not present in every environment.

> Note: `scope_smoke` / `scope_backend_smoke` currently invoke the *full* `dotnet test` (not a reduced subset) — the "smoke" backend body is identical to the full backend body in `project-validate.sh`. Treat backend smoke as a full backend run today.

## Continuous integration — dormant

> **GitHub Actions is disabled on this repository and has never produced a successful run.** Nothing in `.github/workflows/` gates a merge or a release today. Verified 2026-07-24 via `gh workflow list --all` and `gh run list`.

| Workflow file | Registered state | Run history |
|---|---|---|
| `build-and-test.yml` | `disabled_manually` | 3 runs, **3 failures**, last attempt 2026-04-20 |
| `release.yml` | `disabled_manually` | 3 runs, **3 failures**, all 2026-06-27, each dead in ~40 s |
| `e2e.yml` | **not a registered workflow** | never run; its nightly cron has never fired |

Six runs, six failures, zero successes, in the repository's whole history. Two further reasons `build-and-test.yml` could not have gated the current RC even if it were enabled: it triggers only on `pull_request`/`push` to `develop`/`main`, the RC branch is `feature/agent-mode-foundation`, and **`main` does not exist** in this repo.

The workflow files are still tracked and are the design of record — read them for intent, and keep them accurate if you change the validation commands. But treat every gate below as **what would run if the workflows were re-enabled**, not as something protecting the branch you are on.

**Where the gates actually live today:** [`publish/package-tester-win.ps1`](../../publish/package-tester-win.ps1) is the only enforced quality gate in the project. It runs the frontend gate set (frozen install, lint, OpenAPI drift check, third-party license check, coverage-gated tests, production dependency audit, production build) and the backend gate set (restore, transitive NuGet vulnerability audit, Release build, solution-wide serial tests with a hollow-gate guard) itself, on the packaging machine, at release time. Every published tester RC came from it. A gate added to a workflow file enforces nothing; a gate added to that script is real.

Between releases, the enforcement is you: run [`.opencode/scripts/project-validate.sh`](../../.opencode/scripts/project-validate.sh) or the raw commands above before you call a change done.

### What the dormant workflows describe

**`build-and-test.yml`** — designed to run on `pull_request`/`push` to `develop`/`main` plus `workflow_dispatch`, and to be `workflow_call`-reusable (`release.yml` calls it so the exact tagged commit re-runs these gates before packaging). Two jobs:

- **`build-and-test` (ubuntu-latest)** — SDK from `global.json`, restore + `build -c Release --no-restore`, then a single auto-enrolled solution-wide `dotnet test XE-Local-AI-Engine.slnx -c Release --no-build --max-parallel-test-modules 1`. Solution-level `dotnet test` runs every MTP test project (name ends `.Tests` / contains `.Tests.`), so a new suite needs no workflow edit; output is piped through `tee` and a **hollow-gate guard** greps for a `Passed!`/`Failed!` summary, failing if none is found (catching a silent green where zero suites enrolled). `package-tester-win.ps1` implements the same hollow-gate guard, which is why that one is load-bearing today.
- **`client-react` (ubuntu-latest)** — pnpm + Node 22, `install --frozen-lockfile`, then in order `openapi:check`, `licenses:check`, `lint`, `test:coverage:check`, `build`, and `pnpm audit --prod --audit-level=high`. The same set now runs in the packaging script's frontend leg.

Two design choices worth preserving in the file: **`--max-parallel-test-modules 1`** (concurrent modules time out / exhaust the WSL `inotify` watch limit on shared runners) and **`TZ=Europe/Berlin`** on the test step (a non-UTC zone deliberately exposes time-zone bugs; the comment cites `CapabilityReporterTests`). Note that `TZ` is a **Unix-only** mechanism in .NET (`TimeZoneInfo.Unix.NonAndroid.cs` reads it; the Windows implementation resolves the zone from `kernel32!GetDynamicTimeZoneInformation` and reads no environment variable). It therefore cannot be reproduced on a Windows packaging machine by setting a variable — `package-tester-win.ps1` instead **requires** the machine's own time zone to be non-UTC, throwing before the test leg if the current offset is `+00:00` and pointing at `tzutil /s`, with `-AllowUtcTestTimeZone` to accept the reduced coverage.

**`e2e.yml`** is written for a nightly cron, manual dispatch, and PRs labelled `run-e2e`, building the SPA and installing Playwright browsers. It has never executed. E2E is a manual lane only: `project-validate.sh --scope e2e --confirm-e2e`, or the raw commands with `-p:RunE2ETests=true`.

### Release-path gates

Two gates ride the packaging path rather than any test suite:

- **Release-notes generation** — git-cliff renders `RELEASE_NOTES.md` from conventional commits between the previous `v`-prefixed tag and HEAD (config `cliff.toml`), and the notes are fed to `vpk pack --releaseNotes`. `package-tester-win.ps1` downloads a **checksum-pinned** git-cliff and invokes it directly; it does **not** call `scripts/generate-release-notes.sh`. See [Hosting & Deployment](11-hosting-and-deployment.md).
- **SPA-build-required publish gate** — the `GuardNodeReactBuildPresentOnPublish` MSBuild target **fails a publish whose React `dist/` build is missing**, so a packaged build can never ship a blank page. This one is enforced by MSBuild, so it holds on every publish path including a hand-run `dotnet publish`. Build the SPA (`pnpm run build`) first. See [Hosting & Deployment](11-hosting-and-deployment.md).

## Coverage gates

- **Backend**: `--collect:"XPlat Code Coverage"` into `.tmp/validate-logs/coverage/` (collection; no hard solution-wide threshold enforced in the script).
- **Frontend**: Vitest v8 coverage. Thresholds are **only** enforced when `VITEST_COVERAGE_CHECK=true` (the `test:coverage:check` script). Current bar in `vite.config.ts`: branches 35, functions 34, lines 39, statements 38. Generated/locale/test/route-tree files are excluded from coverage (`vite.config.ts:13-20, 101-115`).

## RC evidence requirements

The README's "RC readiness status" section is the contract: **do not mark release or documentation work complete until matching validation evidence is available.** Required evidence:

- restore/build/test transcripts (the green output of the backend + frontend commands above),
- generated schema / sample-manifest validation (incl. the `openapi:check` drift gate passing),
- pinned runtime binary and package checksums (llama.cpp release pins; see [Local Runtime & Providers](03-local-runtime-and-providers.md)),
- a runtime smoke-test transcript.

Two things that **cannot** be proven in WSL2 or on a headless runner and must be verified on a real desktop (call this out explicitly in any RC sign-off): the no-orphan guarantee (terminal/console close reaps the `llama-server` child) and the Windows Job Object hard-kill path — both require a real desktop with a model loaded (README "Self-contained desktop run"). See [Hosting & Deployment](11-hosting-and-deployment.md).

## Maintainer checklist

- Use `--treenode-filter`, never `--filter`, when targeting individual MTP tests.
- New EF migration → add a `<Name>MigrationTests.cs` in `Client.Persistence.Tests`. Coverage is not strictly 1:1 today (22 test files for 40 migrations), but any migration that changes table/column/index shape should ship its test.
- New persistence entity surface change → re-check the `NegativeFence` compile fence still builds.
- New WorkerHub outbound call → assert it through `RecordingHubMessageSender` and confirm no secret crosses the boundary ([Security & Privacy](12-security-and-privacy.md)).
- New backend behavior that touches a model → drive it through `FakeOllama` (script the response) rather than a live runtime; only flip `RUN_LOCAL_INTEGRATION=true` for fidelity runs.
- React change → run `pnpm run lint` + `pnpm test`; if you touched API calls, run `pnpm run openapi:check` yourself — nothing else will catch the drift until the release packager runs it.
- Before claiming done: backend + frontend transcripts green, `openapi:check` clean, and (for RC) the desktop-only smoke evidence captured.

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
- [Home](Home.md)
