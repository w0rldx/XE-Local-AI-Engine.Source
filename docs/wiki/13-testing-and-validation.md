# Testing & Validation

> Reviewed: 2026-09-15 · Code-grounded.

This page is the contributor map of how XE Local AI Engine is tested and what counts as "validated". For *how to write a new test* — which project it belongs in, the fixture patterns, the parallelism keys, the per-kind recipes — see [Writing Tests](17-writing-tests.md), whose [Test principles](17-writing-tests.md#1a-test-principles) section states the independence, self-validation and mocking rules every suite on this page is held to. It covers the test-project topology (backend integration, AI/agent, persistence + migration, Playwright E2E, plus the FakeOllama and Client.Testing support libraries), validation commands and standalone runners, the tracked GitHub Actions gate design, and the RC evidence bar a maintainer must clear before claiming release/doc work is done. For *what each suite asserts about a subsystem*, follow the per-subsystem links — this page owns the harness, not the features.

Repository tests and scripts are evidence that controls can be exercised; their presence is not proof
that a particular deployment or release ran them, passed them, retained the output, or made that output
available to an auditor. Operational evidence must be identified separately rather than inferred.

Test stack at a glance: **TUnit 1.65.68** on **Microsoft.Testing.Platform (MTP)** for the three unit-test projects (the E2E project's `TUnit.Playwright` is pinned **1.65.68**), **NSubstitute 6.2.0** for mocks, **Microsoft.Playwright 1.62.0 + TUnit.Playwright** for browser E2E, and **Vitest (v8 coverage)** for the React client. `global.json` pins `10.0.401` with `rollForward: latestPatch` (only the patch digit may move, so the SDK stays inside the `10.0.4xx` feature band; CI's `actions/setup-dotnet` reads the same file and honours `rollForward`, so local and CI resolve the same band) and `"test": { "runner": "Microsoft.Testing.Platform" }`, so the whole repo runs under MTP, not VSTest.

> ⚠️ MTP gotcha (repo-wide): filter by `--treenode-filter`, NOT the legacy VSTest `--filter`. The repository's TUnit/MTP runners and examples support only the tree-node form.

## Test topology

| Project | Kind | Framework | What it covers | Subsystem page |
|---|---|---|---|---|
| `XE-Local-AI-Engine.Tests` | Backend integration (in-process host via `TestServerWebAppFactory`) | TUnit + NSubstitute | The whole node host: endpoints, hubs, auth, chat, agents, scheduler, model-fit, capacity, MCP, providers, memory, shutdown, and development mode | [Architecture](01-architecture-overview.md), [API & Hubs](09-api-and-hubs.md) |
| `XE-Local-AI-Engine.AI.Agent.Tests` | Unit/component for the agent runtime | TUnit + NSubstitute | MAF/MEAI wiring: `Chat/`, `Eval/`, `Invocation/`, `Tools/`, plus project smoke tests | [Agent Mode](04-agent-mode.md), [Chat](05-chat.md) |
| `XE-Local-AI-Engine.Client.Persistence.Tests` | Persistence + EF migration tests | TUnit | SQLite stores with selected encrypted fields, AEAD cipher, migration tests — **every migration implementation is named by a test; all but three have their own `*MigrationTests.cs`, and those three are asserted inside a neighbouring migration's test** (`ls Client.Persistence/Migrations/*.cs` minus `.Designer.cs`/`*ModelSnapshot.cs` is the current count) — and the `NegativeFence/` compile-fence | [Data & Persistence](08-data-and-persistence.md) |
| `XE-Local-AI-Engine.Tests.E2ETests` | Browser E2E | Playwright + TUnit.Playwright | Real Chromium against the in-process host serving the real built React SPA (one `*E2ETests.cs` per surface: Chat, Agents, Scheduler, Models, NodeSettings, Dashboard, Graph Workflows, Development Workflows, Work Sessions, Live Transcription, smoke, viewport, …; `ls XE-Local-AI-Engine.Tests.E2ETests/Tests/*E2ETests.cs` is the current inventory) | [React Client](10-react-client.md), [Hosting](11-hosting-and-deployment.md) |
| `XE-Local-AI-Engine.Testing.FakeOllama` | Support library (not a test suite) | — | In-memory fake Ollama HTTP server + deterministic embeddings, so backend tests never need a real model runtime | [Local Runtime & Providers](03-local-runtime-and-providers.md) |
| `XE-Local-AI-Engine.Client.Testing` | Support library | — | Outbound-event recorders + `RecordingHubMessageSender` to assert what the node *would* send over WorkerHub without a real platform | [API & Hubs](09-api-and-hubs.md), [Security & Privacy](12-security-and-privacy.md) |

React unit/component tests live **inside** the client tree (`XE-Local-AI-Engine.Client.React/src/**/*.test.{ts,tsx}`), colocated with source per the repo convention, and run under Vitest. See [React Client](10-react-client.md).

> Test-file totals change frequently and are not a validation result. Run the backend gate below ([`scripts/run-backend-tests.sh`](../../scripts/run-backend-tests.sh)); for a targeted run, use `--treenode-filter`.

### Suites added since the last review

These suites landed with the 2026-06-24…27 subsystems and are confirmed present in the tree (counts left qualitative on purpose):

- **Inference optimizer / per-machine tuning** (`XE-Local-AI-Engine.Tests`): `Inference/InferenceProfileResolverTests.cs`, `Inference/InferenceProfileServiceTests.cs`, `Inference/MachineKeyProviderTests.cs`, and the provider-side `Providers/LlamaServer/LlamaListDevicesProcessVramBudgetProbeTests.cs` and `Providers/LlamaServer/LlamaDeviceInventoryProbeTests.cs` (the two `--list-devices` consumers: the process VRAM-budget figure and the structured device inventory). See [Local Runtime & Providers](03-local-runtime-and-providers.md).
- **Inference profile operator endpoints**: `Endpoints/ModelFit/V1/InferenceProfileEndpointTests.cs` (explore / benchmark / freeze). See [API & Hubs](09-api-and-hubs.md).
- **GGUF quant recommendation / quant ladder**: `ModelFit/GgufVariantRecommenderTests.cs`, `ModelFit/QuantLadderTests.cs` (quality-tier + hardware-fit + recommended-variant logic). See [Model Fit](07-model-fit.md).
- **Client voice runtime**: frontend tests cover Web Speech capability detection, platform-voice selection, playback, and settings/preview behavior. The backend retains only node-settings coverage for the `VoiceFeatureEnabled` gate and legacy settings compatibility. See [React Client](10-react-client.md).
- **Desktop hosting**: `Hosting/DesktopPortStoreTests.cs` (loopback port persistence across launches). See [Hosting & Deployment](11-hosting-and-deployment.md).
- **Persistence** (`XE-Local-AI-Engine.Client.Persistence.Tests`): `ConversationUploadedFileStoreTests.cs` (encrypted chat file-upload store). See [Data & Persistence](08-data-and-persistence.md).
- **Custom Tools:** backend service/schema/template/SSRF tests under `XE-Local-AI-Engine.Tests/CustomTools/`, host-process and HTTP-fetch executor coverage under `Services/CustomTools/`, `CustomToolStoreTests` for encrypted persistence, agent resolver tests proving approval-wrapped resolution, and colocated React mapper/form/query tests under `src/features/customTools/`.
- **Automatic runtime acquisition:** `RuntimeAcquisitionProgressTests` and `RuntimeAcquisitionStatusRegistryTests` cover snapshot sequencing/progress publication; React hook/banner tests cover hydrate-vs-push ordering, invalid payload rejection, reconnect invalidation, and layout rendering.
- **Windows per-application capture** (see [Audio Transcription](24-audio-transcription.md#windows-per-application-capture)): `Transcription/WindowsProcessLoopbackTests.cs` is the only **platform-gated** class in the backend suite, and it carries **two** class attributes that do different jobs. `[RunOn(OS.Windows)]` is TUnit's gate: on the Linux gate the class reports *skipped* with the reason `Test is restricted to run on the following operating systems: 'Windows'`, which is the honest result and **not** evidence the WASAPI path works. `[SupportedOSPlatform("windows10.0.19041.0")]` is what satisfies the CA1416 platform analyzer for `WasapiRecorderBuilder.WithProcessLoopback` — `[RunOn]` is invisible to Roslyn, a class-level `"windows"` does not satisfy a versioned API, and the `IsTestOrToolingProject` exemption covers only Meziantou and BannedApiAnalyzers, so without the versioned attribute the file does not compile in Release on Linux. No `#pragma warning disable CA1416` is acceptable in its place, in tests or product code. Each body opens with `AssertEx.True(OperatingSystem.IsWindows(), …)` so a gate that stops working **fails** rather than passing quietly. Everything platform-independent in the feature — the documented build floor, the PCM downmix/resample against real `NAudio.Core` types, the coordinator lifecycle and the endpoint shapes — stays ungated in `Transcription/ProcessAudioCaptureTests.cs`, `Transcription/ProcessAudioCaptureCoordinatorTests.cs` and `Endpoints/Transcription/TranscriptionCaptureEndpointTests.cs`, because that is the only coverage a Linux runner can give this slice.
- **External Apps** (see [External Apps](23-external-apps.md)): four suites talk to a **real Docker daemon** — `DockerSandboxRealDaemonTests` (the Development Mode sandbox), `ContainerRuntimeRealDaemonTests` (the engine-owned runtime layer's guards, identity mapping and pull progress), `ContainerBridgeRealDaemonTests` (a container reaching the container bridge through the same-host peer guard and token, rootless daemons only) and `ExternalAppRealDaemonTests` (a two-service application through the real service, resolver, factory, store and daemon, install to uninstall). All three are **opt-in**: they run only under `XE_REQUIRE_DOCKER_TESTS=1`, which is what [`scripts/run-docker-smoke-local.sh`](../../scripts/run-docker-smoke-local.sh) sets, and otherwise skip with a reason naming that runner. CI sets neither the variable nor any `docker pull` step, so the PR gate needs no daemon and no registry. What those suites used to be the only proof of — the Engine API wire shape and the response mapping — now runs unconditionally against [`XE-Local-AI-Engine.Testing.FakeDocker`](../../XE-Local-AI-Engine.Testing.FakeDocker), a loopback HTTP fake of the routes `DockerDotNetRuntimeClient` calls, driven by the production client over a real socket: `FakeDockerDaemonRouteTests`, `FakeDockerContainerRouteTests`, `FakeDockerNetworkAndExecRouteTests`, `DockerSandboxFakeServerTests`, and the `FakeServer` arm of `ContainerRuntimeContractTests`. What stays real-daemon is what a fake cannot falsify: a flag actually honoured, a uid actually mapped, egress actually denied, a healthcheck actually reaching a verdict, and the daemon's own status prose and pull narration, which are the sentinels for Docker rewording either. Counts come from the runner's own output, never from this page. `ExternalAppStorageHelperTests` covers the storage-wipe helper's specification and its leftover count; the rest of the feature runs daemon-free against `FakeDockerRuntimeClient`, which moves in lockstep with the production client so both answer the same guards.

### Architecture guards

`XE-Local-AI-Engine.Tests/Architecture/` holds the rules that fail the gate instead of a reviewer's memory. They are ordinary TUnit tests inside the backend suite, so the gate below runs them; there is no separate runner. `ls XE-Local-AI-Engine.Tests/Architecture/*.cs` is the current inventory. Older members pin placement (`PlacementConventionTests`, `ServiceModelColocationTests`, described in [Code Conventions](16-code-conventions.md)) and assembly dependency direction (`LayerDependencyTests`). The 2026-09-16 static-quality slices added four guards and one shared helper:

- **`EndpointConventionTests`**: one endpoint type per file named after it, the plural `*Endpoints.cs` groupings frozen in an allowlist (34 paths as measured 2026-09-16), every endpoint `sealed` through ArchUnitNET, and every route derived from `LocalApiRoutes` rather than typed as a literal. Reflection over the compiled host decides *which* types are endpoints; a comment-stripped scan of the whole `Client` project locates each declaration, so an endpoint declared outside `Endpoints/` is found and rejected rather than escaping the filename rules.
- **`EndpointDependencyTests`**: what an endpoint may take as a constructor dependency, with a shrink-only ratchet keyed by fully-qualified endpoint-and-parameter pairs (117 pairs across 98 endpoints, measured 2026-09-16). Generic arguments, array element types and `Nullable<T>` are walked recursively, so a forbidden type wrapped in an allowed generic still counts.
- **`ThirdPartySdkBoundaryTests`**: the fence keeping `OllamaSharp`, `Docker.DotNet`, `Azure.*` and `Microsoft.Identity.Client` out of the host, application, persistence, agent and contracts layers, with one shrink-only allowlist per SDK. Each list has a second test that fails on an entry whose file no longer references that SDK, so an exemption cannot outlive what it excused.
- **`EndpointAuthorizationPolicyTests`**: deny-by-default authorization, read from each route's effective metadata rather than from FastEndpoints' own bookkeeping. Every FastEndpoints endpoint resolves to the `NodeOperator` policy or appears in the pre-authentication allowlist (four entries as measured 2026-09-16, with its own stale-entry check); every SignalR hub route requires `NodeOperator` under the configuration that maps it, with the Development-gated hub's absence proved separately under `EnableDevelopmentMode = false`; and the hand-mapped minimal APIs plus the inbound MCP route carry their named policies. The guard's teeth are `ConfiguratorCanaryProbeEndpoint`, a permanent endpoint that calls neither `Policies()` nor `AllowAnonymous()`: the global configurator in `Program.cs` is its only protection, so deleting that one line reds this test by name while every endpoint carrying its own redundant `Policies()` call stays green. See [Security & Privacy](12-security-and-privacy.md) §3.2.
- **`Support/SourceCommentStripper`**: the shared string-aware comment stripper the source-scanning guards call, replacing the regex each had grown. A regex that erases everything after `//` also erases it inside a string literal, so a banned reference written after a URL on the same line passed a guard that never saw it. `ContainerBridgeLayeringArchitectureTests` and `ExternalAppsRuntimeIsolationArchitectureTests` both route through it now, and both carry string-shaped controls that the old regex loses.

`LayerDependencyTests` grew in the same slice: the test and support projects now have their exact `ProjectReference` sets pinned alongside the production ones (seven entries as of 2026-09-16, keyed by repository-relative csproj path because the negative-fence probe nests inside its parent project's directory), cross-checked against the solution's `/Tests` folder membership so a newly added test or fixture project cannot ship unpinned.

Every one of these asserts a **non-vacuity floor**, a minimum number of types, files or list entries scanned, before the rule itself runs, because a marker type resolving to the wrong assembly, an allowlist emptied by a bad merge and a directory walk that found nothing all look identical to "scanned everything, found no violation" in a pass/fail summary. Floors are measured through the guard itself, never from a grep over source; the two populations differ. A guard whose scan target is missing reports **skipped** with a reason rather than passing.

### `XE-Local-AI-Engine.Tests` — backend integration

This is the heaviest suite and the heart of validation. `TestServerWebAppFactory.cs` spins up the real node host in-process:

- It builds the app through `Program.CreateAppAsync` and serves it on `TestServer` — deliberately **not** `WebApplicationFactory<Program>`, whose entry-point resolution leaks every built host for the process lifetime (docs/agent-knowledge.md §1). It runs under environment `Testing`, serialises host startup behind the static `TestServerWebAppFactory.HostStartupLock` (TUnit runs classes in parallel; the host bootstrap is not re-entrant), and exposes `CreateNodeAccessToken()` / `AddNodeBearerToken(request)` helpers to mint an admin JWT for the loopback admin API. Per-test host tweaks go through the `ConfigureAdditionalTestServices` / `AdditionalConfiguration` / `EnableDevelopmentMode` / `EnvironmentName` / `SkipDefaultBaseUrlOverride` init-properties (there is no `WithWebHostBuilder`); `EnvironmentName` is what exercises production-only middleware such as the rate limiter, which the `Testing` environment skips. Suites whose tests are read-only or `Guid`-isolated share **one host per class** via `[ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]` instead of building one per test — see [Writing Tests](17-writing-tests.md) for when that is safe.
- Unless `RUN_LOCAL_INTEGRATION=true`, `TestServerWebAppFactory` starts a `FakeOllamaServer` seeded with `["qwen3.5:0.8b", "qwen3-embedding:0.6b"]` and wires the host's provider HTTP base to it — so the suite exercises the real provider/abstraction seam with a fake backend instead of a live model. Setting `RUN_LOCAL_INTEGRATION=true` opts into a real local runtime for fidelity runs.
- `Fixtures/FakeWorkerNodeFixture.cs` plus the recorded-events helpers stand in for the platform side of the WorkerHub connection.

`Integration/ApplicationStartupTests.cs` and `Integration/EmbeddingSmokeTests.cs` are the boot/smoke anchors — if these fail, nothing else is trustworthy.

### `XE-Local-AI-Engine.Client.Persistence.Tests` — migrations & the negative fence

EF migrations have dedicated `*MigrationTests.cs` files that apply the migration to a fresh SQLite DB and
assert the resulting shape. Every migration implementation in `Client.Persistence/Migrations/` is named by a test.
All but three have a dedicated file; those three (`AddNodeMessageLifecycleColumns`,
`AddAgentDefinitionBaseScaffoldOptOut`, `AddDevelopmentCommandProfile`) are asserted substantively inside a
neighbouring migration's test —
`NodeMessageLifecycleMigrationTests.cs`, `AddAgentDefinitionsMigrationTests.cs` and
`Development/DevelopmentMigrationTests.cs` respectively — so a separate file would only restate them.
The file follows the migration
name rather than always taking an `Add*` prefix (for example, `NodeChatOriginMigrationTests.cs` and
`NodeMessageLifecycleMigrationTests.cs`), and the table/column/index queries come from the shared
`Testing/MigrationSchemaProbe.cs` rather than hand-rolled `PRAGMA` SQL per file. The AEAD cipher (`INodeAeadCipher` → `AesGcmNodeAeadCipher`)
and persistence encryption are tested directly. The `NegativeFence/` folder is a separate **compile-only** project
(`XE-Local-AI-Engine.Client.Persistence.NegativeFence`) whose `Program.cs` constructs a `NodeMessage`;
it guards a compile-time visibility/constructibility contract rather than runtime behavior. See
[Data & Persistence](08-data-and-persistence.md).

### `XE-Local-AI-Engine.Tests.E2ETests` — Playwright

The E2E harness is the highest-fidelity path: a real browser drives the real SPA served by the real host.

- `Infrastructure/XEReactClientFixture.cs` runs `pnpm install --frozen-lockfile` then **`pnpm run build:e2e`** — a bare `vite build` that deliberately does **not** typecheck — of the actual React client (serialising across fixtures behind a `BuildLock`, one retry on transient pnpm contention), then copies `dist/` into a temp web-root that the host serves at `/` via `UseWebRoot` — same-origin, no `/app` prefix. This is why E2E is slow and ask-gated: it builds the frontend. **It is not `pnpm run build`, so a green E2E run does not prove the frontend typechecks** — `pnpm run lint` is the only typecheck; see the runner note below.
- `Infrastructure/XENodeE2EWebApplicationFactory.cs` boots the host and seeds a single admin (`AdminEmail`/`AdminPassword`); `StubTokenStore.cs` stands in for credential storage.
- Two base classes split the suite into disjoint parallel phases: `Common/XEPooledE2ETestBase.cs` (group `BrowserPooled`) leases one of several seeded users per test so browsers run concurrently, and `Common/XESerialE2ETestBase.cs` (group `BrowserSerial`) runs one at a time as the canonical admin for tests that mutate session-global state or assert a node-wide empty state. Which phase runs first is **not** guaranteed.
- `Common/XEE2ETestBase.cs` is the shared per-test base: headless Chromium (set `HEADED=true` for a visible browser), `--ignore-certificate-errors`, Playwright tracing that is **saved only on failure** to `test-results/traces/*.zip`, real password login in `LoginBeforeEachTestAsync()` so the context holds the HttpOnly refresh cookie, and `ResetWorkerEventDispatcher()` before each test to stop a completed invocation leaking into another test's empty-state assertion.
- `Common/BrowserParallelLimit.cs` (`[ParallelLimiter<BrowserParallelLimit>]`) bounds concurrent browsers so CI/WSL2 runners don't thrash.
- `Tests/LiveTranscriptionE2ETests.cs` drives [live audio capture](24-audio-transcription.md#browser-capture-and-the-live-ui) end to end against **Chromium's fake audio device**: `Common/XEFakeAudioE2ETestBase.cs` launches its **own** Chromium before each test, with `--use-fake-device-for-media-stream`, `--use-fake-ui-for-media-stream` and `--use-file-for-fake-audio-capture=<wav>` on that browser's command line, so the browser plays a WAV as the microphone and auto-accepts the permission prompt. It drives its own `FakeAudioPage` and leaves the harness's shared `Page`/`Context` untouched, and it has to: TUnit.Playwright caches one browser per worker under a fixed service key, so per-class `BrowserTypeLaunchOptions` are honoured only for the first class to launch and the second audio suite would otherwise capture the first one's WAV. The file **loops** (no `%noloop` suffix), so a test waits for the first matching commit and stops the session itself rather than waiting for the stream to end; `FirstCommitBudgetMs` on that base is the one named budget both suites use. The file is two classes because the WAV is a browser command-line switch, fixed for a launched browser's lifetime and declared once per suite in its constructor: the positive case plays the repository's `jfk.wav`, and `LiveTranscriptionProvenanceControlE2ETests` plays a generated 440 Hz tone. `FakeJfkWhisperTranscriber` (`Infrastructure/TranscriptionE2ETestDoubles.cs`) is the only double — the segmenter, registry, hub and persistence are all real — and it asserts **provenance**, not loudness: it correlates the received PCM's 500 ms RMS envelope against the fixture's at every circular lag and requires > 0.8, so the suite cannot go green on Chromium's default beep if the file switch is ever lost. The control awaits the double's first-non-silent-input signal and asserts the measured correlation is **below** that threshold — proving the check ran and rejected the audio on its merits, rather than inferring it from elapsed silence. The fake device replaces the **microphone only**; headless Chromium has no `getDisplayMedia` equivalent, so the system-audio path is covered by frontend unit tests and a manual live check.
- `Tests/GraphWorkflowE2ETests.cs` drives the [Graph Workflows](21-graph-workflows.md) loop end to end — create a definition, drop and wire nodes from the palette, validate, save, start a run, answer the pause, read the event tab — against `Testing.FakeOllama` rather than a real model runtime.

### Support libraries

- **FakeOllama** (`XE-Local-AI-Engine.Testing.FakeOllama`): an in-memory HTTP server (`FakeOllamaServer.StartAsync`) implementing the Ollama API surface the provider calls — `Endpoints/`: `Chat`, `Generate`, `Embed`, `Show`, `Tags`, `Ps`, `Pull`, `Delete`, plus `TestControlEndpoints` for scripting responses/failures. `Determinism/EmbeddingDeterminism.cs` produces a stable SHA256-seeded vector for any input so embedding/RAG tests are deterministic. `FakeOllamaOptions`, `FakeOllamaScriptRequest`, and `FakeOllamaFailure*` let a test pre-program model lists, scripted turns, and induced failures.
- **Client.Testing** (`XE-Local-AI-Engine.Client.Testing`): `RecordingHubMessageSender` decorates the real `IHubMessageSender`, recording every outbound WorkerHub call (chunks, tool-call requests, approval requests, completed/failed envelopes) with a monotonic sequence number before delegating, so tests assert the node's *outbound contract* without a live platform. `IOutboundEventRecorder` has `HttpForwardingOutboundEventRecorder` (forward to a sink) and `NoOpOutboundEventRecorder` implementations; `AddHubMessageRecording(...)` wires it. This seam exercises the [security invariant](12-security-and-privacy.md) at the application boundary; it is not network observation or operating-effectiveness evidence.

## Validation commands

### Raw commands (from repo root)

```bash
# Backend — the whole gate: build Release, then every enrolled test project
scripts/run-backend-tests.sh
```

A local build needs an SDK in the `10.0.4xx` band installed, because `global.json` pins that band; `dotnet --list-sdks`
must show one.

[`scripts/run-backend-tests.sh`](../../scripts/run-backend-tests.sh) builds the solution once in Release and then
runs two lanes concurrently: `XE-Local-AI-Engine.Tests` through
[`scripts/run-tests-memory-safe.sh`](../../scripts/run-tests-memory-safe.sh), and every other test project
auto-enrolled from `XE-Local-AI-Engine.slnx` (E2E excluded) as a `dotnet test --no-build` with its own
`--results-directory`. It takes the build lock **once** for the whole run — the lock cannot subdivide a critical
section, and the memory-safe runner holds it for its entire run, so the siblings deliberately do not take it
themselves — and wraps each sibling in the assembly guard. `NO_BUILD=1` skips the build, `--siblings-only` skips the
batched module (the shape CI's `siblings` leg uses, through this same script), and `COVERAGE_DIR` adds Cobertura +
TRX per project.

Two operating rules for that script are stated once, in [AGENTS.md](../../AGENTS.md) §Validation, and not repeated
here: what `COVERAGE_DIR` costs you in contamination detection, and how to cancel a non-interactive run without
orphaning its lanes.

Each sibling runs at a pinned `--maximum-parallel-tests`, not at TUnit's default: TUnit runs tests in parallel with
no formula and no ceiling, and `XE-Local-AI-Engine.Client.Persistence.Tests` at that default measured 6:08 of wall
and 11.6 GB of RSS against 102 s and 2.7 GB at width 4. The defaults are 4 for that project and 8 for the rest;
`XE_TEST_WIDTH_DEFAULT` and `XE_TEST_WIDTH_<Project>` override them (CI passes 2). Pinning the width is what makes
the modules safe to run concurrently, and is why the gate no longer serializes them.

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

```bash
# Python (tools/training + scripts/**) — the same gate CI's python-quality job runs
scripts/python-validation.sh --scope full      # deps, then style/types/tests/security in parallel
scripts/python-validation.sh --scope changed   # auto-detect scope from the diff against develop
```

Notable React scripts (from `package.json`): `test:coverage` / `test:coverage:check` (the latter sets `VITEST_COVERAGE_CHECK=true` to enforce thresholds), `openapi:check` (regenerate the hey-api client from the committed spec and fail on drift — see below), `dependencies:refresh` (frozen install followed by aggregated OpenAPI, generated-license, validation, and production-build diagnostics for dependency-update branches), `validate` (lint + knip + depcruise), `knip`, `depcruise`, `spellCheck`. The dependency refresh skips every generator when the frozen install fails and never retargets curated license evidence automatically. The lint chain is strict: type-check, a custom `currentTarget`-in-updaters guard, Biome, and Stylelint all run before the build.

### The canonical validation path

There is no scope runner in this repository. The raw commands above, plus the
[root `AGENTS.md`](../../AGENTS.md#validation) and the standalone runners below, are the canonical
validation path for a fresh clone. A successful pre-merge/pre-packaging pass also needs a live
desktop-backend OpenAPI comparison (`pnpm openapi:check:live` against a running desktop backend) and
the frontend coverage gate described below.

### The four standalone runners

All four exist for local, CI-independent validation. The CI gate is `build-and-test.yml` (on `develop` PRs/pushes)
and the immutable-tag-bound `release.yml` (on `v*` tags, which itself calls `build-and-test.yml` before packaging).
These standalone runners exercise additional target-specific checks locally.

- **[`scripts/run-e2e-local.sh`](../../scripts/run-e2e-local.sh)** — opt-in local runner for the Playwright suite. Nothing invokes it automatically; run it by hand before cutting a tester RC. It performs a frozen frontend install plus `pnpm run lint` before the fixture's intentionally bare Vite `build:e2e`, installs Playwright browsers (via `playwright.ps1`, so it needs `pwsh` — a missing `pwsh` is a prerequisite failure, never a skip), and rejects zero-test or missing-summary runs. `--list` enumerates without running; `--filter` accepts a TUnit/MTP tree-node expression; `--skip-browser-install` skips the install step and is only safe when `~/.cache/ms-playwright` already carries a matching Chromium build, which the script verifies before honouring the flag. Exit codes: `0` pass, `1` tests failed or the run was vacuous, `2` a prerequisite/usage error, `75` build contamination (**void; rerun**). No external services needed — FakeOllama plus the in-process host, so no `llama-server` and no `scripts/dev-stop.sh`.
- **[`scripts/lint-release-scripts.sh`](../../scripts/lint-release-scripts.sh)** — shellcheck + PSScriptAnalyzer over the packaging scripts. `publish/package-tester-win.ps1` and `publish/package-rc.sh` are deprecated, reference-only manual packagers now (the release path is the tag-triggered `release.yml`), but this script still gives them static analysis and runs `package-tester-win.ps1`'s [`publish/tests/package-tester-win.Tests.ps1`](../../publish/tests/package-tester-win.Tests.ps1) Pester suite on every default run. A **missing linter or test module exits 2** rather than passing silently.
- **[`scripts/run-gpu-smoke-local.sh`](../../scripts/run-gpu-smoke-local.sh)** — opt-in **live GPU smoke** against a real, locally started node. Nothing invokes it automatically; run it by hand before cutting a tester RC or after touching the inference/runtime path. It owns the AppHost lifecycle (`dev-start.sh` → `aspire wait app` → `dev-stop.sh`), discovers the port from `dev-status.sh --json` (it changes on every restart), and asserts in order: the installed llama.cpp identity, the `IRuntimeDeviceAudit` verdict, a real streamed chat turn, **that the GPU actually did the work** (nvidia-smi utilisation during generation plus a VRAM rise over a pre-host baseline), a real tool call, optionally image generation (`--images`), and that eject returns VRAM to baseline. Every step must record a verdict, so "nothing ran" can never read as green. **This is the only gate that proves the GPU did the work** — a correct reply proves nothing, because a CPU fallback answers correctly, just slowly (measured in one local run: GPU 72% / +1199 MiB VRAM versus CPU-fallback 11% / +0 MiB, identical correct answer). Exit codes: `0` pass, `1` the product failed a judged step (always with a `=== Summary ===`), **`5` an infrastructure abort where nothing was judged and no summary prints** (AppHost never became healthy, base URL undiscoverable, auth failed) — so a wrapper can treat 1 as "product says no" and 5 as "fix the machine and re-run"; `3` an instance is already running, `4` could not tell, `2` a missing prerequisite (including a host with no NVIDIA GPU), `75` contamination (**void; rerun**), `130` interrupted. Its refuse-to-pass logic is itself tested without a GPU by `scripts/tests/gpu-smoke.test.sh`.
- **[`scripts/run-tool-grammar-smoke-local.sh`](../../scripts/run-tool-grammar-smoke-local.sh)** — opt-in live compatibility check against a real, non-reasoning, tool-capable `llama-server`. Run it after changing any offered tool schema (including Custom Tools schema compilation) or bumping llama.cpp. It posts the production offer twice: the sanitized form must return 200, while the deliberately unsanitized negative control must still fail with the grammar 400. If the control succeeds, the run is inert rather than green: either the model template bypassed constrained tools or llama.cpp's repetition limit changed and `LlamaGrammarToolSchemaCompatibility.MaxGrammarRepetitionBound` must be re-measured.

> **A zero-test or missing-control run is a failure, not a pass.** The test runners enforce non-vacuity directly; the GPU smoke requires a verdict for every step, and the tool-grammar smoke requires its negative control. The E2E project sets `IsTestProject=false`/`OutputType=Library` unless `-p:RunE2ETests=true` is passed, so without it `dotnet test` discovers nothing and exits **0**. Never read a green E2E run without checking that a non-zero number of tests actually ran.

> **The frontend prerequisite is explicit.** The standalone runner executes `pnpm run lint` before
> E2E because the fixture uses bare `pnpm run build:e2e`; do not infer type/lint correctness from a
> green Vite build alone. Use `--list` for the current discovered-test count.

## Continuous integration

The CI gate is two workflows: **`build-and-test.yml`** (`pull_request`/`push` to `develop`, plus
`workflow_dispatch` and `workflow_call`) and **`release.yml`** (a pushed `v*` tag, plus `workflow_dispatch`), which
calls `build-and-test.yml` as a reusable workflow so the exact tagged commit re-runs the full gate set before
packaging. The release workflow is immutable-tag-bound: the environment-protected `prepare-release-draft` job builds
no assets, but uploads, merges, and remotely verifies the retained matrix output as a draft; a separately approved
`publish-release` job re-verifies and promotes that same draft without rebuilding or replacing assets.

GitHub Actions is **enabled and green** on this repository, so `build-and-test.yml` is a live PR gate on
`develop`, not a paper design. The workflow files are still the design of record — read them for intent, and keep
them accurate if you change the validation commands.

**Where the release-time gates live:** `release.yml`'s `validate` job runs the full backend + frontend gate set via
`build-and-test.yml` against the exact tagged commit before anything is packed or uploaded — downstream `version`,
`build-pack`, `prepare-release-draft`, and `publish-release` work cannot run past a failing validation. The deprecated
manual packager,
[`publish/package-tester-win.ps1`](../../publish/package-tester-win.ps1), ran the same shape of gate set (frontend:
frozen install, lint, OpenAPI drift check, third-party license check, coverage-gated tests, production dependency
audit, production build; backend: restore, transitive NuGet vulnerability audit, Release build, solution-wide serial
tests with a hollow-gate guard) on the packaging machine by hand — it was the release path from `0.1.0-rc.4.0` through
`0.1.0-rc.5.1` and is now reference-only. A gate belongs in `build-and-test.yml` (or the packaging scripts, for
release-script lint) to be enforced; documentation describing a gate is not evidence it ran.

CI is the gate, but it is not a substitute for running the raw commands above (and the root
[`AGENTS.md`](../../AGENTS.md#validation) Validation section) before you push — a red CI run after the fact costs
more than a local Release build.

### What `build-and-test.yml` and `e2e.yml` describe

**`build-and-test.yml`** — runs on `pull_request`/`push` to `develop` plus `workflow_dispatch`, and is `workflow_call`-reusable (`release.yml` calls it as its `validate` job so the exact tagged commit re-runs these gates before packaging). **Five jobs**, and every job carries an explicit `timeout-minutes` so a hung step cannot burn the runner budget — note that the reusable-workflow *call* in `release.yml` cannot carry one, which is a GitHub limitation, not an oversight:

- **`python-quality` (ubuntu-latest)** — sets up `uv` with a pinned version and Python 3.13, then runs [`scripts/python-validation.sh`](../../scripts/python-validation.sh) `--scope full --serial`: `uv sync --locked --all-groups` followed by ruff (`format --check` + `check`), pyrefly, pytest with coverage, and bandit over `tools/training` and `scripts/**`. The tooling config is the **root** `pyproject.toml` + its own small `uv.lock` — deliberately *not* `tools/training/pyproject.toml`, which with its lockfile is the shipped training-runtime manifest (see [ADR 0005](../adr/0005-training-runtime-python-exclusivity-and-project-placement.md) and [Training](18-training.md)). Locally: `scripts/python-validation.sh --scope full`, or `--scope changed` to auto-detect from the diff. The same job then runs [`scripts/docs-inventory-check.py`](../../scripts/docs-inventory-check.py), which re-derives five inventories from the code — SignalR hubs, `LocalApiRoutes` route families, React `features/` directories, numbered wiki pages, solution projects — and fails when one of them is missing from the wiki page that claims to enumerate it.
- **`release-contracts` (ubuntu-latest)** — runs [`scripts/run-release-contract-tests.sh`](../../scripts/run-release-contract-tests.sh) plus `scripts/lint-release-scripts.sh --no-behavior --bootstrap`. Contract discovery is **auto-enrolling** across `scripts/tests`, `scripts/compliance/tests`, and `scripts/performance/tests`, matching `*.test.sh`, `*.test.py`, and `test_*.py` — a new script test needs no workflow edit. The Pester leg of `lint-release-scripts.sh` covers `publish/tests` and `scripts/performance/tests`; **zero discovered Pester tests is a failure, not a pass**.
- **`backend-tests` (ubuntu-latest, five-leg matrix)** — the backend gate, one runner per leg: `siblings` runs every enrolled test project except `XE-Local-AI-Engine.Tests`, and `tests-0`…`tests-3` each run one `TEST_SHARD` quarter of that module through [`scripts/run-tests-memory-safe.sh`](../../scripts/run-tests-memory-safe.sh) at `TEST_GROUPS=16`. Every leg does its own checkout, restore and `build -c Release --no-restore`; the built test output tree is over 1 GB, so it is rebuilt per leg rather than passed between them. The live OpenAPI comparison ([`scripts/openapi-live-check.sh`](../../scripts/openapi-live-check.sh)) runs only on `siblings`. No leg pulls an image or sets `XE_REQUIRE_DOCKER_TESTS`: the real-daemon suites are opt-in and skip here, and their wire-shape half runs daemon-free against the fake Docker server. Every project emits **Cobertura** coverage into its own `--results-directory`, because MTP resolves `--coverage-output` relative to it and a shared directory would let concurrent modules overwrite each other's report. The `siblings` leg runs [`scripts/run-backend-tests.sh`](../../scripts/run-backend-tests.sh) `--siblings-only` — the same script as the local gate, so the enrolment rule, the per-project results directory, the concurrency and the **hollow-gate guard** (a `Passed!`/`Failed!` summary must appear, catching a silent green where zero suites enrolled) are one implementation with two callers rather than two copies that drift. The `--maximum-parallel-tests` cap stays at **2** here, passed as `XE_TEST_WIDTH_DEFAULT`, because TUnit otherwise runs every test in parallel and leaves the concurrency level to the .NET thread pool — its docs state no formula and no ceiling — which is what made concurrent modules time out on shared runners. The script's local defaults are higher because they were measured on a 32-core box; raising CI's is a separate, measured change. `fail-fast: false`, so one red leg does not cancel the evidence from the others. Each leg uploads its reports as **`backend-test-results-<leg>`**.
- **`build-and-test` (ubuntu-latest)** — the merge gate over every `backend-tests` leg, and the job that must keep this exact id: `build-and-test` is the required status check configured on `develop`'s branch protection, and a matrix job reports as `backend-tests (siblings)`, which can never satisfy it. It downloads every leg's artifact unmerged, then cross-checks before merging — the sibling reports number one per enrolled project minus the batched module (re-derived from the solution, not hard-coded), each shard leg produced one Cobertura report per line of its `units.txt`, and the group indices parsed from every leg's unit names, sorted, equal `0`…`GROUPS-1` exactly. That last check is the one that proves the shards **partition** the module — every group run, and run once. Checking only for duplicates would pass a run that silently *skipped* groups (three legs dividing by four leave four groups unrun), and [`scripts/merge-cobertura.py`](../../scripts/merge-cobertura.py) can see neither failure: it unions by `(filename, line)`, so a gap and an overlap both merge to a perfectly plausible percentage. It then merges the reports without double-counting shared source lines and enforces the floor in [`scripts/backend-coverage-baseline.txt`](../../scripts/backend-coverage-baseline.txt) — currently **90.50**.
- **`client-react` (ubuntu-latest)** — pnpm + Node 22, the `global.json` SDK, a .NET 8 runtime, and the restored pinned repository tools; then `install --frozen-lockfile`, `openapi:check`, `licenses:check`, **`pnpm run validate`** (`lint` → `knip` → `signalr:check` → `depcruise` — not bare `lint`), `test:coverage:check`, `test:tooling`, `build`, and `pnpm audit --prod --audit-level=high` in order. `spellCheck` exists as a script but is **not** a gate. A clean local clone must run `dotnet tool restore --tool-manifest dotnet-tools.json` before `licenses:check`.

#### Measuring what the daemon-free backend legs cost

The four `tests-*` legs used to pre-pull three digest-pinned images and set `XE_REQUIRE_DOCKER_TESTS=1`; they now
pull nothing and set nothing. Nobody has measured the difference yet, because **no CI run of either shape exists
to compare**: nothing has been pushed since the External Apps merge that introduced those steps, so the newest run
on the remote predates them. The comparison is therefore a recipe, not a number, and becomes available once a run
exists at a commit before this change and one after it.

Find the two runs with `gh run list --workflow build-and-test.yml --json databaseId,headSha,createdAt,conclusion
--limit 50`, preferring the same trigger type so the matrix shape is comparable. Per-job wall clock comes from
`gh run view <id> --json jobs -q '.jobs[] | {name, startedAt, completedAt}'`, and the individual steps from
`gh api repos/{owner}/{repo}/actions/jobs/{job_id} -q '.steps[] | select(.name | test("Pre-pull")) | {name,
started_at, completed_at}'` on each non-`siblings` leg. Report both figures: the per-leg wall clock, which is what
a developer waiting on the gate feels, and that duration summed across all four legs, which is what the billed
minutes reflect, since every leg paid the pull independently and in parallel. Expect the seconds to be small. The
point of the change was never the seconds — it was removing two external dependencies from a gate whose own
comments already declared that trade-off, so a registry rate-limit or outage can no longer fail the backend
matrix.

For scale only, and explicitly **not** a CI measurement: on the maintainer's development box on 2026-09-12, a
forced-cold pull of the pinned redis digest took 2.8 s and of the pinned busybox digest 1.6 s, over an
already-warm path to the registry. A hosted runner's cold network is a different and unmeasured one, so treat
these as an illustrative floor rather than as an estimate.

Exact-pinned React Doctor is a developer-invoked advisory (`pnpm run doctor`), not a fifth CI stage and not part of
`pnpm run validate`. Knip remains the strict unused-surface no-growth gate, dependency-cruiser remains the architecture
gate, and the production audit remains the vulnerability gate.

One design choice worth preserving in the file: **`TZ=Europe/Berlin`** on the backend test step (a non-UTC zone deliberately exposes time-zone bugs; the comment cites `CapabilityReporterTests`). Note that `TZ` is a **Unix-only** mechanism in .NET (`TimeZoneInfo.Unix.NonAndroid.cs` reads it; the Windows implementation resolves the zone from `kernel32!GetDynamicTimeZoneInformation` and reads no environment variable). It therefore cannot be reproduced on a Windows packaging machine by setting a variable — the deprecated `package-tester-win.ps1` instead **required** the machine's own time zone to be non-UTC, throwing before the test leg if the current offset is `+00:00` and pointing at `tzutil /s`, with `-AllowUtcTestTimeZone` to accept the reduced coverage.

**`e2e.yml`** runs on manual dispatch, or on a `develop` PR opted in with the `run-e2e` label — it is deliberately not a blocking merge gate, since it builds the SPA in-fixture and needs Playwright browsers. E2E is otherwise a manual lane: [`scripts/run-e2e-local.sh`](../../scripts/run-e2e-local.sh), or the raw commands with `-p:RunE2ETests=true`.

### Release-path gates

Two gates ride the packaging path rather than any test suite:

- **Release-notes generation** — git-cliff renders `RELEASE_NOTES.md` from conventional commits between the previous `v`-prefixed tag and HEAD (config `cliff.toml`), and the notes are fed to `vpk pack --releaseNotes`. `package-tester-win.ps1` downloads a **checksum-pinned** git-cliff and invokes it directly; it does **not** call `scripts/generate-release-notes.sh`. See [Hosting & Deployment](11-hosting-and-deployment.md).
- **SPA-build-required publish gate** — the `GuardNodeReactBuildPresentOnPublish` MSBuild target **fails a publish whose React `dist/` build is missing**, so a packaged build can never ship a blank page. This one is enforced by MSBuild, so it holds on every publish path including a hand-run `dotnet publish`. Build the SPA (`pnpm run build`) first. See [Hosting & Deployment](11-hosting-and-deployment.md).

## Coverage gates

- **Backend**: enforced. Every `backend-tests` leg emits Cobertura reports — one per sibling project, one per
  namespace group on each shard leg — and the `build-and-test` job merges them all;
  [`scripts/merge-cobertura.py`](../../scripts/merge-cobertura.py) merges them (deduplicating source lines that
  appear in more than one report) and fails the job if merged line coverage falls below the value in
  [`scripts/backend-coverage-baseline.txt`](../../scripts/backend-coverage-baseline.txt), currently **90.50**.
  The baseline file is the single place to change that number — raise it when coverage rises; lowering it is a
  reviewed decision, not a way to make a red build green. The merged XML and the TRX files are retained as the
  per-leg `backend-test-results-<leg>` artifacts, so a failure can be inspected rather than re-run blind.
- **Frontend**: Vitest v8 coverage. Thresholds are enforced only by the `test:coverage:check` script, which sets
  `VITEST_COVERAGE_CHECK=true`. The thresholds live in the `coverageThresholds` constant in
  [`XE-Local-AI-Engine.Client.React/vite.config.ts`](../../XE-Local-AI-Engine.Client.React/vite.config.ts) — read them
  there rather than here: the comment beside them marks it a **ratchet**, raised as coverage grows and never lowered to
  make a red run green, so any number quoted in this page goes stale on the next raise. Generated, locale, test, and
  route-tree files are excluded by the `coverage.exclude` configuration.

## RC evidence requirements

The README's "RC readiness status" section is the contract: **do not mark release or documentation work complete until matching validation evidence is available.** Required evidence:

- the release workflow's (or, for a manual rehearsal, the deprecated packager's) frontend, backend, vulnerability, and package-gate transcript,
- a clean default `scripts/lint-release-scripts.sh` result, including the mandatory Pester suite,
- a non-vacuous `scripts/run-e2e-local.sh` result with no exit-75 contamination,
- a passing `scripts/run-gpu-smoke-local.sh` run on a GPU box — the only evidence that the GPU actually
  did the work rather than a silent CPU fallback; an exit 5 is an infrastructure abort in which nothing
  was judged, so it is not evidence either way and the run must be repeated,
- generated schema/sample-manifest validation, including a clean `openapi:check`,
- pinned runtime binary and package checksums (llama.cpp release pins; see [Local Runtime & Providers](03-local-runtime-and-providers.md)),
- the matching `v<version>` source tag on the exact packaged commit,
- a real-Windows smoke transcript for the exact generated `Portable.zip`,
- the generated release assets and their checksums, pushed source-tag verification, and
- confirmation that `prepare-release-draft` uploaded and remotely verified the Windows Portable ZIP, Linux AppImage,
  both OS feeds, checksums, release manifest, and detached SPDX envelope in one draft, and that the protected
  `publish-release` job re-verified and promoted that unchanged draft without rebuilding or replacing assets.

This baseline documentation review does not assert that those release artifacts or transcripts exist,
were retained, or are available to the recipient.

Two things that **cannot** be proven in WSL2 or on a headless runner and require target-OS evidence
for an RC claim: the no-orphan design (terminal/console close reaps the `llama-server` child) and the
Windows Job Object hard-kill path. Both require a real desktop with a model loaded; without the matching
retained transcript their operating status is unknown. See [Hosting & Deployment](11-hosting-and-deployment.md).

## Maintainer checklist

- Use `--treenode-filter`, never `--filter`, when targeting individual MTP tests.
- New EF migration → add a `<Name>MigrationTests.cs` in `Client.Persistence.Tests`, built on the shared `Testing/MigrationSchemaProbe.cs`. Every migration must be named by a test; prefer a dedicated file (all but three have one) so a schema regression fails in a file named after its migration.
- New or changed tool schema → run the schema/compiler unit tests and the live `run-tool-grammar-smoke-local.sh`; the negative control is required evidence, not optional diagnostics.
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
- [Code Organization Conventions](16-code-conventions.md)
- [Writing Tests](17-writing-tests.md)
- [Technical/Security Architecture Dossier](../audits/technical-security-architecture/README.md)
- [Home](Home.md)
