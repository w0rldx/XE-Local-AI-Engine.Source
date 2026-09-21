# Architecture Overview

> Reviewed: 2026-09-15 · Code-grounded.

XE Local AI Engine (product name **XE AI-Engine**) is a self-contained local AI node: a single ASP.NET Core process
(`XE-Local-AI-Engine.Client`) that hosts the React management UI, serves local APIs and SignalR hubs,
persists selected sensitive fields in SQLite with per-column AEAD encryption,
and supervises node-owned `llama-server`, `sd-server` and `whisper-server` host child processes. This page is the map:
it shows the machine boundary, the in-process layering and one-way dependency flow, and the
post-re-architecture runtime model (host llama.cpp, **no Docker on the inference path, no HostAgent**;
Development Mode execution is the one scoped exception — see [ADR 0004](../adr/0004-development-mode-container-execution-docker-stopgap.md)). Subsystem detail lives
in the per-topic pages linked throughout.

---

## What the system is

The engine is **one deployable web server** plus a Vite/React SPA it serves. There is no separate worker
process, no container runtime on the inference path, and (since the 2026-06-17 re-architecture) no
HostAgent sidecar. Everything — the management UI, the local REST/SignalR surface, the agent execution
loop, and the model runtime supervisor — lives inside the `XE-Local-AI-Engine.Client` host.

> **Scoped exception: Development Mode execution.** [ADR 0004](../adr/0004-development-mode-container-execution-docker-stopgap.md)
> (Accepted 2026-07-29) permits Docker for Development Mode build/test/lint execution **only**, as a
> stopgap ahead of MXC. It changes nothing above: the engine is still one process, and inference, model
> acquisition, embedding and image generation carry no container dependency. The provider is chosen
> **per feature** — AgentHome and Coder stay on the process sandbox provider. The container provider
> itself has **shipped and is opt-in**: `Development:Sandbox:Provider=docker` selects it, and the
> shipped config sets no `Development:Sandbox` key, so a default node still runs Development Mode on
> the process provider. Read the ADR for what is *decided* and
> [Development Mode container implementation status](../roadmaps/development-mode-container-status.md)
> for what is *built* — that page is canonical and this one deliberately does not restate it.

| Concern | Where it lives | Evidence |
|---|---|---|
| Host / web pipeline / endpoints / hubs | `XE-Local-AI-Engine.Client` | `Client/Program.cs`, `Client/ConfigureServices.cs` |
| Application logic, options, service areas | `XE-Local-AI-Engine.Client.Application` | `NodeApplicationServiceCollectionExtensions.cs` (`AddNodeApplication`) |
| Agent/AI wiring (MAF + MEAI) | `XE-Local-AI-Engine.AI.Agent` | `AI.Agent/DependencyInjection/AgentServiceCollectionExtensions.cs` |
| Provider seams (no SDK leak) | `XE-Local-AI-Engine.Providers.Abstractions` | `Providers.Abstractions/ILocalModelProvider.cs` |
| Local inference runtime | `XE-Local-AI-Engine.Providers.LlamaServer` | `LlamaServerLocalModelProvider.cs`, `LlamaServerProcessSupervisor.cs` |
| Local image runtime (`sd-server`) | `XE-Local-AI-Engine.Providers.StableDiffusionCpp` | `StableDiffusionCppRuntime.cs`, `ImageServerProcessSupervisor.cs` |
| HF GGUF discovery/download | `XE-Local-AI-Engine.Providers.HuggingFace` | provider project |
| Optional secondary provider | `XE-Local-AI-Engine.Providers.Ollama` | `OllamaLocalModelProvider.cs` |
| Cloud chat provider (ChatGPT OAuth) | `XE-Local-AI-Engine.Providers.CodexOAuth` | `CodexOAuthChatClientFactory.cs` |
| Hardware probing | `XE-Local-AI-Engine.Providers.Capabilities` | `HardwareProfiler.cs` |
| SQLite persistence with selected per-column AEAD encryption | `XE-Local-AI-Engine.Client.Persistence` | `Client/Program.cs` migration runners; `NodeEncryptionSaveChangesInterceptor.cs` |
| React management UI (one directory per feature) | `XE-Local-AI-Engine.Client.React` | `Client.React/src/features/` |
| Dev orchestration only | `XE-Local-AI-Engine.AppHost` | `AppHost/AppHost.cs` |
| Packaged Windows bootstrap | `XE-Local-AI-Engine.WindowsLauncher` | `WindowsLauncherApplication.RunAsync`; hands off to the `Client` host after Velopack lifecycle handling |

> Aspire (`AppHost.cs`) is a **development-only** orchestrator (`app` project + Vite `client-react` + a
> dev SQLite resource). It is not part of the shipped runtime — production/desktop launches the `Client` host directly on non-Windows packages; the packaged Windows path enters through `WindowsLauncher`, which then starts `Client`. See [Hosting & Deployment](11-hosting-and-deployment.md). For the full project inventory see
> [Project Layout](02-project-layout.md).

---

## The machine boundary

The node holds **no outbound control connection at all**. Every inbound surface is loopback-bound
(`/api/local/v1`, the SignalR hubs, the inbound MCP server), and the only traffic that leaves the machine
is what an operator configured a feature to fetch: a model download, an operator-declared cloud or
OpenAI-compatible provider, an operator-registered MCP server.

An earlier design paired the node with a central platform over an outbound `WorkerHub` SignalR
connection, with device binding, a token refresh loop, an auto-connect hosted service and
end-to-end-encrypted invocation envelopes. None of it was reachable in any shipped build, and it is gone.

**Invariant:** nothing in the node opens an outbound connection to a control plane. Cloud-provider
credentials and HMAC/endpoint tokens stay **local** — never returned to the browser, never logged.
See [Security & Privacy](12-security-and-privacy.md) and [API & Hubs](09-api-and-hubs.md).

---

## The local surface (browser ↔ node)

Distinct from the platform link, the React SPA talks to the host over a **loopback/local** surface:

- REST endpoints (FastEndpoints) under the prefix `LocalApiRoutes.Prefix` (`/api/local/v1`), with a
  global operationId name generator feeding the OpenAPI doc that generates the hey-api React SDK
  (`Program.cs`, `config.Endpoints.NameGenerator`).
- Local SignalR hubs, each `RequireAuthorization(NodeAuthorizationPolicies.Operator)`. **The `MapHub`
  block in `Client/Program.cs` is the inventory**, and [API & Hubs §2](09-api-and-hubs.md) is the one page that
  enumerates it — this page does not repeat the list. All of them are mapped unconditionally except
  `DevelopmentAttemptHub`, which is mapped only when `Development:Enabled` (default `true`). The work-session
  and workflow hubs are mapped unconditionally on purpose: their feature flags are enforced by request-path
  middleware that answers 404 for the whole prefix, hub path included, rather than by leaving a route unmapped.
- JWT-bearer auth (operator role), antiforgery, per-IP rate limiting, and a
  `LocalApiSecurityMiddleware` that enforces the loopback/`Host`/`Origin` posture
  (`ConfigureServices.cs`, `Program.cs`).
- The SPA itself is served as static files with `MapFallbackToFile("index.html")`.

**Message size on this surface is anchored to the transport.** `AddSignalR` sets
`MaximumReceiveMessageSize = 512 KB` for the whole hub-invocation payload (`ConfigureServices`), and
`Security:MaxMessageSizeKb` defaults to 256 KB of message content — half of it. That leaves ample room for the
JSON envelope around the content *and* guarantees the app-level check fires first, with a legible "your message
is too large", instead of the transport tearing the connection down with an opaque frame-size error. 256 KB of
text is also roughly the byte size of the ~65k-token context window the budgeter trims against, so a paste that
gets through is a paste the rest of the pipeline can actually work with; larger documents belong on the
attachment upload path (`Security:MaxUploadFileSizeMb`), which extracts and inlines them under its own budget.
The cap is enforced at the entry seams only and never re-applied to stored history — see the
`SecurityOptions.MaxMessageSizeKb` remarks for why.

See [API & Hubs](09-api-and-hubs.md) and [React Client](10-react-client.md).

---

## Development Mode: registered source, managed worktree

Development Mode is available by default. `DevelopmentOptions.Enabled` is the backend emergency switch;
setting `Development:Enabled=false` disables the capability when same-host-user code execution is not
acceptable. The operator does not send an arbitrary repository path with every action. Instead, the node
registers a local Git repository through `ISelectedFolderResolver` and exposes only its opaque selected-folder
ID and operator-chosen alias. The host path remains an internal, encrypted node-persistence value.

The registered folder authorizes the **source repository**, not the agent's working directory. For each task,
`DevelopmentWorkspaceProvider` creates or reattaches an engine-owned detached Git worktree under the node data
directory, outside the selected source repository, and binds that worktree to `ISandboxRuntimeProvider` as a
trusted host workspace. The persisted selected-folder ID and canonical repository identity hash must still
match before execution. Legacy projects without a selected-folder ID must reconnect to a registered repository
whose canonical identity hash matches the original project.

The source repository changes only through the final apply path. Preview and independent review bind the patch
to the expected base commit and evidence hashes; apply revalidates those values before mutating the registered
source. An agent cannot make its managed worktree authoritative merely by changing files there.

This is an application-enforced workflow boundary, not operating-system isolation. Build and test code runs as
the host user and retains the host filesystem and network access available to that user. MXC is not integrated
through the `ISandboxRuntimeProvider` or workspace-provider seams, and the current architecture does not present
an MXC security boundary. See [Security & Privacy](12-security-and-privacy.md#development-mode-source-and-execution-boundary).

**Docker provider boundary.** [ADR 0004](../adr/0004-development-mode-container-execution-docker-stopgap.md) (Accepted
2026-07-29) defines an opt-in **container provider behind the same `ISandboxRuntimeProvider`
seam**, with a running Docker daemon as a hard requirement for the feature and **no unisolated fallback** — no
daemon means no Development Mode, deliberately, so the isolation posture cannot depend on what happens to be
installed. Repository-supplied container configuration is rejected wholesale (engine-generated canonical mounts,
operator-approved digest-pinned images, no socket or device mounts, no repository Dockerfile builds); on Linux,
Docker-socket access is root-equivalent, and the ADR documents that rather than mitigating it. Docker is recorded
as a **stopgap**, not a replacement for MXC, and does not close the seam. That provider has **shipped as an opt-in
choice, not the default**: `Development:Sandbox:Provider=docker` selects it, and because the shipped config sets no
`Development:Sandbox` key, a default node still executes exactly as the paragraph above describes. See
[Development Mode container implementation status](../roadmaps/development-mode-container-status.md) for the
maintained provider coverage and limitations — this page does not duplicate them.

**Where the code and the decisions live.** Backend: 21 endpoint classes under
`Client/Endpoints/Development/V1/`, one `*DevelopmentEndpoint.cs` file per class covering the artifact,
capability, event, patch, project, repository, task and template families (routes on
`LocalApiRoutes.Development`), services under
`Client.Application/Services/Development/`, live attempt output over `DevelopmentAttemptHub` — all in
[API & Hubs](09-api-and-hubs.md). Schema: six migrations — `AddDevelopmentModeFoundation`,
`BindDevelopmentProjectsToSelectedFolders`, `AddDevelopmentCommandProfile`, `AddDevelopmentAttemptCommandProfile`,
`AddDevelopmentTemplates` and `WidenDevelopmentTasksPerProject` ([Data & Persistence](08-data-and-persistence.md)). Frontend:
`Client.React/src/features/development/` at route `/development` ([React Client](10-react-client.md)); an attempt that ends
without a verdict renders its `terminalReason`, so a failed attempt names *why* it stopped rather than showing an empty
result. Three accepted ADRs record the non-obvious decisions:

- [ADR 0001 — restart recovery uses replacement attempts](../adr/0001-development-mode-restart-recovery.md): an attempt found
  `Running` at restart is marked `Interrupted` exactly once and continued only through a **new** attempt pointing at it; the
  provider stream is never resumed, the worktree and artifacts are preserved, and validation/review evidence is bound to the
  base commit + workspace subject hash + changed-files manifest hash.
- [ADR 0002 — cloud authorization uses `ChatOptions.AdditionalProperties`](../adr/0002-development-cloud-egress-carrier.md): the
  carrier that authorizes every raw cloud round, including the function-result follow-up round created inside
  `FunctionInvokingChatClient`. Version-aware against the pinned `Microsoft.Extensions.AI` 10.9.0 — re-verify it when that pin moves.
- [ADR 0004 — Development Mode container execution (Docker stopgap)](../adr/0004-development-mode-container-execution-docker-stopgap.md):
  the scoped Docker permission described above, as a stopgap ahead of MXC. Its living implementation-status companion is
  [Development Mode container implementation status](../roadmaps/development-mode-container-status.md).

Command execution is selected from the code-owned `dotnet-slnx`, `dotnet-csproj`, or `generic-git`
profiles. The React project-creation form requires confirmation before it submits a detected
proposal, but that confirmation is a normal UI workflow control, not a backend authorization
invariant. The backend accepts an omitted profile and applies this precedence: explicit
`CommandProfileId`, then the optional repository import at `.xe-dev/profile.json`, then read-only
detection. The import may name a profile and build target, but it cannot define commands,
arguments, executables, or timeouts.

Four integrity values have different purposes and must not be collapsed into one “profile
digest”:

1. At project creation, the SHA-256 digest of the exact import-file bytes is stored as provenance
   in the canonical profile snapshot.
2. At the start of an attempt, the managed worktree's import-file digest (including absence) is
   captured independently; a fixed catalog command that changes it makes the next invariant check
   fail closed.
3. Artifacts store a digest of the canonical command-profile snapshot so evidence can be bound to
   the selected command definition.
4. The artifact protocol version is independent of the command-profile catalog version and all
   three digest values.

Stored profiles are reconstructed from the current code-owned catalog and rejected when the
catalog version or canonical content no longer matches. The `generic-git` profile runs only fixed
Git status and `git diff --check`; it can detect whitespace errors but provides no build or test
evidence. The test-write policy permits adding or copying protected test files, but rejects
modification, deletion, or rename of a protected test that existed at the base commit. An attempt
does not re-read the repository import file as its command source
(`DevelopmentCommandProfileCatalog.cs`, `DevelopmentCommandProfileImport.cs`,
`DevelopmentManagementService.ResolveCommandProfile`).

`DevelopmentOptions` (`Client.Application/Services/Development/DevelopmentOptions.cs`) also carries the per-attempt guardrails:
`MaxArtifactBytes` (16 MiB), `MaxAttemptDurationSeconds` (30 min), `MaxToolCalls` (64), `MaxChangedFiles` (256),
`MaxFileWriteBytes` (1 MiB), `MaxPatchBytes` (8 MiB), `MaxCommandOutputBytes` (256 KiB).

---

## In-process layering & one-way dependency flow

Inside the single host the code is layered so dependencies flow **one way** (host → application →
agent/providers → persistence). The host only wires web-framework concerns; all node logic is registered
through `AddNodeApplication`, which composes the `AddNode*` feature modules in one ordered list —
`NodeApplicationServiceCollectionExtensions.AddNodeApplication` is the inventory, and the ordering comments
there are load-bearing (`AddNodeImages` and `AddNodeTrainingRuntime` reuse the Hugging Face download client
`AddNodeModelRuntime` registers; `AddNodeTrainingRuns` needs the llama.cpp supervisor's runtime-mutation
lease). Today it covers core options, auth/connection, invocation, workspace/agents, analysis, adaptive
memory, drafting, eval, golden harvest, scheduling stores, model fit, benchmarks, training datasets,
capacity, MCP agent runs, playbook retrieval/monitoring, worker infrastructure, model capabilities/MCP,
AgentHome, Coder, graph workflows, document ingestion, knowledge base, chat, chat stream budget,
development, the container sandbox, model runtime, images, the training runtime, and training runs.

```
                         ┌──────────────────────────────────────────────────────┐
                         │  XE-Local-AI-Engine.Client  (the Node Web Server)     │
                         │  ┌────────────────────────────────────────────────┐  │
                         │  │ Host wiring: FastEndpoints, JWT auth, SignalR,  │  │
                         │  │ rate-limit, health, hosted services, static SPA │  │
                         │  └───────────────────────┬────────────────────────┘  │
                         │                          │ AddNodeApplication         │
   Browser SPA           │  ┌───────────────────────▼────────────────────────┐  │
  ┌────────────┐  REST + │  │ Client.Application  (Services/* areas)          │  │
  │  React UI  │◀──hubs──┼─▶│ chat · agents · scheduler · model-fit · capacity│  │
  │ (loopback) │ (local) │  │ mcp · eval · training · benchmarks · images…    │  │
  └────────────┘         │  └───────┬───────────────────────────┬────────────┘  │
                         │          │                           │               │
                         │  ┌───────▼────────┐         ┌────────▼────────────┐  │
                         │  │  AI.Agent      │         │ Providers.*          │  │
                         │  │ (MAF + MEAI    │────────▶│ Abstractions seam:   │  │
                         │  │ agent loop)    │  IChat  │ ILocalModelProvider  │  │
                         │  └────────────────┘ Client  │ IChatClient/IEmbed   │  │
                         │                              │  ├ LlamaServer (RT)  │  │
                         │  ┌────────────────┐          │  ├ HuggingFace (GGUF)│  │
                         │  │ Client.        │◀─────────┤  ├ Ollama (optional) │  │
                         │  │ Persistence    │  EF/state│  ├ CodexOAuth (cloud)│  │
                         │  │ (SQLite;       │          │  ├ Capabilities (HW) │  │
                         │  │ selected AEAD) │          │  ├ StableDiffusionCpp│  │
                         │  └────────────────┘          │  └ Training (SFT)    │  │
                         │                              └────┬────────────┬────┘  │
                         │                                   │ spawn      │ spawn │
                         │                       ┌───────────▼──────┐ ┌───▼─────┐ │
                         │                       │ host llama-server│ │sd-server│ │
                         │                       │ child process(es)│ │ daemon  │ │
                         │                       │ (no Docker/WSL;  │ └─────────┘ │
                         │                       │  GPU driver only │             │
                         │                       │  to RUN — see    │             │
                         │                       │  invariant 6)    │             │
                         │                       └──────────────────┘             │
                         └──────────────────────────────────────────────────────┘
```

**Text fallback for the diagram:** the loopback browser reaches the node through REST and local SignalR;
nothing reaches it from off the machine.
Inside the node, the web host composes application services, AI/agent services, provider abstractions,
and SQLite persistence. Provider implementations may start host-user `llama-server` and `sd-server`
child processes. Those process boundaries are supervision boundaries, not container or OS-isolation
boundaries.

### Provider seam — the key abstraction
Application and agent code depend only on `ILocalModelProvider` / `IChatClient` /
`IEmbeddingGenerator` (`Providers.Abstractions/ILocalModelProvider.cs` — members like
`CreateChatClient(LocalModelSelection)`, `CreateEmbeddingGenerator(...)`,
`Pull/Delete/Warm/UnloadModelAsync`). **Provider-specific SDK types never leak across this seam** —
they stay inside the provider projects (`LlamaServerLocalModelProvider`, `OllamaLocalModelProvider`).
For OllamaSharp this is enforced, not just intended: `Providers.Ollama`'s `OllamaSharp` reference is
`PrivateAssets="compile"` so its compile assets do not flow to consumers, and
`LayerDependencyTests.ProductionProjects_HaveOnlyTheApprovedPackageReferences` catches a direct re-add of the
package. Docker and Azure remain source-scan-only, with no compile-asset boundary yet.
Exactly **two** classes implement `ILocalModelProvider` — `LlamaServerLocalModelProvider` (`llamacpp`, the
default) and `OllamaLocalModelProvider` (`ollama`, the gated secondary). The remaining `Providers.*`
projects sit alongside that seam rather than on it: `Abstractions` (contracts), `HuggingFace` (the
`IGgufModelStore` the llama-server provider delegates model acquisition to), `CodexOAuth` (a cloud
`IChatClient` factory, not an `ILocalModelProvider`), `Capabilities` (hardware probing),
`StableDiffusionCpp` (`IImageRuntime`, the image daemon — see [Image Generation](14-image-generation.md)),
and `Training` (`ITrainingRuntimeService`, the uv-provisioned Python fine-tuning runtime — see
[Training](18-training.md)).
See [Local Runtime & Providers](03-local-runtime-and-providers.md).

**Provider options are seeded at the composition root.** `Providers.*` reference only `Providers.Abstractions` and
never `Client.Application`, so a provider option object cannot read `INodeRuntimeSettings` itself. The `AddNode*`
modules build each seeded options instance from the accessor and register it **before** the provider's own
extension method, whose `TryAddSingleton` default then becomes a no-op. The same last-registration-wins shape runs
the other way for implementations: a plain `AddSingleton` in `Client.Application` overrides a provider's
`TryAddSingleton` floor (the DB-backed `IInferenceProfileResolver` over the explore-only in-provider default, the
real GPU-load admission gate over each provider's no-op). Seeded values are read once at host build, so an operator
edit applies on the next process restart.

### Launch-args seam — the inference profile resolver
Inside the LlamaServer provider stack the supervisor no longer hard-codes placement: `LlamaServerProcessSupervisor`
resolves the launch arguments for each `(model, role, backend)` spawn through a second seam,
`IInferenceProfileResolver` (`Providers.LlamaServer/Contracts/IInferenceProfileResolver.cs`). The interface is
deliberately **defined inside the provider** so the supervisor depends only on its own contract and never on
`Client.Application` (preserving the one-way arrow). The in-project `DefaultInferenceProfileResolver`
(`Implementation/DefaultInferenceProfileResolver.cs`) always returns explore-mode so the provider self-satisfies;
the real DB-backed implementation in `Client.Application` (replays a frozen profile or triggers explore) is
DI-injected over it. Two real `--list-devices` consumers sit alongside it: the process VRAM-budget probe
(`Implementation/LlamaListDevicesProcessVramBudgetProbe.cs`, `IProcessVramBudgetProbe`) and the structured
device-inventory probe (`Implementation/LlamaDeviceInventoryProbe.cs`, `ILlamaDeviceInventoryProbe`) behind the
runtime device audit. See [Local Runtime & Providers](03-local-runtime-and-providers.md).

### Per-send model routing
Because llama.cpp is **spawn-per-model** (one process/port per model, unlike Ollama's hot-swap behind one
endpoint), the runtime client selects/routes per send rather than caching one baked-in client —
`RuntimeChatClient` (`Client.Application/Services/CloudProviders/Implementation/RuntimeChatClient.cs`)
re-selects local vs cloud and the target llama-server process by `ChatOptions.ModelId` on each request.

`LocalModelProviderResolver` maps `ModelName` → `ProviderName` over the persisted `model_provider_map` and then
`ProviderName` → `ILocalModelProvider` over the registered set. **An unmapped name defaults to `llamacpp`**: Ollama
is the optional secondary runtime and the shipped default model is a GGUF, so a name with no map row — a
pre-existing GGUF install, or a registry/map divergence — still routes to llama.cpp. Genuine Ollama models are
mapped to `ollama` at pull time by the symmetric upsert on the Ollama pull endpoints, and a model pulled before
that upsert existed is repaired once at startup by `OllamaProviderMapBackfill`, so the default only ever governs
truly unmapped names. The resolver constructor validates that its default provider is registered, and `llamacpp`
always is. The supervisor's loaded-cap is surfaced through the same resolver for the preview reject-at-start check.
See [Chat](05-chat.md) and [Agent Mode](04-agent-mode.md).

---

## Post-re-architecture runtime model (locked 2026-06-17)

The runtime was deliberately re-architected (status: *decisions locked*). The driving goals: GPU inference with **zero CUDA-toolkit install** and
**removing Docker entirely**. The locked decisions that shape this whole map:

| # | Decision | Reality in code |
|---|---|---|
| 2 | **Docker removed entirely** — **amended 2026-07-29 to "no Docker on the inference path"** by [ADR 0004](../adr/0004-development-mode-container-execution-docker-stopgap.md) | No container sandbox as the inference path; `AppHost.cs` comment confirms the in-Aspire HostAgent/Docker resource was removed. The amendment permits a Docker Engine API client (`Docker.DotNet.Enhanced` — the maintained testcontainers fork, whose assembly/namespace is still `Docker.DotNet`) and a running daemon **for Development Mode build/test/lint execution only**, as a bounded stopgap that leaves the MXC provider seam open. The epic's grep-clean acceptance criterion was amended in the same change. |
| 5/6 | Hybrid spawn-per-model lifecycle; app-controlled HF download + local store | `LlamaServerProcessSupervisor`, `LlamaServerLocalModelProvider`, `Providers.HuggingFace`. |
| 7/8 | Prebuilt llama.cpp, recommended-pinned + user-upgradable; GPU variant selection only | `LlamaCppBinaryManager`, `LlamaCppReleasePins.cs`, `IGpuVariantSelector`, `LlamaCppUpdateCheckService` (notify-only update check). **Amended since the lock:** prebuilt download is still the default and the only *automatic* path, but two opt-in operator paths now sit ahead of it in `EnsureBinaryAsync` — an environment-variable bring-your-own binary override, and an in-app **source build** (`ILlamaCppSourceBuildService`, `LlamaCppSourceBuildService.cs`) that closes the missing-Linux-CUDA-prebuilt gap. Neither ever runs implicitly. See [Local Runtime & Providers §2.6](03-local-runtime-and-providers.md#26-in-app-source-builds-linux). |
| 14 | **Ollama kept as optional native secondary** (no Docker) | `Providers.Ollama` still exists; **de-orchestrated** from Aspire dev — `AppHost.cs` orchestrates only `app` + Vite + SQLite, llama.cpp is the dev runtime. |
| 16 | Embeddings via llama.cpp day one | embedding GGUF on a pooling-enabled llama-server process, lexical ranker as fallback. |
| 17 | **HostAgent deleted entirely** | The old `XE-Local-AI-Engine.HostAgent.*` projects no longer exist in the solution. The supervisor runs in-app as an unprivileged same-user child (localhost port, Job Object tree-kill on Windows). |

> **Discrepancy note (code vs. some older docs/comments):** stale comments still reference "host-agent"
> enums (e.g. the OpenAPI enum comment in `ConfigureServices.cs`). These are leftover wording, **not** a
> live HostAgent layer — there is no HostAgent project or gRPC/HMAC socket in the build. Treat HostAgent
> as **deleted** per decision #17. See [Local Runtime & Providers](03-local-runtime-and-providers.md)
> for the teardown detail.

---

## Architecture invariants

A maintainer must preserve these. Each is enforced or anchored in code today:

1. **No control-plane link.** The node opens no outbound connection to a control plane. Outbound traffic
   exists only where an operator configured a feature to fetch something (model downloads, a declared
   cloud or OpenAI-compatible provider, a registered MCP server).
2. **Secrets stay local.** Worker creds, cloud-provider creds, and HMAC/endpoint tokens are never
   returned to the browser and never logged (request-log query redaction in `Program.cs`,
   `AccessTokenQueryRedactor`). See [Security & Privacy](12-security-and-privacy.md).
3. **Local admin surface is loopback/local-only, authenticated, strict on Host/Origin, secret-redacted.**
   `LocalApiSecurityMiddleware` + JWT operator policy + antiforgery + rate limiting (`Program.cs`,
   `ConfigureServices.cs`).
4. **One-way dependency flow.** Host → `Client.Application` → `AI.Agent`/`Providers` → `Persistence`.
   The host wires only web concerns; logic registers via `AddNodeApplication`.
5. **Provider SDK types do not cross `Providers.Abstractions`.** Depend on `ILocalModelProvider` /
   `IChatClient` / `IEmbeddingGenerator`; keep OllamaSharp / llama-server HTTP types inside provider
   projects. OllamaSharp is enforced by a compile-asset boundary (`PrivateAssets="compile"` on
   `Providers.Ollama`'s reference) plus `LayerDependencyTests.ProductionProjects_HaveOnlyTheApprovedPackageReferences`
   against a direct re-add; Docker and Azure remain source-scan-only.
6. **No inference path requires Docker or WSL, and nothing requires a CUDA toolkit to *run*.** The only
   external dependency for inference is a GPU driver — and CPU fallback always works (re-architecture
   invariant §3). Two opt-in features sit outside this and need more: the **in-app source build**, an
   explicit operator action guarded by a prerequisite checklist (`cmake`/`gcc`/`g++`/`git`, plus `nvcc` for
   the CUDA backend) and unavailable off Linux (see
   [Local Runtime & Providers §2.6](03-local-runtime-and-providers.md#26-in-app-source-builds-linux)); and
   **Development Mode**, which per [ADR 0004](../adr/0004-development-mode-container-execution-docker-stopgap.md)
   takes a running Docker daemon as a hard requirement (on Windows, with the Dev Mode data root forced into
   the WSL2 filesystem). Neither ever runs implicitly, and a node that uses neither needs none of it — chat,
   embedding and image generation are unaffected either way.
7. **Inference = host llama.cpp process(es).** spawn-per-model, supervised in-app; routing is per-send by
   `ChatOptions.ModelId` (`RuntimeChatClient`). Ollama is an optional secondary, not the dev default.
   Launch args follow an **explore → freeze → replay** lifecycle: the supervisor no longer forces
   `--n-gpu-layers 999` — placement comes from `IInferenceProfileResolver` (explore-mode auto-fit, or a frozen
   per-machine profile replayed verbatim), and every spawn pins `--parallel 1` + `--no-warmup`
   (the `BuildLaunchSpec` path in `LlamaServerProcessSupervisor.cs`). See [Local Runtime & Providers](03-local-runtime-and-providers.md).
8. **Privacy-sensitive AI ops run node-local models only** (playbook analysis, eval). Cloud providers are
   never used for those paths. See [Agent Mode](04-agent-mode.md).
9. **OpenAPI is the single source of truth for all React REST clients** (hey-api generation off the
   FastEndpoints OpenAPI doc). Don't hand-write REST clients. See [API & Hubs](09-api-and-hubs.md).
10. **No autostart side effects.** Desktop launch is strictly opt-in (`XE_LAUNCH_MODE=desktop` /
    `--desktop`); off-flag the pipeline is byte-identical to a headless/Aspire/CI run (`Program.cs`,
    `DesktopLaunch`). See [Hosting & Deployment](11-hosting-and-deployment.md).
11. **Development source authority is explicit and revalidated.** A registered selected-folder ID plus
    canonical repository identity authorizes the source; the agent works in an engine-owned detached worktree,
    and only reviewed, base/hash-bound apply may change the source. This does not make executed repository code
    OS-isolated. See [Security & Privacy](12-security-and-privacy.md#development-mode-source-and-execution-boundary).

---

## Startup sequence (host boot order)

From `Client/Program.cs`, the host performs a deterministic startup before serving traffic:

1. Resolve desktop mode (opt-in), optionally bind loopback + fill per-user data config.
2. `AddServiceDefaults()` (Aspire/OTel) + `AddServices()` (the whole node — see `ConfigureServices.cs`).
3. Apply EF migrations: node chat DB, then node identity DB; recover interrupted chat messages; reconcile
   stale scheduled runs; activate the invocation resume registry; register the worker shutdown drain.
4. Build the HTTP pipeline: exception handler (RFC7807), HTTPS/HSTS (skipped in desktop), antiforgery,
   static files, health checks, `LocalApiSecurityMiddleware`, auth, FastEndpoints, hubs, (dev) Scalar, SPA fallback.

Persistence specifics (selected per-column encryption, the migrations under
`Client.Persistence/Migrations/` plus 2 model snapshots, and recovery services) are covered in
[Data & Persistence](08-data-and-persistence.md).

---

## Where to go next

- The runtime/provider internals (supervisor, binary manager, GPU variant, HF GGUF, Ollama):
  [Local Runtime & Providers](03-local-runtime-and-providers.md)
- The agent execution loop, MAF/MEAI wiring, tools, governance: [Agent Mode](04-agent-mode.md)
- Chat send path, streaming, per-send routing: [Chat](05-chat.md)
- Scheduler (Quartz) and model-fit advisor: [Scheduler](06-scheduler.md), [Model Fit](07-model-fit.md)
- The full project inventory and folder conventions: [Project Layout](02-project-layout.md)

---

## Related pages

- [Home](Home.md)
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
- [Testing & Validation](13-testing-and-validation.md)
- [Image Generation](14-image-generation.md)
- [Knowledge Base / RAG](15-knowledge-base.md)
- [Code Organization Conventions](16-code-conventions.md)
- [Writing Tests](17-writing-tests.md)
- [Training](18-training.md)
- ADRs: [0001 — Development Mode restart recovery](../adr/0001-development-mode-restart-recovery.md), [0002 — Development cloud-egress carrier](../adr/0002-development-cloud-egress-carrier.md), [0003 — Six-plan implementation scope](../adr/0003-six-plan-operator-decisions.md), [0004 — Development Mode container execution](../adr/0004-development-mode-container-execution-docker-stopgap.md), [0005 — Training runtime Python exclusivity](../adr/0005-training-runtime-python-exclusivity-and-project-placement.md)
