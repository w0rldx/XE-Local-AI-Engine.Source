# Enabling telemetry export (OpenTelemetry) — operator runbook

**Audience:** an operator running the packaged desktop/RC build (or any headless launch outside Aspire) who wants to
capture the engine's `gen_ai` traces and metrics for usage inspection or post-hoc incident diagnosis.
**Feature:** BE-03 — this is a docs-only lane; see `XE-Local-AI-Engine.ServiceDefaults/Extensions.cs`.

---

## Why this exists

The engine instruments every chat/agent turn with OpenTelemetry: `gen_ai` spans (provider round, budget, cancellation
status) and metrics (token counts, duration) from meters `XE.Node`, `XE.LocalAiEngine.AI.Agent`, and
`Microsoft.Extensions.AI*` (`ConfigureOpenTelemetry`, `XE-Local-AI-Engine.ServiceDefaults/Extensions.cs:51-99`). That
instrumentation runs **unconditionally**, in every hosting mode — but an **OTLP exporter is only attached when
`OTEL_EXPORTER_OTLP_ENDPOINT` is set**:

```csharp
// XE-Local-AI-Engine.ServiceDefaults/Extensions.cs:107-115
private void AddOpenTelemetryExporters()
{
    var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

    if (useOtlpExporter)
    {
        builder.Services.AddOpenTelemetry().UseOtlpExporter();
    }
}
```

Under **Aspire** (`aspire start` / dev), the AppHost auto-injects `OTEL_EXPORTER_OTLP_ENDPOINT` for every project it
launches (standard `AddProject` behavior), so the Aspire dashboard shows spans/metrics with zero extra config.

Under **desktop/RC** (the shipped app, and any other headless launch), nothing sets this variable, so:

- the exporter is never registered — no export loop, no overhead;
- spans/metrics still exist in-process (visible to an attached profiler/debugger) but **evaporate when the process
  exits**;
- an operator has **no live telemetry backend** to inspect after the fact.

Without an explicit `OTEL_EXPORTER_OTLP_ENDPOINT`, that is the whole story: telemetry export is off, by design, until
you turn it on. This runbook covers turning it on, and what's available when you don't.

---

## Turning it on: set `OTEL_EXPORTER_OTLP_ENDPOINT`

Point the standard OpenTelemetry variable at any OTLP-capable collector before launching the engine:

```bash
export OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317   # gRPC; use :4318 + /v1/traces etc. for HTTP/protobuf
```

Nothing else needs to change — the meters/sources are already wired (`ConfigureOpenTelemetry`,
`XE-Local-AI-Engine.ServiceDefaults/Extensions.cs:51-99`); setting the endpoint is the only switch.

### Minimal local-collector recipe (no Aspire required)

The simplest OTLP-capable target for a desktop/RC operator is a standalone **Aspire Dashboard** container — it needs
no Aspire orchestration, just an OTLP receiver + a viewer UI:

```bash
docker run --rm -it -p 18888:18888 -p 4317:4317 -p 4318:4318 \
  mcr.microsoft.com/dotnet/aspire-dashboard:latest
```

Then open `http://localhost:18888` and set `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317` in the shell that
launches the engine. Any other OTLP-capable collector (e.g. an `otel-collector` you already run) works the same way —
the engine only cares that the endpoint speaks OTLP.

> This is a standalone diagnostics container an operator runs themselves, unrelated to the engine's own runtime —
> Docker was deliberately removed as an execution substrate for the engine itself (see `docs/agent-knowledge.md`), not
> as a tool an operator can't use to host their own collector.

If you already run the app under Aspire (`aspire start`), you get this for free: the AppHost's own dashboard already
receives the export, no extra steps.

---

## Privacy posture: enabling export does not start shipping conversation content

Turning on export widens *where* telemetry goes, not *what* it contains. Message content (prompts, reasoning,
completions, tool-call arguments) is a **separate, code-owned opt-in** that defaults OFF and is never driven by the
ambient environment:

```csharp
// XE-Local-AI-Engine.AI.Agent/Configuration/AgentTelemetryOptions.cs:18-24
/// When true, the gen_ai instrumentation captures sensitive message content (prompts, reasoning, completions, and
/// tool-call arguments) into telemetry spans. Default false: this is a privacy-sensitive opt-in that must be turned
/// on deliberately and is NEVER driven by an ambient environment variable.
public bool CaptureSensitiveContent { get; set; }
```

The MEAI `OpenTelemetryChatClient` would otherwise honor the ambient `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT`
variable when left unset — and Aspire dev injects that variable as `true` — so the pipeline sets
`EnableSensitiveData` **explicitly** from `CaptureSensitiveContent` (bound from config section `Agent:Telemetry`) so
the environment can never re-enable capture behind the operator's back
(`XE-Local-AI-Engine.AI.Agent/DependencyInjection/AgentServiceCollectionExtensions.cs:130-156`). If an operator does
turn it on, the engine logs a prominent startup warning naming the setting.

**Bottom line:** setting `OTEL_EXPORTER_OTLP_ENDPOINT` exports the shape of activity (spans, token counts, durations,
status) to your collector. It does not, by itself, export prompt or completion text — that needs the separate,
explicitly-logged `Agent:Telemetry:CaptureSensitiveContent` opt-in.

---

## Without a live collector: durable diagnostics still exist

Even with no collector running (the shipped default), the engine separately persists a durable, queryable record of
every agent invocation — this does **not** depend on OpenTelemetry export and survives process restarts:

| Store / endpoint | What it holds | Route | Auth |
|---|---|---|---|
| Run envelopes (`ListRunEnvelopesEndpoint`) | Terminal status, usage/timing counters, correlation id, trace id, schema version — newest-first, paged, optionally scoped to a conversation. No message content. | `GET agents/run-envelopes` | `NodeAuthorizationPolicies.Operator` |
| Agent execution logs (`ListAgentExecutionLogsEndpoint`) | Per-agent adaptive-memory execution telemetry: latency, tokens, success, error class, config hash, link ids — newest-first, paged. No message content. | `GET agents/{agentDefinitionId}/execution-logs` | `NodeAuthorizationPolicies.Operator` |

(`XE-Local-AI-Engine.Client/Endpoints/Agents/V1/ListRunEnvelopesEndpoint.cs`,
`.../ListAgentExecutionLogsEndpoint.cs`; routes from `LocalApiRoutes.Agents.RunEnvelopes` /
`LocalApiRoutes.Agents.ExecutionLogs`.)

Both are read-only, Operator-policy-gated, metadata-only projections of durable rows keyed by `agent_execution_logs`
(see [Data & Persistence](../wiki/08-data-and-persistence.md)) — there is no message content in either store, so
there is nothing to redact, and `FailureCategory`/`ErrorClass` are enum/type names only. That means an operator can
diagnose "did this invocation fail, how long did it take, how many tokens did it use, which run/trace id do I
correlate against" **without ever standing up an OTLP collector** — the live-export path in this runbook is for
richer, real-time trace/metric inspection (e.g. correlating a specific span's timeline), not a prerequisite for basic
incident diagnosis.

See [API & Hubs](../wiki/09-api-and-hubs.md) for the full endpoint inventory and auth model.

---

## Quick reference

```bash
# Turn export ON
export OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317

# Turn export OFF (default desktop/RC state — no exporter registered, zero overhead)
unset OTEL_EXPORTER_OTLP_ENDPOINT

# Durable diagnostics without any collector (Operator-authenticated):
#   GET agents/run-envelopes
#   GET agents/{agentDefinitionId}/execution-logs
```
