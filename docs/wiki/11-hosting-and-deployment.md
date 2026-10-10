# Hosting, AppHost & Deployment

> Reviewed: 2026-10-02 · Code-grounded.

**What this page covers.** How the node process is hosted and shipped: the dev-only Aspire AppHost, `ServiceDefaults`, the host pipeline in `Program.cs`, the configuration layers, the packaged desktop, browser, headless and MCP-only modes, publish profiles, installers and release channels. Aspire never ships; packaged builds serve plain HTTP on loopback from a per-user data directory.

**Read this if you are** changing startup order or a hosted service, adding a configuration key, or touching packaging, installers or update channels. **Skip to** [§3](#3-the-node-host-pipeline-programcs) for the boot pipeline; **reference tables** are in [§4](#4-configuration-layering) and [§6](#6-publish-profiles-launchers--rc-packaging); **related pages:** [01](01-architecture-overview.md), [13](13-testing-and-validation.md), [12](12-security-and-privacy.md).

## Contents

- [1. Aspire AppHost (dev/integration)](#1-aspire-apphost-devintegration)
- [2. ServiceDefaults](#2-servicedefaults)
- [3. The node host pipeline (Program.cs)](#3-the-node-host-pipeline-programcs)
- [4. Configuration layering](#4-configuration-layering)
- [5. Packaged local modes: native desktop, browser and headless](#5-packaged-local-modes-native-desktop-browser-and-headless)
- [6. Publish profiles, launchers & RC packaging](#6-publish-profiles-launchers--rc-packaging)
- [7. Installers & uninstaller](#7-installers--uninstaller)
- [8. Release channels and CI status](#8-release-channels-and-ci-status)
- [Isolated managed-runtime storage](#isolated-managed-runtime-storage)
- [Related pages](#related-pages)

This page covers how the XE Local AI Engine node process is **hosted and shipped**: the Aspire AppHost used for local dev/integration, the shared `ServiceDefaults`, the configuration layers (`appsettings` + the user-editable `node-settings.json` + the encrypted `hf-token.enc`), the background hosted services that run inside the node, packaged **desktop mode** (`XE_LAUNCH_MODE=desktop`), the asymmetric Windows/Linux publish profiles, the Windows C# launcher, and the legacy/manual cleanup scripts.

The engine supports these hosting paths; packaged launches additionally select a native window, browser or headless operation:

| Mode | Entry point | Used for | HTTP/HTTPS | DB + secrets source |
|------|-------------|----------|------------|---------------------|
| **Aspire dev / integration** | `XE-Local-AI-Engine.AppHost/AppHost.cs` orchestrates the `app` project | Local development and integration checks via the worktree-scoped `scripts/dev-*.sh` helpers | HTTPS (Kestrel default URLs) | Aspire parameters + env (`XE_NODE_SQLITE_KEY`, SQLite resource) |
| **Packaged desktop** | Linux self-contained native shell or Windows framework-dependent launcher + native shell + engine DLL | Shipped portable app a user double-clicks | Plain HTTP on loopback `127.0.0.1:<auto-port>` | Per-user data dir; connection string + operator key synthesized at startup |
| **MCP-only local mode** | The same packaged binary with `--mcp-only` or `XE_LAUNCH_MODE=mcp-only` | Unattended external-agent operation without browser launch | Plain HTTP on loopback `127.0.0.1:<remembered-or-requested-port>` | The same per-user data, provisioning, and single-instance lease as desktop |

Aspire hosting remains independent of the shell. Packaged browser and headless modes reuse the local engine bootstrap without starting Avalonia.

---

## 1. Aspire AppHost (dev/integration)

`XE-Local-AI-Engine.AppHost/AppHost.cs` is a thin Aspire orchestration host (`IsAspireHost=true`). The AppHost SDK is 13.5.4. It references only the `Client` project and four hosting packages: `Aspire.Hosting.AppHost` 13.5.4, `Aspire.Hosting.JavaScript` 13.5.4, `Aspire.Hosting.Browsers` 13.5.4-preview.1.26464.4, and `CommunityToolkit.Aspire.Hosting.Sqlite` 13.5.0.

From the repository root, use the worktree-scoped lifecycle wrappers:

```bash
scripts/dev-start.sh
scripts/dev-status.sh
scripts/dev-stop.sh
```

Start always uses Aspire isolated mode, so parallel worktrees receive randomized ports and isolated
user secrets. Status output is an allowlisted projection: resource state/health and query-free URLs,
not raw environment/properties or dashboard login tokens. Stop is AppHost-qualified and contains a
bounded Aspire 13.4 snapshot fallback. It anchors the DCP sibling by its exact
`--monitor <AppHost PID>` token pair, follows the complete descendant closure regardless of process
name, and revalidates each PID's kernel start time before signalling. Unrelated processes are
untouched and do not invalidate successful teardown of the selected graph; Aspire
query/malformed-JSON failures remain nonzero rather than being treated as stopped. For a live
integration probe, `scripts/aspire-readiness-smoke.sh` starts a fresh instance, uses `aspire wait app`
for readiness, and traps cleanup through that scoped stop path.

What it wires (`AppHost.cs`):

- **`node-sqlite-key`** — an Aspire parameter marked sensitive (`builder.AddParameter("node-sqlite-key", secret: true)`). **No value is tracked.** The AppHost's `appsettings.Development.json` used to commit one shared development-only default, which meant anyone with the source could derive keys for data created under it; that default was removed. Seeding is now the dev scripts' job: `dev_ensure_node_operator_secret` (`scripts/dev-aspire-common.sh`) mints a per-checkout base64 32-byte secret at `XE-Local-AI-Engine.AppHost/.data/node.key` (owner-only, `.gitignore`d) on first use and reuses it afterwards, and `scripts/dev-start.sh` hands it to Aspire as the environment variable `Parameters__node-sqlite-key` — the env form of the `Parameters:node-sqlite-key` configuration key Aspire resolves the parameter from. `--non-interactive` rules out a prompt and `--isolated` rules out the normal user-secrets store, so configuration is the only route. The value never reaches a command line, only process environments. A developer starting the AppHost some other way (IDE F5, bare `aspire run`) is prompted for the parameter instead, or can set it with `dotnet user-secrets set "Parameters:node-sqlite-key" <base64-32-byte> --project XE-Local-AI-Engine.AppHost/XE-Local-AI-Engine.AppHost.csproj`.
- **`node-sqlite`** — a SQLite resource (`builder.AddSqlite(...)`) backed by a file under `.data/node-sqlite/node-chat.db`. In Development it also enables `WithSqliteWeb()` (a browser DB inspector).
- **`app`** — the node web server (`AddProject<XE_Local_AI_Engine_Client>("app", "https")`) with external HTTP endpoints, `ASPIRE_ENABLED=true`, `ASPNETCORE_ENVIRONMENT=Development`, the SQLite key piped in as `XE_NODE_SQLITE_KEY`, `NodeAuth__Jwt__*` issuer/audience, a `WithReference`/`WaitFor` dependency on the SQLite resource, and two health checks (`/health/live`, `/health/ready`). Extra development URLs are surfaced for `/scalar` and `/openapi/local/v1/v1.json`.
- **`client-react`** — the Vite dev server (`AddViteApp(...)` with `WithPnpm()`), HTTPS endpoint on port **5175**, proxying to the `app` HTTPS endpoint via `VITE_PROXY_TARGET`, `WaitFor(app)`, and isolated Chromium browser logs.
  - **Exactly one endpoint, reconfigured rather than added.** `AddViteApp` already provisions one endpoint named `http` that the Vite dev server binds to, passing that endpoint's port to Vite through `--port`. Adding a second endpoint leaves the original `http` one claimed by the DCP proxy but never served, because Vite listens on a single port only — it accepts TCP and never responds. The AppHost therefore configures that same `http` endpoint to serve HTTPS on the fixed dev port, which is what `AddViteApp` itself upgrades when its cert mechanism is used. Since Vite receives the port through `--port`, the endpoint's own target-port environment variable name is irrelevant (nothing in `vite.config` reads it); only the scheme and the fixed host port matter.

> **No HostAgent, and no Docker resource in the AppHost.** The old in-Aspire `HostAgent.Linux` (Docker) sandbox/runtime resource and the HostAgent gRPC client are **gone** — the AppHost contains an explicit comment to that effect. Inference and the AgentHome sandbox run as **host processes** now (see [Local Runtime & Providers](03-local-runtime-and-providers.md)). The **Ollama** provider still exists in the codebase but was **de-orchestrated** from the AppHost — `llama.cpp` is the dev runtime and there is no Ollama resource in `AppHost.cs`.
>
> [ADR 0004](../adr/0004-development-mode-container-execution-docker-stopgap.md) (Accepted 2026-07-29) does **not** change this list: it permits Docker for **Development Mode build/test/lint execution only**, where the engine talks to the daemon itself at runtime. Aspire orchestrates no container, and reintroducing a Docker-backed AppHost resource is still out of scope. What it does add is a **packaging and quality-gate requirement** — the release machine needs a daemon, because Development Mode's real-daemon integration tests must run, and *daemon unavailable* is reported as blocked or skipped-with-reason, never as a pass.

---

## 2. ServiceDefaults

`XE-Local-AI-Engine.ServiceDefaults/Extensions.cs` provides the `AddServiceDefaults()` / `ConfigureOpenTelemetry()` extension over `IHostApplicationBuilder`. The key seam:

```csharp
builder.ConfigureOpenTelemetry();
var aspireEnabled = string.Equals(builder.Configuration["ASPIRE_ENABLED"], "true", ...);
if (aspireEnabled) { /* service discovery + resilience/discovery HTTP defaults */ }
```

OpenTelemetry logging, metrics, and tracing instrumentation is registered in **every** hosting mode. An
OTLP exporter is attached only when `OTEL_EXPORTER_OTLP_ENDPOINT` is configured; Aspire normally
injects that endpoint, while a default desktop/headless run records in-process without an exporter.
Only service discovery and the standard resilience/discovery HTTP defaults remain gated on
`ASPIRE_ENABLED=true` (which the AppHost sets). Tracing sources include
`XE.LocalAiEngine.AI.Agent`, `Microsoft.Agents.AI*`, and `Microsoft.Extensions.AI*`; the metrics meter
`XE.Node` is added by literal string because ServiceDefaults cannot reference the Client project.

---

## 3. The node host pipeline (`Program.cs`)

`XE-Local-AI-Engine.Client/Program.cs` builds a `WebApplication`. Startup order matters:

1. **Resolve launch mode early** — `DesktopLaunch.ResolveLaunchMode(args, VelopackInstall.IsManaged())`
   yields Headless, Desktop, or McpOnly. For either local mode, startup (a) resolves the per-user data
   directory, (b) acquires the single-instance lease, (c) binds through
   `DesktopPortStore.ResolveBindUrl(...)` or the validated requested port, and (d) synthesizes config
   via `DesktopBootstrap.EnsureLocalDataConfiguration(builder.Configuration)` **before** `AddServices`
   reads configuration.
2. `AddServiceDefaults()` then `AddServices(builder.Configuration)`.
3. After `Build()`: apply node-chat + node-identity EF migrations, recover interrupted chat messages, reconcile stale scheduled runs, eagerly activate the invocation-resume registry, and register the **worker shutdown drain** on `ApplicationStopping`.
4. Pipeline: Serilog request logging (with access-token query redaction), `UseExceptionHandler` (RFC7807), **HTTPS redirect + HSTS bypassed in desktop mode**, antiforgery, static files, health checks, `LocalApiSecurityMiddleware`, routing, rate limiter, auth, FastEndpoints (route prefix `LocalApiRoutes.Prefix`), 10 unconditional SignalR hubs plus conditional `DevelopmentAttemptHub` (all `RequireAuthorization(Operator)`), Scalar/Swagger (non-Production), and `MapFallbackToFile("index.html")` for the SPA.
5. **Desktop only**: `ActivateDesktopLifecycle(app)` installs the console-close → graceful-stop triggers and the on-started browser launch.

See [API & Hubs](09-api-and-hubs.md) for endpoint/hub detail and [Security & Privacy](12-security-and-privacy.md) for the loopback / `LocalApiSecurityMiddleware` invariants.

Three startup details are easy to undo by accident:

- **W3C trace correlation is forced, so it works with OpenTelemetry OFF** (the desktop/RC default). Setting `Activity.DefaultIdFormat`/`ForceDefaultIdFormat` to W3C and registering an `ActivityListener` for the `Microsoft.AspNetCore` source makes ASP.NET create a request `Activity` from an inbound `traceparent` even when no OTel listener is present; otherwise `Activity.Current` would be null in the pipeline and the emitted trace id would regress to the Kestrel connection id (`TraceIdentifier`). The listener is scoped to that one source — the only one producing the request activities this needs — rather than every source in the process. `AllData` makes it request all data for the activities that source creates, so their W3C trace and span ids are populated; it does not by itself record them, which would need `AllDataAndRecorded`. The emitted `traceresponse` trace-flags byte follows the activity's actual recorded state: with only this listener attached, activities are never recorded, so the byte is `00` regardless of the inbound sampled flag, while in the normal host the OpenTelemetry `TracerProvider` is also running and its default `ParentBased(AlwaysOn)` sampler does record, so a sampled inbound parent yields `01`. It is process-global, so it is set once before `Build()`.
- **The Open Canvas import is split around the migration pass.** `DropCanvasWorkflows` removes the table the saved workflows live in and no migration can decrypt their graph blob, so the read runs first and the write IMMEDIATELY after the node-chat pass. The read stages the encrypted rows into `canvas_workflow_import_recovery` before the drop, and the write imports from that table and removes it in one transaction, so a crash or a throw between the two passes is retried on the next start rather than lost. A failed read or import stops startup instead of continuing without the canvases. Details: `docs/wiki/21-graph-workflows.md` §9.
- **The rate-limiting middleware is skipped in the `Testing` environment**, where the permit limits are relaxed to non-limits anyway. `RateLimitingMiddleware` never disposes its `PartitionedRateLimiter` (verified against `Microsoft.AspNetCore.RateLimiting` 10.0), so its 100 ms replenishment timer outlives host disposal and GC-roots the middleware pipeline — logger, DI root scope, the entire host — for the process lifetime. `RequireRateLimiting` endpoint metadata stays registered and is inert without the middleware, and no test asserts 429s. See "Registration-time closures".

### Background (hosted) services

Registered via `AddHostedService<>` in `XE-Local-AI-Engine.Client/ConfigureServices.cs`. These are the always-on workers inside the node process. A hosted service that needs a persistence store or a concrete provider's contract is **not** one of these: the host-dependency rule (see [Code Conventions](16-code-conventions.md)) forbids the host from holding either, so such a service lives in `Client.Application` and is registered by its own feature module. Feature modules register more of their own — `grep -r AddHostedService` across `Client.Application/DependencyInjection/Modules/` and the provider projects is the complete inventory; the module-owned queue workers are called out under the table:

| Service | Role |
|---------|------|
| `ModelRecommendationScheduleSeeder` | seeds the model-fit recommendation schedule (see [Model-Fit](07-model-fit.md)) |
| `DefaultAgentSeeder`, `CoderAgentSeeder`, `MathematicianAgentSeeder` | seed built-in agent definitions (see [Agent Mode](04-agent-mode.md)) |
| `ToolCallCleanupService` | clears stale tool-call state |
| `NodeChatContentEncryptionBackfillService` | one-shot backfill upgrading legacy plaintext message/metadata rows to the encrypted at-rest envelope |
| `KnowledgeVectorNormalizationBackfillService` | one-shot backfill L2-normalizing legacy (pre-normalization) KB chunk vectors so cosine search can score with a plain dot product |
| `NodeChatTitleEncryptionBackfillService`, `OllamaProviderMapBackfillService`, `ExternalAccessProfileBackfillService`, `UiModeBackfillService`, `SandboxSecurityProfileBackfillService` | one-shot data backfills (the last three: see [Upgrade backfills](#upgrade-backfills-and-their-discriminators)) |
| `FirstRunModelProvisioningService` | desktop first-run GGUF starter-model download |
| `BenchmarkRunHubEventRelay`, `DatasetGenerationHubEventRelay`, `TrainingRunHubEventRelay` | drain each feature's in-process event buffer onto its SignalR hub, keeping `Client.Application` free of a SignalR dependency (see [API & Hubs](09-api-and-hubs.md)) |

Module-owned workers worth knowing about, registered alongside their feature rather than here:

| Service | Registered in | Role |
|---------|---------------|------|
| `BenchmarkQueueHostedService` | `AddNodeBenchmarksExtensions` | single-consumer durable benchmark run queue |
| `DatasetGenerationHostedService` | `AddNodeTrainingDatasetExtensions` | single-consumer durable dataset-generation queue (see [Training](18-training.md)) |
| `TrainingRunQueueHostedService`, `TrainingRunStartupReaper` | `AddNodeTrainingRunExtensions` | the single-consumer training/evaluation run queue, and the startup reaper that kills Python trainers orphaned by a host crash using their persisted launch receipts |
| `KnowledgeIngestionWorker`, `KnowledgeScheduledModelReindexWorker`, `KnowledgeBlobOrphanSweeper` | `AddNodeKnowledgeBaseExtensions` | KB ingestion queue, scheduled reindex, and the one-shot startup sweep that reclaims document blobs whose row is gone (see [Knowledge Base](15-knowledge-base.md)) |
| `McpAgentRunDispatcher`, `McpAgentRunRecoveryService`, `McpAgentRunCompactionService` | `AddNodeMcpAgentRunsExtensions` | inbound-MCP agent run dispatch, restart recovery, compaction |
| `RetentionSweeperService` | `AddNodeChatExtensions` | chat retention sweep, disabled by default (see [Security & Privacy](12-security-and-privacy.md)) |
| `AgentExecutionLogRetentionService` | `AddNodeAdaptiveMemoryExtensions` | ages out the append-only `agent_execution_logs` telemetry |
| `SchedulerHistoryRetentionService`, `SchedulerJobDetailReconciliationService` | `NodeSchedulerServiceCollectionExtensions` | scheduler history sweep and the Quartz job-detail startup self-heal (see [Scheduler](06-scheduler.md)) |
| `KeepModelWarmBackgroundService`, `LlamaCppUpdateCheckService` | `AddNodeModelRuntimeExtensions` | opt-in local-model residency keeper, and the one-shot llama.cpp runtime update check (see [Local Runtime & Providers](03-local-runtime-and-providers.md)) |
| `ImageJobStartupReconciler`, `DevelopmentStartupReconciler`, `LocalModelDeletionStartupReconciler`, `SandboxOrphanReaper`, `DetachedInvocationReaper` | their feature modules | restart reconciliation and orphan reaping |
| `StaleProcessReaper` (llama-server instance), `CudaBuildStartupService`, `LlamaServerRuntimeOverrideStartupNotice` | `Providers.LlamaServer` | see [Local Runtime & Providers](03-local-runtime-and-providers.md) |
| `StaleProcessReaper` (sd-server instance), `StableDiffusionCppSourceBuildLifecycle` | `Providers.StableDiffusionCpp` | see [Image Generation](14-image-generation.md) |
| `GgufAcquisitionArtifactStartupReaper` | `Providers.HuggingFace` | sweeps partial GGUF acquisition artifacts left by a previous run |
| `AppUpdateCheckService` | `AddAppUpdateExtensions` (local mode only) | one app self-update check per start, off the startup path (see [In-app self-update](#in-app-self-update-velopack)) |
| `AgentHomeRunRetentionService` | `AddNodeAgentHomeExtensions` | retention sweep for the on-disk AgentHome run directories |
| `DevelopmentWorkspaceRetentionService` | `AddNodeDevelopmentExtensions` | retention sweep for finished Development Mode task workspaces (see [Architecture Overview](01-architecture-overview.md#development-mode-registered-source-managed-worktree)) |
| `DevWorkflowDefinitionSeeder`, `DevWorkflowStartupReconciler` | `AddNodeDevWorkflowsExtensions` | seed the shipped definition templates by slug; make node runs a crashed host left mid-flight dispatchable again (see [Dev Workflows](25-dev-workflows.md)) |
| `GraphWorkflowStartupReconciler` | `AddNodeGraphWorkflowsExtensions` | make node runs a crashed host left in flight judgeable again (see [Graph Workflows](21-graph-workflows.md)) |
| `WorkSessionStartupReconciler`, `WorkSessionAgentSeeder` | `AddNodeWorkSessionsExtensions` | collapse sessions a crashed host left mid-flight to `Interrupted`; seed the General and Research work-session personas |
| `DockerSandboxOrphanSweeper` | `AddNodeContainerSandboxExtensions` | remove Development Mode containers orphaned by a previous run of this installation |
| `ExternalAppStateObserver` | `AddNodeExternalAppsExtensions` | notice that a container the engine believes is running has stopped (see [External Apps](23-external-apps.md)) |
| `ExternalProviderStartupReconciler` | `AddNodeExternalProvidersExtensions` | one external-provider reconciliation pass at startup, priming the registry snapshot |
| `IntegrationExecutionCoordinator` | `AddNodeIntegrationsExtensions` | single consumer of the integration accept queue and sole producer of an execution's terminal event |
| `ToolCapableModelBackfillService`, `McpServerStartupConnector` | `AddNodeModelCapabilitiesAndMcpExtensions` | back-fill the tool-capable model allow-list from installed models; connect enabled MCP servers once, off the hot path |
| `TranscriptionSessionLifecycleService` | `AddNodeTranscriptionExtensions` | fail sessions a dead process left `Transcribing`; end live sessions on graceful stop (see [Audio Transcription](24-audio-transcription.md)) |
| `WhisperCppSourceBuildLifecycle` | `Providers.WhisperCpp` | reconcile managed source-build state before the host is ready; drain an in-flight build on shutdown |

### First-run model provisioning (desktop)

`FirstRunModelProvisioningService` ensures a small node-local GGUF chat model is installed through the bundled llama.cpp runtime and selected, so a fresh double-click install can chat without the operator first downloading a model or running Ollama. The shipped starter is `FirstRunModel` = `unsloth/granite-4.1-3b-GGUF` at `Q4_K_M` (about 2.1 GB; `Agent:LocalChat:DefaultModel` carries the same `{RepoId}:{Quant}`), picked because it handles tool calls and background jobs reliably at that size.

- **Where the desktop decision comes from.** Whether the process was launched in desktop mode is a host fact — the process's own command line plus the Velopack install kind — that `Program.CreateAppCoreAsync` resolves once and registers as the `NodeLaunchContext` singleton. The service reads that, so it lives in `Client.Application/Services/ModelFit/Implementation/` while `ConfigureServices` still registers it at the same position. The three llama.cpp contracts it needs (the GPU-variant probe, the binary ensure and the acquisition-status report) arrive through `LlamaCppRuntimeOrchestrationService`.
- **Desktop-gated, non-blocking, offline-tolerant, idempotent.** The whole flow runs only when the process was launched in desktop mode (`XE_LAUNCH_MODE=desktop` / `--desktop`), so headless, Aspire and CI runs are byte-behavior-unchanged and never auto-download a model. All work runs in `ExecuteAsync` off the startup path, so a multi-GB binary or model download never blocks the host from coming up, and any transport failure (Hugging Face unreachable, binary acquisition failure) is caught and logged, leaving the empty-picker onboarding as the fallback. It no-ops when a GGUF is already installed or a non-default `DefaultModelName` is set, so it provisions at most once and is safe to run on every boot.
- **Acquisition visibility, scoped.** The GPU-probe segment reports to the acquisition-status registry so the operator sees why a fresh install sits idle; the binary manager reports the download/verify/extract phases itself. This service owns the terminal `RuntimeAcquisitionPhase.Failed` for the probe segment **alone**, never for the whole flow: the outer `ExecuteAsync` catch also spans the model download and the settings save, so a throw from either would overwrite a legitimate `Completed` with a false runtime failure, and the banner's retry would be a dead button attached to a wrong diagnosis. Cancellation is excluded, because a shutting-down host is not an acquisition failure. The channel opens at the probe rather than at the top of the flow, since the probe is the first of the two silent multi-second phases an operator sees no explanation for.
- **One ceiling over the whole GPU-variant selection.** Detection prefers a non-shelling NVML driver-presence signal and only shells out to vendor tools (`nvidia-smi` / `wmic`) as a fallback, and those can hang on some Windows hosts. A linked `CancellationTokenSource` with a hard ceiling governs the whole selection; the probe is cancellation-linked and reaps any child process it spawned in a `finally`, so cancelling it can never leave an orphan, and there is no second wall-clock race or abandoned probe task. On timeout the service falls back to the CPU runtime — a FALLBACK, not a failure, so nothing publishes `Failed` for it, because the run goes on to acquire the CPU runtime and provision normally.
- **The binary is ensured before the model is downloaded**, so the model is immediately runnable, and the download goes through the coordinator's detached path so progress, cancel and the llama.cpp `model_provider_map` write all happen through the SAME code as an operator-initiated download (FRR-2). The ticket carries the canonical `{repo:quant}` identity the model is installed under.
- **Selecting the model is a read-modify-write under the settings store's lock**, never a save of the record loaded before the download. That wait can run for minutes and the settings record is whole-file, so saving the stale copy would silently roll back everything written meanwhile — a machine key minted at boot, an operator's edit. The skip precondition is re-checked against the write-time record too: the operator can pick a model from the picker during those minutes, and assigning unconditionally reverted their choice to the auto-provisioned one.

### Upgrade backfills and their discriminators

Four hosted services exist only to decide what an UPGRADING node should have been carrying all along. None is desktop-gated: an upgrading node exists on every launch mode.

**`ExternalAccessProfileBackfillService`** stamps `recommended` on a node that carries no external-access member at all. **A null profile alone is NOT the discriminator.** The legacy install this backfill exists for predates every external-access member, so all four are absent; a record whose profile is null but which carries any of the three switches has been touched by an operator or a hand edit, so keying on the profile alone would backfill all three switches to `true` over persisted `false` opt-outs and restart the update checks and the starter-model download the operator had turned off. `IsUntouchedByTheFeature` is what guards that switches-without-profile case, and such a node stays undecided until an operator answers on the Node Settings page. An unrecognised profile (`"Offline"`, say) never reaches the backfill as null: `NodeSettingsStore.NormalizeExternalAccessProfile` loads it as `pending`, a non-null profile the backfill leaves alone by the ordinary rule.

**`UiModeBackfillService`** stamps `advanced` on a node whose operator has already been through first-run onboarding, which is exactly the navigation such a node showed before the mode existed. **The discriminator is not "an administrator exists":** that is the moment first-run onboarding STARTS, not the moment it finishes, since the setup endpoint persists the administrator and stamps the external-access profile `pending` in the same call before the operator answers the external-access step and the mode step. Keying on the administrator alone would stamp `advanced` on a brand-new node that restarted while its operator was still looking at the external-access chooser, and that operator would never be asked. So the discriminator is the SUCCESSOR state of the step before it: an administrator exists AND the external-access profile is anything other than `pending`. That deliberately includes a null profile — what an upgraded node carries until `ExternalAccessProfileBackfillService` stamps it, and what a node that answered its switches by hand keeps for good — because both are installs whose operator long since finished whatever onboarding existed for them; reading the state rather than the other service's output is what keeps the two backfills independent of their start order. **One window stays open:** a node restarted between the external-access answer and the mode answer is stamped `advanced` and not asked again. Closing it would need a third `pending`-style literal the mode does not otherwise need, and the cost of leaving it open is that such an operator sees the full navigation and changes it on the Node Settings page, which is what the step's own copy tells them.

**`SandboxSecurityProfileBackfillService`** stamps `low`, today's sandbox behaviour, on an upgrading node whose `SandboxSecurityProfile` is null. **The discriminator is the one `UiModeBackfillService` uses**: an administrator exists AND the external-access profile is anything other than `pending`. The setup endpoint stamps both `pending` literals in the same call, so a brand-new node that restarts while its operator is still on the external-access chooser keeps `pending` on the profile and is asked, while a node that long since finished onboarding is not asked a question it never had. Reading that state rather than another service's output keeps all three backfills independent of their start order. **A fresh install is always asked:** setup stamps both literals in one write, so a restart between the external-access answer and the profile answer keeps the profile `pending`. A null profile beside a decided external-access profile exists only on a node whose setup ran on a build before ADR 0020 and that was upgraded between the two answers: it is not asked, runs under `low` (null reads as `low`), and is stamped `low` at its next boot; that operator changes it on the "Sandbox & isolation" section of Node settings. There is no configuration seed for the profile: a fresh node is asked, an upgraded node is backfilled.

**These three run their work in `StartAsync`, not a background loop.** `OllamaProviderMapBackfillService` is the precedent for their shape, scoping, idempotence and swallowed failures, but its justification for being OFF the startup path — Ollama may be slow or unreachable — does not apply: each is one node-local identity read and at most one settings write, no network, so blocking start costs milliseconds. Blocking is the POINT. Under minimal hosting (`Program.CreateAppCoreAsync` builds with `WebApplication.CreateBuilder`) the web-host service is registered last, so a user-registered hosted service's `StartAsync` completes before Kestrel accepts a request; a background loop would leave a window in which the SPA could read `externalAccessProfile: null`, `uiMode: null` or `sandboxSecurityProfile: null` from a node that has been running for months and offer its operator a first-run choice they already made. `Program.CreateAppAsync` additionally runs `ApplyNodeIdentityMigrationsAsync` before `app.RunAsync()`, so the identity read cannot race the identity migration. All three also take the **STRICT** settings load rather than `LoadAsync`: a present-but-unreadable settings file must not read as "nothing decided yet", because `LoadAsync` hands back a default record and the backfill would then decide on the strength of a corrupt file.

**`OllamaProviderMapBackfillService`** closes the FRR-2 upgrade gap. Before the unmapped-routing default was flipped to `llamacpp`, the Ollama pull endpoints never wrote a `model_provider_map` row, so every model pulled on an EARLIER build is unmapped; under the flipped default those models would silently re-route to llama.cpp and fail to dial Ollama on the next send. The service maps each currently-installed Ollama model that lacks a map row to `ollama`, restoring its routing, and leaves already-mapped models untouched — new pulls write the row at pull time, so this is purely a migration for pre-existing data. It is a node-local read of installed models followed by additive upserts: it never starts an advisor run, downloads anything, or contacts the central platform. Unlike the three above it runs in `ExecuteAsync`, off the startup path (mirroring `NodeChatTitleEncryptionBackfillService` and `FirstRunModelProvisioningService`), so a slow or unreachable Ollama never blocks the host from coming up; listing failures are swallowed and logged, and each model is mapped only when it has no existing row, so re-running on every boot is a cheap no-op once the rows exist. As an `IHostedService` it is also removed by the test host's `RemoveAll<IHostedService>()`, so it never perturbs request-path tests.

### The container bridge listener

The bridge is the engine's one deliberately non-loopback Kestrel endpoint. `ContainerBridgePipeline.Map` branches its connections into a pipeline of their own, **first** in the application pipeline: everything the node serves — the SPA bundle, `/api/local/v1`, the SignalR hubs, the MCP endpoint — is registered after it, so a request that arrived on the bridge port never reaches any of it, and the branch predicate is the arrival port, so nothing mapped inside the branch is reachable on the main listener. The discriminator is the socket's whole local end, address AND port, because that is the one fact a caller cannot forge and because the port alone is ambiguous on a node whose loopback listener happens to carry the bridge's port. See [ADR 0011](../adr/0011-container-bridge-listener.md) and [API & Hubs](09-api-and-hubs.md).

**Two machine questions are answered before the bridge URL joins the bind list**, by `ContainerBridgeListenerProbe`. They live there rather than on `ContainerBridgeEndpointResolver` on purpose: the resolver is a pure function of an interface snapshot, which is what makes its rules testable against machines this one is not, and opening a socket there would end that property. The resolver decides what the bridge WOULD be; the probe decides whether the node can actually have it.

- **Can the exact address and port be bound right now?** The bridge port is a fixed default, deliberately: a container is handed the endpoint when it is created and has to find the same port after the node restarts, which a randomised or persisted port cannot promise. The cost is that a SECOND node on the same machine (another checkout, or a desktop node beside a dev one) resolves the same address and port as the first; without the probe Kestrel fails that bind, and because the bridge URL travels in the same bind list as the loopback one, the whole host fails to start and the second checkout loses its UI and API entirely. That breaks the isolated-checkout contract, so the second node opens no bridge and boots. The probe uses **no `SO_REUSEADDR`, deliberately**: the question is whether Kestrel will succeed, and a probe more permissive than the real bind would answer yes and still let the host die. A .NET socket is created with `SO_REUSEADDR` clear and Kestrel's listening socket takes that default too, so a probe with these defaults answers what Kestrel answers BY CONSTRUCTION and can never be the stricter of the two. The restart case the option is usually reached for does not arise: a port whose previous listener closed leaves a `TIME_WAIT` entry that Linux lets the next bind have anyway, which `ContainerBridgeListenerProbeTests` pins against a real Kestrel host rather than by assertion.
- **Is the port already claimed by one of the host's own bind URLs?** This is the case the socket probe CANNOT see, because at that point in startup the node has not bound its loopback listener yet — the port is free, and would stop being free a moment later. It matters because the branch predicate would then be ambiguous: a desktop launch given `--port 18790` puts the loopback listener on the bridge's port, and ordinary SPA and API requests would arrive on a port the bridge claims. Refusing to open the bridge keeps the node's own surface working; the bridge is the optional half.

**The bridge's host names are added to `AllowedHosts`**, without which the listener is unreachable. `HostFilteringMiddleware` is installed by an `IStartupFilter`, so it runs before every middleware the composition root registers, the bridge branch included; the shipped `AllowedHosts` is `localhost;127.0.0.1;[::1]`, exactly right for a loopback-only node and exactly wrong for the one listener that deliberately is not — a container's `Host: 172.20.0.1:18790` would be refused with 400 before anything could look at it. Widening the list widens it for the loopback listener too, since host filtering is per host and not per endpoint, and that costs nothing the node was relying on: `LocalApiSecurityMiddleware` checks `Host` and `Origin` against its own list for every `/api/local/v1` request and rejects a non-loopback peer outright, and neither check reads this setting. The widening is written through a **dedicated in-memory provider** rather than the configuration indexer: the indexer calls `Set` on every provider and a read then wins from the last provider holding the key, so the widening would survive only while a non-reloading provider happened to sit after the JSON one — and since `HostFilteringOptions` is monitor-backed and `appsettings.json` reloads on change, that is a reload away from silently reverting. One highest-precedence provider cannot be replaced by a reload.

**Any future non-loopback listener needs the same step**, in the same place the listener is opened, with `ContainerBridgePipeline.AllowBridgeHost` as the shape. In-process tests cannot catch a miss: `DefaultHttpContext` has no host filter, and an in-process `HttpClient` sends `Host: 127.0.0.1`, which the shipped list already allows. The live symptom is a 400 that none of the node's logs record, because the request is refused before any middleware of ours runs; capture the raw bytes first (`nc -l` on the bound address) instead of adding logging. `ContainerBridgeHostFilteringTests.AllowBridgeHost_IsCalledByTheCompositionRoot` is what ties the bridge's widening to the composition root.

The bridge answers in the OpenAI-style error envelope its callers understand, not RFC 7807, and its routes sit deliberately OUTSIDE `/api/local/v1`: inside that prefix `LocalApiSecurityMiddleware`'s loopback-peer check would reject exactly the traffic the bridge exists to accept, and the peer guard plus the per-instance token gate are what replace it. It runs the **same** `LocalModelProxyForwarder` the loopback model proxy uses rather than a second copy: the five-step call sequence that type owns — model existence, supervisor, endpoint, inference lease, streamed forward with an idle-read watchdog — is the whole reason a container's request is safe to serve.

---

## 4. Configuration layering

Configuration resolves through several layers (later wins where noted):

1. **`appsettings.json` + `appsettings.Development.json`** (in `XE-Local-AI-Engine.Client/`) — static defaults shipped with the binary.
2. **Environment / Aspire parameters** — e.g. `XE_NODE_SQLITE_KEY`, `NodeAuth__Jwt__*`, the node-sqlite connection string. In Aspire these come from `AppHost.cs`; the operator-secret parameter has **no tracked value at all** — the dev scripts mint a per-checkout one, as §1 describes.
3. **Local-mode in-memory overrides** (`DesktopBootstrap`, Desktop/McpOnly only — added last so they
   intentionally win over `appsettings`, but only reached behind a local launch mode). See §5.
4. **`node-settings.json`** — a **user-editable, cached** settings file (not env/appsettings). `NodeSettingsStore` (`Client.Application/Services/NodeSettings/Implementation/NodeSettingsStore.cs`) reads/writes `node-settings.json` under the node data directory, with both an async and a sync (startup/DI factory) load path, tolerant JSON deserialize, and a `SemaphoreSlim` write lock. The shape is `StoredNodeSettings`. This is the runtime-editable settings store that supersedes baking values only into `appsettings`.
   **It is also read once BEFORE the host is built.** `NodeStartupSettings.Read` (`Client.Application/Services/NodeSettings/Implementation/NodeStartupSettings.cs`) loads the file, plaintext so it works while the vault is locked, after the vault step and `EnsureLocalDataConfiguration`, and resolves the three switches that decide what is registered or bound: `Development` (the Development endpoints and hub), `Scheduler` (Quartz, or the refusing management service) and `ExternalApps` (the container bridge listener, which also needs `ContainerBridge:Enabled`), each as stored ?? appsettings seed ?? code default. It resolves the data directory the way `NodeDataDirectory` does, and when the configured directory has no file yet it reads a legacy `node-settings.json` in the content root, the one `NodeDataDirectory` is about to move there. A test host (`customization` set) skips that legacy content-root file, because its content root is the source tree. The result is registered as a singleton, and a stored change to one of these three applies after a restart.
5. **`hf-token.enc`** — the optional Hugging Face access token, encrypted at rest. `HfTokenStore` (`Providers.HuggingFace/Implementation/HfTokenStore.cs`) uses an `IDataProtector` (`WorkerNode.HfTokenStore.v1`) to write `hf-token.enc` under the node data dir. The token is exposed **only** to the download client, **never** logged, never put in exceptions, never indexed — the same `IDataProtector` pattern as the cloud credential / worker token stores. See [Security & Privacy](12-security-and-privacy.md).

All per-node runtime artifacts (settings, encrypted credential stores, cert pins, the AgentHome workspace, the hardware-profile cache, the GGUF model cache) live under the **node data directory** (`INodeDataDirectory`), which defaults to `ContentRootPath` but is redirected to a per-user data dir in desktop mode (§5). See [Data & Persistence](08-data-and-persistence.md).

These options are set only through configuration (`appsettings*.json`, environment variables or `--` arguments) and have no field in the Node Settings form. Each binds at startup, so a change needs a restart. The list is what was checked against the options class and the SPA's `node-settings` feature; it is not every configuration-only key.

| Key | Default | What it governs | Read in |
|---|---|---|---|
| `AgentHome:Sandbox:RequireEgressDenial` | `false` | When set, the AgentHome, Coder and work-session sandboxes refuse to prepare on a backend that cannot deny egress, instead of running with the weaker posture. | `SandboxOptions`, decided in `SandboxEgressPolicy` |
| `Development:Sandbox:RequireEgressDenial` | `false` | The same precondition for Development Mode's agent-facing sandbox. The warm-restore sandbox is exempt. | `DevelopmentSandboxOptions`, decided in `SandboxEgressPolicy` |
| `Development:WorkspaceRetention:Enabled` / `RetentionAge` / `OrphanGrace` / `SweepInterval` | `true` / 7 days / 15 minutes / 6 hours | Whether and when finished Development Mode task workspaces and orphaned directories are deleted. | `DevelopmentWorkspaceRetentionOptions`, run by `DevelopmentWorkspaceRetentionService` |
| `Transcription:SegmentReplayLimit` | `500` | Maximum transcript segments replayed to a subscriber of `TranscriptionHub`; a "there is more" flag covers the rest. | `TranscriptionOptions`, read in `TranscriptionHub` |
| `Transcription:AbandonedSessionGraceSeconds` / `MaxBufferedAudioMb` | `60` / `512` | How long a session with no connection survives, and the cap on queued untranscribed audio. | `TranscriptionOptions` |
| `Agent:ToolPipeline:MaxToolCallsPerResponse` | `32` | Ceiling on tool calls one model response may hand the invocation loop; identical calls collapse and the rest are dropped. | `AgentToolPipelineOptions`, applied in `ProviderCallBudgetChatClient` |
| `Agent:ToolPipeline:MaximumToolIterationsPerRequest` / `MaxToolResultCharacters` / `MaxConsecutiveInvalidToolCallsPerTool` | `40` / `65536` / `3` | Round-trip cap per request, backstop size of one tool result, and consecutive invalid calls before a tool is disabled for the request. | `AgentToolPipelineOptions` |

`KnowledgeBase:AllowCloudModelAccess` is deliberately not in this table: the SPA does expose it as a Node Settings field.

The sandbox security profile is the opposite case and is not a row above: `SandboxSecurityProfile` is a stored node setting (`StoredNodeSettings`, literals `low`, `high`, `pending`; null means undecided and backfillable) with **no configuration seed**. It is written only through the Operator settings endpoint (REST), never through an agentic surface, and read on every sandbox create through `INodeRuntimeSettings`, so a change applies without a restart. The two `RequireEgressDenial` keys above stay configuration-only and tighten on top of it. Meaning and consequences: [ADR 0020](../adr/0020-sandbox-security-profile.md); backfill: [Upgrade backfills](#upgrade-backfills-and-their-discriminators).

### Registration-time closures

Two registrations in `ConfigureServices` are written the way they are purely so a disposed host can be collected, and both were measured at roughly 20 MB per test host (gcroot evidence in `docs/agent-knowledge.md` §1).

- **The rate limiter's permit limits are computed outside the `AddRateLimiter` lambda**, so its closure captures ints and never `builder`. The rate-limiting middleware's partitioned limiter runs a replenishment timer that is never disposed with the host, and a closure over `builder` let that immortal timer root the builder, its `ServiceCollection`, and the entire disposed host graph.
- **The MCP tools are registered with a per-host copy of the SDK's serializer options**, for the same class of reason: `Microsoft.Extensions.AI` caches each reflection-built tool descriptor in a static `ConditionalWeakTable` keyed by those options, and each descriptor captures the host's root service provider. See `AddNodeMcpServerExtensions.AddNodeMcpServer`.

### Logging and the Data Protection key-ring at registration time

Both are wired in `ConfigureServices.AddServices` and both have a rule that is easy to undo by accident.

- **Serilog's `writeToProviders` MUST stay `true`.** It defaults to `false`, which makes Serilog the terminus of the logging pipeline: events reach Serilog's own sinks and no other registered `ILoggerProvider`. The OpenTelemetry logger provider that `ConfigureOpenTelemetry` registers is one of those, so with the default every `ILogger` call dead-ends before the OTLP log exporter and the Aspire dashboard shows zero structured logs while traces and metrics still flow, because those bypass `ILoggerFactory` entirely. `Program.cs` calls `Logging.ClearProviders()` before `AddServiceDefaults`, so OpenTelemetry is the only other provider in the chain and forwarding cannot resurrect a duplicate console logger. The console sink is always on; a date-rolled file sink is added under the per-user data dir — the same resolution the key-ring below uses — so desktop and dev logs survive the console window closing and a tester bug report has on-disk history. It is disabled in `Testing`, where many parallel hosts would contend for the exclusive file.
- **Verbose logging is a session-only switch.** `NodeLogLevelSwitch` (a singleton, read from `Serilog:MinimumLevel:Default`, Information when unset) owns a Serilog `LoggingLevelSwitch`, and `AddServices` applies it with `MinimumLevel.ControlledBy` **after** `ReadFrom.Configuration`. The order matters: applied first, the configured default would overwrite it. Because only the default level changes, the configured `Microsoft*` and `System` Warning overrides keep applying. For the same reason verbose lowers the default for every namespace without an override, so third-party categories such as Quartz also log at Debug. The switch lives in memory only, so a restart restores the configured level. Each change writes one Information line (`SetLogLevelEndpoint`). `Serilog__MinimumLevel__Default` stays the way to change the level from start-up.
- **Crash hooks are registered first.** `Program.cs` calls `ProcessCrashHooks.Register` before the host is built. An `AppDomain` unhandled exception is logged at Fatal, appended to `startup-crash.log` through `StartupCrashLog.Record`, and the logger is flushed before the process ends. An unobserved task exception is marked observed and logged at Error. Both handlers never throw. The desktop shell (`DesktopStartupDiagnostics`) and the launcher (`StartupDiagnostics`) keep hand copies of the same idea because they take no project references.
- **`BackgroundServiceExceptionBehavior` stays at `StopHost`, deliberately.** A hosted service that throws already logs Error and Fatal and stops the host visibly. `Ignore` would keep the process up with a silently degraded node, which is the worse outcome for a local engine whose users read the log after the fact.
- **The key-ring is pinned and co-located, which is stability hardening rather than a confidentiality fix.** The framework already auto-registers Data Protection, so the encrypted token stores (`CloudCredentialStore`, `CodexTokenStore`, `HfTokenStore`) and the auth `TokenStore` are protected today. What the explicit registration adds is a STABLE application-name discriminator, so the key-ring never shifts between Velopack updates, and persistence under the SAME per-user data directory as the rest of the node state — the `NodeData:Directory` key `DesktopBootstrap` layers in for desktop mode, the content root otherwise, preserving the off-flag byte-behavior invariant — so the ring is co-located with `node.sqlite` / `node.key` and survives reinstalls instead of landing in the volatile default location. `AddDataProtection()` is idempotent (TryAdd-based), so this neither double-registers nor changes the `IDataProtectionProvider` existing consumers resolve.
- **At rest, Windows keeps DPAPI (CurrentUser) and everything else gets AES-256-GCM** under a KEK derived from the node operator secret, so the key-ring inherits the same env/secret-file protection as `node.sqlite` instead of sitting in plaintext beside the ciphertext it unlocks. The encryptor is WRITE-side only: existing plaintext keys and existing `IDataProtector` payloads keep reading, because Data Protection reads each key in whatever form it was written, and the current active key is deliberately not re-wrapped proactively. The fail-closed key resolver is applied on **both** schemes and deliberately outside that OS branch; details in [Security & Privacy](12-security-and-privacy.md) and in the `Client/Security/DataProtection/` type docs.


### Log files, child-process tails and the support bundle

- **The rolling node log is bounded.** `LoggerExtensions.WriteToRollingFile` writes `xe-node-<date>.log` under the data root's `logs/` directory, rolls at 50 MB and keeps 7 files, so a wedged component cannot fill the disk. Each line carries `[trace:<W3C trace id> span:<span id>]` (both empty outside a request), the same trace id a client receives in a problem body.
- **Request completion levels** (`Program.ConfigureRequestLogging`, `Program.Logging.cs`): an exception or a 5xx is Error, a 4xx other than 401 and 404 is Warning, and everything else (2xx, 3xx, the 401 token refresh, SPA-fallback 404s) is Debug, so the SPA's polling cannot dominate the file. Method and path are sanitized and the access-token query is redacted before they are written.
- **Three auxiliary logs are append-only and capped at 1 MB.** `startup-crash.log` (`StartupCrashLog`, before the real logger exists), `desktop.log` (`DesktopStartupDiagnostics`, a failure-only breadcrumb: empty after a healthy start) and `launcher.log` (`StartupDiagnostics` in the launcher) keep only their newest 256 KB, cut at a line start, once an append finds the file past 1 MB.
- **Child-process output tails.** `ChildProcessOutputTailRegistry` (singleton behind `IChildProcessOutputTails`) holds the output of the 8 most recent llama-server, sd-server and whisper-server processes, exited ones included: 200 lines or 32 KB each, path-sanitized on the way in. When full it drops the oldest exited entry first, then the oldest running one. The tail keeps every line; the llama-server launcher logs only marker lines at Information (error, warn, fail, CUDA, out-of-memory, backend, tensor-load and offload summaries, listening, all-slots-idle; see `LlamaServerProcessLauncher.IsMarkerLine`) plus a rate-limited `slot` progress line, and everything else at Debug. A crash that scrolled out of the node log therefore still shows up in the tail.
- **The support bundle** is `GET diagnostics/support-bundle` (`GetSupportBundleEndpoint`, Operator policy, `application/zip`, built in memory by `SupportBundleService`, well under 2 MB). It holds `manifest.json` (schema version, creation time, version, one status per entry: `included`, `absent` or `unreadable`, and whether it was truncated), `node-info.json` (the same report as `diagnostics/node-info`), the newest and previous `xe-node-*.log` tails (256 KB and 128 KB), the three auxiliary logs (64 KB tails) and one `processes/*.txt` per child-process tail. Logs are read from the first of these directories that holds the file: the data root, `XE_DATA_DIR`, then the per-user `XE-Local-AI-Engine` application-data folder (the desktop shell and launcher write there in a headless run). A fresh install or the Testing host has no log directory, so those entries are `absent`. The log and `processes/*` entries pass through `SupportBundleScrubber` as text; `node-info.json` and `manifest.json` are never scrubbed as serialized text (a text rule once turned the escaped `+` of a version into invalid JSON) — their free-text values (warnings, CPU model, settings values) are scrubbed before serialization and the files are written with relaxed JSON escaping. What the scrubber does and does not remove is in [Security & Privacy](12-security-and-privacy.md) under "Support bundle redaction".

---

## 5. Packaged local modes: native desktop, browser and headless

Why the shell is a separate process, who owns the engine, and why the Linux and Windows document
policies differ: [ADR 0013](../adr/0013-native-desktop-shell.md).

Packaged Windows and Ubuntu launches default to `XE-Local-AI-Engine.Desktop`.
`DesktopEngineSession` discovers a healthy engine for the selected data root or starts the
adjacent engine with `--desktop --no-browser`. The existing React bundle still uses REST,
SignalR and normal authentication; no second native application API is introduced.
`--browser`, `--headless`, MCP-only and operator commands bypass the native window
(`DesktopCommandLine.RunsEngine`, `WindowsLauncherApplication`).

The shell has a per-data-root single-instance lease and same-user activation pipe
(`DesktopInstance`). Starting it again restores the existing window. First close offers
Keep in tray, Quit or Cancel, with an optional remembered choice and desktop settings to
change it. Windows supports tray operation; Linux currently keeps tray unavailable rather
than hiding a window without a usable restore path. Quit stops only an engine the shell
started, never a separately running engine it attached to. The shell's own dialogs and tray menu
follow the SPA's `i18nextLng` language choice, persisted as `desktop-language.txt` in the data root,
with the OS UI culture only as the first-run fallback (`DesktopText`).

For an owned engine, `DesktopParentLifetime` connects before host construction. Parent loss
requests graceful shutdown with a bounded exit watchdog; `DesktopEngineSession` bounds its
own shutdown wait and owned-process cleanup. An attached standalone engine refuses in-app
update while a native shell holds its lease: close that shell and update through the browser.
Owned-engine updates coordinate through the shell/launcher process lifetime
(`AppUpdateService`, `FrameworkDependentVelopackBootstrap`).

A desktop launch shows no console. The Windows launcher starts the shell with `CreateNoWindow` and
calls `FreeConsole` (`WindowsLauncherApplication.ShouldDetachConsole`); every engine mode keeps its
console. The shell redirects an owned engine's stdout and stderr and reads them only for the
`XE_READY` line and a 4 KB error tail, so the engine's log is read from `<data dir>/logs`
(`xe-node-<date>.log`, beside `desktop.log` and `launcher.log`). The tray menu and the startup-failure
view both offer **Open logs folder**, which creates the directory if needed and opens it in the OS
file manager.

`--debug` is the opt-in exception, for desktop launches only. The launcher keeps its console and
forwards the flag; the shell (`DesktopStartupOptions.Debug`) enables WebView DevTools and, for an
engine it starts, sets `Serilog__MinimumLevel__Default=Debug` on the child and echoes its stdout and
stderr to its own console. The echo goes through a bounded drop-oldest channel with one writer task
(`DesktopEngineEcho`), so a blocked console (a Windows text selection, a paused pipe) drops lines
instead of stalling the engine's synchronous console sink. When the shell attaches to a running
engine, or hands off to another shell instance, it prints one line naming the logs directory
instead. Closing that console quits the app. Passing `--debug` together with an engine mode is
harmless: the engine reads its modes from the raw arguments, and the configuration command-line
provider only records an unused key.

Windows requires the .NET desktop payload's framework prerequisites and WebView2; missing
WebView2 produces an actionable startup error. Ubuntu requires WebKitGTK 4.1. Its restricted
GTK adapter verifies the actual top-level document policy, blocks embedded frames, and offers
microphone-only consent; camera/screen capture require browser mode. Blob export uses a
bounded Save dialog and validates the transfer (maximum 50 MiB). These restrictions do not
change the ordinary browser or Windows document policy (`NativeDesktopDocumentPolicy`,
`GtkDesktopBridge`).

For compositor-specific flicker, `WEBKIT_DISABLE_COMPOSITING_MODE=1` is an opt-in workaround,
not a production default. Real Ubuntu LTS X11/Wayland acceptance was waived for this delivery;
WSLg prototype checks are not equivalent. The delivery evidence and the acceptance checks left open
are recorded in [Native desktop checkpoint](../audits/2026-10-02-native-desktop-1.0-delivery-record.md) (historical record).

### Underlying engine bootstrap


`DesktopLaunch.ResolveLaunchMode` selects `Headless`, `Desktop`, or `McpOnly`. Desktop is chosen by
`XE_LAUNCH_MODE=desktop`, `--desktop`, or a managed package's default launch; MCP-only requires
`XE_LAUNCH_MODE=mcp-only` or `--mcp-only`. An explicit local-mode argument wins over the managed
desktop default, which prevents one-shot installer commands from opening a browser. With no local
signal, Aspire/CI headless behavior is unchanged. The shell binary honours the same variable:
`XE_LAUNCH_MODE=mcp-only` with no `--desktop` argument runs the engine unattended without a window
(`DesktopCommandLine.RunsEngine`), so the variable keeps working on a display-less machine.

```
 launcher/package selects a local mode
            │
            ▼
 Program.cs: isLocalMode = true
   ├─ Kestrel binds DesktopPortStore.ResolveBindUrl(dir)   (remembered port, else 127.0.0.1:0)
   ├─ DesktopBootstrap.EnsureLocalDataConfiguration(config)
   │     • NodeData:Directory   → %LOCALAPPDATA%/XE-Local-AI-Engine  (or $XDG_DATA_HOME)
   │     • ConnectionStrings:node-sqlite → Data Source=<dir>/node.sqlite   (if absent)
   │     • operator secret      → generated once, persisted to <dir>/node.key  (if absent); a v2 node.key
   │                              is the locked vault: nothing is injected, the pre-host runs first (ADR 0018)
   │     • HuggingFace:ModelsDirectory → <dir>/models                  (if absent)
   │     • Agent:LocalChat:DefaultModel → FirstRunModel repo:quant     (if configured)
   │     (each key filled ONLY when not already supplied → env/Aspire always wins)
   │
   ├─ HTTPS redirect + HSTS bypassed  (loopback HTTP is safe)
   │
   └─ ActivateDesktopLifecycle(app)  (DesktopLifecycle)
         ├─ on ApplicationStarted → write ready.json + exact XE_READY line
         ├─ desktop only → open default browser (`--no-browser` suppresses it)
         ├─ MCP-only → never open a browser
         └─ console-close → graceful StopApplication() (→ llama-server child reaped)
```

**Text fallback:** both local modes select a per-user data directory, fill only absent local configuration,
binds Kestrel to a remembered/free loopback port, skips HTTPS redirect/HSTS for that loopback HTTP
listener, write readiness evidence, and request graceful application stop when their console closes.
The standalone desktop bootstrap opens the browser unless suppressed; the native shell always suppresses it. MCP-only never opens a browser. Real packaged behavior still
requires observation on the target OS; this flow description is not a retained smoke-test transcript.

`--port <1-65535>` pins the requested loopback port and exits 6 if unavailable. On
`ApplicationStarted`, both modes print exactly one unformatted line in stable key order and write
canonical `ready.json` with `{version,url,mcpUrl,dataDir,pid,startedAtUtc}`. Graceful shutdown removes
the file; consumers must reject it when the PID is dead and poll `/health/ready` before trusting it.
`--status --json` is one-shot, never starts the host or creates the data directory, and returns
`{running,version,url,mcpUrl,dataDir,setupRequired,vault,installKind}`, where `vault` is `pending`, `locked` or `unlocked` (null when not running).

Three startup refusals exit with their own code and an operator-facing stderr message instead of crashing: a node key
that cannot open the database (it does not match the database's recorded key check, or `node.key` is missing next to
an existing `node.sqlite`) exits **8**, a failed database migration exits **9** after the restore or move-aside
hint, and an unusable data directory exits **10**. Exit 10 covers a data directory that cannot be created or written
(`DesktopDataDirectoryException`, raised by `SingleInstanceLease.TryAcquire` on an access denial) and a
`node-settings.json` that is present but unreadable. That file is checked right after the lease with a strict read
(`NodeStartupSettings.IsStoredFileUnreadableAsync`), because the tolerant read every service uses would start the node
on default settings and show first-run onboarding. A missing file is a fresh node, not a failure. The desktop shell
exits with the engine's code (`DesktopEngineStartupException`, `DesktopApplication.QuitAsync`) and the launcher
forwards it. A busy data directory answers every one-shot command, the maintenance commands included, with **4**.
The full exit-code list is in `--help` and the [agent install guide](../agentic-support/agent-install.md#11-exit-codes-and-troubleshooting).

**Windows command line.** The top-level `XE-Local-AI-Engine.exe` is Velopack's native stub: it starts the current
version but forwards neither standard output nor the exit code. Commands (`--help`, `--status --json`, `--setup`,
`--reset-admin-password`, the knowledge-downgrade commands) therefore run through
`current\XE-Local-AI-Engine.WindowsLauncher.exe`, which returns the engine's output and exit code. The first line of
`--help` names it. Interactive launches keep using the top-level exe.

**Locked start ([ADR 0018](../adr/0018-local-vault-passphrase-wrapped-node-key.md)).** When `node.key` is a v2 vault
(`DesktopBootstrap.EnsureLocalDataConfiguration` returns `VaultState.Locked`), `Program` holds the single-instance
lease and then runs the unlock pre-host (`Hosting/Vault/VaultUnlockHost`, sequenced in `Program.Vault.cs`) on the
resolved bind URL **before** the real host is built. The pre-host publishes readiness itself through
`DesktopReadyPublisher.Publish`, the helper `DesktopLifecycle` also uses: the port file, `ready.json` (same pid) and
the `XE_READY=1 …` line, so the shell's 2-minute start deadline, attach logic and `--status --json` see a running
engine; the lock is visible as `vault: "locked"` in both `auth/status` and `--status --json`. After a
successful unlock the pre-host stops, the real host re-binds the same URL, probing the port for about 5 s; if it stays taken the engine exits **7** with a
"start the engine again" message rather than move to a new origin the shell and the open tab would never find. It prints a second `XE_READY` line over a rewritten `ready.json`. A pre-host
that ends without an unlock (SIGTERM, or the shell's parent pipe closing) exits 0 and removes `ready.json`. Because the
real host does not exist while locked, no scheduler, inbound MCP, integration API or retention work runs; every update
restart returns to the unlock page.

**Unattended serve start.** A locked host started in any serve mode (including `--mcp-only`) with `XE_ADMIN_PASSWORD` set or `--admin-password-stdin` unlocks non-interactively, never shows the unlock page and goes straight to the real host; a wrong password exits 5 with "The admin password does not unlock this node's key.". Without a password it serves the unlock page as above. `install.sh --setup --start` and `install.ps1` pass the setup password to the started engine this way (environment only, never argv or disk); autostart units and launchers carry no credentials, so an autostarted node waits on the unlock page.

**CLI one-shots against a locked vault** never start a pre-host. They unlock from what they are given and exit **5**
with a stderr message, building nothing, when it is missing or wrong:

- `--setup` and `--mcp-key` read the admin password from `XE_ADMIN_PASSWORD` (cleared after reading) or one stdin line with `--admin-password-stdin`.
- `--reset-admin-password <new>` needs the recovery code on stdin with `--recovery-code-stdin`; it also exits 5 when a v2 vault exists and no code was piped. It resets the Identity hash and the vault's password wrap together, and rotates the recovery code: it prints one `XE_RECOVERY_CODE=<code>` line with the new code, and the code that proved the reset stops working. The unlock page's recovery path rotates it the same way and shows the new code once.
- A fresh `--setup` creates the vault and prints one `XE_RECOVERY_CODE=<code>` line on stdout after `XE_SETUP=created` and `XE_ADMIN_EMAIL=`; the node keeps no copy. Under an operator-supplied secret (`XE_NODE_SQLITE_KEY`, secrets file, Aspire) no vault exists and no line is printed. `install.sh` and `install.ps1` relay the line once and never write it to a file.

The repo-root installers can register user-scoped MCP-only autostart only through explicit
`--autostart`/`-Autostart`: a systemd user service on Linux or limited current-user Scheduled Task on
Windows. Installation never enables autostart by default.

### Loopback auto-port + persisted port + browser open

- The bind URL comes from `DesktopPortStore.ResolveBindUrl(dataDirectory)` (`Client/Hosting/DesktopPortStore.cs`, called by the desktop Kestrel branch in `Program.cs`), **not** a hard-coded `:0`. `DesktopPortStore` **remembers the last loopback port** in a `desktop-port.txt` file under the per-user data dir and re-binds it when it is still free; only when there is no remembered port (or it is taken/invalid) does it fall back to the dynamic `http://127.0.0.1:0`. This matters because a fresh OS-assigned port every launch changes the browser **origin** (scheme+host+port) and silently resets every `localStorage`-backed user preference between runs — pinning the port keeps preferences alive. The store writes via temp-file+move (no torn file), probes availability with a throwaway `TcpListener`, and is best-effort: any IO/parse failure resolves to a dynamic bind rather than throwing.
- Kestrel still binds loopback only. The concrete URL is known **post-bind**, so `LoopbackUrlResolver.Resolve` (`Client/Hosting/LoopbackUrlResolver.cs`) reads `IServerAddressesFeature.Addresses`, prefers an explicit `127.0.0.1`/`localhost` address, and **rewrites any wildcard host (`0.0.0.0`/`::`) back to `127.0.0.1`** so the browser never targets a routable interface.
- `DesktopLifecycle.OnApplicationStarted` resolves that URL and, unless browser launch is suppressed, calls `BrowserLauncher.OpenBrowser` (`Client/Hosting/BrowserLauncher.cs`): `explorer <url>` on Windows, `xdg-open <url>` on Linux, **never via a shell** (`UseShellExecute = false`). Browser launch is strictly non-fatal — failure logs the URL and the server keeps serving.

### Persistent per-user data + operator key

`DesktopBootstrap` (`Client/Hosting/DesktopBootstrap.cs`) exists because a double-click launch supplies neither a DB connection string nor the operator secret. It targets `Environment.SpecialFolder.LocalApplicationData` (Windows `%LOCALAPPDATA%`, Linux `$XDG_DATA_HOME`/`~/.local/share`) so portable application replacement and Linux single-file extraction never relocate persistent data. The operator key is **generated once and persisted** to `node.key` (atomic temp-file write, `0600` on non-Windows); a torn/corrupt or wrong-length key **fails loudly** rather than regenerating (regenerating would brick the encrypted DB). A freshly generated or legacy raw/DPAPI key is the `pending` state: it is injected as the in-memory operator secret and the first admin setup (or, for a legacy key, the SPA's password-confirm step) replaces the file with the passphrase-wrapped v2 vault, after which the raw key is no longer on disk. The Windows re-wrap of a legacy plaintext key with DPAPI no longer exists. A v2 file injects nothing and starts the locked pre-host (see above). See [Security & Privacy](12-security-and-privacy.md) §2.1.

### No-orphan shutdown (the load-bearing invariant)

`DesktopLifecycle` (`Client/Hosting/DesktopLifecycle.cs`) fills the two OS gaps `ConsoleLifetime` doesn't cover, so a closed window drains gracefully and the singleton `LlamaServerProcessSupervisor` disposes & tree-kills its `llama-server` child (no orphan):

- **Linux `SIGHUP`** (terminal close) → a `PosixSignalRegistration` with `context.Cancel = true` → `StopApplication()`.
- **Windows `CTRL_CLOSE_EVENT` / logoff / shutdown** → a `SetConsoleCtrlHandler` callback (kept rooted so the GC can't reclaim the native delegate) that calls `StopApplication()` then **blocks up to ~4s** (`ConsoleCloseDrainBudget`, safely under Windows' ~5s force-kill window) for the drain.

The **Windows Job Object is the source-defined hard-kill safety net** regardless of whether the drain completes. `WindowsJobObjectProcessHandle` (`Providers.ProcessSupervision/WindowsJobObjectProcessHandle.cs`, shared by the llama-server, sd-server and whisper-server launchers) wraps the child in a job created with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`: closing the job handle (on `TreeKill`/`Dispose`) is designed to terminate the whole process tree. This Win32 path is `[SupportedOSPlatform("windows")]` and the source notes it must be verified on real Windows 11; the WSL build cannot exercise it, and this baseline review does not include an operating transcript. See [Local Runtime & Providers](03-local-runtime-and-providers.md) for the supervisor side.

---

## 6. Publish profiles, launchers & RC packaging

### Publish profiles

Publishing is deliberately platform-specific:

| Payload | `SelfContained` | `PublishSingleFile` | `UseAppHost` | Contract |
|---|---:|---:|---:|---|
| Linux client | `true` | `true` | default | Self-contained AppImage payload; runtime/native libraries are bundled; no system .NET prerequisite |
| Windows client | `false` | `false` | `false` | Managed DLL/deps/runtimeconfig and application dependencies only |
| Windows C# launcher | `false` | `false` | `true` | MIT Microsoft apphost plus launcher DLL/deps/runtimeconfig; validates the ASP.NET Core runtime its `runtimeconfig.json` requires (`$(DotNetRuntimeVersion)` from `eng/ReleaseVersion.props`, or a later patch) and starts the client DLL |

The two Windows projects publish into the same payload directory before Velopack packs it. No Windows runtime pack,
`coreclr.dll`, `hostfxr.dll`, or .NET Library License may appear. Trimming remains off for the reflection-heavy client.

`RuntimeLicensePackagingTests` (`XE-Local-AI-Engine.Tests/Architecture/`) pins this table: the runtime version in
`eng/ReleaseVersion.props` and its import by `Directory.Build.props`, the three profiles' `SelfContained` /
`PublishSingleFile` / `UseAppHost` / `RollForward` / `RuntimeFrameworkVersion` values, and the license and notice
files each publish must carry. Changing a profile means changing that test in the same commit.

### Official Windows launcher and legacy scripts

`XE-Local-AI-Engine.WindowsLauncher` is the official Windows entry point. Its apphost provides Microsoft's standard
missing-.NET behavior; once running, the C# code validates architecture, required adjacent files, and the ASP.NET Core
runtime floor, forwards all Velopack arguments to `dotnet XE-Local-AI-Engine.Desktop.dll` (or to
`XE-Local-AI-Engine.Client.dll` for an engine mode or operator command), sets desktop mode, waits, and
propagates the child exit code. A desktop launch releases the console unless `--debug` is passed (see the native shell
section above). The main host wraps Velopack's process locator so an update waits for both the managed
host and launcher to exit before replacing the current directory.

The launcher is a first-class solution project, not a packaging-script shim. `Program.Main` must remain synchronous
with `VelopackApp.Build().Run()` as its first statement because `vpk pack` verifies the packaged main executable's
lifecycle-hook entry point. `.github/workflows/release.yml` publishes the Client and WindowsLauncher separately into
one framework-dependent Windows payload, selects `XE-Local-AI-Engine.WindowsLauncher.exe` as the Velopack main exe,
and verifies that exactly one `Portable.zip` was produced.

For deprecated manually unzipped self-contained builds, the old scripts remain under `publish/`:

- `publish/linux/run-xe-local-ai-engine.sh` — sets `XE_LAUNCH_MODE=desktop`, resolves its own dir (symlink-safe), and `exec`s the binary **in the foreground** so closing the terminal delivers `SIGHUP` to the process group → graceful teardown.
- `publish/windows/run-xe-local-ai-engine.cmd` — sets `XE_LAUNCH_MODE=desktop` and runs the exe **in the current console window** (no `START`/`Start-Process`); a new/detached window would break the `CTRL_CLOSE_EVENT` → graceful-shutdown chain.

Both scripts carry an explicit **single-instance caveat**: only one instance per user-data dir (the auto-port avoids a listener collision but not SQLite contention — a second instance can corrupt the DB).

### RC bundle packaging

`publish/package-tester-win.ps1` was the Windows tester RC path from `0.1.0-rc.4.0` through `0.1.0-rc.5.1` and is now
**deprecated, reference-only** — the tag-triggered `.github/workflows/release.yml` is the canonical release path (§8).
`publish/package-rc.sh` is likewise deprecated and reference-only. Their private tester-repository, GitHub App,
manual-draft, and Linux-ZIP behavior describes superseded distribution and must not be applied to official releases.
Both remain covered by `scripts/lint-release-scripts.sh`.

Official release payloads include the Apache-2.0 license, third-party license disclosures, and a validated payload SPDX
manifest. Detached checksum, release-manifest, and SPDX evidence is attached after remote draft-byte verification.

### Changelog automation & release notes

Release notes are generated from conventional-commit history rather than hand-written:

- `cliff.toml` (repo root) configures **git-cliff** to render an auto-grouped changelog from the commits between the previous release tag and HEAD. The output is a `RELEASE_NOTES.md`, fed to **`vpk pack --releaseNotes <file>`** to embed the notes into the Velopack package; `vpk upload github` then publishes them as the GitHub release body.
- **Two producers of `RELEASE_NOTES.md`, and they are not the same code path.** `scripts/generate-release-notes.sh` is the standalone/manual helper. `publish/package-tester-win.ps1` — the deprecated manual packaging path — **does not call that script**: it downloads a checksum-pinned git-cliff and invokes it directly. They also disagree on the empty-range case: the shell script falls back to writing a `## <version>` / "Maintenance release — no user-facing changelog entries." body (the fallback containing the quoted phrase `Maintenance release — no user-facing changelog entries.` in `scripts/generate-release-notes.sh`), while the packaging script **hard-throws** rather than shipping a release with no notes. Both share the one rule that matters: `--latest` when HEAD is already tagged, `--unreleased --tag` otherwise (a tagged HEAD makes `--unreleased` empty).
- The repo-root `CHANGELOG.md` is **not** generated. git-cliff writes `RELEASE_NOTES.md` only; `CHANGELOG.md` is hand-maintained in Keep-a-Changelog form, which is why it drifts from the tags if nobody updates it at release time.
- **Release tags are standardised on a `v` prefix.** All nine historical source release tags carry it (`v0.1.0-rc.1.0` … `v0.1.0-rc.5.1`) — there are **no unprefixed release/version tags in this repository**. The separate non-release tag `codex/rollback-prior-ai-pins` is outside this release convention. Bare release tags exist only on the historical **tester artifact repo** (`0.1.0-rc.4.1` and earlier; see §8), which is a different repository. `cliff.toml`'s `tag_pattern` is an anchored regex (`^v[0-9]`), paired with `ignore_tags = "^dev/"`, so neither bare tags nor `dev/` snapshots are release boundaries; git-cliff runs against the local working tree, `origin` is the source repo, and no tester ref is ever fetched here. The range is driven by `--latest`/`--unreleased` rather than by the pattern alone. **The code that genuinely must handle both release spellings is `package-tester-win.ps1`'s `Find-GitHubRelease`**, which queries the tester repo over the GitHub API — not git-cliff.
- **`scripts/generate-release-notes.sh` has a `--since <ref>` range mode**, used by the Development build: the base
  is the previous `dev/` tag's commit, or the newest `v*` tag on the first build. Its "is HEAD tagged" probe is
  restricted to `--match 'v*'`, so a `dev/` tag sitting on HEAD can never make an official release emit `--latest`
  against the wrong range.
- **`vpk pack` (1.2.0) has no `--pre` flag** — passing it fails with `'--pre' was not matched`. Prerelease state rides on the **SemVer suffix in `--packVersion`** (`0.1.0-rc.1.0` *is* a prerelease); the GitHub-release prerelease marker is set with `--pre` only on `vpk upload github` (the `vpk upload github` invocation in `.github/workflows/release.yml`).

### In-app self-update (Velopack)

The desktop app can update itself: `AddAppUpdateExtensions.cs` wires a Velopack updater to the public GitHub release
feed with a null access token. Update checks are anonymous; no GitHub device flow or stored update token remains. The
update path is **desktop-only** (gated like every other desktop branch; see the endpoint desktop-gate tests under
`XE-Local-AI-Engine.Tests/AppUpdate/`). Update-feed configuration lives in
`appsettings.AppUpdate.{main,tester,dev}.json`, selected at publish time by `-p:UpdateChannel=main|tester|dev`
(default `main`).

The baked flavour now supplies only the node's **default** update channel. The effective channel is node-settings
state, chosen in the About dialog, so a published build can move between channels without being repackaged:

| File | `GitHubRepositoryUrl` | `DefaultChannel` | Sees |
|---|---|---|---|
| `appsettings.AppUpdate.main.json` | `https://github.com/w0rldx/XE-Local-AI-Engine.Source` — public, intentional, non-secret | `Stable` | stable tags only |
| `appsettings.AppUpdate.tester.json` | `https://github.com/w0rldx/XE-Local-AI-Engine.Source` — public, intentional, non-secret | `Preview` | stable + RC tags |
| `appsettings.AppUpdate.dev.json` | `https://github.com/w0rldx/XE-Local-AI-Engine.Source` — public, intentional, non-secret | `Development` | stable + RC + Development snapshots |

All three flavours point at the same public repository. Velopack's `--channel` is the OS discriminator: `win` and
`linux` carry the Stable and Preview payloads, `win-dev` and `linux-dev` carry the Development payloads. A
Development release therefore carries only `releases.<os>-dev.json`, and a Stable or Preview feed read skips it
silently — which is what keeps Development builds invisible to everyone who did not ask for them. Updates are
forward-only in every channel; moving back down needs a reinstall. The packaging job asserts the flavour and its
default channel agree before it packs (`Verify packaged update release track` in
`.github/workflows/package-velopack.yml`). These URLs are public configuration, not secrets,
and must not be replaced with placeholders. The manual packagers are deprecated, reference-only, and not release
alternatives. See [`docs/velopack-release-install-guide.md`](../velopack-release-install-guide.md).

---

## 7. Installers & uninstaller

**OS-native installers (MSI / DEB / RPM) are deferred.** Official binaries are Velopack-managed portable applications:
Windows `Portable.zip` produced with `--noInst` (no `Setup.exe`) and a Linux AppImage. Both self-update. Windows requires
the separately installed x64 ASP.NET Core Runtime 10.0.12+; Linux bundles .NET. The application still self-provisions
its llama.cpp binary and GGUF models into the per-user data directory.

### Legacy manual-bundle cleanup scripts

Two cleanup scripts remain for deprecated manual bundle layouts; they are **not** user-facing assets in the official
Windows Portable ZIP or Linux AppImage:

- `publish/windows/uninstall-xe-local-ai-engine.ps1` (packaged as `Uninstall-XE-Local-AI-Engine.ps1`) — PowerShell 5.1-compatible.
- `publish/linux/uninstall-xe-local-ai-engine.sh` (packaged as `uninstall-xe-local-ai-engine.sh`) — plain POSIX `sh`.

Both always **stop** the running node process and the `llama-server` / `sd-server` child runtimes it spawned — matched **strictly** by executable path under the app's own per-user data dir, mirroring the startup `StaleProcessReaper`'s own-binaries-root discrimination so an unrelated `llama-server` (e.g. Ollama's) is never touched. They then branch:

- **Velopack-managed install detected** (a `current/` dir or `Update`/`Update.exe` helper at the data-dir root — on the default Windows layout the managed install root *is* the data dir): the script **does not delete** the tree. It delegates to Velopack (on Windows it can best-effort invoke `Update.exe --uninstall`; otherwise it points at the OS "Apps & features" uninstall) and stops. This is the safety valve that prevents brute-force-deleting a live managed install.
- **Portable / manual install** (no Velopack tree present): after an **explicit confirmation** (typed `y`; `--yes`/`-Yes` skips it for automation), it deletes **only** the per-user data dir (`%LOCALAPPDATA%\XE-Local-AI-Engine` / `$XDG_DATA_HOME/XE-Local-AI-Engine`) — `node.sqlite`, `node.key`, `node-settings.json`, `hf-token.enc`, the downloaded `llama.cpp`/`stable-diffusion.cpp` binaries, `models/`, and the AgentHome workspace.

Both refuse to run elevated/as-root (a per-user data dir would resolve to the wrong profile), support `--dry-run` and `--keep-data`, and never delete anything outside that exact directory. Portable-zip users delete the unzipped app folder by hand afterward.

> **Do not resurrect the old HostAgent-era uninstaller.** A prior install-type-aware teardown existed but predates the runtime re-architecture — it referenced a WSL managed distro (`wsl --unregister xe-engine-runtime`), Docker containers/volumes/network, and `HostAgent.Windows`, **all removed** when Docker/HostAgent were torn down (see [Architecture Overview](01-architecture-overview.md)). The current scripts deliberately target **only** the per-user data dir + child runtimes. [ADR 0004](../adr/0004-development-mode-container-execution-docker-stopgap.md)'s Development-Mode Docker permission revives none of that — there is still no managed WSL distro, no `HostAgent.Windows`, and no engine-owned Docker network or volume set for the uninstaller to reason about. The scripts do not enumerate or remove Development Mode containers; that separate lifecycle does not justify restoring the old script.

---

## 8. Release channels and CI status

### One repository, one tag form

Source, `v<version>` tags, official binaries and the public update feeds all live in this repository, and the
tag-triggered `.github/workflows/release.yml` publishes the `win-x64` and `linux-x64` Velopack packages to its
GitHub Releases. Source tags are always `v`-prefixed; there are no bare release tags here.

A second tag form exists alongside them: **`dev/<version>` lightweight tags** mark automated Development snapshots
of `develop` (for example `dev/1.0.0-rc.2.dev.20260922.1`). They are created by the publish job of
`.github/workflows/dev-build.yml`, are **never deleted** — so every reported Development version maps to a commit
forever — and are deliberately not release tags: they carry no `v` prefix and never steer changelog generation.

**Hand-cut tester snapshots** must be stamped inside the Development scheme: `<anchor>.dev.<yyyymmdd>.<100+n>`, for
example `1.0.0-rc.2.dev.20261008.101` for the first snapshot cut on that date. The update check compares versions as
SemVer only, with no notion of build date, and SemVer ranks the alphanumeric `dev` identifier above any number. A
snapshot stamped `<anchor>.<yyyymmdd>.<n>` therefore loses to every Development build, so a tester on it is offered an
older build as an update. Inside the scheme a snapshot orders by date against the automated builds, and the counter
of 100 or more keeps it clear of the automated counters for the same date. `AppUpdateVersionsTests` pins this
ordering.

Releases through `0.1.0-rc.5.1` came out of a separate tester repository under a hand-run packaging flow with a
different tag form. That provenance, including the tag-form change mid-flight, is recorded once in the
[CHANGELOG](../../CHANGELOG.md) header — this page describes only the current path.

### GitHub Actions and the release workflow

`.github/workflows/release.yml` is the canonical tag-triggered release path. It validates the exact tagged commit,
binds SemVer/tag/source identity, and gives matrix jobs build-only responsibility. The serialized
`prepare-release-draft` job receives write access only after approval through the `open-source-release` environment;
it creates the Velopack draft, merges the Windows and Linux channels, verifies the remote bytes, uploads the tagged
`openapi/v1.json` contract as `openapi-v1.json` with the detached SPDX/release-manifest/checksum evidence that covers
it, and re-verifies the complete draft. A separately approved `publish-release`
job re-downloads and verifies that same draft, then promotes it without rebuilding, repacking, re-uploading, or
replacing any asset before anonymous public-feed verification. Windows packing is `--noInst`; Linux packing produces
an AppImage. The workflow uses the built-in `GITHUB_TOKEN`, not a maintainer PAT.

`.github/workflows/package-velopack.yml` is the shared `workflow_call` packaging matrix. `release.yml` calls it
with `require-tag-binding: true`; `dev-build.yml` calls it with `require-tag-binding: false`,
`update-channel: dev` and `velopack-channel-suffix: -dev`. Packaging behaviour for a tag release is unchanged by
the extraction — the same 17 steps run against the same commit with the same values.

`.github/workflows/dev-build.yml` runs daily at 03:17 UTC and on dispatch, only from `develop`. It skips the entire
run when the previous `dev/` tag already points at the develop tip, reuses `build-and-test.yml` as its validation
gate, and publishes with `GITHUB_TOKEN` and **no** `open-source-release` approval — an explicit posture decision
([ADR 0014](../adr/0014-update-channels-and-development-builds.md), D7) — while keeping every technical gate an
official release has: validation, license corpus, SBOM, artifact-shape verification, checksums, remote-asset
verification and envelope verification. It keeps the newest 30 Development releases, deleting older **releases**
but never their tags.

The manual packagers are now deprecated, reference-only material: `publish/package-tester-win.ps1` was the RC path
from `0.1.0-rc.4.0` through `0.1.0-rc.5.1`, run by hand on Windows against the tester repo; `publish/package-rc.sh`
remains a historical manual portable-zip helper. Both still carry static analysis via `scripts/lint-release-scripts.sh`. See
[Testing & Validation](13-testing-and-validation.md) for where the quality gates run, and
[`docs/velopack-release-install-guide.md`](../velopack-release-install-guide.md) for the full release story.

The presence and content of these workflow and scripts files is repository design evidence, not evidence that a
particular release ran them successfully. A release claim needs the matching retained transcript, hashes, tag,
and target-OS smoke evidence; this page does not assert those artifacts are available.

---

## Isolated managed-runtime storage

`XE_RUNTIME_DATA_DIR` overrides the shared managed-runtime cache root for llama.cpp,
stable-diffusion.cpp, whisper.cpp, Python training/compute runtimes and benchmark KLD caches. It covers downloads, installed/desired runtime
metadata, launch fallbacks, conversion scripts, source-build staging/recovery and
startup orphan-reaper roots. `RuntimeCacheDirectory.Resolve` is the common resolver.
Unset, the default remains `<LocalApplicationData>/XE-Local-AI-Engine`. An explicitly
empty, whitespace-only, relative or control-character-containing value fails closed.
Use an absolute, dedicated directory; this does not migrate an existing cache.

This is separate from `XE_DATA_DIR`: the latter selects the node database, keys and
desktop profile. For a Windows scratch launch, set both in the launching process
before starting the packaged launcher (the children inherit them):

```powershell
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ('XE-scratch-' + [guid]::NewGuid())
$env:XE_DATA_DIR = Join-Path $scratch 'node'
$env:XE_RUNTIME_DATA_DIR = Join-Path $scratch 'runtimes'
$env:FirstRunModel__Enabled = 'false'
$env:HuggingFaceImageModels__ModelsDirectory = Join-Path $scratch 'image-models'
# Start the packaged XE-Local-AI-Engine.WindowsLauncher.exe from its payload directory.
```

Use a dedicated PowerShell process so these overrides cannot leak into ordinary
launches. Desktop GGUF models default under the node data root, as do transcription
models. Image weights otherwise default under the application payload, not the
user runtime root; the explicit image-model override above isolates them too.
Existing explicitly configured model directories still take precedence. On Linux,
the same two environment variables isolate the node and managed-runtime roots.
Do not point two active engines at the same runtime cache during isolation testing.

---

## Related pages

- [Architecture Overview](01-architecture-overview.md) — where hosting sits in the whole node
- [Project Layout](02-project-layout.md) — solution projects, including Desktop, AppHost, ServiceDefaults, and WindowsLauncher
- [Local Runtime & Providers](03-local-runtime-and-providers.md) — llama.cpp supervisor, process reaping, the Job Object's counterpart
- [Data & Persistence](08-data-and-persistence.md) — node data directory, SQLite, selected per-column encryption, EF migrations
- [API & Hubs](09-api-and-hubs.md) — endpoints, SignalR hubs, OpenAPI/Scalar
- [React Client](10-react-client.md) — the SPA served from `wwwroot`
- [Security & Privacy](12-security-and-privacy.md) — loopback-only, `LocalApiSecurityMiddleware`, secret stores
- [Scheduler](06-scheduler.md), [Model-Fit](07-model-fit.md), [Agent Mode](04-agent-mode.md) — features driven by the background hosted services
