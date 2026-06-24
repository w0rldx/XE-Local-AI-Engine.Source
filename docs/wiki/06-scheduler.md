# Scheduler

> Last reviewed: 2026-06-24 · Code-grounded.

The node runs a **Quartz.NET-backed job scheduler** entirely in-process inside the Node Web Server (`XE-Local-AI-Engine.Client`). It lets an operator define recurring/one-shot/manual jobs from a registered set of *templates*, fires them through a thin dispatch job, records every fire as a run-history row, supports best-effort cancellation (operator interrupt + auto-interrupt timeout), and pushes live lifecycle events to the React management UI over a SignalR hub. The data layer is encrypted node-local SQLite; the canonical model-fit "Model recommendation check" job is the only template shipped today (see [Model Fit](07-model-fit.md)).

This page covers the C# scheduler subsystem (`XE-Local-AI-Engine.Client.Application/Services/Scheduler/*` + the Client host wiring), the SignalR hub (`XE-Local-AI-Engine.Client/Hubs/*`), and the React scheduler feature (`XE-Local-AI-Engine.Client.React/src/features/scheduler/*`).

---

## Where it lives

| Concern | Location |
|---|---|
| Application service + contracts | `XE-Local-AI-Engine.Client.Application/Services/Scheduler/` |
| Dispatch executor + jobs + registry | `…/Services/Scheduler/Implementation/` |
| Template handler(s) | `…/Services/Scheduler/Handlers/` |
| DI registration (Quartz) | `XE-Local-AI-Engine.Client.Application/DependencyInjection/NodeSchedulerServiceCollectionExtensions.cs` |
| SignalR hub + publisher | `XE-Local-AI-Engine.Client/Hubs/SchedulerHub.cs`, `…/Hubs/SchedulerEventPublisher.cs` |
| Retention sweep | `XE-Local-AI-Engine.Client/BackgroundServices/SchedulerHistoryRetentionService.cs` |
| REST endpoints (FastEndpoints) | `XE-Local-AI-Engine.Client/Endpoints/Scheduler/V1/` |
| Route constants | `XE-Local-AI-Engine.Client/Endpoints/Common/LocalApiRoutes.cs:203` (`Scheduler`) |
| React feature | `XE-Local-AI-Engine.Client.React/src/features/scheduler/` |

---

## Architecture at a glance

```
Operator (React) ──REST /api/local/v1/scheduler/* ──▶ FastEndpoints
                                                          │
                                            IScheduledJobManagementService
                                            (validate → store-first → reconcile Quartz)
                                                          │
   IScheduledJobDefinitionStore (encrypted SQLite) ◀──────┤──────▶ ISchedulerFactory (Quartz)
                                                          │            schedules JobKey(group="scheduled-jobs")
                                                          ▼
                            Quartz fires ──▶ (NonOverlapping)SchedulerDispatchJob (thin IJob)
                                                          │ SchedulerDispatchJobRunner.RunAsync
                                                          ▼
                            ISchedulerDispatchExecutor.DispatchAsync
                              · load+gate definition  · TryGetHandler(templateId)
                              · upsert run row by FireInstanceId (idempotent)
                              · invoke IScheduledJobHandler.ExecuteAsync(context, ct)
                              · record terminal state  · publish hub events
                                                          │
                            IScheduledJobRunStore (encrypted run history) + ISchedulerEventPublisher
                                                          ▼
                            SchedulerHub (IHubContext) ──push──▶ React useSchedulerHub → invalidate TanStack Query
```

The invariant: **the store is written first, then Quartz is reconciled to match.** The store owns id/timestamp stamping and the soft-delete/enable lifecycle; the management service never re-implements them (`IScheduledJobManagementService` docstring, `IScheduledJobManagementService.cs:6-14`).

---

## Templates, handlers, and the registry

A **template** is a server-defined job type. Each is an `IScheduledJobHandler` exposing a `TemplateId`, a `ScheduledJobTemplateDescriptor` (display name, JSON-Schema parameter contract, default parameters, supported `ScheduleKind`s, default kind, misfire policy, default max-runtime, manual/agent-creation flags, history detail level), and an `ExecuteAsync(context, ct)`.

`ScheduledJobTemplateRegistry` (`Implementation/ScheduledJobTemplateRegistry.cs:19`) is a **singleton built at startup** from every DI-registered `IScheduledJobHandler`. It snapshots handlers into a `FrozenDictionary` keyed case-sensitively by `TemplateId` and **throws `InvalidOperationException` at construction on a duplicate `TemplateId`** — a programming error deliberately caught at startup, not suppressed at runtime.

### Shipped template

`ModelRecommendationCheckHandler` (`Handlers/ModelRecommendationCheckHandler.cs:29`), template id `model-recommendation-check`, runs the box-aware GGUF Model Advisor and refreshes the cached recommendation snapshot. See [Model Fit](07-model-fit.md) for what it computes. Maintainer-relevant details:

- **It is a singleton** (the registry captures handlers in a `FrozenDictionary`), so it **cannot inject scoped services**. It injects `IServiceScopeFactory` and resolves the scoped `IModelFitRefreshService` inside a per-fire scope (lines 70-103).
- It owns **no scheduler state**: it never touches run rows or SignalR — the dispatcher does. It forwards `context.ReportProgressAsync`, lets `OperationCanceledException` propagate (→ dispatcher records *Cancelled*), and throws a `ScheduledJobExecutionException` carrying the refresh result's contractually-sanitized `SanitizedError` on failure (→ dispatcher records *Failed* with that exact reason).
- Parameters are validated against a draft-07 JSON-Schema (`operation`/`useCase`/`limit`/`quantOverride`/`ctxTarget`) and re-validated through the shared `ModelFitRequestValidator`; raw parameter values are never echoed into error text.

---

## The execution path

### Dispatch jobs (thin)

Two Quartz `IJob` types, both deliberately trivial — all logic lives in the executor:

| Job | Used when | File |
|---|---|---|
| `SchedulerDispatchJob` | `PreventOverlap == false` | `Implementation/SchedulerDispatchJob.cs` |
| `NonOverlappingSchedulerDispatchJob` | `PreventOverlap == true` | `Implementation/NonOverlappingSchedulerDispatchJob.cs:14` |

The only difference is `[DisallowConcurrentExecution]` on the non-overlapping variant. That attribute is **keyed per `JobKey`**, so distinct definitions still run concurrently — only re-entrant fires of the *same* definition are serialized. Both delegate to `SchedulerDispatchJobRunner.RunAsync(executor, logger, context)`.

The management service stamps the definition id into the `JobDataMap` under `SchedulerJobKeys.ScheduledJobIdKey = "scheduledJobId"`, and every job lives in the Quartz group `SchedulerJobKeys.Group = "scheduled-jobs"` (`SchedulerJobKeys.cs:14,20`). With `UseProperties = true` the data map is string-only, so the `Guid` is stored as its string form.

### `SchedulerDispatchExecutor.DispatchAsync`

`Implementation/SchedulerDispatchExecutor.cs:73` (interface `ISchedulerDispatchExecutor`) is the heart of a fire:

1. Load the definition by id; **skip silently** if missing, disabled, soft-deleted, or its template is no longer registered (a faulting/absent handler must never fault the scheduler).
2. `RecordAndRunAsync` (`:112`): **idempotently upsert** the run row keyed on the Quartz `FireInstanceId` via `IScheduledJobRunStore.UpsertByFireInstanceAsync`. If the row is already terminal (a refire / recovery callback re-using the same instance id) the work has run before → skip re-execution.
3. Publish `RunStarted`, build the `ScheduledJobExecutionContext`, invoke the handler.
4. Record the terminal lifecycle state (`Succeeded` / `Failed` / `Cancelled`) and publish the matching hub event.

**Error sanitization is a security boundary.** Any exception → the run records the generic constant `"The scheduled job failed during execution."`. The *only* widening is `ScheduledJobExecutionException`: its message is recorded verbatim (it is a marker type whose construction contract — enforced by reviewers — forbids secrets, raw process output, exception text, or raw parameters). Tests pin all three paths in `SchedulerDispatchExecutorHistoryTests.cs` (generic InvalidOperationException with an embedded secret → message must NOT contain the secret; `ScheduledJobExecutionException` → recorded verbatim; any other type → generic constant). See [Security & Privacy](12-security-and-privacy.md).

### `ScheduledJobExecutionContext`

`Services/Scheduler/ScheduledJobExecutionContext.cs:9` carries everything a handler needs for one invocation: `ScheduledJobId`, `TemplateId`, `DisplayName`, **decrypted plaintext `Parameters`** (treat as untrusted — validate against the descriptor's `ParameterSchema`), `FireInstanceId`, scheduled/actual fire times, `TriggeredBy`, and an optional `ReportProgressAsync(message, percent, ct)` callback (defaults to no-op; null means progress events are silently dropped — acceptable for Summary-level templates).

---

## Run history & retention

Run history is owned by `IScheduledJobRunStore` (`XE-Local-AI-Engine.Client.Persistence/Stores/IScheduledJobRunStore.cs`). Notable contract points:

- **`DetailsJson` is encrypted at rest** by the node encryption interceptors; reads return it decrypted. See [Data & Persistence](08-data-and-persistence.md).
- Runs have **no enforced FK to their definition** so history outlives the definition; their *events* cascade-delete.
- The store owns: id/timestamp stamping, `UpsertByFireInstanceAsync` (idempotent fire-instance open), `UpdateLifecycleAsync` (terminal transitions), `RequestCancellationAsync` (stamp `CancellationRequestedAtUtc` without changing status), `MarkStaleActiveRunsAsync` (startup reconciliation of orphaned `Queued`/`Running` runs to a terminal state), and `SweepOlderThanAsync` (retention deletes by `CreatedAtUtc`, cascade removes events).

`SchedulerHistoryRetentionService` (`XE-Local-AI-Engine.Client/BackgroundServices/SchedulerHistoryRetentionService.cs:13`) is a `BackgroundService` that periodically calls the sweep using `SchedulerOptions.HistoryRetentionDays` (default 30) on a `RetentionSweepIntervalMinutes` cadence (default 60).

---

## Cancellation & auto-interrupt

There are **two ways** a run reaches the handler's `CancellationToken`:

1. **Operator cancel** — `IScheduledJobManagementService.CancelRunAsync(runId)` (`Implementation/ScheduledJobManagementService.cs:315`). It records intent first (`RequestCancellationAsync` stamps `CancellationRequestedAtUtc`), *then* `scheduler.Interrupt(QuartzFireInstanceId)`. Recording intent first lets the dispatcher distinguish an operator cancel (→ `Cancelled`) from an auto-interrupt timeout (→ `TimedOut`) when the token trips. The method returns a `RunCancellationOutcome`:

   | Outcome | Meaning |
   |---|---|
   | `NotFound` | no run has that id |
   | `AlreadyTerminal` | run already finished |
   | `Requested` | Quartz interrupt was active (run is currently executing) |
   | `RequestedButNotRunning` | intent recorded, but no live fire instance to interrupt |

   (`Services/Scheduler/RunCancellationOutcome.cs:7`)

2. **Auto-interrupt (timeout)** — opt-in **per job**. `AddNodeScheduler` registers `q.UseJobAutoInterrupt(o => o.DefaultMaxRunTime = options.DefaultMaxRuntimeMinutes)` (`NodeSchedulerServiceCollectionExtensions.cs:50`), but Quartz's auto-interrupt plugin only acts on jobs that **opt in**. A job's effective max-runtime comes from its descriptor / definition (`DefaultMaxRuntimeSeconds`, e.g. 600 for the model-fit template; `MaxRuntimeSeconds` on `ScheduledJobManagementInput`). **Gotcha:** auto-interrupt is *not* applied globally to every job — it is per-job opt-in, so a template/definition that doesn't carry a runtime budget will never be auto-interrupted regardless of the configured default.

Either way, the handler must actually *observe* the token. The dispatcher records the terminal state once the handler throws `OperationCanceledException`.

---

## DI registration & two key gotchas

`AddNodeScheduler` (`NodeSchedulerServiceCollectionExtensions.cs:18`) wires the whole subsystem behind `SchedulerOptions.Enabled`. When disabled, the Quartz hosted service never starts (no jobs fire) but persistence tables and DI registrations remain. It configures `AddQuartz` with `UseProperties = true`, `PerformSchemaValidation = true` (the `QRTZ_` tables are created by the scheduler EF migration), `UseMicrosoftSQLite`, `UseTimeZoneConverter`, `UseJobAutoInterrupt`, and `AddQuartzHostedService(WaitForJobsToComplete = true)`. The dispatch jobs are `Transient`, the executor + management service are `Scoped`, the registry + model-fit handler are `Singleton`.

### Gotcha 1 — Quartz `ConnectionStringName` is resolved lazily, by name

```csharp
// NodeSchedulerServiceCollectionExtensions.cs:39-45
db.ConnectionStringName = "node-sqlite"; // looked up in IConfiguration's ConnectionStrings at scheduler start
```

Quartz resolves the connection **by name at scheduler start (post-config-build)**, exactly like the `NodeChatDbContext` registration reads it lazily. **Reading the literal connection string at registration time would throw under `WebApplicationFactory`**, whose connection string is layered in *after* services are registered. Always reference `node-sqlite` by name here, never inline the value.

### Gotcha 2 — `AddJob`/durable reconcile with `replace: true` self-heals a stale `JOB_CLASS_NAME`

`ReconcileDurableJobsAsync` (`IScheduledJobManagementService.cs:71-78`) re-adds the durable Quartz `JobDetail` **with `replace=true`** for every persisted, enabled, non-deleted definition that already has a Quartz job. This heals a stale persisted `JOB_CLASS_NAME` — e.g. one written before the dispatch job changed namespaces/type. It never changes a trigger's schedule and never fires a job; unknown-template definitions are skipped. It is intended to run **once at startup**. Without this, a persisted Quartz row pointing at a moved/renamed `IJob` type would fail to materialize at fire time.

### `SchedulerOptions`

`Services/Scheduler/SchedulerOptions.cs:8` (section `"Scheduler"`), validated by `SchedulerOptionsValidator`:

| Option | Default | Notes |
|---|---|---|
| `Enabled` | `true` | false → hosted service not started |
| `MaxConcurrency` | `4` | Quartz thread-pool max; must be > 0 |
| `HistoryRetentionDays` | `30` | retention sweep cutoff |
| `RetentionSweepIntervalMinutes` | `60` | sweep cadence |
| `DefaultTimeZoneId` | `"UTC"` | IANA id; non-blank |
| `DefaultMaxRuntimeMinutes` | `5` | auto-interrupt default budget |
| `QuartzTablePrefix` | `"QRTZ_"` | matches the migration's embedded QRTZ DDL |

---

## SignalR hub (live run updates)

`SchedulerHub` (`XE-Local-AI-Engine.Client/Hubs/SchedulerHub.cs:14`) is a **server-push-only** hub: it has no client-callable server methods. It is mapped via `MapHub` at the full path `/api/local/v1/scheduler/hub` (`LocalApiRoutes.cs:226`) and is `[Authorize]`d under the JWT bearer scheme with the `NodeAuthorizationPolicies.Operator` policy — the same operator gate as the other local hubs. See [API & Hubs](09-api-and-hubs.md).

`SchedulerEventPublisher` (`Hubs/SchedulerEventPublisher.cs:12`) implements `ISchedulerEventPublisher` over `IHubContext<SchedulerHub>` and broadcasts each event to `Clients.All` using the event's `EventType` as the SignalR method name. **Payloads are already sanitized by callers** (no parameters, details, or stack traces). In Application-only/test hosts a no-op `NullSchedulerEventPublisher` is registered (`TryAddSingleton`) and superseded by the hub-backed publisher in the Client host.

Event names (`SchedulerHubEvents`, `ISchedulerEventPublisher.cs:28`) and their hub method strings consumed by React:

| `SchedulerHubEvents` constant | React method name |
|---|---|
| `JobDefinitionChanged` | `scheduler.jobDefinitionChanged` |
| `RunStarted` | `scheduler.runStarted` |
| `RunProgress` | `scheduler.runProgress` |
| `RunCompleted` | `scheduler.runCompleted` |
| `RunFailed` | `scheduler.runFailed` |
| `RunCancelled` | `scheduler.runCancelled` |

---

## REST surface

All under the FastEndpoints local prefix; route constants in `LocalApiRoutes.Scheduler` (`LocalApiRoutes.cs:203`), one endpoint class per file in `Endpoints/Scheduler/V1/`. DTOs in `SchedulerEndpointDtos.cs`, mapping in `Mappers/SchedulerMapper.cs`.

| Route (relative to `scheduler/`) | Endpoint |
|---|---|
| `templates` (GET) | `ListScheduledJobTemplatesEndpoint` |
| `jobs` (GET / POST) | `ListScheduledJobsEndpoint` / `CreateScheduledJobEndpoint` |
| `jobs/{scheduledJobId}` (GET / PUT / DELETE) | `GetScheduledJobEndpoint` / `UpdateScheduledJobEndpoint` / `DeleteScheduledJobEndpoint` |
| `jobs/{scheduledJobId}/enable` · `/disable` | `EnableScheduledJobEndpoint` / `DisableScheduledJobEndpoint` |
| `jobs/{scheduledJobId}/trigger` | `TriggerScheduledJobEndpoint` |
| `runs` (GET, query-filtered) | `ListScheduledJobRunsEndpoint` |
| `runs/{runId}` (GET) | `GetScheduledJobRunEndpoint` |
| `runs/{runId}/cancel` | `CancelScheduledJobRunEndpoint` |

These endpoints are local/loopback, operator-authenticated, and strict on Host/Origin like the rest of the local admin surface ([Security & Privacy](12-security-and-privacy.md)). The OpenAPI document generated from them is the single source for the React REST clients (hey-api) — see [React Client](10-react-client.md).

---

## React scheduler feature

`XE-Local-AI-Engine.Client.React/src/features/scheduler/`:

| Area | Files |
|---|---|
| Page | `pages/SchedulerPage.tsx`, `pages/SchedulerPageFormMappers.ts` |
| Components | `components/ScheduledJobList.tsx`, `ScheduledJobForm.tsx`, `ScheduledJobRunHistoryPanel.tsx`, `ScheduledJobRunDetail.tsx`, `components/SchedulerRunFormatters.ts` |
| Data hooks | `queries/useScheduler.ts` (hey-api TanStack Query), `hooks/useSchedulerHub.ts` (SignalR) |
| Models / mappers | `models/SchedulerModels.ts`, `models/SchedulerMappers.ts` |
| Store | `stores/SchedulerManagementStore.ts` (UI/dialog state via Zustand) |

### Hub → query invalidation pattern

`useSchedulerHub.ts` is **notification-only**: a push tells the client that state changed but carries no authoritative payload. Each handler simply **invalidates the matching TanStack Query cache** and lets the query refetch canonical state:

- `scheduler.jobDefinitionChanged` → invalidate the jobs list query.
- any of the five `scheduler.run*` events → invalidate the run-history list **and** the per-run detail query.

It connects to `buildLocalApiUrl("scheduler/hub")` with an `accessTokenFactory` from the node auth store, uses `withAutomaticReconnect()`, and tolerates connection failures silently (logged to `console.warn`) so a flaky hub never breaks the page — queries still serve last-good data.

**Gotcha (StrictMode / fast-remount race):** the effect cleanup sets a `disposed` flag and only calls `connection.stop()` *after* `connection.start()` settles (`startPromise.finally(...)`). Stopping during an in-flight negotiation is the "stopped during negotiation" race that left the hub permanently disconnected under React StrictMode's double-invoke. Invalidation keys use the hey-api `_id` partial-object form (`schedulerInvalidationKey(schedulerQueryIds.*)`) so a single invalidation matches every cached variant of an endpoint.

---

## Maintainer checklist

- Adding a template = register one more `IScheduledJobHandler` in `AddNodeScheduler`; **`TemplateId` must be globally unique** or the registry throws at startup.
- Never widen the UI-visible error surface except via `ScheduledJobExecutionException` with proven-safe text.
- Keep dispatch jobs thin — logic belongs in `ISchedulerDispatchExecutor`.
- Reference `node-sqlite` by *name* in Quartz config; never inline the connection string.
- Run `ReconcileDurableJobsAsync` once at startup after any change to a dispatch job's type/namespace.
- Privacy-sensitive work (the model-fit advisor) runs node-local only — see [Security & Privacy](12-security-and-privacy.md).

---

## Related pages

- [Architecture Overview](01-architecture-overview.md)
- [Project Layout](02-project-layout.md)
- [Model Fit](07-model-fit.md) — the shipped `model-recommendation-check` template + manual "Refresh now"
- [Data & Persistence](08-data-and-persistence.md) — encrypted run history, QRTZ migration
- [API & Hubs](09-api-and-hubs.md) — local REST/hub conventions, operator auth
- [React Client](10-react-client.md) — hey-api query layer, feature structure
- [Security & Privacy](12-security-and-privacy.md) — error sanitization, node-local invariants
- [Testing & Validation](13-testing-and-validation.md)
