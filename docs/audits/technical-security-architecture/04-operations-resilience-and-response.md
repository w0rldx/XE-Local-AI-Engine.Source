# 04 — Operations, Resilience, and Response

## Review boundary

| Item | Value |
|---|---|
| Frozen application baseline | `7e64ed589e14eecc0e522e807d2e531a1095d19a` |
| Review date | 2026-07-28 |
| Scope | Runtime observability, diagnostics, failure handling, shutdown/restart behavior, database snapshots, restore boundaries, and incident-response evidence |
| Evidence boundary | Repository implementation, tests, scripts, and documentation were reviewed. No production telemetry, service-management records, incident records, restore exercise, or operating-effectiveness sample was supplied. |

Source paths below are internal traceability. They identify where a claim was checked; they are not evidence assumed to be included with the recipient's dossier.

## Evidence-state convention

- **Implemented** means the behavior is present in the frozen source baseline.
- **Test-supported** means repository tests exercise the behavior. Test source or a passing test result is not the same as evidence that the control operated in a deployed environment.
- **Operationally observed** is used only when runtime or external-system evidence was available. No such operating sample was supplied for this chapter.
- **Gap / not evidenced** means the repository does not define the procedure or the requested operating evidence was not captured.

## Operational posture at a glance

| Area | Baseline posture | Important boundary |
|---|---|---|
| Application logs | Console plus rolling files outside the `Testing` environment | Seven rolled files are retained; this is not centralized log retention or an incident archive |
| Traces and metrics | OpenTelemetry instrumentation is registered; OTLP export is conditional | Packaged desktop/RC launches do not configure an exporter by default, so in-process telemetry is lost on exit |
| Health | Separate liveness and readiness endpoints | Liveness proves only that the HTTP process can answer; readiness reports `Degraded` as HTTP 200 and requires body inspection |
| Browser diagnostics | Redacted, local IndexedDB snapshots with manual zip export | Nothing is uploaded automatically; availability depends on the local browser profile and a user export |
| Local AI runtimes | Supervised child processes with readiness probes, bounded retries, reaping, and tree-kill teardown | This is local process recovery, not host redundancy or service failover |
| Graceful shutdown | Bounded worker drain followed by startup reconciliation | The drain is best-effort and can abandon remaining steps after its deadline |
| Node database protection | Best-effort pre-migration SQLite snapshot, retaining three by default | It is not periodic backup, off-device backup, or a tested restore/DR capability |
| Incident response | Diagnostic primitives exist | No repository-defined incident roles, escalation path, response SLA, evidence-preservation procedure, or post-incident process was found |

## Runtime observability

### Structured logs

The host configures Serilog console logging and, outside the `Testing` environment, a rolling file under the resolved node data directory at `logs/xe-node-.log`.

| Property | Baseline behavior | Internal traceability |
|---|---|---|
| Correlation | Rolling-file entries include W3C `TraceId` and `SpanId` when an activity exists | `XE-Local-AI-Engine.Client/Common/Extensions/LoggerExtensions.cs` |
| Roll conditions | Daily roll and 50 MiB file-size roll | `LoggerExtensions.WriteToRollingFile` |
| Retention | Seven rolled files | `LoggerExtensions.RetainedLogFileCount` |
| Startup failures | The startup logger uses the rolling file so pre-host failures can be retained | `LoggerExtensions.CreateStartupLogger` |
| Request logging | Routine success, 401, and 404 traffic is reduced to `Debug`; failures remain louder | `XE-Local-AI-Engine.Client/Program.cs` |
| Query-token handling | Request query strings are processed by `AccessTokenQueryRedactor` before request-log enrichment | `Program.cs`; `XE-Local-AI-Engine.Client.Application/Services/Auth/AccessTokenQueryRedactor.cs` |

The code-defined file cap limits local growth. It does not establish log shipping, immutable retention, clock synchronization, alerting, review ownership, or evidence preservation after an incident. No log sample or retention-operation sample was supplied.

### OpenTelemetry traces, metrics, and logs

`AddServiceDefaults` registers ASP.NET Core, HTTP-client, runtime, node, agent, and Microsoft.Extensions.AI instrumentation in all hosting modes. The OTLP exporter is registered only when `OTEL_EXPORTER_OTLP_ENDPOINT` is present.

| Hosting case | Export posture |
|---|---|
| Aspire development orchestration | Aspire normally injects an OTLP endpoint, allowing export to its dashboard |
| Packaged desktop/RC default | No exporter is attached; spans and metrics remain in-process and are lost on exit |
| Operator-configured collector | Setting `OTEL_EXPORTER_OTLP_ENDPOINT` enables OTLP export to the chosen collector |

Enabling the exporter does not by itself enable prompt or completion capture. Sensitive `gen_ai` message content is controlled separately by `Agent:Telemetry:CaptureSensitiveContent`, which defaults off and is explicitly bound rather than inherited from an ambient OpenTelemetry variable.

Internal traceability:

- `XE-Local-AI-Engine.ServiceDefaults/Extensions.cs`
- `XE-Local-AI-Engine.Client/Program.cs`
- `XE-Local-AI-Engine.AI.Agent/Configuration/AgentTelemetryOptions.cs`
- `XE-Local-AI-Engine.AI.Agent/DependencyInjection/AgentServiceCollectionExtensions.cs`
- `docs/runbooks/otel-export-operator-runbook.md`
- `XE-Local-AI-Engine.Tests/Configuration/ServiceDefaultsTelemetryTests.cs`

No collector configuration, exported trace sample, dashboard retention setting, alert rule, or alert-response record was supplied. The implementation therefore supports optional telemetry export, but operating effectiveness and coverage are not evidenced.

### Durable execution metadata

The node stores metadata-only run envelopes and agent execution-log records that can be queried through Operator-authorized endpoints. These records can preserve terminal state, timing/usage fields, correlation identifiers, trace identifiers, error classifications, and approval decisions without relying on a live OTLP collector.

This durable metadata is useful for reconstruction, but it is not a full event log:

- message content is deliberately excluded from the metadata projections;
- a restart-reconciled envelope can have unknown model, token, or duration fields;
- retention and purge behavior follows application data lifecycle, not an external evidence-preservation policy;
- no exported baseline sample was supplied to the recipient.

Internal traceability:

- `XE-Local-AI-Engine.Client/Endpoints/Agents/V1/ListRunEnvelopesEndpoint.cs`
- `XE-Local-AI-Engine.Client/Endpoints/Agents/V1/ListAgentExecutionLogsEndpoint.cs`
- `XE-Local-AI-Engine.Client.Persistence/Implementation/AgentExecutionLogStore.cs`
- `XE-Local-AI-Engine.Client.Application/Services/Agents/Approval/Implementation/ToolApprovalAuditRecorder.cs`

## Health and diagnostics

### Health endpoint semantics

| Endpoint | Checks | HTTP behavior | Operational interpretation |
|---|---|---|---|
| `/health/live` | None; returns if the application can serve the request | 200 while the HTTP host answers | Process liveness only; it does not test SQLite, worker connectivity, model readiness, disk, or external providers |
| `/health/ready` | `worker_health` and `node_sqlite` checks tagged `ready` | `Healthy` and `Degraded` return 200; `Unhealthy` returns 503 | Consumers must inspect the JSON body because a degraded worker remains available for local inference |

The readiness response includes per-check status, description, and reason data. `node_sqlite` can make readiness unhealthy; worker connectivity or token state can be degraded without removing the node from rotation.

Internal traceability:

- `XE-Local-AI-Engine.Client/Program.cs`
- `XE-Local-AI-Engine.Client/ConfigureServices.cs`
- `XE-Local-AI-Engine.Client/HealthChecks/NodeSqliteHealthCheck.cs`
- `XE-Local-AI-Engine.Client/HealthChecks/WorkerHealthCheck.cs`
- `XE-Local-AI-Engine.Tests/HealthChecks/NodeSqliteHealthCheckTests.cs`
- `XE-Local-AI-Engine.Tests/HealthChecks/WorkerHealthCheckTests.cs`
- `XE-Local-AI-Engine.Tests/HealthChecks/ReadinessHealthResponseTests.cs`

No external health monitor, polling interval, alert threshold, or response ownership is defined by the repository.

### Local browser diagnostic snapshots

The React client captures redacted diagnostic snapshots on deduplicated errors and on a manual “Report a problem” action.

| Stage | Baseline behavior |
|---|---|
| Capture | Breadcrumbs, network metadata, environment metadata, opted-in state, and an optional Developer Mode rrweb segment are assembled |
| Redaction | Sensitive keys and token-bearing query parameters are masked before persistence; network bodies are not part of the persisted network entry |
| Storage | Snapshots stay in browser IndexedDB; retention is capped at 25 snapshots and approximately 25 MiB |
| Persistence request | Browser persistent storage is requested best-effort; denial is ignored |
| Export | A user can download a zip containing `snapshot.json` |
| Transfer | Export and import are local-only; snapshot content is not transmitted automatically |

Internal traceability:

- `XE-Local-AI-Engine.Client.React/src/core/diagnostics/Redact.ts`
- `XE-Local-AI-Engine.Client.React/src/features/diagnostics/BuildSnapshot.ts`
- `XE-Local-AI-Engine.Client.React/src/features/diagnostics/SnapshotStore.ts`
- `XE-Local-AI-Engine.Client.React/src/features/diagnostics/ExportSnapshot.ts`
- `XE-Local-AI-Engine.Client.React/src/core/diagnostics/Redact.test.ts`
- `XE-Local-AI-Engine.Client.React/src/features/diagnostics/SnapshotStore.test.ts`

Residual boundaries:

- browser eviction, profile deletion, or absence of a manual export can remove the evidence;
- redaction reduces known secret exposure but cannot prove that arbitrary error text contains no personal or confidential data;
- the zip is not signed or encrypted by the export function;
- no support-bundle collection, custody, retention, or deletion procedure is defined.

## Failure handling and recovery boundaries

### Local runtime process supervision

The default llama.cpp runtime is managed by `LlamaServerProcessSupervisor`. The supervisor:

- uses a single-flight gate for a model/role process;
- probes readiness before registration and probes liveness before reuse;
- retries bounded startup failures with backoff;
- classifies slow readiness separately from crash-style failures;
- tears down half-started, exited, or repeatedly unresponsive process trees;
- reaps exited and idle processes;
- prevents idle eviction while a process has an active lease; and
- releases processes and reserved ports during disposal.

The stable-diffusion.cpp provider has a separate `ImageServerProcessSupervisor` and OS-specific process-group/job-object handles.

These controls improve recovery of local child processes. They do not provide node-level high availability, remote failover, automatic restoration of interrupted model output, or a service availability objective.

Internal traceability:

- `XE-Local-AI-Engine.Providers.LlamaServer/Implementation/LlamaServerProcessSupervisor.cs`
- `XE-Local-AI-Engine.Providers.StableDiffusionCpp/Implementation/ImageServerProcessSupervisor.cs`
- `XE-Local-AI-Engine.Tests/Providers/LlamaServer/SupervisorCrashAndSurfaceTests.cs`
- `XE-Local-AI-Engine.Tests/Providers/LlamaServer/SupervisorRaceTests.cs`

### Graceful shutdown

On `ApplicationStopping`, the worker drain performs one bounded sequence:

1. stop accepting new remote invocation assignments;
2. wait for active invocations;
3. flush the dead-letter outbox; and
4. disconnect the WorkerHub.

The configured default end-to-end drain timeout is 30 seconds. `Program.cs` adds a five-second outer grace ceiling so an operation that does not honor cancellation cannot block shutdown indefinitely. Incomplete steps are logged and the process continues toward shutdown.

This behavior is intentionally best-effort. It does not guarantee that every active invocation completes or that every volatile event is flushed.

Internal traceability:

- `XE-Local-AI-Engine.Client.Application/Services/Shutdown/Implementation/WorkerShutdownDrainService.cs`
- `XE-Local-AI-Engine.Client.Application/Services/Shutdown/WorkerShutdownDrainOptions.cs`
- `XE-Local-AI-Engine.Client/Program.cs`
- `XE-Local-AI-Engine.Tests/Shutdown/WorkerShutdownDrainServiceTests.cs`

### Restart reconciliation

Before the host begins normal service:

- non-terminal assistant messages in `Pending`, `Queued`, or `Streaming` are moved to `Interrupted`;
- missing terminal run envelopes are backfilled from persisted terminal message rows;
- scheduled runs left `Queued` or `Running` are moved to `Failed` with a restart reason.

The reconciliation is terminalization, not automatic resume or re-dispatch. It prevents indefinitely “running” records but does not reconstruct lost model output, token counts, duration, or in-memory execution state.

Internal traceability:

- `XE-Local-AI-Engine.Client.Application/Services/Chat/Implementation/NodeChatRestartRecoveryService.cs`
- `XE-Local-AI-Engine.Client.Persistence/Implementation/ScheduledJobRunStore.cs`
- `XE-Local-AI-Engine.Client/Program.cs`
- `XE-Local-AI-Engine.Tests/Chat/NodeChatRestartRecoveryServiceTests.cs`
- `XE-Local-AI-Engine.Client.Persistence.Tests/ScheduledJobRunStoreTests.cs`

## Backup, restore, and disaster-recovery boundary

### Implemented pre-migration snapshot

Before pending node-chat migrations are applied, `NodeDbBackupService` attempts a SQLite `VACUUM INTO` snapshot.

| Property | Baseline behavior |
|---|---|
| Trigger | Only when node-chat database migrations are pending |
| Consistency mechanism | SQLite `VACUUM INTO`, producing one database snapshot and folding WAL content into the copy |
| Default location | `<node-data-root>/backups` |
| Naming | `node-chat-<UTC timestamp>.sqlite` |
| Default retention | Three most recent snapshots |
| Failure behavior | Error is logged and swallowed; startup migration continues without a fresh snapshot |
| Data protection | The snapshot preserves the same application-encrypted ciphertext columns as the source database; it is not a SQLCipher whole-file backup |

Internal traceability:

- `XE-Local-AI-Engine.Client.Application/Services/Persistence/Implementation/NodeDbBackupService.cs`
- `XE-Local-AI-Engine.Client.Application/Services/Persistence/NodeDbBackupOptions.cs`
- `XE-Local-AI-Engine.Client/Program.cs`
- `XE-Local-AI-Engine.Client.Persistence.Tests/NodeDbBackupServiceTests.cs`

### What this snapshot does not establish

The repository does not establish:

- a periodic backup schedule;
- an off-device, offline, or geographically separate copy;
- backup monitoring or a failed-backup alert;
- a documented operator restore command or automated restore workflow;
- a restore validation exercise or evidence sample;
- backup custody, access review, or deletion procedure;
- recovery-point or recovery-time objectives; or
- a guarantee that identity data, Data Protection keys, model files, runtime binaries, browser diagnostics, and other node state are recovered together.

Accordingly, the implemented snapshot is a local, best-effort migration safety net. It must not be represented as a disaster-recovery control or a recoverability guarantee.

## Incident-response posture

The baseline provides logs, trace correlation, optional OTLP export, local diagnostic bundles, health endpoints, durable run metadata, graceful drain, and restart reconciliation. Those are diagnostic and containment primitives.

No repository-defined incident-response program was found for the application. In particular, the review found no application-specific:

- incident commander or response roles;
- severity model or escalation path;
- acknowledgment, containment, recovery, or communication SLA;
- evidence-preservation and chain-of-custody procedure;
- credential-compromise or key-rotation playbook;
- backup restoration playbook;
- notification decision process;
- post-incident review template; or
- formal acceptance of the residual risks listed in this chapter.

The absence of those artifacts is an evidence gap, not proof that an organization operating the application has no external process. No external process was supplied for review, so the current dossier position is **not evidenced** and ownership is **unassigned**.

## Residual-risk summary

1. **Telemetry loss by default.** Desktop/RC traces and metrics disappear at process exit unless an operator configures a collector.
2. **Local evidence is mutable and bounded.** Rolling logs and browser snapshots can be evicted or deleted and are not held in an immutable evidence store.
3. **Health is not full service assurance.** Liveness is process-only, and degraded readiness remains HTTP 200.
4. **Graceful shutdown can be incomplete.** The deadline intentionally favors process termination over indefinite draining.
5. **Restart reconciliation loses execution detail.** It terminalizes state but does not resume work or reconstruct volatile output.
6. **Pre-migration snapshots are not disaster recovery.** They are local, conditional, and non-blocking, with no restore evidence.
7. **Incident governance is not evidenced.** Roles, timelines, escalation, evidence handling, and formal risk acceptance are unassigned.

These risks have no formal acceptance record in the reviewed repository or supplied evidence.
