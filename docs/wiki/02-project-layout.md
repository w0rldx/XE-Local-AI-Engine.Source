# Solution & Project Layout

> Baseline: `65de769ded3eb6e7b59eabb5daf6a8d0b89531ba` · Reviewed: 2026-08-17 · Code-grounded.

This page is the inventory and dependency map of the .NET side of XE Local AI Engine. It lists every `.csproj` registered in `XE-Local-AI-Engine.slnx`, explains each project's role, draws the project reference graph (who references whom), and states the layering rule that keeps the runtime, applications, and providers decoupled. The React client (`XE-Local-AI-Engine.Client.React`) is a separate Vite/pnpm tree wired in by Aspire and is documented on [10-react-client.md](10-react-client.md).

## How the solution is organized

The solution is an `.slnx` (XML-format) file, not a classic `.sln`. Source: `XE-Local-AI-Engine.slnx`. It groups projects into solution folders:

| Solution folder | Contents |
|---|---|
| `/Src/` | The core product projects: AI agent, contracts, the Node Web Server (`Client`), its application layer (`Client.Application`), persistence, shared test fixtures, and the packaged Windows launcher. |
| `/Src/Aspire/` | `AppHost` (dev orchestration) and `ServiceDefaults` (shared telemetry/resilience). |
| `/Src/Providers/` | All model/provider projects behind a shared abstraction. |
| `/Tests/` | Unit + E2E test projects. |
| `/Tests/Fixture/` | `Testing.FakeOllama` test fixture. |

Two `.csproj` files on disk are intentionally **not** in `XE-Local-AI-Engine.slnx`:

- `XE-Local-AI-Engine.Client.Persistence.Tests/NegativeFence/...NegativeFence.csproj` — a compile-only "negative fence" guard project under the persistence tests folder (verified absent from `XE-Local-AI-Engine.slnx`).
- `tools/AgentTemplateGenerator/AgentTemplateGenerator.csproj` — a standalone code/asset generator with its **own** `Directory.Build.props` so it escapes the repo-wide analyzer wall (`tools/AgentTemplateGenerator/Directory.Build.props`).

## Project inventory

Every project below is grounded in its `.csproj` (`Sdk=` / `OutputType` / `ProjectReference`) and, where useful, its file set.

### Core (`/Src/`)

| Project | SDK / kind | Role |
|---|---|---|
| `XE-Local-AI-Engine.Client` | `Microsoft.NET.Sdk.Web` | The **Node Web Server**. Hosts the React UI (static files), exposes `/api/local/v1` + local SignalR hubs, owns the single platform WorkerHub connection, and supervises node-owned model-runtime host child processes. The composition root — wires every provider + application service. See [01-architecture-overview.md](01-architecture-overview.md), [09-api-and-hubs.md](09-api-and-hubs.md). |
| `XE-Local-AI-Engine.Client.Application` | `Microsoft.NET.Sdk` | The **application layer**: decisions/orchestration for chat, agents, scheduler, model fit, inference tuning, knowledge/RAG, images, uploads, benchmarking, training, and development mode. Depends on every provider + persistence + agent + contracts. See [03-local-runtime-and-providers.md](03-local-runtime-and-providers.md), [05-chat.md](05-chat.md), [06-scheduler.md](06-scheduler.md), [07-model-fit.md](07-model-fit.md). |
| `XE-Local-AI-Engine.Client.Persistence` | `Microsoft.NET.Sdk` | EF Core + SQLite with selected per-column AEAD encryption. Owns the DbContexts, entities, every migration under `Client.Persistence/Migrations/` (that folder is the inventory — count it there; all but `InitialNodeChatSchema` and `AddNodeMessageLifecycleColumns` are timestamped), and the 2 model snapshots (`NodeIdentityDbContextModelSnapshot`, `NodeChatDbContextModelSnapshot`). References ASP.NET Identity EF Core + `Microsoft.EntityFrameworkCore.Sqlite` (design/tools `PrivateAssets`). References `Providers.Abstractions` (its only project reference). See [08-data-and-persistence.md](08-data-and-persistence.md). |
| `XE-Local-AI-Engine.AI.Agent` | `Microsoft.NET.Sdk`, `net10.0` | Microsoft Agent Framework (MAF) + Microsoft.Extensions.AI (MEAI) wiring: agents, tools, playbooks, the AgentHome write-back loop. See [04-agent-mode.md](04-agent-mode.md). |
| `XE-Local-AI-Engine.AI.Contracts` | `Microsoft.NET.Sdk` | Shared, dependency-free contract types — `Enums/` and `Events/` only (verified). Referenced by both `Client` and `Client.Application` so transport DTOs/events are defined once. Note: despite the name this is an in-tree project, **not** a git submodule (no `.gitmodules` entry matches). |
| `XE-Local-AI-Engine.WindowsLauncher` | `Microsoft.NET.Sdk`, `Exe` | Packaged Windows entry point. Runs `VelopackApp.Build().Run()` before application code, then launches the published `Client` host in desktop mode through `WindowsLauncherApplication`. It has no project references; the boundary is a child-process handoff rather than an assembly dependency. See [Hosting & Deployment](11-hosting-and-deployment.md). |

### Aspire (`/Src/Aspire/`)

| Project | SDK / kind | Role |
|---|---|---|
| `XE-Local-AI-Engine.AppHost` | `Aspire.AppHost.Sdk` 13.5.3, `Exe`, `IsAspireHost` | **Dev-only orchestration.** `AppHost.cs` wires three resources: the `Client` app (`app`, https), the Vite React app (`client-react`, port 5175), and a SQLite resource (`node-sqlite`) while supplying the node key used by application-level field encryption. Hosting packages are `Aspire.Hosting.AppHost` 13.5.3, `Aspire.Hosting.JavaScript` 13.5.3, `Aspire.Hosting.Browsers` 13.5.3-preview.1.26425.3, and `CommunityToolkit.Aspire.Hosting.Sqlite` 13.5.0. Inference runs inside `Client`; the AppHost has no model-runtime resource. |
| `XE-Local-AI-Engine.ServiceDefaults` | `Microsoft.NET.Sdk`, `IsAspireSharedProject` | Shared cross-cutting defaults: OpenTelemetry instrumentation/exporter, `Microsoft.Extensions.Http.Resilience`, service discovery. Referenced by `Client` and `Client.Application`. |

### Providers (`/Src/Providers/`)

Provider projects reference `Providers.Abstractions` and, for the two that speak the OpenAI wire protocol, the leaf `Providers.OpenAICompatible.Core` transport library. SDK-specific types stay inside each provider; consumers depend on the abstraction seams (`ILocalModelProvider`, `IChatClient`, `IEmbeddingGenerator`). No provider references a sibling provider.

| Project | Role | Key symbols |
|---|---|---|
| `XE-Local-AI-Engine.Providers.Abstractions` | The seam layer. Defines `ILocalModelProvider`, `IRerankerClient`, `IModelCapabilityClient`, GGUF contracts (`IGgufModelStore`, `IGgufModelRegistry`, `IHfTokenStore`, `IHuggingFaceGgufDiscovery`), hardware-profile + capability contracts, and `INodeDataDirectory`. `Gguf/` is also the single source of truth for quant logic — `QuantLadder.cs` + `GgufQuantQuality.cs` feed the Model Advisor's quant recommendation. | `ILocalModelProvider.cs`, `Contracts/IRerankerClient.cs`, `Gguf/`, `Capabilities/` |
| `XE-Local-AI-Engine.Providers.LlamaServer` | **The default inference runtime.** Host llama.cpp process: `LlamaServerLocalModelProvider`, `LlamaServerProcessSupervisor`, binary manager/updater (`LlamaCppBinaryManager`), GitHub release catalog + pins (`GitHubLlamaCppReleaseCatalog`, `LlamaCppReleasePins`), GPU variant selection (`GpuVariantSelector`, `ProcessGpuVendorProbe`), cross-platform process-group handles (Windows job object / Linux process group). | `LlamaServerProcessSupervisor.cs` (56 sym), `LlamaCppBinaryManager.cs` (44) |
| `XE-Local-AI-Engine.Providers.HuggingFace` | HuggingFace GGUF discovery + download store (feeds the Model Advisor). See [07-model-fit.md](07-model-fit.md). | — |
| `XE-Local-AI-Engine.Providers.Ollama` | Ollama provider — still **present** as an `ILocalModelProvider` implementation, but **de-orchestrated** from Aspire dev (llama.cpp is the dev runtime; `AppHost.cs` has no Ollama resource). | — |
| `XE-Local-AI-Engine.Providers.OpenAICompat` | Operator-registered external OpenAI-compatible endpoints (self-hosted llama-server/vLLM/LM Studio, or a hosted OpenAI-compatible API). ONE multiplexer `ILocalModelProvider` with provider name `external`, dispatching per connection by parsing the namespaced model id `ext:{connectionId}/{wireId}` through `IExternalProviderRegistry`. Adds the outbound endpoint guard, the reasoning-output rewriter (vLLM `reasoning` field + inline `<think>` fallback) and typed `reasoning_effort` injection. No pull/delete/embeddings. | `ExternalOpenAiModelProvider.cs`, `ExternalOpenAiChatClient.cs`, `ExternalEndpointGuardHandler.cs` |
| `XE-Local-AI-Engine.Providers.OpenAICompatible.Core` | Shared OpenAI-compatible WIRE layer, referenced by both `LlamaServer` and `OpenAICompat`: client construction with a pinned no-retry transport and conditional Bearer/no-auth policy, base-URL normalization to a `/v1/` base, and the request-body patch + `RawRepresentationFactory` chaining discipline for body fields the typed OpenAI schema does not model. A leaf — no project references of its own. | `OpenAICompatibleClientFactory.cs`, `OpenAICompatibleRequestBody.cs`, `OpenAICompatibleBaseAddress.cs` |
| `XE-Local-AI-Engine.Providers.CodexOAuth` | ChatGPT/Codex OAuth cloud chat provider. | — |
| `XE-Local-AI-Engine.Providers.Capabilities` | Sanitized hardware profiling (CPU/RAM/GPU/VRAM/disk) behind `IHardwareProfiler`, consumed by model-fit and runtime auditing. | `HardwareProfiler`, `CapabilitiesServiceCollectionExtensions` |
| `XE-Local-AI-Engine.Providers.StableDiffusionCpp` | Local image-generation provider and supervised `sd-server` runtime. | — |
| `XE-Local-AI-Engine.Providers.Training` | Local fine-tuning runtime (Linux only): provisions a uv-managed Python environment and spawns/supervises the training process behind `ITrainingRuntimeService` / `ITrainingProcessSpawner`, with its own libc tree-kill process-group handle. Implements neither `ILocalModelProvider` nor `IChatClient`. See [18-training.md](18-training.md). | `TrainingRuntimeService.cs`, `UvBinaryAcquirer.cs`, `LinuxTrainingProcessSpawner.cs` |

### Tests & support

| Project | SDK / kind | Role |
|---|---|---|
| `XE-Local-AI-Engine.Tests` | `Exe`, MTP | Main unit suite. References `Client`, `WindowsLauncher`, `Client.Application`, `ServiceDefaults`, every concrete provider project (`Capabilities`, `CodexOAuth`, `HuggingFace`, `LlamaServer`, `Ollama`, `StableDiffusionCpp`, `Training`), and `Testing.FakeOllama`. |
| `XE-Local-AI-Engine.Tests.E2ETests` | `Exe`, MTP | End-to-end suite. References `Client`, `Client.Application`, `Client.Persistence`, `Providers.Abstractions`, `Providers.Ollama`, plus `Testing.FakeOllama` and `Client.Testing` fixtures. See [13-testing-and-validation.md](13-testing-and-validation.md). |
| `XE-Local-AI-Engine.AI.Agent.Tests` | `Exe`, MTP | Unit suite scoped to `AI.Agent`. |
| `XE-Local-AI-Engine.Client.Persistence.Tests` | `Exe`, MTP | Persistence/migration suite. References `Client.Application`, `Client.Persistence`, `Client`. |
| `XE-Local-AI-Engine.Client.Testing` | library | Reusable test fixtures/harness for `Client` + `Client.Application` (shared by E2E). |
| `XE-Local-AI-Engine.Testing.FakeOllama` | library | In-memory fake Ollama server fixture so tests never hit a real model runtime. |
| `...Client.Persistence.NegativeFence` | library, **not in slnx** | Compile-only negative-fence guard over `Client.Persistence`. |
| `tools/AgentTemplateGenerator` | `Exe`, **not in slnx** | Standalone agent-template generator; own `Directory.Build.props`. |

## Dependency graph

Solid arrows are `ProjectReference` edges (verified from each `.csproj`).

```
                         AI.Contracts ◄───────────────┐
                              ▲                        │
            ┌─────────────────┴───────────────┐       │
            │                                  │       │
        Client (Web) ──────────► Client.Application ───┘
            │  │  │  │                  │  │  │  │  │  │
            │  │  │  └► ServiceDefaults ◄┘  │  │  │  │  │
            │  │  └────► AI.Agent ◄─────────┘  │  │  │  │
            │  └───────► Client.Persistence ◄──┘  │  │  │
            │                                      │  │  │
            └► Providers.Ollama ──┐   Providers.{Llama,HF,Codex,Capabilities,Ollama,SDcpp,Training}
                                  ▼                │  │  │
                       Providers.Abstractions ◄────┴──┴──┘ ◄── Client.Persistence (benchmark contracts)
                                  ▲
  Capabilities / CodexOAuth / HuggingFace / LlamaServer / Ollama / OpenAICompat / StableDiffusionCpp / Training
     (each references ONLY Abstractions; LlamaServer + OpenAICompat also ► OpenAICompatible.Core, a leaf)

AppHost ──► Client            (orchestrates; not referenced back)
WindowsLauncher ── child process ──► published Client executable
```

**Text fallback:** `Client` is the composition root. It references `Client.Application`, persistence,
agent, provider, contracts, and service-default projects. `Client.Application` carries the product
orchestration dependencies; provider implementations converge on `Providers.Abstractions`.
`AppHost` starts `Client` for development but is not referenced back by the runtime.

Notable edges:

- **`Client.Application` is the hub of the product graph** — it references every `Providers.*` project (`Abstractions`, `Capabilities`, `CodexOAuth`, `HuggingFace`, `LlamaServer`, `Ollama`, `OpenAICompat`, `StableDiffusionCpp`, `Training`), `Client.Persistence`, `AI.Agent`, `ServiceDefaults`, and `AI.Contracts`.
- **`Client` (Web) references a narrower set** — `AI.Agent`, `Client.Application`, `Client.Persistence`, `Providers.Abstractions`, `Providers.Ollama`, `ServiceDefaults`, `AI.Contracts`. It reaches the other providers transitively through `Client.Application`; only `Ollama` is referenced directly at the web layer (legacy direct dependency).
- **Providers reference `Providers.Abstractions` only** (verified for `Capabilities`, `CodexOAuth`, `HuggingFace`, `Ollama`, `StableDiffusionCpp`, and `Training`), with ONE reviewed exception: `LlamaServer` and `OpenAICompat` also reference the leaf `Providers.OpenAICompatible.Core` so the OpenAI wire layer exists once instead of twice. `Capabilities` is the only provider that another non-abstraction project depends on beyond the normal app/test edges.
- **`Providers.Abstractions` and `AI.Contracts` are leaves** (no outbound project references) — the bottom of the layering. `Client.Persistence` sits one step above: its only project reference is `Providers.Abstractions`.
- **`AppHost` references `Client`** for dev orchestration but nothing references `AppHost`.
- **`WindowsLauncher` is an assembly leaf.** It references no product project; the packaged launcher starts the published `Client` executable as a child process after Velopack lifecycle handling.

## The layering rule

```
   Transport / Web        Client                (Microsoft.NET.Sdk.Web)
        │  orchestration only — endpoints, hubs, WorkerHub, host
        ▼
   Application            Client.Application     (decisions / services)
        │
        ├──► Domain/Persistence   Client.Persistence  (EF Core + SQLite; ──► Providers.Abstractions for benchmark contracts)
        ├──► Agent                AI.Agent            (MAF/MEAI)
        └──► Provider seams       Providers.Abstractions
                                       ▲
                              concrete providers (LlamaServer, HuggingFace,
                              Ollama, CodexOAuth, Capabilities,
                              StableDiffusionCpp, Training)
   Shared, depended-on by all:  AI.Contracts (DTOs/enums/events),
                                ServiceDefaults (telemetry/resilience)
```

**Text fallback:** web/transport code orchestrates application services. Application services depend
on persistence, the agent runtime, and provider contracts. Provider implementations depend inward on
their abstractions; persistence does not depend back on the web host.

Maintainer invariants:

1. **Providers behind abstractions.** Code outside a provider project depends on `ILocalModelProvider` / `IChatClient` / `IEmbeddingGenerator` (MEAI) — never on a provider SDK type. Adding a model backend = a new `Providers.*` project that references **only** `Providers.Abstractions`, plus DI registration in `Client.Application`/`Client`. See [03-local-runtime-and-providers.md](03-local-runtime-and-providers.md).
2. **One-way flow.** Web → Application → (Persistence / Agent / Provider seams). `Providers.Abstractions` and `AI.Contracts` must stay leaves. `Client.Persistence` may reference only `Providers.Abstractions` (it does, for the benchmark contract types persisted in `BenchmarkRun`); do not add any other outbound reference, and never an upward one (e.g. Persistence → Application).
3. **Contracts shared, not duplicated.** Cross-boundary DTOs/events/enums live in `AI.Contracts`; reuse them rather than redefining per layer.
4. **Application holds decisions; Web is wiring.** Business/orchestration logic belongs in `Client.Application` services; `Client` endpoints/hubs orchestrate and apply security (loopback-only, Host/Origin checks, secret redaction). See [12-security-and-privacy.md](12-security-and-privacy.md).
5. **No Docker on the inference path; no HostAgent at all.** Per the 2026-06-17 runtime re-architecture, inference + AgentHome run as host processes (`Providers.LlamaServer` + a process sandbox provider), and there is no HostAgent project. Don't reintroduce a HostAgent reference, and don't put a container between the app and a model. **One scoped exception:** [ADR 0004](../adr/0004-development-mode-container-execution-docker-stopgap.md) permits a Docker Engine API client (`Docker.DotNet.Enhanced`) for **Development Mode build/test/lint execution only**, behind the existing `ISandboxRuntimeProvider` seam — AgentHome and Coder stay on the process provider. A container reference anywhere else is still a defect.

## Build & package conventions

Repo-wide MSBuild config lives at the solution root:

- **`eng/ReleaseVersion.props`** is the single release-identity source (`VersionPrefix` + optional `VersionSuffix`), imported by `Directory.Build.props`. `Directory.Build.props` sets `net10.0`, `Nullable`/`ImplicitUsings` enabled, C# 14 (`LangVersion 14.0`), and a **strict analyzer wall**: `TreatWarningsAsErrors=true`, `AnalysisMode=All`. Production projects additionally get `Meziantou.Analyzer` + `Microsoft.CodeAnalysis.BannedApiAnalyzers` (with `BannedSymbols.txt`); test/tooling projects (`*.Tests`, `*.Testing*`, `*.AppHost`) are exempted via the `IsTestOrToolingProject` flag. A literal `TODO`/`FIXME` in a comment fails the build (Sonar S1135 = error); describe the present limitation or rationale directly without task markers.
- **`Directory.Build.targets`** gates that whole analyzer wall to **Release and CI**. Since 2026-07-31 it sets `RunAnalyzers=false` when `Configuration == Debug` and neither `CI` nor `XE_FULL_ANALYSIS` is set (84 s → 10 s on the Tests module), which maps to csc `-skipanalyzers`. **A green local Debug build proves nothing about Sonar, Meziantou, BannedApiAnalyzers or the `IDExxxx` style rules — including the bare-`TODO` rule above.** Iterate in Debug, then finish with `dotnet build XE-Local-AI-Engine.slnx --configuration Release`, or the packaging script will reject what compiled fine for you. `XE_FULL_ANALYSIS=1` forces the full pass in Debug. `TreatWarningsAsErrors` is unaffected, so genuine compiler warnings still fail a Debug build, and source generators still run (so TUnit discovery is intact).
- **`Directory.Packages.props`** enables **Central Package Management** (`ManagePackageVersionsCentrally=true` in `Directory.Build.props`); every dependency is pinned by a `<PackageVersion>` entry there — that file is the inventory. Add new dependencies as a `<PackageVersion>` there, then a versionless `<PackageReference>` in the consuming `.csproj`.
- **`global.json`** sets a `10.0.100` feature-band **baseline** with `rollForward: latestFeature` (no prerelease) — it rolls forward to the highest installed 10.0 feature band and patch at or above `10.0.100` rather than pinning an exact version — and sets the test runner to **Microsoft.Testing.Platform (MTP)** — test projects are `OutputType=Exe` and run via MTP, not VSTest.
- **`cliff.toml` + `CHANGELOG.md`** (repo root) drive git-cliff changelog automation: conventional-commit history → `CHANGELOG.md` / release notes, consumed by the Velopack release flow. See [11-hosting-and-deployment.md](11-hosting-and-deployment.md).

## Related pages

- [01-architecture-overview.md](01-architecture-overview.md) — how the Node Web Server, application layer, and runtime fit together.
- [03-local-runtime-and-providers.md](03-local-runtime-and-providers.md) — the provider seams and the llama.cpp supervisor in depth.
- [08-data-and-persistence.md](08-data-and-persistence.md) — `Client.Persistence`, SQLite, selected per-column encryption, migrations.
- [04-agent-mode.md](04-agent-mode.md) — `AI.Agent` (MAF/MEAI) internals.
- [09-api-and-hubs.md](09-api-and-hubs.md) — endpoints and SignalR hubs exposed by `Client`.
- [10-react-client.md](10-react-client.md) — the React feature tree and OpenAPI→hey-api clients.
- [11-hosting-and-deployment.md](11-hosting-and-deployment.md) — AppHost orchestration and packaging.
- [12-security-and-privacy.md](12-security-and-privacy.md) — the security invariants that ride on this layering.
- [13-testing-and-validation.md](13-testing-and-validation.md) — the test/fixture projects and MTP.
- [18-training.md](18-training.md) — what `Providers.Training` and the training service areas do.
- [Home.md](Home.md)
