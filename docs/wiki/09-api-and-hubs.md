# API Surface & Realtime Hubs

> Last reviewed: 2026-06-27 · Code-grounded.

This page documents the **Node Web Server transport layer**: the HTTP API exposed under `/api/local/v1` (FastEndpoints), the four SignalR push/stream hubs in `Client/Hubs`, the single outbound **WorkerHub** connection to the C0re platform, the cross-cutting transport concerns (security middleware, exception handling, health checks, auth), and how the backend's OpenAPI document becomes the single source of truth for every React REST client via hey-api.

If you are adding or changing an endpoint or hub, this is the page that tells you *where the route lives, how it is secured, and what regen step you must run for the React client to see it*.

---

## Big picture

```
 Browser (React SPA, same origin)                C0re Platform
        │  HTTP /api/local/v1/*  (FastEndpoints)         ▲
        │  SignalR hubs (chat/sched/preview/gguf)        │  ONE outbound
        ▼                                                │  WorkerHub conn
 ┌──────────────────────────────────────────────────────┴───────────────┐
 │ Node Web Server  (XE-Local-AI-Engine.Client)                          │
 │  • UseExceptionHandler  → IExceptionHandler chain → RFC7807           │
 │  • MapHealthChecks /health/live  /health/ready                        │
 │  • LocalApiSecurityMiddleware (loopback peer + Host/Origin guard)     │
 │  • UseAuthentication / UseAuthorization (JWT bearer, Operator policy) │
 │  • UseFastEndpoints (RoutePrefix = api/local/v1)                      │
 │  • MapHub<LocalChat|Scheduler|Preview|GgufDownload>                  │
 │  • MapFallbackToFile("index.html")  (serves the React build)         │
 └───────────────────────────────────────────────────────────────────────┘
```

Two transport directions to keep straight:

- **Inbound, browser → node** — the local management API and local hubs. Loopback/local-only, authenticated as the node *operator*, secret-redacted. This is everything under `/api/local/v1`.
- **Outbound, node → platform** — exactly **one** SignalR connection, `WorkerHubConnection`. Worker creds and platform tokens live only here and are never returned to the browser. See [Security & Privacy](12-security-and-privacy.md) for the full invariant.

The middleware/mapping order above is authoritative — see `XE-Local-AI-Engine.Client/Program.cs:92-196`.

---

## 1. The inbound HTTP API (FastEndpoints)

### Conventions

- **Framework:** FastEndpoints. One endpoint = one `*Endpoint.cs` class; request/response shapes live in `*EndpointDtos.cs`, FluentValidation rules in `*EndpointValidators.cs`, and entity↔DTO mapping in `Mappers/`.
- **Route prefix:** every endpoint is mounted under `api/local/v1` via `config.Endpoints.RoutePrefix = LocalApiRoutes.Prefix` (`Program.cs:142`, `LocalApiRoutes.cs:8`).
- **Routes are centralized**, not string-literal'd per endpoint. All route templates are constants on nested static classes in `XE-Local-AI-Engine.Client/Endpoints/Common/LocalApiRoutes.cs` (e.g. `LocalApiRoutes.Scheduler.JobById`). Change a path here and both the endpoint and any `Send.CreatedAtAsync<TEndpoint>()` location resolution follow.
- **operationId = camelCased class name.** A single global `NameGenerator` strips the `Endpoint` suffix and lowercases the first char (`Program.cs:149-158`): `CreateScheduledJobEndpoint` → `createScheduledJob`. These operationIds are what the hey-api React SDK function names are derived from, so **the C# class name is the public contract name** — rename with care.
- **Errors → ProblemDetails.** `config.Errors.UseProblemDetails()` (`Program.cs:160`) turns validation failures into RFC7807. Domain exceptions are handled by the `IExceptionHandler` chain (see §4).

### Endpoint inventory (by feature)

Grouped from `Client/Endpoints/**/V1/` and `LocalApiRoutes.cs`. The "Owner page" column points to the subsystem wiki page that explains the behavior behind the routes.

| Group (route base) | Representative routes | Owner page |
|---|---|---|
| **ApiFoundation** | `diagnostics/validation-probe` | (transport diagnostics) |
| **Auth** (`auth/*`) | `auth/status`, `auth/setup`, `auth/login`, `auth/refresh`, `auth/logout`, `auth/change-password`, `auth/me` | [Security & Privacy](12-security-and-privacy.md) |
| **NodeBinding** (`binding/*`) | `binding/start`, `binding/poll`, `binding/cancel` | [Hosting & Deployment](11-hosting-and-deployment.md) |
| **Connection** (`connection/*`) | `connection`, `connection/connect`, `connection/disconnect`, `connection/auto-connect/enable\|disable` | [Architecture Overview](01-architecture-overview.md) |
| **NodeSettings** (`node-settings`) | get/save node settings | [Hosting & Deployment](11-hosting-and-deployment.md) |
| **CloudSettings** (`cloud-settings`) | get/save/clear cloud-provider settings | [Security & Privacy](12-security-and-privacy.md) |
| **Cloud / Codex** (`cloud/codex/*`) | `cloud/codex/login`, `cloud/codex/status`, `cloud/codex/logout` (ChatGPT OAuth) | [Chat](05-chat.md) |
| **LocalChat** (`chat/*`) | `chat/conversations` (+ `{id}` rename/pin/archive/branch/memory-excluded/selected-path), `chat/.../messages/{id}/revisions\|feedback`, `chat/conversations/{id}/uploads(/{fileId})` (file attachments — POST multipart upload / GET list / DELETE), `chat/cancel` | [Chat](05-chat.md) |
| **LocalModels** (`models/*`) | `models`, `models/{name}`, `models/{name}/details\|kind\|unload`, `models/select`, `models/pull`, `models/pull/stream`, `models/running` | [Local Runtime & Providers](03-local-runtime-and-providers.md) |
| **ModelFit** (`model-fit/*`) | `model-fit/recommendations/latest\|refresh`, `model-fit/hardware-profile`, `model-fit/gguf/browse\|inspect`, `model-fit/download(/cancel)`, `model-fit/gguf/downloads(/{modelName})`, `model-fit/running(/eject)`, `model-fit/llamacpp/version\|runtime\|update`, `model-fit/hf-token`, `model-fit/profiles(/explore\|benchmark\|freeze\|invalidate)` (inference optimizer) | [Model Fit](07-model-fit.md) |
| **Voice** (`voice/*`) | `voice/manifest` (GET — config-only TTS manifest: allowed models, voice profiles, feature flag, integrity hashes, download URLs; the backend serves no audio) | [React Client](10-react-client.md) |
| **Tutorial** (`tutorial-state`) | `tutorial-state` (GET reads the current user's recorded tour entries; PUT upserts one) | [React Client](10-react-client.md) |
| **Invocations** (`invocations`) | invocation monitor | [Architecture Overview](01-architecture-overview.md) |
| **Agents** (`agents/*`) | `agents`, `agents/{id}`, `agents/tool-capable-models`, `agents/templates(/import)`, `agents/{id}/playbook(/...)`, `.../golden-conversations(/...)`, `.../feedback-insights`, `.../playbook/monitor`, `.../execution-logs`, `agents/run-envelopes` (operator-gated run-envelope lifecycle list) | [Agent Mode](04-agent-mode.md) |
| **Skills** (`skills/*`) | `skills`, `skills/{skillId}` | [Agent Mode](04-agent-mode.md) |
| **Mcp** (`mcp/*`) | `mcp/servers`, `mcp/servers/{id}(/enabled\|/tools)`, `tool-catalog` | [Agent Mode](04-agent-mode.md) |
| **Scheduler** (`scheduler/*`) | `scheduler/templates`, `scheduler/jobs`, `scheduler/jobs/{id}(/enable\|disable\|trigger)`, `scheduler/runs`, `scheduler/runs/{id}(/cancel)` | [Scheduler](06-scheduler.md) |
| **Preview / Open Canvas** (`preview/*`) | `preview/workflows`, `preview/workflows/{id}(/execute)`, `preview/runs/execute`, `preview/runs/{id}/continue\|cancel` | [React Client](10-react-client.md) |

> **Endpoints are orchestration-only.** They validate input, call a service in `XE-Local-AI-Engine.Client.Application/Services/*`, and map to a DTO. Business logic does not live in the endpoint. When tracing a route, jump straight to the matching service area.

### Design notes on the newer endpoint families

- **Inference optimizer profiles (`model-fit/profiles*`).** The collection `GET model-fit/profiles` lists every persisted node-local profile (machine key omitted). The four actions — `explore`, `benchmark`, `freeze`, `invalidate` — are all **POST with the target carried in the body**, never a route param, so the POST always has a body. The literal action segments follow `profiles`, so none can be parsed as a profile id. `benchmark` is the gate for `freeze` (a profile can only be frozen after a successful benchmark). See `Endpoints/ModelFit/V1/{Explore,Benchmark,Freeze,Invalidate}InferenceProfileEndpoint.cs` + `ListInferenceProfilesEndpoint.cs`; runtime side in [Local Runtime & Providers](03-local-runtime-and-providers.md).
- **Chat file uploads (`chat/conversations/{id}/uploads`).** The upload endpoint is the one multipart surface: it calls `AllowFileUploads()` (`UploadConversationFileEndpoint.cs:32`) and binds an `IFormFile`, enforcing the size cap + extension allowlist and sanitizing the client name to a leaf. List is a plain GET; delete is `DELETE .../uploads/{fileId}` keyed on the server-generated file id. See [Chat](05-chat.md).
- **Body-less POST (415) override.** Route-only POST actions whose id binds from the route (e.g. scheduler `trigger`/`enable`/`disable`, run `cancel`) send no body and therefore no `Content-Type`; FastEndpoints' default `Accepts` metadata answers that with **415**. Those endpoints override it with `Description(x => x.Accepts<T>())` so the body-less request is accepted (`TriggerScheduledJobEndpoint.cs:20`). See [Scheduler](06-scheduler.md).
- **Run-envelope lifecycle records (`GET agents/run-envelopes`).** Read-only, **operator-gated**, metadata-only projection of the durable per-invocation run envelopes (`ListRunEnvelopesEndpoint`). Newest-first, paged (`limit` default 50 / max 200, `offset`), optionally scoped by `conversationId`. The store holds no message content — only terminal status, usage/timing counters, correlation + trace ids — so there is nothing to redact, and `FailureCategory` is a category enum name only. See the shared `agent_execution_logs` schema in [Data & Persistence](08-data-and-persistence.md). This endpoint (plus `GET agents/{agentDefinitionId}/execution-logs`) is also the durable, no-live-collector-needed path for incident diagnosis — see the [OTel export operator runbook](../runbooks/otel-export-operator-runbook.md) for how it complements (and doesn't depend on) OpenTelemetry export.
- **Busy admission → 503 + `Retry-After` (document ingestion).** The knowledge-document upload/reindex endpoints and the chat file upload each guard a bounded capacity with **non-blocking** admission: a full knowledge ingestion queue (`KnowledgeIngestionDispatcher`, bounded capacity 256) returns `QueueFull`, and the synchronous extraction gate (`DocumentExtractionAdmissionGate`, a `SemaphoreSlim`) rejects when every slot is taken — both surface as **503 Service Unavailable with `Retry-After: 5`** rather than holding the request or growing the backlog. Admission is idempotent (a document already queued/in-flight is a no-op), and a knowledge upload whose ingestion a full queue previously rejected (leaving its blob persisted-but-unindexed) is re-enqueued on a later re-upload — so a 503 is a retryable busy signal, not data loss. See [Knowledge Base](15-knowledge-base.md).

### Notable non-typed routes (SSE streams)

A few routes are intentionally **hand-wired on the React client and not consumed through the generated typed SDK**, because they are streaming responses, not request/response JSON:

- `models/pull/stream` — `PullStreamLocalModelEndpoint`, streams pull progress (mirrors the chat SSE pattern). Documented inline at `LocalApiRoutes.cs:114-117`.

Chat *message streaming itself does not go through HTTP at all* — it is a SignalR streaming hub method (see §2).

---

## 2. SignalR hubs (inbound, browser ↔ node)

Only **four** hubs exist (`Client/Hubs/`). All are `[Authorize(... Policy = NodeAuthorizationPolicies.Operator)]` and mapped with `.RequireAuthorization(Operator)` (`Program.cs:198-205`). Their full paths are constants (`...Hub` in `LocalApiRoutes.cs`), mapped via `MapHub` *outside* the FastEndpoints prefix.

| Hub | Path | Direction | Purpose | Owner page |
|---|---|---|---|---|
| `LocalChatHub` | `/api/local/v1/chat/hub` | client→server **streaming** methods | `SendMessage`, `RegenerateMessage`, `ResumeMessage` — each returns `IAsyncEnumerable<ChatStreamEvent>` (server-streaming) | [Chat](05-chat.md) |
| `SchedulerHub` | `/api/local/v1/scheduler/hub` | server→client push only | No client-callable methods; the class body is empty. Events are broadcast via `SchedulerEventPublisher` + `IHubContext<SchedulerHub>` | [Scheduler](06-scheduler.md) |
| `PreviewWorkflowHub` | `/api/local/v1/preview/hub` | mixed | `Subscribe`/`Unsubscribe` opt a connection into a per-run group; `OnDisconnectedAsync` cancels runs owned by a vanished tab; events pushed via `PreviewWorkflowEventPublisher` | [React Client](10-react-client.md) |
| `GgufDownloadHub` | `/api/local/v1/model-fit/gguf/downloads/hub` | server→client push only | No client-callable methods; the class body is empty (`GgufDownloadHub.cs:15`). Sanitized GGUF download-progress events broadcast via `GgufDownloadEventPublisher` + `IHubContext<GgufDownloadHub>`. Replaces the per-second `GET model-fit/gguf/downloads` poll; the list endpoint stays for the one-shot hydrate on mount | [Model Fit](07-model-fit.md) |

### Hub event-name contracts

Push hubs use **stable string constants as the SignalR client-method names**, doubling as the wire event-type discriminator so React can subscribe per event name:

- **Scheduler** (`SchedulerHubEvents`, `ISchedulerEventPublisher.cs:28`): `scheduler.jobDefinitionChanged`, `scheduler.runStarted`, `scheduler.runProgress`, `scheduler.runCompleted`, `scheduler.runFailed`, `scheduler.runCancelled`.
- **Preview** (`PreviewWorkflowHubEvents`, `IPreviewWorkflowEventPublisher.cs:26`): `preview.node.{started|output|debug|completed|failed}` and `preview.run.{started|paused|completed|failed|cancelled}`.
- **GGUF download** (`GgufDownloadHubEvents`, `IGgufDownloadEventPublisher.cs:26`): a single `ggufDownload.statusChanged` event; each push carries the sanitized `GgufDownloadStatusHubEvent` for one tracked download.

### Publisher pattern (default no-op + host swap)

Scheduler and Preview broadcasts go through a publisher interface whose default implementation is a **no-op** (`NullPreviewWorkflowEventPublisher`); the Client host swaps in the hub-backed publisher. This keeps `Client.Application` services free of a SignalR dependency — they call `IPreviewWorkflowEventPublisher`/`ISchedulerEventPublisher`, and the transport is injected at the host. The concrete `PreviewWorkflowEventPublisher`/`SchedulerEventPublisher` (`Client/Hubs/*EventPublisher.cs`) wrap `IHubContext<T>`.

### Per-run scoping & cleanup (Preview)

`PreviewWorkflowHub.RunGroup(runId)` → group name `preview-run-{runId:N}`. A **single hub connection can drive several runs**, so every event payload carries a mandatory `RunId` — that is the real cross-contamination guard, with the group only scoping delivery. `OnDisconnectedAsync` calls `CancelRunsForConnectionAsync(ConnectionId)` so an abandoned browser tab does not keep compute burning.

> **Privacy note (documented exception):** unlike the Scheduler ("sanitize everything"), Preview payloads carry the operator's *own* transient run output over the localhost Operator hub — nothing is persisted, logged, or indexed (`IPreviewWorkflowEventPublisher.cs:9-11`). Scheduler payloads, by contrast, are sanitized. See [Security & Privacy](12-security-and-privacy.md).

### Hub authentication: token-in-query

Browsers cannot set an `Authorization` header on a WebSocket handshake, so JWT bearer is configured with an `OnMessageReceived` handler that pulls the token from the `access_token` query-string parameter **only for requests under `/api/local/v1`** (`ConfigureServices.cs:148-157`). React clients append `?access_token=…` when opening a hub. See `NodeChatConnection.ts` for the client side ([React Client](10-react-client.md)).

---

## 3. The outbound platform connection (WorkerHub)

`WorkerHubConnection` (`XE-Local-AI-Engine.Client.Application/Services/Connection/Implementation/WorkerHubConnection*.cs`) is the **single** SignalR client connection from this node to the C0re platform. It is the only component allowed to hold platform/worker credentials.

- **Partial class, split by concern:** `.cs` (lifecycle/ctor), `.EventHandlers.cs` (inbound platform→node events), `.Payloads.cs` (wire payload parsing), `.Reconnect.cs` (reconnect/backoff).
- **Auth & token lifecycle:** holds `ITokenStore`, `INodeKeyRegistry`, `IWorkerTokenRefreshService`, and `CentralPlatformOptions`; refreshes the access token with a 5-minute skew (`AccessTokenRefreshSkew`) under a `SemaphoreSlim`. These secrets stay in process memory and are **never** surfaced on `/api/local/v1` or logged.
- **Inbound platform events** (registered `.On<…>` handlers in `WorkerHubConnection.EventHandlers.cs`): `InvocationAssigned`, `InvocationAssignedV2`, `ToolCallResult`, `DisconnectRequested`, `ApprovalResolved`, `InvocationCancelled`, `ConversationPurged`. These are re-raised as C# events (`InvocationAssignedReceived`, `ToolCallResultReceived`, etc.) that the application layer subscribes to, then routes into the local runtime.
- **Capability reporting:** `ReportCapabilitiesRequestedAsync` answers the platform's capability probe (what models/tools this node offers). The reporter is injected `Lazy<ICapabilityReporter>` to break a construction cycle.
- **Local-control surface:** the browser drives this connection only through the `connection/*` REST endpoints (connect/disconnect/auto-connect) — it never sees the underlying tokens.

This is the architectural choke point of the platform's "only the Node Web Server talks to the platform" invariant. See [Architecture Overview](01-architecture-overview.md).

---

## 4. Cross-cutting transport concerns

### Exception handling → RFC7807

Registered **before** `UseFastEndpoints` so it wraps endpoints (`Program.cs:91-92`). Two `IExceptionHandler` implementations are chained in order (`ConfigureServices.cs:70-72`) plus `AddProblemDetails()`:

1. `ConflictExceptionHandler` — maps domain conflict exceptions to 409.
2. `DefaultExceptionHandler` — catch-all 500; redacts internal detail outside development (takes `IHostEnvironment`).

This mirrors the central platform's handler-chain pattern: specific handlers first, generic catch-all last. Internal error detail is never leaked to the browser.

### Health checks

Two endpoints, mapped **before** auth so an orchestrator/probe can reach them unauthenticated (`Program.cs:109-132`):

| Endpoint | Behavior |
|---|---|
| `/health/live` | `Predicate = _ => false` — runs no checks; returns 200 if the process can serve requests (liveness). |
| `/health/ready` | runs only checks tagged `ready`; custom JSON writer emits `{status, checks:[{name,status,duration}]}` (readiness). |

### Security middleware & auth ordering

- `LocalApiSecurityMiddleware` (`Endpoints/Common/LocalApiSecurityMiddleware.cs`) runs before routing/auth. For any path under `/api/local/v1` it returns **403** unless the transport **peer** is loopback (`context.Connection.RemoteIpAddress` — the socket peer, so a routable caller is rejected even if it forges a loopback `Host`/`Origin`; a null peer, e.g. the in-process test host or an in-process health probe, is treated as loopback-equivalent) *and* the `Host` is in `{localhost, 127.0.0.1, ::1}` *and* the `Origin` (when present) is same-scheme/same-host/same-port and loopback. This enforces loopback-only access at the edge. See [Security & Privacy](12-security-and-privacy.md) §3.1.
- **Startup bind guard.** `LoopbackBindGuard` (`Hosting/LoopbackBindGuard.cs`, wired via `LoopbackBindGuard.Guard(app)`) is defense-in-depth behind the request-time middleware: after `ApplicationStarted` — so an OS-assigned port and wildcard expansion are already resolved — it inspects the addresses Kestrel *actually* bound and, if any is non-loopback (wildcards `*`/`+`/`0.0.0.0`/`::` count), logs a **critical** line naming the offending address and shuts the app down with exit code 1. The opt-out `Security:AllowNonLoopbackBind=true` defaults to `false` and no supported launch sets it. See [Security & Privacy](12-security-and-privacy.md) §3.4.
- Then `UseRouting → UseRateLimiter → UseAuthentication → UseAuthorization` (`Program.cs:135-138`). Authn is JWT bearer; the `Operator` authorization policy gates both endpoints and hubs. Tests in `XE-Local-AI-Engine.Tests/ApiFoundation/LocalApiSecurityTests.cs` assert: missing/invalid token → 401, unsafe host → 400, unsafe origin → 403, valid token + same-origin → allowed.
- Request logging redacts the access-token query param via `AccessTokenQueryRedactor` before anything is written (`Program.cs:81-86`).

Full rationale: [Security & Privacy](12-security-and-privacy.md).

### Dev-only surfaces

Not mapped in Production (`Program.cs:170-194`): OpenAPI JSON at `/openapi/local/v1/{documentName}.json`, the Scalar API reference at `/scalar`, and (Development only) the Agent Framework DevUI at `/devui` with OpenAI-compatible Responses/Conversations endpoints.

### Static SPA fallback

`UseStaticFiles()` + `MapFallbackToFile("index.html")` serve the built React app from the same origin as the API (the "C0re static-files pattern"). See [Hosting & Deployment](11-hosting-and-deployment.md).

---

## 5. OpenAPI → hey-api: the single source of truth for React REST

**Rule:** the backend's OpenAPI document is the *only* definition of REST request/response shapes used by React. React never hand-writes a REST client; it imports generated functions. Hubs are the exception (hand-wrapped — SignalR isn't in OpenAPI).

### The pipeline

```
FastEndpoints (NSwag doc)                       React build
   │  /openapi/local/v1/v1.json                    ▲
   │                                               │  src/core/api/generated/
   ▼                                               │  (axios SDK + zod + react-query)
 openapi:fetch ──► openapi/v1.json ──► openapi:generate (hey-api/openapi-ts)
```

- **Doc source:** NSwag-generated document served at `/openapi/local/v1/{documentName}.json` (`Program.cs:172-175`, dev-only). `XE-Local-AI-Engine.Tests/ApiFoundation/OpenApiDocumentTests.cs` guards the document.
- **Fetch:** `pnpm run openapi:fetch` → `scripts/FetchOpenapi.mjs` pulls `OPENAPI_SPEC_URL` (default `https://localhost:50722/openapi/local/v1/v1.json`) into `openapi/v1.json`. Set `OPENAPI_INSECURE=1` to accept the dev self-signed cert.
- **Generate:** `pnpm run openapi:generate` → `openapi-ts --file OpenapiTs.config.ts` writes `src/core/api/generated/`.
- **Combined:** `pnpm run openapi` runs both; `openapi:check` / `openapi:check:live` fail CI if the generated output drifts from committed code (`git diff --exit-code`).

### What the generator emits (`OpenapiTs.config.ts`)

- `@hey-api/client-axios` with a custom `Generated.runtime.ts` (sets axios baseURL, remaps thrown `ZodError` → central `ApiError`).
- `@hey-api/typescript` types.
- `zod` response schemas with `dates: { offset: true }` — required so RFC3339 offset timestamps (the backend serializes `DateTimeOffset` as `…+00:00`) validate; the default bare-`Z` schema would reject every offset response.
- `@hey-api/sdk` with `validator: true` — each SDK call validates the response at the transport boundary.
- `@tanstack/react-query` with `queryOptions`, `queryKeys`, `mutationOptions`.

### Maintainer workflow when you touch an endpoint

1. Add/modify the `*Endpoint.cs` + DTOs (the **class name becomes the operationId/SDK function name** — see §1).
2. Run a Client host so the OpenAPI doc is reachable, then `pnpm run openapi` to regen `openapi/v1.json` + `src/core/api/generated/`.
3. Commit the regenerated artifacts (they are tracked; `openapi:check` enforces this).
4. For a **streaming** route or a **hub**, do *not* expect a typed SDK fn — wire it by hand on the client (see `models/pull/stream` and the chat hub).

> The exact regen recipe for a throwaway Client host: pass the connection string as a CLI arg, set `XE_NODE_SQLITE_KEY`, `ASPNETCORE_URLS=:50722`, and `OPENAPI_INSECURE=1`.

This pipeline is unchanged, but the generated SDK has been regenerated several times as new endpoint families landed (tutorial-state, inference-profile operator actions, chat file upload, running-count). The committed `openapi/v1.json` + `src/core/api/generated/` already include them; `openapi:check` still enforces that they stay in sync.

---

## Related pages

- [Architecture Overview](01-architecture-overview.md) — where the transport layer sits; the WorkerHub choke point.
- [Project Layout](02-project-layout.md) — `Client/Endpoints`, `Client/Hubs`, `Client.Application/Services`.
- [Chat](05-chat.md) — `LocalChatHub` streaming methods + Codex/cloud chat routes.
- [Scheduler](06-scheduler.md) — `scheduler/*` endpoints + `SchedulerHub` push events.
- [Model Fit](07-model-fit.md) — `model-fit/*` endpoints (GGUF discovery, advisor, llama.cpp runtime).
- [Local Runtime & Providers](03-local-runtime-and-providers.md) — `models/*` endpoints behind the runtime.
- [Agent Mode](04-agent-mode.md) — `agents/*`, `skills/*`, `mcp/*` endpoints.
- [React Client](10-react-client.md) — generated hey-api SDK consumption + hub client wrappers; Preview hub.
- [Security & Privacy](12-security-and-privacy.md) — loopback guard, Operator policy, secret redaction, hub privacy exceptions.
- [Hosting & Deployment](11-hosting-and-deployment.md) — health checks, static SPA fallback, node binding.
- [Testing & Validation](13-testing-and-validation.md) — `LocalApiSecurityTests`, `OpenApiDocumentTests`, `openapi:check`.
