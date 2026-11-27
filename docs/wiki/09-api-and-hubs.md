# API Surface & Realtime Hubs

**What this page covers.** The Node Web Server transport layer: the FastEndpoints HTTP API under `/api/local/v1`, the SignalR hubs, the cross-cutting middleware (security, exception handling, health, auth) and the OpenAPI document that generates every React REST client through hey-api. Every route is loopback-only, and a backend contract change is not finished until the generated client is regenerated and committed.

**Read this if you are** adding or changing an endpoint or hub, chasing an RFC 7807 error body, or the React client and backend disagree on a contract. **Skip to** [§5](#5-openapi--hey-api-the-single-source-of-truth-for-react-rest) for the regeneration step; **reference tables** are in [the endpoint inventory](reference/09-endpoint-inventory.md) (route families and the design notes behind them) and [the hub reference page](reference/09-signalr-hubs.md) (hub list and event names); **related pages:** [10](10-react-client.md), [12](12-security-and-privacy.md).

## Contents

- [Big picture](#big-picture)
- [1. The inbound HTTP API (FastEndpoints)](#1-the-inbound-http-api-fastendpoints)
- [2. SignalR hubs (inbound, browser ↔ node)](#2-signalr-hubs-inbound-browser--node)
- [3. Outbound traffic](#3-outbound-traffic)
- [4. Cross-cutting transport concerns](#4-cross-cutting-transport-concerns)
- [5. OpenAPI → hey-api: the single source of truth for React REST](#5-openapi--hey-api-the-single-source-of-truth-for-react-rest)
- [Related pages](#related-pages)

This page documents the **Node Web Server transport layer**: the HTTP API exposed under `/api/local/v1` (FastEndpoints), the unconditional SignalR push/stream hubs plus the conditional Development hub, the cross-cutting transport concerns (security middleware, exception handling, health checks, auth), and how the backend's OpenAPI document becomes the single source of truth for every React REST client via hey-api.

If you are adding or changing an endpoint or hub, this is the page that tells you *where the route lives, how it is secured, and what regen step you must run for the React client to see it*.

---

## Big picture

```
 Browser (React SPA, same origin)
        │  HTTP /api/local/v1/*  (FastEndpoints)
        │  SignalR hubs (all but one unconditional)
        ▼
 ┌───────────────────────────────────────────────────────────────────────┐
 │ Node Web Server  (XE-Local-AI-Engine.Client)                          │
 │  • UseExceptionHandler  → IExceptionHandler chain → RFC7807           │
 │  • UseAntiforgery / UseStaticFiles                                    │
 │  • MapHealthChecks /health/live  /health/ready                        │
 │  • LocalApiSecurityMiddleware (loopback peer + Host/Origin guard)     │
 │  • UseRouting / UseRateLimiter                                        │
 │  • UseWebSockets (before auth: hub tokens are read on upgrade only)   │
 │  • UseAuthentication / UseAuthorization (JWT bearer, Operator policy) │
 │  • UseFastEndpoints (RoutePrefix = api/local/v1)                      │
 │  • MapHub × N (see Program.cs), plus DevelopmentAttemptHub when       │
 │    Development is enabled                                             │
 │  • (non-Production) OpenAPI JSON + /scalar                            │
 │  • MapFallbackToFile("index.html")  (serves the React build)         │
 └───────────────────────────────────────────────────────────────────────┘
```

**Text fallback:** there is one transport direction — **inbound, browser → node**: the local management
API and local hubs, loopback/local-only, authenticated as the node *operator*, secret-redacted. This is
everything under `/api/local/v1`. The node holds no outbound control connection; see
[Security & Privacy](12-security-and-privacy.md) for the full invariant.

The middleware/mapping order above is authoritative — see `Program.ConfigureRequestPipeline` in `XE-Local-AI-Engine.Client/Program.cs`, which calls every step in order; the multi-line steps are extension methods in `XE-Local-AI-Engine.Client/Hosting/Pipeline/`.

---

## 1. The inbound HTTP API (FastEndpoints)

### Conventions

- **Framework:** FastEndpoints. One endpoint = one `*Endpoint.cs` class; request/response shapes live in `*EndpointDtos.cs`, FluentValidation rules in `V1/Validators/`, and entity↔DTO mapping in `Mappers/`.
- **A request-shape check belongs in the validator, not the handler.** Required, length, range, enum-membership and format checks over the request's own fields go in a `Validator<TRequest>` under `V1/Validators/`; the handler keeps only what needs a service or the database. Moving one is not free, though: the validator runs before the handler and, by default, reports every failure at once, so a handler that returned on the first failure needs `ClassLevelCascadeMode = CascadeMode.Stop` (or `.Cascade(CascadeMode.Stop)` on the rule) to keep answering with the same single error, and a handler that used the unkeyed `AddError(message)` needs the failure reported under the general-errors field rather than the property. Running earlier also decides more: a check that used to sit behind a lookup now pre-empts the 404/409/403 that lookup answered, so only a check that was already the handler's first statement moves unchanged. The rule must judge the exact string the handler goes on to use — decode, trim and case-fold all sit between what was bound and what is used — and it must not change the OpenAPI document; see the two schema bullets below.
- **Route prefix:** every endpoint is mounted under `api/local/v1` via `config.Endpoints.RoutePrefix = LocalApiRoutes.Prefix` in `XE-Local-AI-Engine.Client/Hosting/Pipeline/FastEndpointsPipelineExtensions.cs`; the value is `LocalApiRoutes.Prefix` in `Endpoints/Common/LocalApiRoutes.cs`.
- **Routes are centralized**, not string-literal'd per endpoint. Every route and hub path is a
  `public const string` on a nested route-family class of the partial `LocalApiRoutes` (for example,
  `LocalApiRoutes.Scheduler.JobById`). The shell `XE-Local-AI-Engine.Client/Endpoints/Common/LocalApiRoutes.cs`
  holds `Prefix`; each family lives beside it in its own `Endpoints/Common/LocalApiRoutes.<Family>.cs`.
  Those files are the inventory — read them rather than a count here, which drifts every time a family
  lands. Change a path there and both the endpoint and any `Send.CreatedAtAsync<TEndpoint>()` location
  resolution follow.
- **operationId = camelCased class name.** The global `NameGenerator` in `FastEndpointsPipelineExtensions.UseNodeFastEndpoints` strips the `Endpoint` suffix and lowercases the first char: `CreateScheduledJobEndpoint` → `createScheduledJob`. These operationIds are what the hey-api React SDK function names are derived from, so **the C# class name is the public contract name** — rename with care.
- **Errors → ProblemDetails.** `config.Errors.UseProblemDetails()` in `FastEndpointsPipelineExtensions.UseNodeFastEndpoints` turns validation failures into RFC7807. Domain exceptions are handled by the `IExceptionHandler` chain (see §4). Note that not every non-2xx is ProblemDetails: several families answer a **typed domain object** instead (the source-build `409` is the notable one — see §1 design notes).
- **Desktop-only endpoints.** The `config.Endpoints.Filter` configured in `FastEndpointsPipelineExtensions.UseNodeFastEndpoints` drops any endpoint implementing `IDesktopOnlyEndpoint` unless the host launched in desktop mode. The `app-update/*` family and Custom Tools executable probe use it, so those endpoints are absent from a headless OpenAPI regen.
- **A `{modelName}` route segment must be decoded before it is validated.** The React client's hey-api path serializer escapes the segment with `encodeURIComponent`, so a Hugging Face reference such as `hf.co/unsloth/gemma-4-12b-it-GGUF:UD-Q4_K_XL` reaches the server as `hf.co%2Funsloth%2Fgemma-4-12b-it-GGUF%3AUD-Q4_K_XL`. Kestrel's route binding decodes most percent-octets (`%3A` becomes `:`) but, by security design, leaves encoded slashes (`%2F`) and backslashes (`%5C`) **encoded**, to defeat path-segment smuggling. The endpoint therefore receives a value still containing a literal `%2F`, whose `%` fails `ModelNameValidator` and surfaces as "Invalid model identifier", while a plain tag like `llama3:8b` arrives fully decoded and passes. `Endpoints/LocalModels/V1/ModelRouteName.Decode` restores the canonical name before validation and before the service call; the validator's own path-traversal and scheme guards still run after it.
- **A validator condition is CHAINED, never blocked.** `RuleFor(x => x.Title).NotEmpty().MaximumLength(n).When(...)` sets the per-component condition FastEndpoints' schema processor reads, so the member stays optional in the OpenAPI document. The block form — `When(pred, () => { … })` — applies the same rules at runtime but leaves the components unconditional, and the processor then emits an OPTIONAL member as **required** on the wire, which the generated client encodes as a contract lie. The chained form loses nothing: a condition at the end of a chain covers every validator before it, because `ApplyConditionTo.AllValidators` is FluentValidation's default. This is why the PATCH validators in `Endpoints/DevelopmentWorkflows/V1/Validators/` spell it that way.
  - **Chaining is not always enough — a conditional rule on an OPTIONAL member belongs on the request object.** The processor lifts any *property* rule into the schema, so the block form published `CreateTranscriptionSessionRequest.languageOverride` as `required`, and switching to the chained `.When(...)` dropped the requiredness but still published `minLength: 2`. Either way the generated client refused every `languageMode: "auto"` create before the request left the browser — a break no backend test can see, because the server answers correctly throughout. `RuleFor(request => request)` has no property to attach to, so nothing about it reaches the schema; `CreateTranscriptionSessionRequestValidator` is the worked example.
  - **A check moved out of a handler must not arrive as a new schema constraint.** The same processor reads the moved rule, so a handler's `string.IsNullOrWhiteSpace` guard rewritten as `NotEmpty()` publishes `minLength: 1` and `required` on a member the wire contract never required, and the generated client then refuses the request before it leaves the browser. `Must(...)`/`Custom(...)` state the same condition and reach no schema at all, which is why `SaveTutorialStateRequestValidator` spells the required-key rule that way. Prove it per move: regenerate the spec from the built host and compare it to `openapi/v1.json` — `pnpm run openapi:check` regenerates from the COMMITTED spec and cannot see a backend-side change.
- **Compatibility.** `/api/local/v1` routes are additive-only within the 1.x line: new routes, new optional request fields and new response fields are allowed; removing, renaming or changing the type of a field or route is a breaking change and ships only with the next major version, announced by a deprecation note in the CHANGELOG at least one minor release ahead. The committed `XE-Local-AI-Engine.Client.React/openapi/v1.json` is the contract, and every release publishes it as `openapi-v1.json`. The SPA's generated hey-api client is the reference consumer: `scripts/openapi-live-check.sh` regenerates the spec from the built host and fails on backend drift (CI runs it), and `pnpm run openapi:check` fails when the generated client drifts from the committed spec. The additive-only rule itself is enforced by `scripts/openapi-breaking-check.py` (`pnpm run openapi:breaking`, a CI step of the `client-react` job and part of `acceptance`): it compares `openapi/v1.json` with the committed release baseline `openapi/v1.baseline.json` and fails on a removed operation, schema or schema property, a property newly required in a request body schema (per media type, also when the body now names another schema), a request body that became required, or a parameter that is new and required or became required. It does not see type, format or enum changes. Each finding prints an id; a break that is intended passes only when that id is listed with a reason in `openapi/breaking-acknowledged.json`, and an entry that matches no finding fails, so stale entries are removed. Before 1.0 that file is the escape hatch for intended removals. At each release `scripts/openapi-breaking-check.py --rebaseline` copies the current spec over the baseline and empties the acknowledgements. The SignalR wire records follow the matching rule in §2. The routes and hubs of an experimental feature (the `Training`, `ModelLab` and `ExternalApps` families of `LocalApiRoutes` and their hubs) are outside this promise ([feature tiers](../user-guide/docs/for-experienced-users.md#feature-tiers)); the External Apps operations and their tag carry `x-experimental: true` in the document (`ExternalAppsExperimentalOpenApiDocumentProcessor`). The breaking-change check does not know tiers, so a break there still needs an acknowledged id.

### Endpoint inventory (by feature)

One row per route family (`Endpoints/Common/LocalApiRoutes.<Family>.cs`), each with its routes and the subsystem page that explains them; `scripts/docs-inventory-check.py` fails when a family has no row.

See [the reference page](reference/09-endpoint-inventory.md#endpoint-inventory-by-feature) for the inventory.

> **Endpoints are orchestration-only.** They validate input, call a service in `XE-Local-AI-Engine.Client.Application/Services/*`, and map to a DTO. Business logic does not live in the endpoint. When tracing a route, jump straight to the matching service area.

### Inbound MCP scopes and registered tools

`POST mcp/server-key` accepts an optional JSON body whose `scope` is `delegate` or `agentic`; the
historical body-less form remains `delegate`. One singleton row is replaced atomically, so rotation
has no dual-valid window. Authentication emits the bounded `xe:mcp_scope` and
`xe:mcp_key_prefix` claims. SDK authorization filters apply `McpServer` to the eight shared
`NodeAgentMcpTools` and `McpAgentic` to the admin tools declared across the `NodeAdminMcpTools`
partials, so `tools/list` returns the eight shared tools for a `delegate` key and those eight plus
every admin tool for an `agentic` key. The admin set is enumerated — and drift-tested — in
[`references/mcp-tools.md`](../../skills/xe-local-ai-engine/references/mcp-tools.md); no other page
restates it. Direct calls to unauthorized admin tools are rejected too.

The admin class calls application services directly — no internal HTTP hop — for node/runtime
status, runtime acquisition, GGUF pull lifecycle, model delete/default selection, the exact 19-field
node-settings allowlist, and saved-agent CRUD. The canonical names and inputs are drift-tested
against [`references/mcp-tools.md`](../../skills/xe-local-ai-engine/references/mcp-tools.md).

The transport answers only `POST` with `Content-Type: application/json`. `McpEndpointMethodGuard`
(`Endpoints/Common/`) runs after the rate limiter and before authentication, so another verb gets 405
(`Allow: POST`) and another body type 415 instead of the JWT fallback policy's 401, which read as a
rejected key. The `McpPolicy` rate limit partitions on whether the request presents a bearer header:
120 requests per minute with one (valid or not, since the limiter runs before authentication), a
separate 20 per minute without, so unauthenticated discovery probes cannot spend the key holder's
budget. Every tool failure is a `CallToolResult` with `isError: true` and snake_case `failure_code` /
`display_message`: the single call-tool filter `McpToolCallFilter` checks arguments against each
tool's input schema before binding (`invalid_arguments`, unknown names included) and flags any
structured result carrying a top-level `failure_code`. Argument problems are tool errors, not
JSON-RPC `InvalidParams`, as the MCP spec routes input validation to `isError`.

Durable inbound runs (`start_agent_run`) are admitted against the singleton ledger in `mcp_agent_run_ledger`:
at most 32 queued or running runs, 1,000,000 request identities, 256 MiB of active encrypted payload and
128 MiB of tombstone charge (the constants on `McpAgentRunStore`). A start past any cap answers
`capacity_exceeded`. A finished run keeps its payload for 24 hours; `McpAgentRunCompactionService` then
compacts it to a payload-free tombstone that still answers a retried request id with `result_expired`. The
same sweep deletes a tombstone 30 days after compaction and releases its identity and byte charge in the same
transaction, so the caps bound a window of recent work rather than the node's lifetime. A request id older
than that window is admitted as new work.

A durable run, like a synchronous `run_agent`, carries the node's provider-call budget (the same ceilings as a chat turn): a run that exceeds them
fails `budget_exceeded`, and one whose rounds cannot fit the model window is rejected `task_too_large`. A result clipped to the configured bound ends with the
server's truncation marker, and `get_agent_run` reports `result_truncated` for it.

Outbound registrations (`mcp/servers`) answer a nullable `slug` — the segment of the server's qualified tool names (`mcp__{slug}__{tool}`), null until the first connect assigns it — so a client can map a tool name back to its server. They carry two fields beside the command/URL: `headers`, HTTP
request headers sent on every request of an HTTP server's session (for example `Authorization`),
and `sessionScope` (`Shared` or `PerConversation`, see [Agent Mode](04-agent-mode.md)). Responses
return header and environment NAMES with a fixed mask in place of every value, and the URL with its
userinfo and every query value masked (`?token=***`); an update that sends a masked value or the
masked URL back keeps the stored one. Validation answers 400 for headers on a stdio server,
environment variables on an HTTP server, a URL carrying userinfo, a header name that is not an HTTP
token or is `Host`/`Content-Length`, and a header value over 4,096 UTF-8 bytes or containing a control character other than tab. An existing HTTP
registration that still carries environment values therefore has to drop them before it can be
saved again.

### Design notes on the newer endpoint families

The newer families (inference profiles, runtime acquisition, custom tools, uploads, training, document ingestion and others) each carry a design note on their status codes, admission and ownership.

See [the reference page](reference/09-endpoint-inventory.md#design-notes-on-the-newer-endpoint-families) for the notes.

### Notable non-typed routes

**No endpoint on this surface authors SSE.** The former `models/pull` / `models/pull/stream` pair (`PullStreamLocalModelEndpoint`) is gone — model acquisition is now GGUF download under `model-fit/*` with progress pushed over `GgufDownloadHub`, and chat streaming is a SignalR streaming hub method (§2). A grep for `text/event-stream` under `XE-Local-AI-Engine.Client/` returns nothing. The one place a stream still crosses this boundary is the hand-mapped model proxy: `LocalModelProxyForwarder` reverse-proxies the caller's request (with the two body edits listed above) to the resolved `llama-server` child and relays that child's response body (SSE included, with its upstream content type) back, bounded by an upstream-idle timeout — it neither parses nor reorders the event stream; it authors exactly one frame, the surface's terminal error event when the child dies mid-stream.

**`/llm/v1/*` is not on this surface at all.** `/llm/v1/chat/completions`, `/llm/v1/embeddings` and `/llm/v1/models` are served only on the **container bridge listener** — the engine's one deliberately non-loopback Kestrel endpoint, mapped by `ContainerBridgePipeline.Map` before everything else in the pipeline and branched on the arrival port. They are absent from the loopback listener, absent from the OpenAPI document and absent from the generated client, because their only caller is an application container calling in with a per-instance bearer token. They reuse `LocalModelProxyForwarder`, so this is the same forwarding behaviour §`proxy/v1/*` describes, reached from a different listener under a different gate; the bridge maps only those three, not the Responses or Messages routes. See [ADR 0011](../adr/0011-container-bridge-listener.md) and [Security §3.5](12-security-and-privacy.md).

**The three hand-mapped inbound surfaces all sit INSIDE `/api/local/v1`, and that is load-bearing.** `MapMcp`, the OpenAI-compatible model proxy and the external integration API are mapped beside the hubs rather than as FastEndpoints endpoints, because each owns a transport or an envelope that is not a node DTO — JSON-RPC, arbitrary OpenAI JSON plus SSE, an integration envelope — so none of them may appear in the OpenAPI document or the generated React SDK; only external clients talk to them. But `LocalApiSecurityMiddleware` matches on the `/api/local/v1` prefix ALONE, so mapping any of them at a bare path (`/mcp`, say) would silently drop the loopback peer, Host and Origin gate and leave the bearer key as the only control. Do not move them out of the prefix. Each carries a policy that accepts ONLY its own key scheme, never the operator's JWT and never another surface's key, and the integration routes' route-level rate limit is the COARSE per-IP abuse ceiling — per-principal fairness is the limiter the handler consults after authentication, the only place a principal exists to partition on. The integration body cap is read from configuration at map time, because endpoint metadata is baked then and an `IOptions` instance resolved per request would be a second source that can disagree with what the route already carries.

A small set of routes is still **hand-wired on the React client rather than consumed through the generated typed SDK**, because the response is not request/response JSON or the call must run outside the SDK's auth path:

- `images/{imageId}` — fetched as a `Blob` to build an object URL (`features/images/hooks/useImageObjectUrl.ts`).
- `knowledge-base/documents` (POST) — multipart upload with progress (`features/knowledge/queries/useKnowledgeUpload.ts`).
- `auth/status|setup|login|refresh|logout|vault/*` — the **one documented second axios instance** (`authClient` in `core/auth/api/NodeAuthApi.ts`). It exists precisely so the shared instance's 401-refresh interceptor cannot recurse through the refresh call itself.

Everything except `authClient` goes through the shared axios instance via `buildLocalApiUrl()`. See [React Client](10-react-client.md).

---

## 2. SignalR hubs (inbound, browser ↔ node)

Hub classes live in `Client/Hubs/` and every one of them is mapped by a `MapHub<>` call in `Program.cs` — that call site, not a number here, is the inventory. All are `[Authorize(AuthenticationSchemes = JwtBearer, Policy = NodeAuthorizationPolicies.Operator)]` and mapped with `.RequireAuthorization(Operator)`. Their full paths are constants (`...Hub` on the `LocalApiRoutes` families), mapped via `MapHub` *outside* the FastEndpoints prefix.

**Adding a hub is two edits, not one.** Besides the `MapHub` call, the hub's path goes into `XE-Local-AI-Engine.Client.React/config/signalr-proxy-paths.json`, which `vite.config.ts` reads to decide which paths get the WebSocket-capable dev proxy. A hub missing from that file has no dev transport, and because the client shares one connection manager the failure is not confined to that hub: every hub in the dev app stops delivering (this has shipped once). Two guards read the real `MapHub` calls and fail on a mapped path missing from the file and on a proxied path no longer mapped: `SignalRProxyPathDriftTests` in the backend suite, and `pnpm run signalr:check` (`scripts/CheckSignalrProxySync.mjs`, which also checks the hub payload enums against their client member lists) in the frontend `validate`. Neither is the check `scripts/docs-inventory-check.py` runs: that one only requires every mapped hub to be named in the hub table of [the hub reference page](reference/09-signalr-hubs.md#hub-inventory).

That hub table (path, direction, purpose and owner page of every hub) is this wiki's single enumeration of the hubs; every other page links to it instead of repeating it. Every hub except one is mapped unconditionally; that one, `DevelopmentAttemptHub`, is mapped **only when Development Mode is enabled** — `isDevelopmentModeEnabled` in `Program.cs` reads `Development:Enabled` and defaults to `true` (the same default as `DevelopmentOptions.Enabled`). When it is `false` the hub is not mapped, its services are not registered, and the disabled-capability middleware in `Program.cs` answers **404** for every `/api/local/v1/development/*` path except `development/capability` — deliberately *before* local-API security or authentication can challenge the caller, so a disabled capability stays opaque.

Work sessions take the **other half** of that pattern deliberately: `WorkSessionHub` and every `work-sessions/*` route are mapped and discovered unconditionally, and only their behaviour is gated. Dropping them from discovery would drop the whole family out of the OpenAPI document and therefore out of the generated React SDK, which would make the client's surface depend on the node that produced the spec. `WorkSessions:Enabled=false` (the option default; `appsettings.json` ships `true`) instead gets the same 404 middleware ahead of authentication, and `SubscribeSession` throws a `HubException`. As with Development Mode, exactly one route is carved out of that sweep — `work-sessions/capability`: the 404 those blocks write has no body at all, so without a route that still answers, a client cannot tell a switched-off feature from a broken node, and both SPA pages showed a generic "could not load" on every node that had the switch off.

**Compatibility.** The hub paths and event-name constants (both in [the hub reference page](reference/09-signalr-hubs.md)) and the wire records in `Client/Hubs/*HubContracts.cs` follow the same additive-only rule as the HTTP API (§1 "Conventions"): within 1.x a wire record may gain members; removing, renaming or retyping a member, or renaming an event or hub method, is a breaking change and ships only with the next major version, announced by a CHANGELOG deprecation note at least one minor release ahead. `ProviderStatusHubWireTests` and `ApplicationHubWireTests` pin the provider-status and application publisher frames; the other hubs' wire records are pinned by their own feature tests, not frame for frame.

### Hub event-name contracts

Push hubs name their client methods with stable string constants (`*HubEvents` in `Client/Hubs/*HubContracts.cs`), which double as the wire event-type discriminator; a provider or Application service publishes a neutral domain type and the `Client/Hubs` publisher owns the method name and the wire shape.

See [the reference page](reference/09-signalr-hubs.md#hub-event-name-contracts) for every hub's event names and payloads.

### Hub payloads use the app's JSON policy

`AddSignalR(...).AddJsonProtocol(options => ConfigureJsonSerializerOptions(options.PayloadSerializerOptions))` in `ConfigureServices` hands the hub protocol the **same** `JsonSerializerOptions` configurer the REST surface gets (§5) — camelCase property names, the two custom converters, and `JsonStringEnumConverter`, so a CLR enum member goes out as its **declared name** (`"Indexed"`, `"Succeeded"`, `"TerminalSnapshotAvailable"`), matching the string union the OpenAPI-generated type carries for the same field. Without that call SignalR uses its own defaults, which agree on camelCase but have no enum converter, so every enum member left as a **raw integer** while the generated TS type said string: five browser handlers compared a number against a name and did nothing — silently, with no error, log line or failing test, because each one's own test fed it a string. The wire form is now identical on both transports; do not configure a second options instance for hubs.

This does **not** change the hand-written projections the [event-name contracts](reference/09-signalr-hubs.md#hub-event-name-contracts) describe. `ToWireKind`/`ToWireChannel`/`ToWireStatus` and the `Phase` members of the source-build, acquisition and image-job payloads are already `string` on the CLR side, deliberately, and the enum converter never sees them — their documented lowercase / lowerCamelCase / PascalCase spellings are unaffected. Inbound (client→server) arguments are scalars everywhere, including `TranscriptionHub.PushAudioFrame`'s `int channel`, so nothing the browser sends changes meaning either.

### Publisher pattern (default no-op + host swap)

Scheduler and Graph Workflow broadcasts go through a publisher interface whose default implementation is a **no-op** (`NoOpGraphWorkflowEventPublisher`, registered with `TryAddSingleton`); the Client host swaps in the hub-backed publisher, so a host without the hub stays resolvable. This keeps `Client.Application` services free of a SignalR dependency — they call `IGraphWorkflowEventPublisher`/`ISchedulerEventPublisher`, and the transport is injected at the host. The concrete `GraphWorkflowEventPublisher`/`SchedulerEventPublisher` (`Client/Hubs/*EventPublisher.cs`) wrap `IHubContext<T>`. Graph Workflows go one step further: the publisher is invoked from a **store decorator** (`PublishingGraphWorkflowStore`), so exactly one ping is emitted per committed mutation with the sequence that commit allocated, and a publisher failure is logged rather than failing a write that already landed.

### Per-run scoping (Graph Workflows)

`GraphWorkflowHubGroups.Run(runId)` → group name `graph-workflow-run-{runId:N}`. A **single hub connection can drive several runs**, so every payload carries a mandatory `RunId` — that is the real cross-contamination guard, with the group only scoping delivery. A **disconnect cancels nothing**: a workflow run is durable and outlives both the browser tab and the engine, which is the property the module exists to prove.

> There is no privacy exception to declare here. The graph-workflow payload is content-free — `(RunId, Seq, Kind)` and nothing else — so a subscriber re-reads the named feed from its own watermark and no run output ever crosses the hub. See [Security & Privacy](12-security-and-privacy.md).

### Chat hub: attachment and refusals

`HubException` is the **only** exception type whose *message* SignalR forwards to the client without detailed errors enabled; any other type reaches the browser as the bare "An unexpected error occurred invoking 'SendMessage' on the server." SignalR still PREPENDS that generic sentence plus `HubException:` to the forwarded text, which is why the chat page's `errorMessage` helper strips the wrapper so the operator reads the real sentence first.

`LocalChatHub` throws it in two places. `EnsureMessageWithinSizeCap` is the message-size cap's only enforcement point for a local send, and it sits in the hub rather than deeper because `NodeChatStreamService.SendMessageCoreAsync` persists the user turn before anything downstream can inspect its content — a check any later leaves the oversized row in the conversation for every subsequent turn to trip over. `TranslateDomainRejections` re-throws the stream services' typed caller-triggerable rejections: a send or regenerate against an `Origin=Remote` (view-only) conversation, a normal send into a conversation whose bound graph-workflow run is still live, a deleted conversation or message, a correlation already being generated, or a send whose attachment ids are not uploads of that conversation (`NodeChatInvalidRequestException`, naming the ids, before the user turn is persisted). The conversion is deliberately narrow, matched by exception TYPE and never by inspecting a message string, because widening it to every fault would forward internal detail to the browser — which is what SignalR's generic message exists to prevent. Only the read-only and live-workflow rejections are PREFIXED, with the `NodeConflictProblemType.ReadOnlyConversation` and `GraphWorkflowRunLiveInConversation` names: the exact discriminator tokens the REST 409 carries as `ConflictProblemDetails.conflictType`, so the SPA's `isNodeChatReadOnlyConflict` recognises both shapes off one constant, and a live-workflow refusal reads the same way on either path. It is referenced via the enum, never a literal, so a rename cannot silently desynchronise the two paths. The others carry no token — no caller discriminates on them, and an unstripped token would be the first thing the operator reads in the toast.

`TranslateDomainRejections` is composed into `TrackAttachment` rather than applied at the individual call sites, so all four stream entry points are covered by one seam; it is a no-op for the resume paths, which never run the mutation guard. The rejections are thrown LAZILY during enumeration (they live inside the services' async iterators), so the conversion has to happen around `MoveNextAsync`; C# forbids `yield return` inside a `try` that has a `catch`, hence the manual enumeration with the yield outside the try.

`TrackAttachment` marks the invocation as WATCHED for as long as its stream is being consumed, so `DetachedInvocationReaper` can end a run whose client went away and never came back. The hub is the only attach site because all four stream entry points return from that one file. The invocation id is latched off the FIRST event's `ChatStreamEvent.RequestId` rather than taken from the arguments — `SendMessage` and `ResumeMessage` know it up front, but `RegenerateMessage` and `ResumeConversation` mint it server-side, and latching uniformly means an entry point added later is covered without touching the method. The release is driven by the cancellation token SignalR cancels when the client unsubscribes or disconnects, NOT by the source enumerable completing: `NodeChatStreamService.SendMessageCoreAsync` awaits its pump and runner tasks in the `finally` after its SSE loop exits, so the enumerable does not return until the entire run is over, and releasing only from the method's own `finally` records the detach *after* the run has already terminalized — leaving the reaper nothing to reap and the grace silently dead. The `finally` stays as the normal-completion path; the handle is idempotent.

### Hub authentication: the bearer subprotocol

Browsers cannot set an `Authorization` header on a WebSocket handshake, and a token in the socket URL is printed by every failed socket line, so the SPA offers it as a subprotocol instead: `Sec-WebSocket-Protocol: xe.bearer, <jwt>`. JWT bearer is configured in `ConfigureServices.AddServices` with `Security/NodeJwtBearerEvents` (via `JwtBearerOptions.EventsType`), whose `MessageReceived` override reads that header **only on a genuine WebSocket upgrade of a hub transport endpoint** (`HubMetadata` without `NegotiateMetadata`, and `HttpContext.WebSockets.IsWebSocketRequest`), **only as the raw, unquoted two-element list** `[xe.bearer, <jwt>]`, and **only when the request carries no `Authorization: Bearer` header** (a valid or invalid `Bearer` header always wins; any other scheme, such as a proxy's `Basic`, does not block the socket). Negotiate, a long poll, a send, a DELETE and every REST route carrying only the subprotocol get 401. The `access_token` query parameter is read nowhere. `UseWebSockets()` runs immediately before `UseAuthentication()` in `Program.cs` so `IsWebSocketRequest` is known to the JWT events; `MapConnections`' own `UseWebSockets` then finds the feature and adds nothing. Every `MapHub` call passes `Program.ConfigureHubTransport`, whose `SubProtocolSelector` echoes `xe.bearer` (a browser fails a socket whose offered protocol is not echoed) and selects nothing for a client that offered none. Long polling keeps `Authorization: Bearer` on every request; the SPA does not use server-sent events, which can carry a token only in the URL. Header clients (the CLI, `scripts/gpu-smoke-driver.py`) are unchanged. The client side is `HubBearerTransport.ts` ([React Client](10-react-client.md)).

The handshake is the only point the JWT events see a WebSocket hub token, so sign-out is enforced by the global hub filter `Security/NodeSignOutHubFilter` (registered in `AddSignalR`): it registers each connection in `NodeHubConnectionRegistry`, which `NodeAuthService` uses to abort the connections a logout, session revoke, sign-out-everywhere, password change or reset has just ended, and it re-runs the stamp and session check on every invocation, refusing with a `HubException` and aborting the connection when the sign-in is gone. Expiry is deliberately not enforced on an open connection (no `CloseOnAuthenticationExpiration`): a chat stream outlives its access token. On the client, `markHubConnectionLost` makes the next connect after a drop refresh first, so a dead cookie answers 401 and the SPA lands on sign-in instead of reconnecting with a revoked token ([Admin API surface §3.2](reference/12-admin-api-surface.md)).

---

## 3. Outbound traffic

The node holds **no outbound control connection**. An earlier design paired it with a central platform
over a single outbound `WorkerHub` SignalR connection (device binding, token refresh, encrypted
invocation envelopes, a file-backed dead-letter queue); none of it was reachable in a shipped build and
it has been removed, along with the `binding/*` and `connection/*` route families.

What still leaves the machine does so only because an operator configured a feature to fetch it: model
downloads, a declared cloud or OpenAI-compatible provider, a registered MCP server. Each is documented on
its own subsystem page.

---

## 4. Cross-cutting transport concerns

### Exception handling → RFC7807

Registered **before** `UseFastEndpoints` so it wraps endpoints (see the `UseExceptionHandler` / `UseFastEndpoints` order in `Program.cs`). A chain of `IExceptionHandler` implementations is registered in order by `ConfigureServices.AddServices()` plus `AddProblemDetails()`; the registration block itself is the inventory, and `ExceptionHandlerRegistrationOrderTests` pins the order. The shape of the chain:

1. **Shared wire contracts** — `ConflictExceptionHandler` (typed domain conflict → the one 409 `ConflictProblemDetails` envelope) and `DomainValidationExceptionHandler` (single-message validation → the FastEndpoints 400 body).
2. **Per-family handlers** — one per area that owns its own status split or response DTO: Development conflicts and not-found, the selected-folder family (404 / 409 / 400 by type), Training, Benchmark, the GGUF import and download mappers, WorkSession / GraphWorkflow not-found.
3. **Host refusals** — `RequestBodyTooLargeExceptionHandler`, which answers Kestrel's own body-cap rejection (raised inside model binding, before any endpoint runs).
4. `DefaultExceptionHandler` — catch-all 500; redacts internal detail outside development (takes `IHostEnvironment`).

This mirrors the central platform's handler-chain pattern: specific handlers first, generic catch-all last. Internal error detail is never leaked to the browser. An endpoint-local `catch` that re-states a mapping the chain already owns is the anti-pattern `EndpointExceptionMappingSourceGuardTests` guards against — a type belongs in the chain when its status is the same at every endpoint that raises it, and stays local when it is not.

### Status choices worth their reasons

A handful of handlers in the chain answer a status whose reason is not obvious from the type they catch.

- **Once the host starts stopping, every new request answers 503 with `Connection: close`.** A SignalR hub reconnect that arrives during shutdown therefore cannot hold the stop for the 30 s host timeout.

- **No usable container runtime → 503, not 409.** `ContainerRuntimeUnavailableExceptionHandler` maps the routes that reach `IContainerRuntimeResolver.CreateRuntimeAsync` inside the request rather than inside a background operation; without it, reading a log tail on a node whose Docker daemon is down is a 500, which tells the operator the engine broke rather than that the machine has an obstacle to clear. It is not a state transition the operator can decline and not a conflict with anything the node holds: the request is well-formed and will succeed once the daemon is back, which is what "the service is temporarily unable" means. [ADR 0009](../adr/0009-conflict-envelope-versus-operational-block.md)'s Blocked-family carve-out does not apply, because those exist for refusals the service RETURNS as a value, whereas this one can only be thrown. The detail is the resolution's own operator message — the same prose the runtime card renders, already redacted to `DockerDaemonEndpoint.Display` — and never a daemon exception message, which can carry the environment it failed on. The lifecycle verbs are unaffected: they admit with a 202 and settle the same unavailability into the row's `RuntimeUnavailable` failure category.
- **A capped route has two refusal paths and one 413 answer.** A route carrying `IRequestSizeLimitMetadata` has two of them and only one used to be answered. The endpoint's own early exit reads `Content-Length` and sends the declared 413, but Kestrel enforces the cap as it READS, inside model binding and before any handler code runs; its refusal is a `BadHttpRequestException` carrying status 413, which nothing mapped, so `DefaultExceptionHandler` turned the refusal a real connection actually takes into a 500. `RequestBodyTooLargeExceptionHandler` answers it, and it is transport-level rather than per-family — one handler instead of one per capped route, so every route that declares a body cap reaches the same throw and the same answer. The two paths used to write two different shapes for that one status (ASP.NET `ProblemDetails` from the handler, FastEndpoints' `errors[]` from the endpoint) while both DECLARE `ProducesProblem(413)`, which made the second a contract lie the generated client encodes. Both now go through `RequestBodyTooLargeProblem.WriteAsync`, the single writer: the handler awaits it and an endpoint sends `RequestBodyTooLargeProblem.Result` through `Send.ResultAsync`. Only `Detail` differs between them, because the host knows nothing but "too large" while an endpoint can name the cap it enforces. The four multipart upload routes (chat attachments, knowledge-base documents, images, transcription audio) answer this 413 for a file past `Security:MaxUploadFileSizeMb` through `MultipartUploadLimits`. The two auto-bound ones (chat attachments, knowledge base) need an endpoint filter that reads `Content-Length` before form binding: the form reader enforces the multipart limit while it binds, and FastEndpoints reported that as "The request body contains malformed form data!". An upload sent without `Content-Length` (chunked) has no declared length for the filter to read, so it still trips the form reader and gets that 400; browsers and the SPA always send the length.
- **The Development family's 409s keep the FastEndpoints body.** `DevelopmentConflictExceptionHandler` writes `AddError(message) + Send.ErrorsAsync(409)`'s body through `FastEndpointsProblemWriter` — a relocation of what every endpoint used to write by hand, not a reshape. It deliberately does NOT route through `ConflictExceptionHandler`, whose `ConflictProblemDetails` envelope is a different body: the message moves from the `errors` array to `detail`, `title` becomes "Conflict", and a `conflictType` discriminator appears. Folding Development into that envelope is worth doing, but it is an OpenAPI-visible contract change needing its own `NodeConflictProblemType` members and a regenerated client. `DevelopmentWorkspaceSecurityException` is absent on purpose: the patch and next-action endpoints answer it 409 while register/create/reconnect answer it 400, so no single status is correct for it — and its derived `DevelopmentRepositoryStateConflictException` is absent for the same reason, since the reconnect endpoint still catches the base type locally and would swallow the derived one before a global handler saw it.
- **Two `type` URIs coexist, deliberately.** `ConflictExceptionHandler` sets `https://tools.ietf.org/html/rfc7231#section-6.5.8`, which is also what ASP.NET Core's own `ProblemDetailsDefaults` emits and what the API already serves unmodified wherever `Results.Problem` builds the body (`BenchmarkEndpointSupport.Problem`); `DevelopmentConflictExceptionHandler` reaches FastEndpoints' `www.rfc-editor.org/rfc/` base by setting no `Type` at all. Aligning the first line would move it AWAY from the framework's, so one canonical `type` across the API is unreachable. `DevelopmentExceptionHandlerTests` pins the divergence and [ADR 0009](../adr/0009-conflict-envelope-versus-operational-block.md) records why it stands.

### The three error bodies and their declarations

This API writes **three** distinct error shapes, and each has its own `Produces*` declaration. Declaring the wrong one is a contract lie the generated hey-api client encodes, and nothing at runtime corrects it. `Endpoints/Common/ProblemDetailsProducesExtensions.cs` holds the one custom declaration.

| Body | Written by | Declare with | Schema |
|---|---|---|---|
| FastEndpoints' own error body | `AddError` + `Send.ErrorsAsync`, and the global `DomainValidationExceptionHandler`, which mimics it | FastEndpoints' `ProducesProblemDetails(status)` | `additionalProperties: false` with an `errors[]` member |
| The `ConflictProblemDetails` envelope | `ConflictExceptionHandler` | `ProducesConflictProblemDetails()` (`ProblemDetailsProducesExtensions`) | carries a `conflictType` discriminator and cap members the FastEndpoints schema does not know |
| Plain ASP.NET `ProblemDetails` | `Results.Problem(...)`, including `ConflictProblemSenderExtensions.ConflictProblemAsync` for a service that reports a conflict as a *return value* rather than an exception, so `ConflictExceptionHandler` never sees it | ASP.NET's own `ProducesProblem(status)` | permits additional properties, so extension members such as `code` / `outcome` / `traceId` are legal |

FastEndpoints' `ProducesProblemFE` advertises its `ErrorResponse`, which this host never writes because `Program.cs` configures `Errors.UseProblemDetails`; `EndpointErrorBodyDeclarationTests` bans it. `Send.NotFoundAsync` writes no body, so its 404 is declared with a plain `Produces(404)`.

### Health checks

Two endpoints, each mapped with an explicit `.AllowAnonymous()` so an orchestrator or probe can reach them without a token (see `HealthEndpointExtensions.MapNodeHealthChecks`). That call is load-bearing rather than decorative, and it is the *only* thing making these anonymous: what decides whether a request is challenged is the authorization metadata attached to the selected endpoint, never where the `Map*` call sits in the file. Until the deny-by-default pass these two were reachable purely by omission, because no `FallbackPolicy` existed; under the fallback policy an endpoint with no auth metadata is challenged, and Aspire's `WithHttpHealthCheck` poll carries no token. See "Security middleware & auth ordering" below.

| Endpoint | Behavior |
|---|---|
| `/health/live` | `Predicate = _ => false` — runs no checks; returns 200 if the process can serve requests (liveness). |
| `/health/ready` | runs only checks tagged `ready`; custom JSON writer emits `{status, checks:[{name,status,duration}]}` (readiness). |

### Security middleware & auth ordering

- `LocalApiSecurityMiddleware` (`Endpoints/Common/LocalApiSecurityMiddleware.cs`) runs before routing/auth. For any path under `/api/local/v1` it returns **403** unless the transport **peer** is loopback (`context.Connection.RemoteIpAddress` — the socket peer, so a routable caller is rejected even if it forges a loopback `Host`/`Origin`; a null peer, e.g. the in-process test host or an in-process health probe, is treated as loopback-equivalent) *and* the `Host` is in `{localhost, 127.0.0.1, ::1}` *and* the `Origin` (when present) is same-scheme/same-host/same-port and loopback. This enforces loopback-only access at the edge. See [Security & Privacy](12-security-and-privacy.md) §3.1.
  - **Why the socket peer is trusted as the client.** A reverse proxy running on the *same* host would appear as a loopback peer and defeat this check, since every forwarded request would arrive from `127.0.0.1`. That is by design and acceptable because a proxied or headless deployment is **unsupported**: the app binds loopback-only (`LoopbackBindGuard` shuts the process down on a routable bind, next bullet), and no forwarded-headers middleware is registered, so `X-Forwarded-For` is never honoured and the socket peer is always the real client on every supported (same-machine, single-user) launch. Deployments that put this surface behind a proxy, or expose it beyond the local machine, are out of scope for this guard.
- **Startup bind guard.** `LoopbackBindGuard` (`Hosting/LoopbackBindGuard.cs`, wired via `LoopbackBindGuard.Guard(app)`) is defense-in-depth behind the request-time middleware: after `ApplicationStarted` — so an OS-assigned port and wildcard expansion are already resolved — it inspects the addresses Kestrel *actually* bound and, if any is non-loopback (wildcards `*`/`+`/`0.0.0.0`/`::` count), logs a **critical** line naming the offending address and shuts the app down with exit code 1. The opt-out `Security:AllowNonLoopbackBind=true` defaults to `false` and no supported launch sets it. One bind is exempt **by name rather than by flag**: the container bridge deliberately listens on a LAN-facing address, and its resolved listener URL is passed in as the expected non-loopback bind. Naming exactly that address keeps the guard failing closed on every OTHER routable bind, which the flag cannot do — the flag would also silence the guard for `/api/local/v1` going routable by operator error, which is the thing it exists to catch. An expected bind is compared by scheme, host and port rather than as raw text, because Kestrel reports back what it actually bound and that string need not be character-identical to the one it was given; a wildcard is never "expected", since it is not one address and allow-listing it would exempt every interface the app bound rather than the single one the caller named (both the Kestrel spellings `*`/`+`, which parse to an empty host, and `0.0.0.0`/`[::]`, which parse to a real one). Desktop mode always binds `127.0.0.1` and Aspire dev binds `localhost` — external exposure is handled by the DCP proxy, not the app process — so the guard is a no-op on every supported launch. The shutdown sets `Environment.ExitCode = 1` **before** `StopApplication()`, so the value is in place before the host tears down and `Program`'s final return reads it; a supervisor or CI then treats the guarded shutdown as an error rather than a clean stop. See [Security & Privacy](12-security-and-privacy.md) §3.4.
- Then `UseRouting → UseRateLimiter → UseMcpEndpointMethodGuard → UseWebSockets → UseAuthentication → UseAuthorization` in `Program.cs` (the guard touches only the inbound MCP path; see §1). Authn is JWT bearer; the `Operator` authorization policy gates both endpoints and hubs. Tests in `XE-Local-AI-Engine.Tests/ApiFoundation/LocalApiSecurityTests.cs` assert: missing/invalid token → 401, unsafe host → 400, unsafe origin → 403, valid token + same-origin → allowed.
- **Deny by default, in two layers.** The FastEndpoints global configurator (`config.Endpoints.Configurator` in `FastEndpointsPipelineExtensions.UseNodeFastEndpoints`) forces `Policies(NodeAuthorizationPolicies.Operator)` onto every discovered endpoint, so an endpoint whose own `Configure()` forgets the call still ships protected. An endpoint that deliberately opted out with `AllowAnonymous()` still wins, because FastEndpoints emits no conflicting authorize metadata for a verb its anonymous verbs cover; the four pre-authentication endpoints under `Endpoints/Auth/V1/` (`NodeAuthStatusEndpoint`, `NodeSetupEndpoint`, `NodeLoginEndpoint`, `NodeRefreshEndpoint`) are the whole anonymous set. Behind that, `AuthorizationOptions.FallbackPolicy` (`ConfigureServices.cs`) requires JWT bearer plus an authenticated user for every routed surface carrying no auth metadata of its own: the hand-mapped minimal APIs, the hubs, the health probes, the SPA shell. It is deliberately weaker than `Operator`, asking for a valid token rather than the Admin role, so it is the second layer and not a replacement for the first. `EndpointAuthorizationPolicyTests` is what proves the first layer stays wired; see [Testing & Validation](13-testing-and-validation.md). The Configurator is applied **unconditionally** rather than "only when `AnonymousVerbs` is empty", because the order between it and an endpoint's own `Configure()` is unspecified and the unconditional call needs no such guarantee. `ConfiguratorCanaryProbeEndpoint` exists solely to make the deletion of that line fail the test.
  - **One documented side effect.** Because the Configurator sets `PreBuiltUserPolicies` on EVERY endpoint, FastEndpoints' Swagger generator treats every endpoint as `RequiresAuthorization()` and adds an auto-403 — including to the four anonymous auth operations (login, setup, status, refresh), whose auto-401 the same generator does suppress off `AnonymousVerbs`. The asymmetry is the library's. The 403 is kept because it is TRUE: `LocalApiSecurityMiddleware` answers a real 403 on every `/api/local/v1/*` route, anonymous ones included, for a foreign Origin/Host or a non-loopback peer, so an anonymous login request can and does get one. Suppressing it would mean unsetting the very policy the canary endpoint pins, trading a truthful declaration for a deny-by-default hole; `OpenApiDocumentTests` asserts the 403 is on every operation.
- **A path the node does not serve now answers 401, not 404.** The fallback policy is also evaluated when routing selects no endpoint at all, so an anonymous request to a missing file-like asset, or to a route registered only in desktop mode, fails closed instead of reporting 404 or 405. That is intended: an unauthenticated caller learns nothing about which paths and verbs exist. A caller holding an operator token still gets the ordinary 404 or 405.
- Request logging redacts the `access_token` (sent only by old clients now), `id` (the hub connection token) and `query` (search text) query params via `SensitiveQueryRedactor` before anything is written (the `UseSerilogRequestLogging` request-path projection in `Program.cs`).

Full rationale: [Security & Privacy](12-security-and-privacy.md).

### Dev-only surfaces

Not mapped in Production: OpenAPI JSON at `/openapi/local/v1/{documentName}.json` and the Scalar API reference at `/scalar`.

`UseSwaggerGen`, which serves the OpenAPI JSON, runs **before** `app.UseAuthentication()`. It is raw middleware rather than an endpoint-convention builder, so it registers no endpoint for `.AllowAnonymous()` to attach to, and the fallback policy would otherwise challenge it. Scalar stays where it is: `MapScalarApiReference` is a `Map*` call already chained with `.AllowAnonymous()`, and a `Map*` call's position in the file never decides which middleware wraps it.

Development Mode's endpoints are kept out of **discovery**, not merely out of routing, when the feature is off (`config.Endpoints.Filter` in `ConfigureServices`). The routing filter in `UseFastEndpoints` is too late: FastEndpoints instantiates every discovered endpoint at startup before evaluating that filter, and `AddNodeDevelopment` registers the services those endpoints take through their constructors only when the feature is on, so a discovered-but-unrouted Development endpoint would fail the node's boot. `GetDevelopmentCapabilityEndpoint` deliberately does not carry the marker, so it stays discoverable and answers the disabled state; its one dependency, `DevelopmentCapabilityService`, is registered before `AddNodeDevelopment`'s early return. The 404 the other routes give while the feature is off still comes from the request-path middleware in `Program.cs`, which runs before authentication and is therefore unaffected by whether the route exists.

The diagnostics canary probe (`diagnostics/configurator-canary-probe`) is registered like any other endpoint but excluded from the OpenAPI description, so it never reaches the generated client. It exists only so the global configurator's deletion fails a named test; it answers 204 to an operator and 401 to anyone else.

### Static SPA fallback

`UseStaticFiles()` + `MapFallbackToFile("index.html")` serve the built React app from the same origin as the API (the "C0re static-files pattern"). See [Hosting & Deployment](11-hosting-and-deployment.md).

`MapFallbackToFile` carries an explicit `.AllowAnonymous()`: the login page has to load before anyone can hold a token, and the fallback policy would otherwise challenge the SPA shell. Static assets are served by the `UseStaticFiles` middleware ahead of routing and are unaffected either way. A file-like path that no asset matches is a different story. The fallback route's `{*path:nonfile}` constraint keeps dotted paths out of the SPA shell, so such a request matches nothing and an anonymous caller sees 401. Under `/assets`, where the bundle lives, the middleware after `UseRouting` answers an unmatched request 404 instead, so a missing bundle file reads as missing; a route mapped under `/assets` keeps its endpoint and is unaffected.

The shell is also kept off the API. An extensionless path satisfies `{*path:nonfile}`, so a mistyped or removed `/api/local/v1/...` route used to be answered with a 200 `text/html` shell — success, to a JSON client, for a route that does not exist. A small middleware immediately after `UseRouting` fixes that: the SPA fallback is mapped with a marker (`SpaFallbackMarker`), and when that endpoint is selected for a path under `LocalApiRoutes.Prefix` the middleware calls `SetEndpoint(null)`. The request then takes exactly the route an unmatched *dotted* path already takes — anonymous 401 from the fallback policy, operator token a bare 404 — and only the SPA fallback is detached, so real endpoints under the prefix (the hubs and their `negotiate`, `MapMcp`, the OpenAI-compatible proxy, the integration API) are untouched.

Detaching rather than mapping a second catch-all over the prefix is deliberate. `MapFallbackToFile` registers a **GET/HEAD-only** endpoint, which is why routing's own 405 still answers a wrong-verb request on a real API route; a routed all-verb catch-all under `/api/local/v1` would join the candidate set of every route below it and replace that 405 with 404 across the whole API. `RouteCoexistenceTests` pins both halves.

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

- **Doc source:** NSwag-generated document served at `/openapi/local/v1/{documentName}.json` (the non-Production `UseSwaggerGen` branch in `DevelopmentApiDocsExtensions.UseDevOpenApiDocument`). `XE-Local-AI-Engine.Tests/ApiFoundation/OpenApiDocumentTests.cs` guards the document.
- **Fetch:** `pnpm run openapi:fetch` → `scripts/FetchOpenapi.mjs` pulls `OPENAPI_SPEC_URL` (default `https://localhost:50722/openapi/local/v1/v1.json`) into `openapi/v1.json`. HTTPS uses Node's normal certificate validation. When Node does not already trust the local .NET development certificate, export its public certificate as PEM and set `NODE_EXTRA_CA_CERTS` before starting Node; never disable TLS verification. The sanctioned desktop live contract script uses an isolated random loopback HTTP endpoint instead.
- **Normalize:** the fetch does not commit the document verbatim. `normalizeOpenapiDocument` in the same script is the single place the committed file is materialized, and the live check compares through it too, so the fetch and the check cannot disagree. It (a) drops `format: int64` so the zod plugin does not emit `bigint` validators against `number` types, (b) removes the dynamic loopback `servers` entry whose port changes per run, and (c) sorts the three maps the generator emits in **discovery order** — `paths`, each path item's operations, and `components.schemas`. That order is assembly type order, hence compile order, hence the order the `.cs` files sort in, so without the sort renaming one endpoint file reshuffles thousands of lines of an unchanged contract. Only maps are sorted, never arrays: `required`, `enum`, `parameters`, `oneOf`/`anyOf`, `tags` and a schema's `properties` order all reach the generated TypeScript.
- **Generate:** `pnpm run openapi:generate` → `openapi-ts --file OpenapiTs.config.ts` writes `src/core/api/generated/`.
- **Combined:** `pnpm run openapi` runs both; `openapi:check` / `openapi:check:live` fail CI if the generated output drifts from committed code (`git diff --exit-code`). The two see different things: `openapi:check` regenerates the client from the **committed** spec and so cannot see a backend-side change at all, while `openapi:check:live` compares the committed spec against a running host. Only the first is in a gate, because the second needs a host.

### Why the FastEndpoints serializer global is seeded at registration time

The OpenAPI document is generated against a COPY of the FastEndpoints serializer global — `FastEndpoints.Swagger`'s `SwaggerDocument` registration does `new JsonSerializerOptions(Config.SerOpts.Options)` — and NSwag's `UseOpenApi` resolves that registration EAGERLY while the pipeline is being built, whenever the document path contains `{documentName}`, which this one does. `UseFastEndpoints`, which is what normally populates that global from `IOptions<JsonOptions>`, has not run at that point, so the generator would describe a camelCase API with PascalCase property and parameter names. Seeding the global in `ConfigureServices`, at registration time, makes the document correct no matter where the middleware sits in the pipeline; `UseFastEndpoints` later replaces the instance outright, so runtime serialization is unaffected. The camelCase policy is **stated rather than inherited** in the shared JSON configuration: every options instance it is applied to at runtime already carries it from `JsonSerializerDefaults.Web`, but the FastEndpoints global starts life as a bare `JsonSerializerOptions` (PascalCase) and the document generator snapshots that one.

The global is process-wide and shared with every other host in the process, so it must not be mutated once it has been used: `System.Text.Json` seals an options instance on first (de)serialization. Read-only therefore means an earlier host already ran `UseFastEndpoints` and served with it, which is exactly the case where the instance already carries the camelCase policy it took from `IOptions<JsonOptions>` — nothing to seed.

### What the generator emits (`OpenapiTs.config.ts`)

- `@hey-api/client-axios` with a custom `Generated.runtime.ts` (sets the shared axios instance and baseURL); a thrown `ZodError` reaches the shared error sinks as is.
- `@hey-api/typescript` types.
- `zod` response schemas with `dates: { offset: true }` — required so RFC3339 offset timestamps (the backend serializes `DateTimeOffset` as `…+00:00`) validate; the default bare-`Z` schema would reject every offset response.
- `@hey-api/sdk` with `validator: true` — each SDK call validates the response at the transport boundary.
- `@tanstack/react-query` with `queryOptions`, `queryKeys`, `mutationOptions`.

### Maintainer workflow when you touch an endpoint

1. Add/modify the `*Endpoint.cs` + DTOs (the **class name becomes the operationId/SDK function name** — see §1).
2. Regenerate against a host: `OPENAPI_LIVE_SCRIPT=openapi scripts/openapi-live-check.sh`. That script already starts the host the document must come from — **desktop mode**, an isolated `XDG_DATA_HOME`/`HOME`, the build lock, the assembly guard, the port read from `desktop-port.txt`, and the host killed by process group on the way out — and `OPENAPI_LIVE_SCRIPT` chooses what it runs against it (`openapi:check:live` to compare, `openapi` to regenerate, `openapi:fetch` for the document alone). Desktop mode is not optional: off it, `Program.cs`'s `config.Endpoints.Filter` drops every `IDesktopOnlyEndpoint` from the document, and the regen then silently deletes those paths and their SDK functions.
3. Commit the regenerated artifacts (they are tracked; `openapi:check` enforces this).
4. For a **hub**, or a route whose response is not request/response JSON, do *not* expect a typed SDK fn — wire it by hand on the shared axios instance / through `SharedHubConnection` (see the hand-wired list above and the chat hub).

> Hand-rolling the throwaway host instead (connection string as a CLI arg, `XE_NODE_SQLITE_KEY`, `ASPNETCORE_URLS`, and `NODE_EXTRA_CA_CERTS` pointing at the trusted PEM development certificate, which Node reads only at startup) is the older recipe and gets the document **wrong** unless it also runs in desktop mode. Prefer the script in step 2; it is the same host start CI uses.

The generated SDK is regenerated as endpoint families land; the current artifacts include Custom Tools CRUD/probe, runtime-acquisition status, automation commands, workspaces, the Benchmarks and Training families, `proxy/key`, and the other routes in the [endpoint inventory](reference/09-endpoint-inventory.md). The committed `openapi/v1.json` + `src/core/api/generated/` already include them; `openapi:check` still enforces that they stay in sync. The proxy **passthrough** (`proxy/v1/*`) is deliberately absent from that document — it is hand-mapped precisely so it never reaches the generated SDK.

---

## Related pages

- [Architecture Overview](01-architecture-overview.md) — where the transport layer sits.
- [Project Layout](02-project-layout.md) — `Client/Endpoints`, `Client/Hubs`, `Client.Application/Services`.
- [Chat](05-chat.md) — `LocalChatHub` streaming methods + Codex/cloud chat routes.
- [Scheduler](06-scheduler.md) — `scheduler/*` endpoints + `SchedulerHub` push events.
- [Model Fit](07-model-fit.md) — `model-fit/*` endpoints (GGUF discovery, advisor, llama.cpp runtime).
- [Local Runtime & Providers](03-local-runtime-and-providers.md) — `models/*` endpoints and the `source-build` family behind the runtime.
- [Agent Mode](04-agent-mode.md) — `agents/*`, `skills/*`, `mcp/*` endpoints.
- [React Client](10-react-client.md) — generated hey-api SDK consumption + hub client wrappers.
- [Security & Privacy](12-security-and-privacy.md) — loopback guard, Operator policy, secret redaction, hub privacy exceptions.
- [Hosting & Deployment](11-hosting-and-deployment.md) — health checks, static SPA fallback, node binding, `app-update`/`github-auth`.
- [Image Generation](14-image-generation.md) — `images/*` endpoints + `ImageJobHub`.
- [Knowledge Base / RAG](15-knowledge-base.md) — `knowledge-base/*` endpoints + `KnowledgeBaseHub`.
- [Graph Workflows](21-graph-workflows.md) — the `graph-workflows/*` family, `GraphWorkflowRunHub`, and the `graphWorkflowChanged` contract.
- [Testing & Validation](13-testing-and-validation.md) — `LocalApiSecurityTests`, `OpenApiDocumentTests`, `openapi:check`.
- [Model Lab](25-model-lab.md) — the `model-lab/*` family; its routes are listed in [the Model Lab reference](reference/25-model-lab.md#endpoints).
- Reference pages: [Endpoint Inventory and Design Notes](reference/09-endpoint-inventory.md) (every route family, its routes and owner page) · [SignalR Hub Inventory and Event Names](reference/09-signalr-hubs.md) (every hub, its path, direction and event names).
- ADRs behind the `development/*` surface: [ADR 0001](../adr/0001-development-mode-restart-recovery.md), [ADR 0002](../adr/0002-development-cloud-egress-carrier.md).
