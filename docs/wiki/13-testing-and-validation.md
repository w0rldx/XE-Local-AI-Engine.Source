# Testing & Validation

> Reviewed: 2026-10-07 · Code-grounded.

**What this page covers.** How XE Local AI Engine is tested and what counts as "validated": the test projects and
their stack (TUnit on Microsoft.Testing.Platform for .NET, Vitest for the React client), the local gate commands, the
CI workflows and the coverage gates. Two invariants matter most: a change is judged by the Release-configuration
backend gate `scripts/run-backend-tests.sh` plus the frontend `pnpm run acceptance`, and a test run that overlapped a
build is void. The gates live in `scripts/` and `.github/workflows/`.

**Read this if you are** changing a gate script or CI workflow, debugging a red or contaminated test run, or deciding
what evidence a change or release candidate needs. **Skip to** [Validation commands](#validation-commands) for the
commands to run; the **CI job list** is in [Continuous integration](#continuous-integration) and the thresholds in
[Coverage gates](#coverage-gates); **related pages:** [Writing Tests](17-writing-tests.md), [Code Organization
Conventions](16-code-conventions.md).

## Contents

- [Test topology](#test-topology)
- [Validation commands](#validation-commands)
- [Continuous integration](#continuous-integration)
- [Coverage gates](#coverage-gates)
- [RC evidence requirements](#rc-evidence-requirements)
- [Maintainer checklist](#maintainer-checklist)
- [Related pages](#related-pages)

This page is the contributor map of how XE Local AI Engine is tested and what counts as "validated". For *how to write a new test* — which project it belongs in, the fixture patterns, the parallelism keys, the per-kind recipes — see [Writing Tests](17-writing-tests.md), whose [Test principles](17-writing-tests.md#1a-test-principles) section states the independence, self-validation and mocking rules every suite on this page is held to. It covers the test-project topology (backend integration, AI/agent, persistence + migration, Playwright E2E, plus the FakeOllama and Client.Testing support libraries), validation commands and standalone runners, the tracked GitHub Actions gate design, and the RC evidence bar a maintainer must clear before claiming release/doc work is done. For *what each suite asserts about a subsystem*, follow the per-subsystem links — this page owns the harness, not the features.

Repository tests and scripts are evidence that controls can be exercised; their presence is not proof
that a particular deployment or release ran them, passed them, retained the output, or made that output
available to an auditor. Operational evidence must be identified separately rather than inferred.

Test stack at a glance: **TUnit** on **Microsoft.Testing.Platform (MTP)** for the three unit-test projects, **NSubstitute** for mocks, **Microsoft.Playwright + TUnit.Playwright** for browser E2E, and **Vitest (v8 coverage)** for the React client. `global.json` pins `10.0.401` with `rollForward: latestPatch` (only the patch digit may move, so the SDK stays inside the `10.0.4xx` feature band; CI's `actions/setup-dotnet` reads the same file and honours `rollForward`, so local and CI resolve the same band) and `"test": { "runner": "Microsoft.Testing.Platform" }`, so the whole repo runs under MTP, not VSTest.

> ⚠️ MTP gotcha (repo-wide): filter by `--treenode-filter`, NOT the legacy VSTest `--filter`. The repository's TUnit/MTP runners and examples support only the tree-node form.

## Test topology

| Project | Kind | Framework | What it covers | Subsystem page |
|---|---|---|---|---|
| `XE-Local-AI-Engine.Tests` | Backend integration (in-process host via `TestServerWebAppFactory`) | TUnit + NSubstitute | The whole node host: endpoints, hubs, auth, chat, agents, scheduler, model-fit, capacity, MCP, providers, memory, shutdown, and development mode | [Architecture](01-architecture-overview.md), [API & Hubs](09-api-and-hubs.md) |
| `XE-Local-AI-Engine.AI.Agent.Tests` | Unit/component for the agent runtime | TUnit + NSubstitute | MAF/MEAI wiring: `Chat/`, `Eval/`, `Invocation/`, `Tools/`, plus project smoke tests | [Agent Mode](04-agent-mode.md), [Chat](05-chat.md) |
| `XE-Local-AI-Engine.Client.Persistence.Tests` | Persistence + EF migration tests | TUnit | SQLite stores with selected encrypted fields, AEAD cipher, migration tests — **most migrations have their own `*MigrationTests.cs`, and `MigrationChainTests` guarantees that every declared migration in both the chat and identity chains is applied from an empty database and recorded in its history table** (`ls Client.Persistence/Migrations/*.cs` minus `.Designer.cs`/`*ModelSnapshot.cs` is the current count) — and the `NegativeFence/` compile-fence | [Data & Persistence](08-data-and-persistence.md) |
| `XE-Local-AI-Engine.Tests.E2ETests` | Browser E2E | Playwright + TUnit.Playwright | Real Chromium against the in-process host serving the real built React SPA (one `*E2ETests.cs` per surface: Chat, Agents, Scheduler, Models, NodeSettings, Dashboard, Graph Workflows, Development Workflows, Work Sessions, Live Transcription, smoke, viewport, …; `ls XE-Local-AI-Engine.Tests.E2ETests/Tests/*E2ETests.cs` is the current inventory) | [React Client](10-react-client.md), [Hosting](11-hosting-and-deployment.md) |
| `XE-Local-AI-Engine.Testing.FakeOllama` | Support library (not a test suite) | — | In-memory fake Ollama HTTP server + deterministic embeddings, so backend tests never need a real model runtime | [Local Runtime & Providers](03-local-runtime-and-providers.md) |
| `XE-Local-AI-Engine.Testing.FakeDocker` | Support library (not a test suite) | — | In-memory fake Docker Engine API (loopback HTTP), so the container and External Apps tests need no daemon | [External Apps](23-external-apps.md) |
| `XE-Local-AI-Engine.Client.Testing` | Support library | — | Shared host fixtures for the test projects | [API & Hubs](09-api-and-hubs.md) |

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

`XE-Local-AI-Engine.Tests/Architecture/` holds the rules that fail the gate instead of a reviewer's memory. They are ordinary TUnit tests inside the backend suite, so the gate below runs them; there is no separate runner. `ls XE-Local-AI-Engine.Tests/Architecture/*.cs` is the current inventory. Older members pin placement (`PlacementConventionTests`, `ServiceModelColocationTests`, described in [Code Conventions](16-code-conventions.md)) and assembly dependency direction (`LayerDependencyTests`). **`FilePlacementConventionTests`** generalises the file-identity half of that: a production `.cs` file declares one top-level type unless it is a family file, an `IFoo`+`Foo` pair, one type beside only enums and delegates, or an `I*Store.cs`/`I*Service.cs` contract bundle, against `Architecture/FilePlacementAllowlist.txt` — `file|count`, shrink-only in both directions, `XE_FILE_PLACEMENT_SHRINK=1` to regenerate it downward and then fail so the regeneration is never a green run. It counts brace depth over comment- and literal-stripped source, so a nested type is never read as a top-level one (the mistake that made every regex inventory of this repository over-count) and C# quoted as test data is not a declaration. The same scan holds a multi-type family file to data: a class or struct there that declares a non-private, non-override method fails `FamilyFiles_DeclareOnlyDataShapedTypes`, with no allowlist. The 2026-09-16 static-quality slices added four more guards and one shared helper:

- **`EndpointConventionTests`**: one endpoint type per file named after it, no plural `*Endpoints.cs` allowed to declare one (the grandfathered groupings were split in S7f-1 and `PluralEndpointFiles` is now empty), every endpoint `sealed` through ArchUnitNET, and every route derived from `LocalApiRoutes` rather than typed as a literal. Reflection over the compiled host decides *which* types are endpoints; a comment-stripped scan of the whole `Client` project locates each declaration, so an endpoint declared outside `Endpoints/` is found and rejected rather than escaping the filename rules.
- **`EndpointDependencyTests`**: what an endpoint may take as a constructor dependency. It holds for every endpoint in the host and has no exemption list. Generic arguments, array element types and `Nullable<T>` are walked recursively, so a forbidden type wrapped in an allowed generic still counts. The same fence covers every SignalR hub and every DI-constructed class under `Endpoints/`, since both are the request edge by another name; background services and the local-model proxy forwarder are deliberately outside it.
- **`ApplicationDbContextFenceTests`**: operator decision D5 — no `DbContext` in `Client.Application`. Type-based through NetArchTest, so it sees a body resolution inside an async state machine and a lambda parameter inside a display class, not only a constructor parameter, and it asserts that it still sees each of those shapes against a live holder. `Architecture/DbContextUserAllowlist.txt` is the authoritative list of remaining holders, split into a `permanent` section (composition root, migration bootstrap, the maintenance jobs whose subject is the file or the schema) and a `migrate` section grouped by feature area; shrink-only in both directions, so a stale entry fails as loudly as an unlisted holder. Rule and rationale: [Code Conventions](16-code-conventions.md).
- **`ThirdPartySdkBoundaryTests`**: the fence keeping `OllamaSharp`, `Docker.DotNet`, `Azure.*` and `Microsoft.Identity.Client` out of the host, application, persistence, agent and contracts layers, with one shrink-only allowlist per SDK. Each list has a second test that fails on an entry whose file no longer references that SDK, so an exemption cannot outlive what it excused.
- **`ModelTrustAuthorityGuardTests`**: keeps `IModelTrustResolver` the single trust authority. A comment-stripped text scan of the host and application projects allows `CodexModelCatalog.IsCodexModel(` only in the routing rule, the trust resolver, the capability matrix pick and the deployment-name reservation, and refuses any member named `IsOutsideTrustBoundary`; the allowlist is compared exactly, so a file that stops calling it fails too.
- **`EndpointAuthorizationPolicyTests`**: deny-by-default authorization, read from each route's effective metadata rather than from FastEndpoints' own bookkeeping. Every FastEndpoints endpoint resolves to the `NodeOperator` policy or appears in the pre-authentication allowlist (four entries as measured 2026-09-16, with its own stale-entry check); every SignalR hub route requires `NodeOperator` under the configuration that maps it, with the Development-gated hub's absence proved separately under `EnableDevelopmentMode = false`; and the hand-mapped minimal APIs plus the inbound MCP route carry their named policies. The guard's teeth are `ConfiguratorCanaryProbeEndpoint`, a permanent endpoint that calls neither `Policies()` nor `AllowAnonymous()`: the global configurator in `Program.cs` is its only protection, so deleting that one line reds this test by name while every endpoint carrying its own redundant `Policies()` call stays green. See [Security & Privacy](12-security-and-privacy.md) §3.2.
- **`TestCategoryConventionTests`**: the test-classification guard. Every class with `[Test]` methods carries exactly one `[Category(TestCategories.Unit|Integration|ExternalInfra)]` — checked by reflection in this assembly, which is the only thing that sees which `CategoryAttribute` the class actually bound to, and by source scan in the two sibling test projects, which are not referenced from here. A `Unit` class may reach no Integration mechanism: only the primitives (host factory, `SqliteConnection`/`UseSqlite`/`EnsureCreated`, the fake servers, a real socket, a child process) are written down, and every helper that reaches one — directly or through another helper — is derived at run time, so the marker list maintains itself and needs no allowlist.
- **`ConfigureAwaitPolicyTests`**: both halves of the contextual-`ConfigureAwait` rule ([Code Conventions](16-code-conventions.md)). The token is refused outright in every project that is not a library, and every awaited call inside one that is must carry a configuration — checked by counting per statement rather than searching, so the rule needs no type information. The project split is derived: the library list is closed and everything else in the solution is an application project, so a project added to the solution is governed the day it appears. The one exemption is the guard's own file, which quotes the token as test data, and a test fails if that path stops resolving.
- **`Support/SourceCommentStripper`**: the shared string-aware comment stripper the source-scanning guards call, replacing the regex each had grown. A regex that erases everything after `//` also erases it inside a string literal, so a banned reference written after a URL on the same line passed a guard that never saw it. `ContainerBridgeLayeringArchitectureTests` and `ExternalAppsRuntimeIsolationArchitectureTests` both route through it now, and both carry string-shaped controls that the old regex loses.

Two more read source because the shape they pin leaves no mark in IL, and both fence every solution project plus the C# under `tools/`: **`PrimaryConstructorConventionTests`** refuses a primary constructor on a class or struct, with no exemption list, and **`PositionalRecordConventionTests`** refuses a `record` of class kind that carries a parameter list unless `Architecture/PositionalRecordAllowlist.txt` lists that declaration — grouped by the reason `required` cannot apply to those types, one line per declaration, and shrink-only in both directions, so an entry that no longer matches a positional record fails as loudly as an unreasoned one. **`RequiredMemberSerializationTests`** closes the same rule from the other side by reflection over the product assemblies: a `required` member may not also be conditionally omitted from the JSON. Rules and rationale: [Code Conventions](16-code-conventions.md).

**`CommentBudgetConventionTests`** fences the same roots through `Support/EnforcedSourceFiles`, and ratchets the nine comment and XML-doc rules of [Code Conventions](16-code-conventions.md). Five are length budgets: a `<summary>` over 240 characters, a `<param>`/`<returns>`/`<value>` over 160, a `<remarks>` over 5 content lines or 600 characters, a `///` block over 15 lines, and more than 2 consecutive own-line `//` lines. Four are shapes that satisfy a budget while being wrong: a block carrying a second `<summary>` or `<remarks>` (`multi`), a `<seealso>` carrying a prose body (`seealso`), a block with `<remarks>` but no `<summary>` or `<inheritdoc>` (`nosummary`), and a block that is not well-formed XML (`xml`) — csc parses one too, since `Directory.Build.props` sets `GenerateDocumentationFile` to make IDE0005 run, but it reports only well-formedness (`CS1570`) and not any of the other eight rules. `Architecture/CommentBudgetAllowlist.txt` records a COUNT per file and rule rather than a per-item key, because the items are prose and a count is the only key that rewording cannot break; it is shrink-only in both directions, and `XE_COMMENT_BUDGET_SHRINK=1` regenerates it downward and then fails so the regeneration is never a green run. Test projects are ratcheted exactly like production; production measures zero for the four shape rules.

`LayerDependencyTests` grew in the same slice: the test and support projects now have their exact `ProjectReference` sets pinned alongside the production ones (seven entries as of 2026-09-16, keyed by repository-relative csproj path because the negative-fence probe nests inside its parent project's directory), cross-checked against the solution's `/Tests` folder membership so a newly added test or fixture project cannot ship unpinned.

Every one of these asserts a **non-vacuity floor**, a minimum number of types, files or list entries scanned, before the rule itself runs, because a marker type resolving to the wrong assembly, an allowlist emptied by a bad merge and a directory walk that found nothing all look identical to "scanned everything, found no violation" in a pass/fail summary. Floors are measured through the guard itself, never from a grep over source; the two populations differ. A guard whose scan target is missing reports **skipped** with a reason rather than passing.

The rest of the folder, one rule each:

- **`HostServiceResolutionTests`**: the other half of the `EndpointDependencyTests` fence. A host class may not pull a forbidden type (a persistence store, a concrete provider's contract, the EF Core API, the Docker SDK) out of an `IServiceProvider` by hand (`GetRequiredService<T>`, `GetService(typeof(T))`, `CreateInstance`) either; the composition root is excluded by name. Rule: [Code Conventions](16-code-conventions.md).
- **`ConcreteStoreConstructionArchitectureTests`**: a concrete store that a publishing decorator wraps (`DevWorkflowStore`, `GraphWorkflowStore`, and `AgentWorkSessionStore` pre-emptively) is named only in the DI module that registers it, so no caller can resolve the undecorated store and skip its events.
- **`ProviderMapCoordinationArchitectureTests`**: the model-provider map is reached only through `ICoordinatedModelProviderMapStore`; outside the facade and its DI module no production file in the host or application project names `IModelProviderMapStore` or `ModelProviderMapStore`, and that module must register the shared lock domain and the coordinators.
- **`HostPatchApplyReachArchitectureTests`**: `INodePatchApplyService` writes to the operator's own folders, so only a fixed allow-list of files (declaration, models, implementation, DI module, the operator endpoint pair and its mapper, `LocalApiRoutes`) may name it. A new caller fails and is added deliberately, which is the moment to ask whether it acts for the operator or for the model. Feature: [Agent Mode](04-agent-mode.md), [Security & Privacy](12-security-and-privacy.md).
- **`ContainerBridgeLayeringArchitectureTests`**: the container-runtime layer (`Services/Containers/`) must not name `Services.ExternalApps` or any `ExternalApp*` type. External Apps is the bridge's first user, not its owner; the token seam points one way only (`IContainerBridgeTokenVerifier` under Containers, its implementation under ExternalApps). See [External Apps](23-external-apps.md).
- **`ExternalAppsRuntimeIsolationArchitectureTests`**: External Apps code must not call Development Mode's container verbs (`CreateContainerAsync`, `InspectContainerAsync`, `ExecuteAsync`). They compile from an `IContainerRuntime` reference because the application-container runtime derives from Development Mode's client, but they create a container with no environment, ports, network, healthcheck or restart policy. Both this guard and the previous one read comment-stripped source through `Support/SourceCommentStripper`; string literals stay visible.
- **`SandboxSubstrateSelectionArchitectureTests`**: every sandbox requirements declaration the engine owns is asserted against the exact set of backends allowed to serve it, evaluated against each backend's maximum capability set, so a feature declares requirements and never names a backend (ADR 0007). See [Security & Privacy](12-security-and-privacy.md).
- **`ToolRelevanceOfferArchitectureTests`**: `new ListToolsFunction(` appears in exactly one production file, `InvocationAgentFactory`. The send-time tool-relevance hop only trims an array that carries `list_tools`, so a second construction site would make orchestration-participant and sub-agent arrays trimmable with no way to get a hidden tool back. See [Agent Mode](04-agent-mode.md).
- **`ProviderTelemetryWrapGuardTests`**: every background `provider.CreateChatClient(...)` under `Client.Application/Services` ends in `.WithProviderTelemetry()`, with `ModelRoutingLocalChatClient` the one named exception. Rule: [Code Conventions](16-code-conventions.md).
- **`ExceptionInnerExceptionContractTests`**: every production exception type that declares a `(string message, Exception innerException)` constructor is constructed by reflection and must hand both back. Most of those constructors have no caller, so compilation alone would let one that drops its inner exception ship green.
- **`SqliteFileProbeConnectionStringGuardTests`**: every SQLite connection string the backend test projects build is the bare `Data Source=<path>` that `SqliteFileProbe` needs, or is a named exception with its reason. See [Writing Tests](17-writing-tests.md).
- **`RuntimeLicensePackagingTests`**: the runtime version in `eng/ReleaseVersion.props`, the publish-profile shape and the license and notice files each payload carries. See [Hosting & Deployment](11-hosting-and-deployment.md) §6.
- **`RuntimeStatePackagingTests`**: the node's runtime-state directories under the client project (`development/`, `generated-images/`, `logs/`, `backups/`, `dp-keys/`, `models/`, `uploaded-files/`, `agent-home-state/`, `knowledge-base/`) are removed from every Web SDK item type in the client csproj and ignored by git, so neither a publish nor a commit can carry them. The test's own list is the authority.

Guards that live outside `Architecture/`, beside the feature they protect:

- **`Hosting/SignalRProxyPathDriftTests`**: every hub `Program.cs` maps is in `XE-Local-AI-Engine.Client.React/config/signalr-proxy-paths.json` and nothing else is. This is not the hub check in `scripts/docs-inventory-check.py`, which only requires each hub to be named in the [API & Hubs](09-api-and-hubs.md) table. The frontend `signalr:check` enforces the same pair from the other side.
- **`ApiFoundation/EndpointExceptionMappingSourceGuardTests`**: `catch` blocks in the Training, Benchmarks, WorkSessions, DevelopmentWorkflows and Development endpoint folders are limited to reviewed per-file sites; everything else is answered by the global exception handlers. See [API & Hubs](09-api-and-hubs.md).
- **`Sandbox/SandboxContractGuardTests`**: the sandbox contract namespace references no `Docker`, `OpenSandbox`, `Grpc` or `Hyperlight` type. See `docs/security/sandbox-threat-model.md`.
- **`Documentation/McpToolsReferenceDriftTests`**: the registered inbound MCP tools and their scopes match `skills/xe-local-ai-engine/references/mcp-tools.md` exactly, prose counts included.
- **`Onboarding/OnboardingTourKeyDriftTests`**: the tutorial persistence keys and progress-storage prefix in the React onboarding feature match the constants the E2E host factory and its assertions use, so the browser fixture keeps representing a returning user.

The remaining `*GuardTests` classes (`LoopbackBindGuardTests`, `SkillImportArchiveGuardTests`, `CustomToolSsrfGuardTests`, `ImageModelPathGuardTests`, `WorkspacePathGuardTests`, `SandboxJailPathGuardTests`, `DevelopmentProfileGuardTests`, and `IdleStreamGuardTests` in `XE-Local-AI-Engine.AI.Agent.Tests`) are behaviour suites for runtime guards, not rules on how code is written; the guard each one exercises is described on its feature's page.

### `XE-Local-AI-Engine.Tests` — backend integration

This is the heaviest suite and the heart of validation. `TestServerWebAppFactory.cs` spins up the real node host in-process:

- It builds the app through `Program.CreateAppAsync` and serves it on `TestServer` — deliberately **not** `WebApplicationFactory<Program>`, whose entry-point resolution leaks every built host for the process lifetime (docs/agent-knowledge.md §1). It runs under environment `Testing`, serialises host startup behind a private static `HostStartupLock` semaphore in `XE-Local-AI-Engine.Tests/TestServerWebAppFactory.cs` (TUnit runs classes in parallel; the host bootstrap is not re-entrant), and exposes `CreateNodeAccessToken()` / `AddNodeBearerToken(request)` helpers to mint an admin JWT for the loopback admin API. Per-test host tweaks go through the `ConfigureAdditionalTestServices` / `AdditionalConfiguration` / `EnableDevelopmentMode` / `EnvironmentName` init-properties (there is no `WithWebHostBuilder`); `EnvironmentName` is what exercises production-only middleware such as the rate limiter, which the `Testing` environment skips. Suites whose tests are read-only or `Guid`-isolated share **one host per class** via `[ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]` instead of building one per test — see [Writing Tests](17-writing-tests.md) for when that is safe.
- Unless `RUN_LOCAL_INTEGRATION=true`, `TestServerWebAppFactory` starts a `FakeOllamaServer` seeded with `["qwen3.5:0.8b", "qwen3-embedding:0.6b"]` and wires the host's provider HTTP base to it — so the suite exercises the real provider/abstraction seam with a fake backend instead of a live model. Setting `RUN_LOCAL_INTEGRATION=true` opts into a real local runtime for fidelity runs.

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

A third lane, `release-contract-tests`, runs
[`scripts/run-release-contract-tests.sh`](../../scripts/run-release-contract-tests.sh) whenever anything under
`scripts/`, `publish/` or `.github/workflows/` differs from the merge-base of `HEAD` with `develop` (`origin/develop`
when there is no local `develop`). Committed, staged, unstaged, deleted and untracked files all count. When that
comparison cannot be made (no develop ref, a shallow clone without the merge-base, not a git checkout) the lane runs
and says why; otherwise a `>> Release contract tests: skipped` line says why it did not. On a GitHub Actions runner
the lane is skipped, because the `release-contracts` job runs the same tests. `XE_GATE_CONTRACT_TESTS=run` or `skip`
overrides the detection (default `auto`). The lane is independent of `NO_BUILD` and `--siblings-only`, because it
tests scripts rather than assemblies, and it runs unguarded.

For a memory-constrained development machine, use
`XE_TEST_PROFILE=low-memory scripts/run-backend-tests.sh`. The same gate runs project lanes serially and
defaults `JOBS`, `PAR` and `XE_TEST_WIDTH_DEFAULT` to 1. Explicit overrides still win, including per-project
widths. The standalone memory-safe runner also accepts the profile. Normal and CI defaults are unchanged;
the profile trades elapsed time for lower concurrent memory demand without dropping tests or guards.
`low-memory` is an explicit choice, not the default on a loaded box — it serializes lanes even when RAM is free.

When `XE_TEST_PROFILE` is unset, `JOBS` is not a fixed default either: `scripts/lib/test-sizing.sh` (sourced by
both scripts) reads `MemAvailable` from `/proc/meminfo` and computes
`JOBS = clamp(1, default, floor((MemAvailable − SIBLING_RESERVE_GB − HEADROOM_GB) / BATCH_GB))`, with
`BATCH_GB=1.5`, `SIBLING_RESERVE_GB=5` and `HEADROOM_GB=4` — all three measured against this repo's own runs, not
modeled, with the measurements next to the constants in that file. Today's numbers: 0.3–1.5 GB per batch of the
batched module, and `Client.Persistence.Tests` 3.2 GB at its pinned sibling width (11.6 GB only at TUnit's
unpinned default, which the gate never uses) — do not quote the older "~15 GB per lane" rationale in
`with-build-lock.sh`'s header as current; it predates the 2026-08-15 leak fix and the test-perf program. The
computed `JOBS` is printed as a `>> Sizing: MemAvailable=… GB → JOBS=… (default …)` line in the gate log, so the
log is the evidence for what ran. Explicit `JOBS`, `PAR`, `XE_TEST_WIDTH_*` and `XE_TEST_PROFILE=low-memory` all
still win over the computed value; unknown memory (no `/proc/meminfo`, e.g. macOS) falls back to the measured
default unchanged.

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
means the lock timed out and nothing ran (default timeout 3600 s, `BUILD_LOCK_TIMEOUT` to override). Exit `75`
means **CONTAMINATED, result void, rerun required**.

The lock's shared-path resolution lives in `scripts/lib/build-lock-common.sh`, sourced by both `with-build-lock.sh`
and `scripts/build-lock-status.sh` so the two can never disagree about where the lock lives. The holder writes
`.tmp/build.lock.owner` (`pid= started= cwd= worktree= cmd=`), truncated on exit; a process that has to wait
registers `.tmp/build.lock.waiters/<pid>` (`pid= since= cwd= worktree= cmd=`) before blocking and removes it on
acquisition or exit. While waiting it prints a line once a minute — holder, age, command, and how many waiters are
ahead — instead of staying silent for the whole timeout.

`scripts/build-lock-status.sh [--json]` shows the same picture on demand, read-only: holder (pid, liveness via
`kill -0`, age, worktree, cmd), stale-owner detection (record present, pid dead → lock reported free), waiters
oldest-first, and, when the holder is running the gate or the runner, its progress — the newest
`.tmp/backend-test-results/**/gate.log` carrying a `>> ` phase line or a batch pass/fail line — plus current
`MemAvailable`. Sample human-format output:

```
lock:      $REPO/.tmp/build.lock
holder:    pid=<pid> alive=yes worktree=<worktree> age=00:00:58
cmd:       <holder command>
waiters:   0
memAvailable: 23.3 GB
```

Check the status script before queueing a gate rather than waiting blind: an agent that can see who holds the lock
and how far along they are makes a better call than one that waits out the full timeout. `build-lock.test.sh` and
`test-sizing.test.sh` under `scripts/tests/` are self-checks for the lock and the sizing formula, auto-enrolled by
`scripts/run-release-contract-tests.sh` and shellchecked by `scripts/lint-release-scripts.sh`.

```bash
# Backend inner loop — scope by category (see 17-writing-tests.md §1b for what each one means)
dotnet test <project> -c Release --no-build -- --treenode-filter '/*/*/*/*[Category=Unit]'
dotnet test <project> -c Release --no-build -- --treenode-filter '/*/*/*/*[Category!=ExternalInfra]'
dotnet test <project> -c Release --no-build -- --treenode-filter '/*/*/AgentHomeServiceTests/*[Category=Unit]'
```

Four path segments — assembly, namespace, class, test — then one property group in brackets. A class filter goes in
the third segment and combines with the category as shown; several properties go inside **one** bracket joined by
`&` or `|` (`[(Category=Unit)&(Category=Integration)]`), because only one property group per segment is allowed.
`!=` excludes, and it also matches a test that carries no such property at all — harmless now that every class
carries exactly one category, and the guard keeps it that way. Discovery counts come from `--list-tests` against the
built test host executable in `bin/<configuration>/net10.0/`; `dotnet test … -- --list-tests` reports "Zero tests
ran" instead. The category counts sum exactly to the unfiltered count in every project — a class with no
category, or with two, breaks that sum, and `TestCategoryConventionTests` fails the gate when one does.

```bash
# React client
cd XE-Local-AI-Engine.Client.React
pnpm install --frozen-lockfile
pnpm run acceptance  # validate + coverage thresholds + tooling tests + production bundle
```

`acceptance` runs static checks once and then tests and `build:bundle` on the same unchanged source tree.
Its first step, `node:check`
([`scripts/CheckNodeMajor.mjs`](../../XE-Local-AI-Engine.Client.React/scripts/CheckNodeMajor.mjs)), reads the Node
major from the `client-react` job of `build-and-test.yml` and fails on any other major, because a green run on another
major is not CI evidence. `XE_ALLOW_NODE_DRIFT=1` runs anyway, with a warning that the run is not CI evidence.
Standalone `build` still runs the full lint chain before `build:bundle`; the latter alone is not a gate.
Use `pnpm run lint` and `pnpm test` for the focused inner loop.

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
[`scripts/openapi-live-check.sh`](../../scripts/openapi-live-check.sh), which CI's `siblings` leg runs, wraps that
comparison: it runs an incremental Release build of the host under the build lock, starts the host from that output
and runs `openapi:check:live`. `OPENAPI_LIVE_SKIP_BUILD=1` skips the build with a warning, and the host then runs
whatever Release binaries are already there.

### The four standalone runners

All four exist for local, CI-independent validation. The CI gate is `build-and-test.yml` (on `develop` PRs/pushes)
and the immutable-tag-bound `release.yml` (on `v*` tags, which itself calls `build-and-test.yml` before packaging).
These standalone runners exercise additional target-specific checks locally.

- **[`scripts/run-e2e-local.sh`](../../scripts/run-e2e-local.sh)** — opt-in local runner for the Playwright suite. Nothing invokes it automatically; run it by hand before cutting a tester RC. It performs a frozen frontend install plus `pnpm run lint` before the fixture's intentionally bare Vite `build:e2e`, installs Playwright browsers (via `playwright.ps1`, so it needs `pwsh` — a missing `pwsh` is a prerequisite failure, never a skip), and rejects zero-test or missing-summary runs. `--list` enumerates without running; `--filter` accepts a TUnit/MTP tree-node expression; `--skip-browser-install` skips the install step and is only safe when `~/.cache/ms-playwright` already carries a matching Chromium build, which the script verifies before honouring the flag. Exit codes: `0` pass, `1` tests failed or the run was vacuous, `2` a prerequisite/usage error, `75` build contamination (**void; rerun**). No external services needed — FakeOllama plus the in-process host, so no `llama-server` and no `scripts/dev-stop.sh`.
- **[`scripts/lint-release-scripts.sh`](../../scripts/lint-release-scripts.sh)** — shellcheck + PSScriptAnalyzer over the packaging scripts. `publish/package-tester-win.ps1` and `publish/package-rc.sh` are deprecated, reference-only manual packagers now (the release path is the tag-triggered `release.yml`), but this script still gives them static analysis and runs `package-tester-win.ps1`'s [`publish/tests/package-tester-win.Tests.ps1`](../../publish/tests/package-tester-win.Tests.ps1) Pester suite on every default run. A **missing linter or test module exits 2** rather than passing silently.
- **[`scripts/run-gpu-smoke-local.sh`](../../scripts/run-gpu-smoke-local.sh)** — opt-in **live GPU smoke** against a real, locally started node. Nothing invokes it automatically; run it by hand before cutting a tester RC or after touching the inference/runtime path. It owns the AppHost lifecycle (`dev-start.sh` → `aspire wait app` → `dev-stop.sh`), discovers the port from `dev-status.sh --json` (it changes on every restart), and asserts in order: the installed llama.cpp identity, the `IRuntimeDeviceAudit` verdict, a real streamed chat turn, **that the GPU actually did the work** (nvidia-smi utilisation during generation plus a VRAM rise over a pre-host baseline), a real tool call, optionally image generation (`--images`, step `6-image`) and an image edit (step `7-image-edit`: it uploads a generated fixture PNG, runs an img2img job from it and asserts the stored lineage, then runs a negative control, a refused unsupported mode, and fails when no control record was produced), and that eject returns VRAM to baseline. Every step must record a verdict, so "nothing ran" can never read as green. **This is the only gate that proves the GPU did the work** — a correct reply proves nothing, because a CPU fallback answers correctly, just slowly (measured in one local run: GPU 72% / +1199 MiB VRAM versus CPU-fallback 11% / +0 MiB, identical correct answer). Exit codes: `0` pass, `1` the product failed a judged step (always with a `=== Summary ===`), **`5` an infrastructure abort where nothing was judged and no summary prints** (AppHost never became healthy, base URL undiscoverable, auth failed) — so a wrapper can treat 1 as "product says no" and 5 as "fix the machine and re-run"; `3` an instance is already running, `4` could not tell, `2` a missing prerequisite (including a host with no NVIDIA GPU), `75` contamination (**void; rerun**), `130` interrupted. Its refuse-to-pass logic is itself tested without a GPU by `scripts/tests/gpu-smoke.test.sh`.
- **[`scripts/run-tool-grammar-smoke-local.sh`](../../scripts/run-tool-grammar-smoke-local.sh)** — opt-in live compatibility check against a real, non-reasoning, tool-capable `llama-server`. Run it after changing any offered tool schema (including Custom Tools schema compilation) or bumping llama.cpp. It posts the production offer twice: the sanitized form must return 200, while the deliberately unsanitized negative control must still fail with the grammar 400. If the control succeeds, the run is inert rather than green: either the model template bypassed constrained tools or llama.cpp's repetition limit changed and `LlamaGrammarToolSchemaCompatibility.MaxGrammarRepetitionBound` must be re-measured.
- **[`scripts/run-retrieval-eval-local.sh`](../../scripts/run-retrieval-eval-local.sh)** — opt-in **real-model retrieval eval**: runs `RetrievalEvalLiveTests` (Client.Persistence.Tests, category `ExternalInfra`) in Release against real `llama-server` processes for the embedder and each reranker (default bge; `--reranker-preset all` adds Qwen3-Reranker-0.6B and jina-reranker-v1-turbo-en, and `--download` fetches them sha256-verified into `.tmp/retrieval-eval/models`, never into the node data root, which it only reads). It writes `retrieval-eval.json` + `retrieval-eval.md` to the report dir. The test skips unless its own `XE_RETRIEVAL_EVAL_*` variables are set, so the runner requires the JSON: no JSON is a failure, never a pass. A config whose forced rerank degraded or whose reranker failed its score sanity gate is INVALID and fails the run, so it is never quoted as a quality number; the `NEG-*` rows are the negative controls and are judged by their own verdicts. Exit codes: `0` pass, `1` failed/skipped/no JSON/INVALID config/failed negative control, `2` prerequisite missing, `5` a llama-server never became healthy or a download failed, `75` contamination (**void; rerun**), `130` interrupted. Its exit contract is tested without models by `scripts/tests/run-retrieval-eval-local.test.sh`.
- **[`scripts/run-model-matrix-local.sh`](../../scripts/run-model-matrix-local.sh)** — opt-in **live model matrix**: the scenario checks of the 2026-10 model-matrix round, re-run per pinned model through a real local node. The other real-model lanes ask whether a model answers; every finding of that round was about how it answers, and those lanes passed on every model before any fix. Models are pinned in [`scripts/model-matrix/models.json`](../../scripts/model-matrix/models.json) (repo, file, SHA-256, size, product model name, architecture, dense/MoE, how thinking is switched off — `template`, `budget` or `null` for a model that does not think — tool-capable) and grouped in cumulative tiers: `fast` = Qwen3.5-0.8B + 4B, `extended` adds Qwen3.5-9B, Granite 4.1-3B and LFM2.5-8B-A1B, `rc` adds Qwen3.6-35B-A3B and Qwen3.8-27B. A pinned model whose repo is also in the curated catalog must carry `"tested": true` there, with `testedQuant` and `testedSizeBytes` matching its pinned file ([Model fit](07-model-fit.md#the-curated-catalog-lane-primary-recommendation-source)); `ModelCatalogBundledLoaderTests` fails otherwise. An installed file whose hash differs from the pin fails the run; `--download` installs missing models through the node's own Hugging Face path and verifies them the same way. It owns the host lifecycle like the GPU smoke; after stopping its host it stops by PID, and logs, any llama-server its node spawned that outlived the stop (seen: an embedding server started as a turn failed), and touches no other process. Driven by [`scripts/model-matrix-driver.py`](../../scripts/model-matrix-driver.py), which reads the wire from llama-server `/slots` (`n_predict` is the limit the node sent; a `generation_prompt` ending in `</think>` is thinking off). Only a llama-server the node under test spawned counts — one whose parent chain reaches this checkout's `XE-Local-AI-Engine.Client` host — so another checkout serving the same model file stays out of the slot evidence, the placement and RSS figures and the eject check. Before any check the driver reads the node's device audit (`model-fit/hardware-profile?refresh=true`, through the GPU smoke driver's `device_audit`) and refuses with exit `2` when the backend is not the requested `--variant`: `cuda` needs `cuda` without a CPU fallback, `cpu` needs `cpu`. Without a bring-your-own `XE_LLAMACPP_SERVER_PATH` the node runs its managed runtime whatever `--variant` says, so a CPU run on a GPU box needs a CPU `llama-server` supplied that way. The observed backend is recorded in `results.json` and `summary.md`. The audit names the backend the runtime selected; it does not prove the GPU did the work, which stays the GPU smoke's job. **Hard checks** (fail the run): the model loads and a streamed turn completes; with no effort sent the request carries limit = default-effort reasoning budget + answer cap; effort `none` switches thinking off: for a `template` switch the answer carries no reasoning and the request on the wire closes the think block; a `budget` switch (a zero reasoning budget, as on LFM2.5) is invisible on the wire and cannot stop the model writing a short deliberation, which the node files under reasoning, so for it "Say OK." and a one-number sum must each get a non-empty stored answer with no think tags and no `EmptyAnswer` notice, reasoning text allowed; a long-form request with a 64-token limit that ends on the limit carries the `OutputLimitReached` notice, while output above the limit or the notice below it fails (a model that stops by itself below the limit proves nothing: up to three samples are taken, and if none reaches the limit the check is n/a, not a pass); one required tool call is made and answered; a memory-extraction job on a thinking model runs capped and does not stall the next turn, with thinking off checked on the wire for a `template` switch (for a `budget` switch neither `/slots` nor the API shows the job's budget or reasoning, and the check's detail says so); on the GPU, a tool turn at a pinned 4,096 window (the model is ejected before the pin, and a pin refused because a launch of the model is still being admitted is retried) either completes with the `ToolsFiltered` notice or ends with one of the product's two context-window refusals — the tool offer does not fit at the first-round fit, or the request is too large before a later round — the documented limit at 4,096 (which one depends on whether the node has calibrated the model's tool-template overhead, see [Agent mode](04-agent-mode.md)); any other failure text, a provider error, a timeout or a completed turn without the notice fails, and `results.json` records the outcome (`completed`, `refused-at-fit`, `refused-before-round-two`) and the detail whether the tool was called; a MoE model that fits the GPU spawns without expert offload and the running view reports its window and `expertsOffloaded: false` (judged after a completed load turn, whatever its answer text); no `<think>`/`</think>` in a persisted answer; eject releases the process. **Measured** (recorded, never fail the run): three-step tool task k/3, time to first token, tokens/s, VRAM and RSS. Every REST call, hub event and slot task is written to the evidence directory as it happens; the run ends with `results.json` and a model × check grid in `summary.md`. **What it does not prove:** answer quality (it is not a benchmark), GPU use (that is the GPU smoke), template/grammar compatibility (tool-grammar smoke), retrieval, vision, Windows. **Run it** only as the last validation step, never while iterating, and only when the change touches inference, thinking, tool offering, window budgeting or model fit (`fast` at least); before a tester release candidate use `--tier rc`. It is slow, so a change outside those areas does not run it. **Prerequisite, once per checkout:** complete first-run setup and set the external-access profile to `offline`; the lane logs in (`XE_MODEL_MATRIX_EMAIL` / `XE_MODEL_MATRIX_PASSWORD`) and never creates an account. The memory-job and 4,096-window checks change a node setting and a window profile and restore both. Exit codes: `0` pass, `1` a hard check failed, none ran, no results were written, or a `--download`ed file does not match its pin, `2` prerequisite missing (a model absent without `--download`, a file on disk that is not the pinned one, no GPU for `--variant cuda`, a device audit that reports another backend, node not set up), `3` a host is already running, `4` could not tell, `5` infrastructure (host did not start, login or download failed), `75` contamination (**void; rerun**), `130` interrupted. Its gates and verdict are tested without a node by `scripts/tests/model-matrix.test.sh` and `scripts/tests/test_model_matrix_driver.py`.

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

- **`python-quality` (ubuntu-latest)** — sets up `uv` with a pinned version and Python 3.13, then runs [`scripts/python-validation.sh`](../../scripts/python-validation.sh) `--scope full --serial`: `uv sync --locked --all-groups` followed by ruff (`format --check` + `check`), pyrefly, pytest with coverage, and bandit over `tools/training` and `scripts/**`. The pytest leg includes `scripts/tests/test_docs_inventory_check.py`, and the job then runs `scripts/docs-inventory-check.py`, which fails on an inventory member no wiki page names, an agent-knowledge cap, or a broken relative Markdown link or `#anchor` in any tracked `.md`. The tooling config is the **root** `pyproject.toml` + its own small `uv.lock` — deliberately *not* `tools/training/pyproject.toml`, which with its lockfile is the shipped training-runtime manifest (see [ADR 0005](../adr/0005-training-runtime-python-exclusivity-and-project-placement.md) and [Training](18-training.md)). Locally: `scripts/python-validation.sh --scope full`, or `--scope changed` to auto-detect from the diff. The same job then runs [`scripts/docs-inventory-check.py`](../../scripts/docs-inventory-check.py), which re-derives five inventories from the code — SignalR hubs, `LocalApiRoutes` route families, React `features/` directories, numbered wiki pages, solution projects — and fails when one of them is missing from the wiki page that claims to enumerate it.
- **`release-contracts` (ubuntu-latest)** — runs [`scripts/run-release-contract-tests.sh`](../../scripts/run-release-contract-tests.sh) plus `scripts/lint-release-scripts.sh --no-behavior --bootstrap`. Contract discovery is **auto-enrolling** across `scripts/tests`, `scripts/compliance/tests`, and `scripts/performance/tests`, matching `*.test.sh`, `*.test.py`, and `test_*.py` — a new script test needs no workflow edit. The Pester leg of `lint-release-scripts.sh` covers `publish/tests` and `scripts/performance/tests`; **zero discovered Pester tests is a failure, not a pass**.
- **`backend-tests` (ubuntu-latest, five-leg matrix)** — the backend gate, one runner per leg: `siblings` runs every enrolled test project except `XE-Local-AI-Engine.Tests`, and `tests-0`…`tests-3` each run one `TEST_SHARD` quarter of that module through [`scripts/run-tests-memory-safe.sh`](../../scripts/run-tests-memory-safe.sh) at `TEST_GROUPS=16`. Every leg does its own checkout, restore and `build -c Release --no-restore`; the built test output tree is over 1 GB, so it is rebuilt per leg rather than passed between them. The live OpenAPI comparison ([`scripts/openapi-live-check.sh`](../../scripts/openapi-live-check.sh)) runs only on `siblings`. No leg pulls an image or sets `XE_REQUIRE_DOCKER_TESTS`: the real-daemon suites are opt-in and skip here, and their wire-shape half runs daemon-free against the fake Docker server. Every project emits **Cobertura** coverage into its own `--results-directory`, because MTP resolves `--coverage-output` relative to it and a shared directory would let concurrent modules overwrite each other's report. The `siblings` leg runs [`scripts/run-backend-tests.sh`](../../scripts/run-backend-tests.sh) `--siblings-only` — the same script as the local gate, so the enrolment rule, the per-project results directory, the concurrency and the **hollow-gate guard** (a `Passed!`/`Failed!` summary must appear, catching a silent green where zero suites enrolled) are one implementation with two callers rather than two copies that drift. The `--maximum-parallel-tests` cap stays at **2** here, passed as `XE_TEST_WIDTH_DEFAULT`, because TUnit otherwise runs every test in parallel and leaves the concurrency level to the .NET thread pool — its docs state no formula and no ceiling — which is what made concurrent modules time out on shared runners. The script's local defaults are higher because they were measured on a 32-core box; raising CI's is a separate, measured change. `fail-fast: false`, so one red leg does not cancel the evidence from the others. Each leg uploads its reports as **`backend-test-results-<leg>`**.
- **`build-and-test` (ubuntu-latest)** — the merge gate over every `backend-tests` leg, and the job that must keep this exact id: `build-and-test` is the required status check configured on `develop`'s branch protection, and a matrix job reports as `backend-tests (siblings)`, which can never satisfy it. It downloads every leg's artifact unmerged, then cross-checks before merging — the sibling reports number one per enrolled project minus the batched module (re-derived from the solution, not hard-coded), each shard leg produced one Cobertura report per line of its `units.txt`, and the group indices parsed from every leg's unit names, sorted, equal `0`…`GROUPS-1` exactly. That last check is the one that proves the shards **partition** the module — every group run, and run once. Checking only for duplicates would pass a run that silently *skipped* groups (three legs dividing by four leave four groups unrun), and [`scripts/merge-cobertura.py`](../../scripts/merge-cobertura.py) can see neither failure: it unions by `(filename, line)`, so a gap and an overlap both merge to a perfectly plausible percentage. It then merges the reports without double-counting shared source lines and enforces the floor in [`scripts/backend-coverage-baseline.txt`](../../scripts/backend-coverage-baseline.txt) — currently **90.50**.
- **`client-react` (ubuntu-latest)** — pnpm + Node 22, the `global.json` SDK, a .NET 8 runtime, and the restored pinned repository tools; then `install --frozen-lockfile`, `openapi:check`, `licenses:check`, **`pnpm run acceptance`** (`node:check` → `validate` → `test:coverage:check` → `test:tooling` → `build:bundle`), and `pnpm audit --prod --audit-level=high` in order. `validate` includes `lint`, `knip`, `signalr:check` and `depcruise`; the combined gate runs those static checks once. `spellCheck` exists as a script but is **not** a gate. A clean local clone must run `dotnet tool restore --tool-manifest dotnet-tools.json` before `licenses:check`.

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

For scale only, and explicitly **not** a CI measurement: on a development machine on 2026-09-12, a
forced-cold pull of the pinned redis digest took 2.8 s and of the pinned busybox digest 1.6 s, over an
already-warm path to the registry. A hosted runner's cold network is a different and unmeasured one, so treat
these as an illustrative floor rather than as an estimate.

Exact-pinned React Doctor is a developer-invoked advisory (`pnpm run doctor`), not a fifth CI stage and not part of
`pnpm run validate`. Knip remains the strict unused-surface no-growth gate, dependency-cruiser remains the architecture
gate, and the production audit remains the vulnerability gate.

One design choice worth preserving in the file: **`TZ=Europe/Berlin`** on the backend test step (a non-UTC zone deliberately exposes time-zone bugs; the comment cites `CapabilityReporterTests`). Note that `TZ` is a **Unix-only** mechanism in .NET (`TimeZoneInfo.Unix.NonAndroid.cs` reads it; the Windows implementation resolves the zone from `kernel32!GetDynamicTimeZoneInformation` and reads no environment variable). It therefore cannot be reproduced on a Windows packaging machine by setting a variable — the deprecated `package-tester-win.ps1` instead **required** the machine's own time zone to be non-UTC, throwing before the test leg if the current offset is `+00:00` and pointing at `tzutil /s`, with `-AllowUtcTestTimeZone` to accept the reduced coverage.

**`e2e.yml`** runs on manual dispatch, or on a `develop` PR opted in with the `run-e2e` label — it is deliberately not a blocking merge gate, since it builds the SPA in-fixture and needs Playwright browsers. E2E is otherwise a manual lane: [`scripts/run-e2e-local.sh`](../../scripts/run-e2e-local.sh), or the raw commands with `-p:RunE2ETests=true`.

**`windows-tests.yml`** is the one Windows test job, on the same triggers as `build-and-test.yml`. It is advisory: not a required status check, not part of `build-and-test.yml`, so neither `release.yml` nor `dev-build.yml` waits on it. On `windows-latest`, with the machine time zone set non-UTC through `tzutil`, it builds `XE-Local-AI-Engine.Tests` and `XE-Local-AI-Engine.Client.Persistence.Tests` in Release and runs their native test hosts directly (the bash gate scripts are Linux-bound): the Windows-gated and Windows-product classes of the former by `--treenode-filter`, all of the latter. It fails on a host exit, a filter or filter class that matched nothing, zero executed tests, or a test skipped for a not-Windows reason, and writes per-host counts and every skip reason to the step summary.

**The `windows-sandbox` job** in `build-and-test.yml` is the one required Windows leg, and is part of the `build-and-test` cross-check. On `windows-2025` it downloads `wxc-host-prep.exe` from the MXC 1.0.0 release (SHA-256 pinned), runs `prepare-system-drive` and `prepare-null-device`, builds `XE-Local-AI-Engine.Tests` in Release and runs only the Windows sandbox classes (`MxcSandboxRuntimeWindowsTests`, `MxcAclResidueSweeperWindowsTests`, `ProcessSandboxAppContainerWindowsTests`) by `--treenode-filter`. Because the host prep runs first, it fails on ANY skip as well as on exit 8, a missing TRX, zero executed tests or a filter class that matched nothing. `windows-tests.yml` lists the same classes. These are the real-SDK proof of the MXC AppContainer boundary ([ADR 0019](../adr/0019-execution-previews-and-the-appcontainer-boundary.md)); every other MXC test is a Linux unit test over the `IMxcSandboxRuntime` seam.

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
- a passing `scripts/run-model-matrix-local.sh --tier rc` run on a GPU box — the evidence that small, cross-family
  and MoE models behave with default settings (thinking, output limits, tools, background jobs, the 4,096 window,
  MoE placement); exit 5 and exit 75 are not evidence either way and the run must be repeated,
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
- New EF migration → add a `<Name>MigrationTests.cs` in `Client.Persistence.Tests`, built on the shared `Testing/MigrationSchemaProbe.cs`. Every migration must be named by a test; prefer a dedicated file (most have one) so a schema regression fails in a file named after its migration.
- New or changed tool schema → run the schema/compiler unit tests and the live `run-tool-grammar-smoke-local.sh`; the negative control is required evidence, not optional diagnostics.
- New persistence entity surface change → re-check the `NegativeFence` compile fence still builds.
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
