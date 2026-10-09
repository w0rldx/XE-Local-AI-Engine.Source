# Solution & Project Layout

> Reviewed: 2026-10-02 · Code-grounded.

**What this page covers.** The inventory of every .NET project in `XE-Local-AI-Engine.slnx`, the project reference graph and the layering rule that keeps runtime, application and provider code decoupled. The rule that matters most: each `Providers.*` project depends only on `Providers.Abstractions` (plus the few reviewed exceptions listed here), and `Client.Persistence` references only `Providers.Abstractions`.

**Read this if you are** adding a project or a project reference, or an architecture test has failed on a dependency. **Skip to** [The layering rule](#the-layering-rule) for what may reference what; **reference tables** are in [Project inventory](#project-inventory); **related pages:** [01](01-architecture-overview.md), [16](16-code-conventions.md).

## Contents

- [How the solution is organized](#how-the-solution-is-organized)
- [Project inventory](#project-inventory)
- [Dependency graph](#dependency-graph)
- [The layering rule](#the-layering-rule)
- [Composition root: the AddNode* modules](#composition-root-the-addnode-modules)
- [Build & package conventions](#build--package-conventions)
- [Related pages](#related-pages)

This page is the inventory and dependency map of the .NET side of XE Local AI Engine. It lists every `.csproj` registered in `XE-Local-AI-Engine.slnx`, explains each project's role, draws the project reference graph (who references whom), and states the layering rule that keeps the runtime, applications, and providers decoupled. The React client (`XE-Local-AI-Engine.Client.React`) is a separate Vite/pnpm tree wired in by Aspire and is documented on [10-react-client.md](10-react-client.md).

## How the solution is organized

The solution is an `.slnx` (XML-format) file, not a classic `.sln`. Source: `XE-Local-AI-Engine.slnx`. It groups projects into solution folders:

| Solution folder | Contents |
|---|---|
| `/Src/` | The core product projects: AI agent, contracts, the Node Web Server (`Client`), its application layer (`Client.Application`), persistence, shared test fixtures, and the packaged Windows launcher. |
| `/Src/Aspire/` | `AppHost` (dev orchestration) and `ServiceDefaults` (shared telemetry/resilience). |
| `/Src/Providers/` | All model/provider projects behind a shared abstraction. |
| `/Tests/` | Unit + E2E test projects. |
| `/Tests/Fixture/` | `Testing.FakeOllama`, `Testing.FakeDocker` and `Testing.FakeOpenAiGateway` test fixtures. |

Two `.csproj` files on disk are intentionally **not** in `XE-Local-AI-Engine.slnx`:

- `XE-Local-AI-Engine.Client.Persistence.Tests/NegativeFence/...NegativeFence.csproj` — a compile-only "negative fence" guard project under the persistence tests folder (verified absent from `XE-Local-AI-Engine.slnx`).
- `tools/AgentTemplateGenerator/AgentTemplateGenerator.csproj` — a standalone code/asset generator with its **own** `Directory.Build.props` so it escapes the repo-wide analyzer wall (`tools/AgentTemplateGenerator/Directory.Build.props`).

One top-level folder holds source that is neither a project nor build tooling: `catalog/external-apps/` is the
authoring source for the External Apps catalog — an upstream Compose file, a variable classification and a metadata
override document per application, converted offline by `tools/build_catalog.py` into `dist/applications.json`.
That generated document is copied byte-for-byte into `Client.Application/Services/ExternalApps/Catalog/` and shipped
as an `<EmbeddedResource>`, so the bundled seed and the authoring output can never drift; a test asserts they are
identical. Neither file is ever hand-edited. See [External Apps](23-external-apps.md).

## Project inventory

Every project below is grounded in its `.csproj` (`Sdk=` / `OutputType` / `ProjectReference`) and, where useful, its file set.

### Core (`/Src/`)

| Project | SDK / kind | Role |
|---|---|---|
| `XE-Local-AI-Engine.Desktop` | `Microsoft.NET.Sdk`, `Exe` | Thin Avalonia NativeWebView shell for Windows and Ubuntu. Owns window, activation and optionally engine lifetime; normal application traffic remains REST/SignalR. No engine project references; browser/headless commands bypass the window. See [Native desktop checkpoint](../audits/2026-10-02-native-desktop-1.0-delivery-record.md) (historical record). |
| `XE-Local-AI-Engine.Client` | `Microsoft.NET.Sdk.Web` | The **Node Web Server**. Hosts the React UI (static files), exposes `/api/local/v1` + local SignalR hubs, and supervises node-owned model-runtime host child processes. The composition root — wires every provider + application service. See [01-architecture-overview.md](01-architecture-overview.md), [09-api-and-hubs.md](09-api-and-hubs.md). |
| `XE-Local-AI-Engine.Client.Application` | `Microsoft.NET.Sdk` | The **application layer**: decisions/orchestration for chat, agents, scheduler, model fit, inference tuning, knowledge/RAG, images, uploads, benchmarking, training, and development mode. Depends on every provider + persistence + agent + contracts. See [03-local-runtime-and-providers.md](03-local-runtime-and-providers.md), [05-chat.md](05-chat.md), [06-scheduler.md](06-scheduler.md), [07-model-fit.md](07-model-fit.md). |
| `XE-Local-AI-Engine.Client.Persistence` | `Microsoft.NET.Sdk` | EF Core + SQLite with selected per-column AEAD encryption. Owns the DbContexts, entities, every migration under `Client.Persistence/Migrations/` (that folder is the inventory — count it there; all but `InitialNodeChatSchema` and `AddNodeMessageLifecycleColumns` are timestamped), and the 2 model snapshots (`NodeIdentityDbContextModelSnapshot`, `NodeChatDbContextModelSnapshot`). References ASP.NET Identity EF Core + `Microsoft.EntityFrameworkCore.Sqlite` (design/tools `PrivateAssets`). References `Providers.Abstractions` (its only project reference). See [08-data-and-persistence.md](08-data-and-persistence.md). |
| `XE-Local-AI-Engine.AI.Agent` | `Microsoft.NET.Sdk`, `net10.0` | Microsoft Agent Framework (MAF) + Microsoft.Extensions.AI (MEAI) wiring: agents, tools, playbooks, invocation and sessions. AgentHome itself is an application area (`Client.Application/Services/AgentHome/`), not part of this project. See [04-agent-mode.md](04-agent-mode.md). |
| `XE-Local-AI-Engine.AI.Contracts` | `Microsoft.NET.Sdk` | Shared, dependency-free contract types — `Enums/`, `Events/` and `Telemetry/` (the pinned telemetry source names in `TelemetrySourceNames`). Referenced by both `Client` and `Client.Application` so transport DTOs/events are defined once. Note: despite the name this is an in-tree project, **not** a git submodule (no `.gitmodules` entry matches). |
| `XE-Local-AI-Engine.WindowsLauncher` | `Microsoft.NET.Sdk`, `Exe` | Packaged Windows entry point. Runs `VelopackApp.Build().Run()` before application code, then launches the published `Client` host in desktop mode through `WindowsLauncherApplication`. It has no project references; the boundary is a child-process handoff rather than an assembly dependency. See [Hosting & Deployment](11-hosting-and-deployment.md). |

### Aspire (`/Src/Aspire/`)

| Project | SDK / kind | Role |
|---|---|---|
| `XE-Local-AI-Engine.AppHost` | `Aspire.AppHost.Sdk` 13.5.4, `Exe`, `IsAspireHost` | **Dev-only orchestration.** `AppHost.cs` wires three resources: the `Client` app (`app`, https), the Vite React app (`client-react`, port 5175), and a SQLite resource (`node-sqlite`) while supplying the node key used by application-level field encryption. Hosting packages are `Aspire.Hosting.AppHost` 13.5.4, `Aspire.Hosting.JavaScript` 13.5.4, `Aspire.Hosting.Browsers` 13.5.4-preview.1.26464.4, and `CommunityToolkit.Aspire.Hosting.Sqlite` 13.5.0. Inference runs inside `Client`; the AppHost has no model-runtime resource. |
| `XE-Local-AI-Engine.ServiceDefaults` | `Microsoft.NET.Sdk`, `IsAspireSharedProject` | Shared cross-cutting defaults: OpenTelemetry instrumentation/exporter, `Microsoft.Extensions.Http.Resilience`, service discovery. Referenced by `Client` and `Client.Application`. |

### Providers (`/Src/Providers/`)

Provider projects reference `Providers.Abstractions` and, for the two that speak the OpenAI wire protocol, the leaf `Providers.OpenAICompatible.Core` transport library; `Providers.Training` also references the leaf-like `Providers.Python` uv layer ([ADR 0016](../adr/0016-managed-python-shared-uv-layer.md)); `LlamaServer`, `StableDiffusionCpp`, `WhisperCpp` and `Python` also reference the leaf-like `Providers.ProcessSupervision` child-process containment. SDK-specific types stay inside each provider; consumers depend on the abstraction seams (`ILocalModelProvider`, `IChatClient`, `IEmbeddingGenerator`). Enforced for OllamaSharp: `Providers.Ollama`'s `OllamaSharp` reference is `PrivateAssets="compile"`, so no consumer can compile against the SDK even though it references the project, and `LayerDependencyTests.ProductionProjects_HaveOnlyTheApprovedPackageReferences` catches a direct re-add of the package in a consumer csproj. Docker and Azure remain source-scan-only, with no compile-asset boundary yet. Apart from those three shared layers, no provider references a sibling provider.

| Project | Role | Key symbols |
|---|---|---|
| `XE-Local-AI-Engine.Providers.Abstractions` | The seam layer. Defines `ILocalModelProvider`, `IRerankerClient`, `IModelCapabilityClient`, GGUF contracts (`IGgufModelStore`, `IGgufModelRegistry`, `IHfTokenStore`, `IHuggingFaceGgufDiscovery`), hardware-profile + capability contracts, and `INodeDataDirectory`. `Gguf/` is also the single source of truth for quant logic — `QuantLadder.cs` + `GgufQuantQuality.cs` feed the Model Advisor's quant recommendation. It also holds the few shared concrete helpers a provider may not duplicate: `SecureFilePermissions`, `SetsidLocator`, `DriveInfoFreeSpaceProbe` — the one free-disk measurement in the node, next to the `IFreeSpaceProbe` it implements — and `PathContainment`, the one "is this path under a root we own?" rule the process reapers, the sandbox orphan reaper and the generated-image store all decide to kill or delete through; because every provider may reference this project and nothing else. | `ILocalModelProvider.cs`, `Contracts/IRerankerClient.cs`, `DriveInfoFreeSpaceProbe.cs`, `PathContainment.cs`, `Gguf/`, `Capabilities/` |
| `XE-Local-AI-Engine.Providers.LlamaServer` | **The default inference runtime.** Host llama.cpp process: `LlamaServerLocalModelProvider`, `LlamaServerProcessSupervisor`, binary manager/updater (`LlamaCppBinaryManager`), GitHub release catalog + pins (`GitHubLlamaCppReleaseCatalog`, `LlamaCppReleasePins`), GPU variant selection (`GpuVariantSelector`, `ProcessGpuVendorProbe`), cross-platform process-group handles (Windows job object / Linux process group). | `LlamaServerProcessSupervisor.cs`, `LlamaCppBinaryManager.cs` |
| `XE-Local-AI-Engine.Providers.HuggingFace` | HuggingFace GGUF discovery + download store (feeds the Model Advisor). See [07-model-fit.md](07-model-fit.md). | — |
| `XE-Local-AI-Engine.Providers.Ollama` | Ollama provider — still **present** as an `ILocalModelProvider` implementation, but **de-orchestrated** from Aspire dev (llama.cpp is the dev runtime; `AppHost.cs` has no Ollama resource). | — |
| `XE-Local-AI-Engine.Providers.OpenAICompat` | Operator-registered external OpenAI-compatible endpoints (self-hosted llama-server/vLLM/LM Studio, or a hosted OpenAI-compatible API). ONE multiplexer `ILocalModelProvider` with provider name `external`, dispatching per connection by parsing the namespaced model id `ext:{connectionId}/{wireId}` through `IExternalProviderRegistry`. Adds the reasoning-output rewriter (vLLM `reasoning` field + inline `<think>` fallback) and typed `reasoning_effort` injection. No pull/delete/embeddings. | `ExternalOpenAiModelProvider.cs`, `ExternalOpenAiChatClient.cs` |
| `XE-Local-AI-Engine.Providers.OpenAICompatible.Core` | Shared OpenAI-compatible WIRE layer, referenced by both `LlamaServer` and `OpenAICompat`: client construction with a pinned no-retry transport and conditional Bearer/no-auth policy, base-URL normalization to a `/v1/` base, the outbound endpoint guard and the custom-header handler that chat, health and the connect-time probe share, the custom-header name rules, and the request-body patch + `RawRepresentationFactory` chaining discipline for body fields the typed OpenAI schema does not model. A leaf — no project references of its own. | `OpenAICompatibleClientFactory.cs`, `OpenAICompatibleRequestBody.cs`, `OpenAICompatibleBaseAddress.cs`, `ExternalEndpointGuardHandler.cs`, `CustomRequestHeadersHandler.cs` |
| `XE-Local-AI-Engine.Providers.CodexOAuth` | ChatGPT/Codex OAuth cloud chat provider. | — |
| `XE-Local-AI-Engine.Providers.Capabilities` | Sanitized hardware profiling (CPU/RAM/GPU/VRAM/disk) behind `IHardwareProfiler`, consumed by model-fit and runtime auditing. | `HardwareProfiler`, `CapabilitiesServiceCollectionExtensions` |
| `XE-Local-AI-Engine.Providers.StableDiffusionCpp` | Local image-generation provider and supervised `sd-server` runtime. | — |
| `XE-Local-AI-Engine.Providers.WhisperCpp` | Local speech-to-text provider and supervised `whisper-server` runtime: pinned binary acquisition, one resident daemon on its own loopback range (18300-18399), health-route readiness, idle-TTL reaping and the static Whisper weight catalogue. Implements neither `ILocalModelProvider` nor `IChatClient` — it publishes `IWhisperTranscriber` and `IWhisperServerSupervisor`. | `WhisperServerProcessSupervisor.cs`, `WhisperCppReleasePins.cs` |
| `XE-Local-AI-Engine.Providers.ProcessSupervision` | Shared child-process containment for the supervised runtimes, referenced by `LlamaServer`, `StableDiffusionCpp`, `WhisperCpp` and `Python`: the Linux process-group handle (`setsid`, `SIGTERM`, 2 s grace, `SIGKILL` on the group), the Windows Job Object handle (`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`), the plain fallback handle behind `IProcessTreeHandle`, the bounded sanitized `ProcessStderrTail`, and the startup `StaleProcessReaper` + `OsStaleProcessScanner` (reaps binaries under the provider-supplied root, via `PathContainment`, plus on Linux any child a `ProcessSpawnReceiptStore` receipt still identifies by pid, `/proc` start time and executable realpath). Holds the libc `kill`/`realpath` and the Job Object `LibraryImport`s, so `AllowUnsafeBlocks` stays out of `Providers.Abstractions`. Leaf-like: references only `Providers.Abstractions`. `Providers.Training` keeps its own stricter handle. | `LinuxProcessGroupHandle.cs`, `WindowsJobObjectProcessHandle.cs`, `StaleProcessReaper.cs` |
| `XE-Local-AI-Engine.Providers.Python` | Shared managed-Python layer ([ADR 0016](../adr/0016-managed-python-shared-uv-layer.md)), consumed by `Providers.Training` and by the compute tool in `Client.Application`: pinned, digest-verified uv acquisition (`UvBinaryAcquirer`, `ManagedPythonPins`), the uv environment allow-list (`ManagedPythonEnvironment`), the scrubbed, tree-killed spawn (`IPythonToolRunner` / `LinuxPythonToolRunner`) and the user-safe `ManagedPythonException`. Owns no feature semantics. Leaf-like: references only `Providers.Abstractions` (`SetsidLocator`, `RuntimeCacheDirectory`) and `Providers.ProcessSupervision` (its Linux tree-kill handle). | `UvBinaryAcquirer.cs`, `ManagedPythonPins.cs`, `LinuxPythonToolRunner.cs` |
| `XE-Local-AI-Engine.Providers.Training` | Local fine-tuning runtime (Linux only): provisions a uv-managed Python environment through `Providers.Python` and spawns/supervises the training process behind `ITrainingRuntimeService` / `ITrainingProcessSpawner`, with its own libc tree-kill process-group handle. Implements neither `ILocalModelProvider` nor `IChatClient`. See [18-training.md](18-training.md). | `TrainingRuntimeService.cs`, `TrainingRuntimeEnvironment.cs`, `LinuxTrainingProcessSpawner.cs` |

### Tests & support

| Project | SDK / kind | Role |
|---|---|---|
| `XE-Local-AI-Engine.Tests` | `Exe`, MTP | Main unit suite. References `Client`, `WindowsLauncher`, `Desktop`, `Client.Application`, `Client.Testing`, `ServiceDefaults`, every concrete provider project (`Capabilities`, `CodexOAuth`, `HuggingFace`, `LlamaServer`, `Ollama`, `OpenAICompat`, `StableDiffusionCpp`, `Python`, `Training`, `WhisperCpp`), the leaf `OpenAICompatible.Core`, `Testing.FakeOllama`, `Testing.FakeDocker` and `Testing.FakeOpenAiGateway`. |
| `XE-Local-AI-Engine.Tests.E2ETests` | `Exe`, MTP | End-to-end suite. References `Client`, `Client.Application`, `Client.Persistence`, `Providers.Abstractions`, `Providers.Ollama`, plus `Testing.FakeOllama` and `Client.Testing` fixtures. See [13-testing-and-validation.md](13-testing-and-validation.md). |
| `XE-Local-AI-Engine.AI.Agent.Tests` | `Exe`, MTP | Unit suite scoped to `AI.Agent`. |
| `XE-Local-AI-Engine.Client.Persistence.Tests` | `Exe`, MTP | Persistence/migration suite. References `Client.Application`, `Client.Persistence`, `Client`, `Providers.LlamaServer`. |
| `XE-Local-AI-Engine.Client.Testing` | library | Reusable test fixtures/harness for `Client` + `Client.Application` (shared by E2E). |
| `XE-Local-AI-Engine.Testing.FakeOllama` | library | In-memory fake Ollama server fixture so tests never hit a real model runtime. |
| `XE-Local-AI-Engine.Testing.FakeDocker` | library | In-memory fake Docker Engine API server fixture (the subset of routes `DockerDotNetRuntimeClient` calls) so tests never need a real daemon for wire-shape coverage. |
| `XE-Local-AI-Engine.Testing.FakeOpenAiGateway` | `Exe` | Loopback fake OpenAI-compatible gateway fixture (bearer and required-header checks, SSE, scripted tool calls, fault queue, request log); also runnable by hand with `dotnet run`. |
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
            └► Providers.Ollama ──┐   Providers.{Llama,HF,Codex,Capabilities,Ollama,OpenAICompat,OpenAICompatible.Core,SDcpp,Python,Training,WhisperCpp}
                                  ▼                │  │  │
                       Providers.Abstractions ◄────┴──┴──┘ ◄── Client.Persistence (benchmark contracts)
                                  ▲
  Capabilities / CodexOAuth / HuggingFace / LlamaServer / Ollama / OpenAICompat / Python / StableDiffusionCpp / Training / WhisperCpp
     (each references ONLY Abstractions; LlamaServer + OpenAICompat also ► OpenAICompatible.Core, a leaf;
      Training also ► Python; LlamaServer, StableDiffusionCpp, WhisperCpp + Python also ► ProcessSupervision,
      which itself references only Abstractions)

AppHost ──► Client            (orchestrates; not referenced back)
WindowsLauncher ── child process ──► published Client executable
```

**Text fallback:** `Client` is the composition root. It references `Client.Application`, persistence,
agent, provider, contracts, and service-default projects. `Client.Application` carries the product
orchestration dependencies; provider implementations converge on `Providers.Abstractions`.
`AppHost` starts `Client` for development but is not referenced back by the runtime.

Notable edges:

- **`Client.Application` is the hub of the product graph** — it references every `Providers.*` project (`Abstractions`, `Capabilities`, `CodexOAuth`, `HuggingFace`, `LlamaServer`, `Ollama`, `OpenAICompat`, `Python`, `StableDiffusionCpp`, `Training`, `WhisperCpp`), `Client.Persistence`, `AI.Agent`, `ServiceDefaults`, and `AI.Contracts`.
- **`Client` (Web) references a narrower set** — `AI.Agent`, `Client.Application`, `Client.Persistence`, `Providers.Abstractions`, `Providers.Ollama`, `ServiceDefaults`, `AI.Contracts`. It reaches the other providers transitively through `Client.Application`; only `Ollama` is referenced directly at the web layer (legacy direct dependency).
- **Providers reference `Providers.Abstractions` only** (verified for `Capabilities`, `CodexOAuth`, `HuggingFace` and `Ollama`), with THREE reviewed exceptions: `LlamaServer` and `OpenAICompat` also reference the leaf `Providers.OpenAICompatible.Core` so the OpenAI wire layer exists once instead of twice; `LlamaServer`, `StableDiffusionCpp`, `WhisperCpp` and `Python` also reference `Providers.ProcessSupervision` so the process-group, Job Object and plain handles, the stderr tail and the stale reaper exist once instead of four times; and `Training` also references `Providers.Python` so uv acquisition, the uv environment allow-list and the scrubbed runner exist once for Training and the compute tool ([ADR 0016](../adr/0016-managed-python-shared-uv-layer.md)). `Providers.ProcessSupervision` is leaf-like, not a leaf: its one reference is `Providers.Abstractions`; `Providers.Python` references it and `Providers.Abstractions`. Apart from those reviewed edges, `Capabilities` is the only provider that another non-abstraction project depends on beyond the normal app/test edges.
- **`Providers.Abstractions` and `AI.Contracts` are leaves** (no outbound project references) — the bottom of the layering. `Client.Persistence` sits one step above: its only project reference is `Providers.Abstractions`.
- **`AppHost` references `Client`** for dev orchestration but nothing references `AppHost`.
- **`WindowsLauncher` is an assembly leaf.** It references no product project; the packaged launcher starts the published `Client` executable as a child process after Velopack lifecycle handling.

## The layering rule

```
   Transport / Web        Client                (Microsoft.NET.Sdk.Web)
        │  orchestration only — endpoints, hubs, host
        ▼
   Application            Client.Application     (decisions / services)
        │
        ├──► Domain/Persistence   Client.Persistence  (EF Core + SQLite; ──► Providers.Abstractions for benchmark contracts)
        ├──► Agent                AI.Agent            (MAF/MEAI)
        └──► Provider seams       Providers.Abstractions
                                       ▲
                              concrete providers (LlamaServer, HuggingFace,
                              Ollama, CodexOAuth, Capabilities,
                              StableDiffusionCpp, Training, WhisperCpp)
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
5. **No Docker on the inference path; no HostAgent at all.** Per the 2026-06-17 runtime re-architecture, inference + AgentHome run as host processes (`Providers.LlamaServer` + the process sandbox backend, `ProcessSandboxRuntimeProvider`), and there is no HostAgent project. The sandbox substrate is an application area (`Client.Application/Services/Sandbox/`, beside `Services/Containers/`), not a `Providers.*` project; its backend choice is fenced by `SandboxSubstrateSelectionArchitectureTests` (see [Security & Privacy](12-security-and-privacy.md)). Don't reintroduce a HostAgent reference, and don't put a container between the app and a model. **One scoped exception:** [ADR 0004](../adr/0004-development-mode-container-execution-docker-stopgap.md) permits a Docker Engine API client (`Docker.DotNet.Enhanced`) for **Development Mode build/test/lint execution only**, behind the existing `ISandboxRuntimeProvider` seam — AgentHome and Coder stay on the process provider. A container reference anywhere else is still a defect.

6. **An OS-specific NuGet stays on a plain `net10.0` target.** `Client.Application` references the leaf `NAudio.Wasapi` for Windows WASAPI process-loopback capture, **not** the `NAudio` meta-package, whose multi-targeting would force a Windows TFM onto the project. A package that ships a plain `netX.0` asset with an assembly-level `[SupportedOSPlatform]` needs no Windows TFM and no `EnableWindowsTargeting`: the platform is enforced by a type-level `[SupportedOSPlatform]` on the implementation plus an `OperatingSystem.IsWindows()` branch at the DI call site, which is what keeps CA1416 green. A CA1416 suppression is never the answer. See [24-audio-transcription.md](24-audio-transcription.md#windows-per-application-capture).

## Composition root: the `AddNode*` modules

Every per-feature registration lives in an `AddNode*` module extension under
`XE-Local-AI-Engine.Client.Application/DependencyInjection/Modules`; `AddNodeApplication`
(`NodeApplicationServiceCollectionExtensions`) is the orchestrator that invokes them, and the host adds only what it
owns on top.

**The order in that orchestrator is load-bearing, because `IHostedService` instances start in registration order.**
Where a module's startup reconciler must terminalize its own crash-orphaned rows before another module's dispatcher
begins adopting them, the only lever is placing the module earlier — the dependency between such modules is often not
one-way (Development tasks and DevTask node runs read each other's stores), so the order buys start order, nothing else.
Two further ordering rules hold today:

- `AddNodeExternalProviders` runs **before** `AddNodeModelRuntime`, which registers the external multiplexer provider
  only when an `IExternalProviderRegistry` is already in the collection. That is a registration-time check: a registry
  added afterwards ships a node on which no external model can route.
- The container feature registers as one chain — `AddNodeContainerSandbox` → `AddNodeContainerRuntime` →
  `AddNodeExternalAppsCatalog` → `AddNodeExternalApps` — because each link takes a singleton the previous one
  registers (the daemon attestation, then the resolver, then the catalog). None of it is resolved on the startup path.

Modules that reuse a shared client rather than registering their own (the images, transcription and training-runtime
modules all reuse the Hugging Face download client `AddNodeModelRuntime` registers) are ordered after their provider
for the same reason.

## Build & package conventions

Repo-wide MSBuild config lives at the solution root:

- **`eng/ReleaseVersion.props`** is the single release-identity source (`VersionPrefix` + optional `VersionSuffix`), imported by `Directory.Build.props`. `Directory.Build.props` sets `net10.0`, `Nullable`/`ImplicitUsings` enabled, C# 14 (`LangVersion 14.0`), and a **strict analyzer wall**: `TreatWarningsAsErrors=true`, `AnalysisMode=All`. Production projects additionally get `Meziantou.Analyzer` + `Microsoft.CodeAnalysis.BannedApiAnalyzers` (with `BannedSymbols.txt`); test/tooling projects (`*.Tests`, `*.Testing*`, `*.AppHost`) are exempted via the `IsTestOrToolingProject` flag. A literal `TODO`/`FIXME` in a comment fails the build (Sonar S1135 = error); describe the present limitation or rationale directly without task markers.
- **`Directory.Build.targets`** gates that whole analyzer wall to **Release and CI**. Since 2026-07-31 it disables build-time analyzers when `Configuration == Debug` and neither `CI` nor `XE_FULL_ANALYSIS` is set (84 s → 10 s on the Tests module), which maps to csc `-skipanalyzers`; the property is `RunAnalyzersDuringBuild=false` (since 2026-09-16), so the skip is build-time only and Rider and Visual Studio keep showing analyzer squiggles while you edit. **A green local Debug build proves nothing about Sonar, Meziantou, BannedApiAnalyzers or the `IDExxxx` style rules — including the bare-`TODO` rule above.** Iterate in Debug, then finish with `dotnet build XE-Local-AI-Engine.slnx --configuration Release`, or the packaging script will reject what compiled fine for you. `XE_FULL_ANALYSIS=1` forces the full pass in Debug. `TreatWarningsAsErrors` is unaffected, so genuine compiler warnings still fail a Debug build, and source generators still run (so TUnit discovery is intact).
- **`Directory.Packages.props`** enables **Central Package Management** (`ManagePackageVersionsCentrally=true` in `Directory.Build.props`); every dependency is pinned by a `<PackageVersion>` entry there — that file is the inventory. Add new dependencies as a `<PackageVersion>` there, then a versionless `<PackageReference>` in the consuming `.csproj`.
- **`global.json`** pins **`10.0.401`** with `rollForward: latestPatch` (no prerelease) — only the patch digit may move, so the SDK stays inside the `10.0.4xx` feature band, and CI's `actions/setup-dotnet` reads the same file and honours `rollForward`, so local and CI share one band — and sets the test runner to **Microsoft.Testing.Platform (MTP)** — test projects are `OutputType=Exe` and run via MTP, not VSTest.
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
