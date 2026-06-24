# Architecture Overview

> Last reviewed: 2026-06-24 · Code-grounded.

XE Local AI Engine is the **node-side runtime** of the C0re platform: a single ASP.NET Core process
(`XE-Local-AI-Engine.Client`) that hosts the React management UI, owns the one outbound platform
`WorkerHub` connection, serves local APIs and SignalR hubs, persists chat/state in encrypted SQLite,
and runs the local model runtime **in-process** via a host `llama.cpp` supervisor. This page is the map:
it shows the node↔platform boundary, the in-process layering and one-way dependency flow, and the
post-re-architecture runtime model (host llama.cpp, **no Docker, no HostAgent**). Subsystem detail lives
in the per-topic pages linked throughout.

---

## What the system is

The engine is **one deployable web server** plus a Vite/React SPA it serves. There is no separate worker
process, no container runtime, and (since the 2026-06-17 re-architecture) no HostAgent sidecar. Everything
— the management UI, the local REST/SignalR surface, the agent execution loop, and the model runtime
supervisor — lives inside the `XE-Local-AI-Engine.Client` host.

| Concern | Where it lives | Evidence |
|---|---|---|
| Host / web pipeline / endpoints / hubs | `XE-Local-AI-Engine.Client` | `Client/Program.cs`, `Client/ConfigureServices.cs` |
| Application logic, options, service areas | `XE-Local-AI-Engine.Client.Application` | `NodeApplicationServiceCollectionExtensions.cs` (`AddNodeApplication`) |
| Agent/AI wiring (MAF + MEAI) | `XE-Local-AI-Engine.AI.Agent` | `AI.Agent/DependencyInjection/AgentServiceCollectionExtensions.cs` |
| Provider seams (no SDK leak) | `XE-Local-AI-Engine.Providers.Abstractions` | `Providers.Abstractions/ILocalModelProvider.cs` |
| Local inference runtime | `XE-Local-AI-Engine.Providers.LlamaServer` | `LlamaServerLocalModelProvider.cs`, `LlamaServerProcessSupervisor.cs` |
| HF GGUF discovery/download | `XE-Local-AI-Engine.Providers.HuggingFace` | provider project |
| Optional secondary provider | `XE-Local-AI-Engine.Providers.Ollama` | `OllamaLocalModelProvider.cs` |
| Encrypted SQLite persistence | `XE-Local-AI-Engine.Client.Persistence` | `Client/Program.cs` migration runners |
| React management UI (17 features) | `XE-Local-AI-Engine.Client.React` | `Client.React/src/features/` |
| Dev orchestration only | `XE-Local-AI-Engine.AppHost` | `AppHost/AppHost.cs` |

> Aspire (`AppHost.cs`) is a **development-only** orchestrator (`app` project + Vite `client-react` + a
> dev SQLite resource). It is not part of the shipped runtime — production/desktop launches `Client`
> directly. See [Hosting & Deployment](11-hosting-and-deployment.md). For the full project inventory see
> [Project Layout](02-project-layout.md).

---

## The node ↔ C0re-platform boundary (WorkerHub)

The single trust boundary that crosses the machine is the **outbound `WorkerHub` SignalR connection** to
the central C0re platform. Only the Node Web Server holds it; the browser never sees it.

- The connection abstraction is `IWorkerHubConnection`
  (`Client.Application/Services/Connection/IWorkerHubConnection.cs`): it sends `WorkerHello`,
  worker-key registration, capabilities, and heartbeats, and receives platform-pushed events
  (`InvocationAssignedReceived`, `ToolCallResultReceived`, `ApprovalResolvedReceived`,
  `InvocationCancelledReceived`, `DisconnectRequestedReceived`, `ConversationPurgedReceived`).
- Lifecycle is driven by hosted services in the host: `AutoConnectBackgroundService` and
  `HeartbeatBackgroundService` (`Client/BackgroundServices/`), and connection health surfaces through
  `WorkerHealthCheck` (the `/health/ready` gate, `Client/HealthChecks/WorkerHealthCheck.cs`).
- On shutdown the host drains the worker cleanly via `IWorkerShutdownDrainService`
  (`RegisterWorkerShutdownDrain` in `Program.cs`).

**Invariant:** only the Node Web Server talks to the platform over `WorkerHub`. Worker credentials, the
HMAC/endpoint tokens, and cloud-provider credentials stay **local** — never returned to the browser,
never logged. See [Security & Privacy](12-security-and-privacy.md) and [API & Hubs](09-api-and-hubs.md).

---

## The local surface (browser ↔ node)

Distinct from the platform link, the React SPA talks to the host over a **loopback/local** surface:

- REST endpoints (FastEndpoints) under the prefix `LocalApiRoutes.Prefix` (`/api/local/v1`), with a
  global operationId name generator feeding the OpenAPI doc that generates the hey-api React SDK
  (`Program.cs`, `config.Endpoints.NameGenerator`).
- Three local SignalR hubs, each `RequireAuthorization(NodeAuthorizationPolicies.Operator)`:
  `LocalChatHub`, `SchedulerHub`, `PreviewWorkflowHub` (`Program.cs` `MapHub<…>`).
- JWT-bearer auth (operator role), antiforgery, per-IP rate limiting, and a
  `LocalApiSecurityMiddleware` that enforces the loopback/`Host`/`Origin` posture
  (`ConfigureServices.cs`, `Program.cs`).
- The SPA itself is served as static files with `MapFallbackToFile("index.html")`.

See [API & Hubs](09-api-and-hubs.md) and [React Client](10-react-client.md).

---

## In-process layering & one-way dependency flow

Inside the single host the code is layered so dependencies flow **one way** (host → application →
agent/providers → persistence). The host only wires web-framework concerns; all node logic is registered
through `AddNodeApplication`, which composes ~20 `AddNode*` feature modules
(`NodeApplicationServiceCollectionExtensions.cs`).

```
                         ┌──────────────────────────────────────────────────────┐
   C0re Platform         │  XE-Local-AI-Engine.Client  (the Node Web Server)     │
  ┌────────────┐  Worker │  ┌────────────────────────────────────────────────┐  │
  │  WorkerHub │◀────Hub─┼──┤ Host wiring: FastEndpoints, JWT auth, SignalR,  │  │
  │ (platform) │  (only  │  │ rate-limit, health, hosted services, static SPA │  │
  └────────────┘  node)  │  └───────────────────────┬────────────────────────┘  │
                         │                          │ AddNodeApplication         │
   Browser SPA           │  ┌───────────────────────▼────────────────────────┐  │
  ┌────────────┐  REST + │  │ Client.Application  (~28 service areas)         │  │
  │  React UI  │◀──hubs──┼─▶│ chat · agents · scheduler · model-fit · capacity│  │
  │ (loopback) │ (local) │  │ connection · mcp · eval · adaptive-memory …     │  │
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
                         │  │ Persistence    │  EF/state│  └ CodexOAuth (cloud)│  │
                         │  │ (encrypted     │          └─────────┬───────────┘  │
                         │  │  SQLite)       │                    │ spawn        │
                         │  └────────────────┘          ┌─────────▼───────────┐  │
                         │                              │ host llama-server    │  │
                         │                              │ child process(es)    │  │
                         │                              │ (no Docker/WSL/CUDA  │  │
                         │                              │  toolkit — driver    │  │
                         │                              │  only)               │  │
                         └──────────────────────────────────────────────────────┘
```

### Provider seam — the key abstraction
Application and agent code depend only on `ILocalModelProvider` / `IChatClient` /
`IEmbeddingGenerator` (`Providers.Abstractions/ILocalModelProvider.cs` — members like
`CreateChatClient(LocalModelSelection)`, `CreateEmbeddingGenerator(...)`,
`Pull/Delete/Warm/UnloadModelAsync`). **Provider-specific SDK types never leak across this seam** —
they stay inside the provider projects (`LlamaServerLocalModelProvider`, `OllamaLocalModelProvider`).
Three concrete impls exist (`LlamaServer`, `Ollama`; plus the HF GGUF store and CodexOAuth cloud path).
See [Local Runtime & Providers](03-local-runtime-and-providers.md).

### Per-send model routing
Because llama.cpp is **spawn-per-model** (one process/port per model, unlike Ollama's hot-swap behind one
endpoint), the runtime client selects/routes per send rather than caching one baked-in client —
`RuntimeChatClient` (`Client.Application/Services/CloudProviders/Implementation/RuntimeChatClient.cs`)
re-selects local vs cloud and the target llama-server process by `ChatOptions.ModelId` on each request.
See [Chat](05-chat.md) and [Agent Mode](04-agent-mode.md).

---

## Post-re-architecture runtime model (locked 2026-06-17)

The runtime was deliberately re-architected (`Plans/2026-06-17-runtime-rearchitecture-epic.md`,
status: *decisions locked*). The driving goals: GPU inference with **zero CUDA-toolkit install** and
**removing Docker entirely**. The locked decisions that shape this whole map:

| # | Decision | Reality in code |
|---|---|---|
| 2 | **Docker removed entirely** | No `Docker.DotNet`, no container sandbox as the inference path. `AppHost.cs` comment confirms the in-Aspire HostAgent/Docker resource was removed. |
| 5/6 | Hybrid spawn-per-model lifecycle; app-controlled HF download + local store | `LlamaServerProcessSupervisor`, `LlamaServerLocalModelProvider`, `Providers.HuggingFace`. |
| 7/8 | Prebuilt llama.cpp, recommended-pinned + user-upgradable; GPU variant selection only | `LlamaCppBinaryManager`, `LlamaCppReleasePins.cs`, `IGpuVariantSelector`, `LlamaCppUpdateCheckService` (notify-only update check). |
| 14 | **Ollama kept as optional native secondary** (no Docker) | `Providers.Ollama` still exists; **de-orchestrated** from Aspire dev — `AppHost.cs` orchestrates only `app` + Vite + SQLite, llama.cpp is the dev runtime. |
| 16 | Embeddings via llama.cpp day one | embedding GGUF on a pooling-enabled llama-server process, lexical ranker as fallback. |
| 17 | **HostAgent deleted entirely** | The old `XE-Local-AI-Engine.HostAgent.*` projects no longer exist in the solution; only a teardown plan remains under `Plans/`. The supervisor runs in-app as an unprivileged same-user child (localhost port, Job Object tree-kill on Windows). |

> **Discrepancy note (code vs. some older docs/comments):** stale comments still reference "host-agent"
> enums (e.g. the OpenAPI enum comment in `ConfigureServices.cs`). These are leftover wording, **not** a
> live HostAgent layer — there is no HostAgent project or gRPC/HMAC socket in the build. Treat HostAgent
> as **deleted** per decision #17. See [Local Runtime & Providers](03-local-runtime-and-providers.md)
> for the teardown detail.

---

## Architecture invariants

A maintainer must preserve these. Each is enforced or anchored in code today:

1. **One platform link.** Only the Node Web Server talks to the platform over `WorkerHub`
   (`IWorkerHubConnection`). Nothing else opens an outbound platform connection.
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
   projects.
6. **No code path requires Docker, WSL, or a CUDA toolkit.** The only external runtime dependency is a
   GPU driver — and CPU fallback always works (re-architecture invariant §3).
7. **Inference = host llama.cpp process(es).** spawn-per-model, supervised in-app; routing is per-send by
   `ChatOptions.ModelId` (`RuntimeChatClient`). Ollama is an optional secondary, not the dev default.
8. **Privacy-sensitive AI ops run node-local models only** (playbook analysis, eval). Cloud providers are
   never used for those paths. See [Agent Mode](04-agent-mode.md).
9. **OpenAPI is the single source of truth for all React REST clients** (hey-api generation off the
   FastEndpoints OpenAPI doc). Don't hand-write REST clients. See [API & Hubs](09-api-and-hubs.md).
10. **No autostart side effects.** Desktop launch is strictly opt-in (`XE_LAUNCH_MODE=desktop` /
    `--desktop`); off-flag the pipeline is byte-identical to a headless/Aspire/CI run (`Program.cs`,
    `DesktopLaunch`). See [Hosting & Deployment](11-hosting-and-deployment.md).

---

## Startup sequence (host boot order)

From `Client/Program.cs`, the host performs a deterministic startup before serving traffic:

1. Resolve desktop mode (opt-in), optionally bind loopback + fill per-user data config.
2. `AddServiceDefaults()` (Aspire/OTel) + `AddServices()` (the whole node — see `ConfigureServices.cs`).
3. Apply EF migrations: node chat DB, then node identity DB; recover interrupted chat messages; reconcile
   stale scheduled runs; activate the invocation resume registry; register the worker shutdown drain.
4. Build the HTTP pipeline: exception handler (RFC7807), HTTPS/HSTS (skipped in desktop), antiforgery,
   static files, health checks, `LocalApiSecurityMiddleware`, auth, FastEndpoints, hubs, (dev) Scalar +
   Agent DevUI, SPA fallback.

Persistence specifics (encrypted SQLite, the ~28 EF migrations, recovery services) are covered in
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
