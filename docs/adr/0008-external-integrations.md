# ADR 0008: External integrations invoke a saved agent through a keyed, loopback-only surface inside `/api/local/v1`

- **Status:** Accepted — by the repository owner (`w0rldx`) on 2026-09-03.
- **Date:** 2026-09-03
- **Scope:** How an external caller invokes a saved agent on this node, and where that surface lives. It changes no
  existing execution path: chat, the platform hub, benchmarks, the scheduler and inbound MCP keep the runners, the
  lease and the approval rules they have today.
- **Authority:** Decided by the maintainer on 2026-09-03, from the external-integrations assessment
  (`Plans/external-integrations-2026-09-03/REPORT.md`) and its five read-only research lanes.

## Context

Three facts about this repository shape every decision below. Each was re-opened in code before it was relied on.

**The process is loopback-only by a bind guard.** `LoopbackBindGuard` kills the process on a routable bind unless
`Security:AllowNonLoopbackBind` is set, and `LocalApiSecurityMiddleware` guards `/api/local/v1` by peer, `Host` and
`Origin`. So "expose it on the LAN later" is an explicit opt-out of the node's security posture, not a scope knob that
a future slice turns. Inbound MCP and the local model proxy are hand-mapped **inside** `/api/local/v1` precisely so
they inherit that gate.

**Every `IInvocationRunner` caller already shares one slot.** Chat, the platform hub, benchmarks and the scheduler all
serialise through a single `SemaphoreSlim(1,1)` lease: `RunSavedAgentHandler.cs:321` calls
`ReportInvocationAssignedAsync`, which is the acquisition (`WorkerEventDispatcher.Inbound.cs:143-152`). Lane 01
originally reported the scheduler as bypassing it; `REPORT.md` §0.1 records the correction. Integration executions
therefore do not need a new admission primitive — they need to queue behind the one that exists, and to reject rather
than accumulate when that queue is full.

**XE already ships an audited auto-approve path for an external principal.** An MCP *Agentic*-scope key wraps every
`ApprovalRequiredAIFunction` in `AutoApprovedFunction` with an audit row (`SubAgentSpawnService.cs:463-467`,
`McpAgenticToolAdapter.cs:19-35`), while `IsUnattended` runs fail closed before any other check
(`ToolApprovalCoordinator.cs:170-180`). Two precedents exist and they disagree, which is why the unattended posture
below is an explicit ruling rather than a default.

## Decision

1. **The external surface lives inside `/api/local/v1`, hand-mapped beside MapMcp, with its own key scheme (D1).**
   The route family is `integration-api/…`, mapped in `Program.cs` next to the MCP and model-proxy maps rather than
   through FastEndpoints, so it inherits `LocalApiSecurityMiddleware` unchanged. It is kept off the OpenAPI document —
   it is not part of the operator SDK — and it authenticates with its own `xeint_` bearer keys under the
   `IntegrationApiKey` scheme, never with an operator JWT. The reuse this buys is the whole security gate: peer check,
   `Host`/`Origin` check, rate-limit middleware and the existing hand-map precedent.

2. **A trigger targets a saved agent, and nothing else, in V1 (D2).** `IntegrationTargetKind` has exactly one member,
   `Agent`. An agent definition that resolves to an orchestration still runs, because that is what
   `IInvocationRunner` already does with one; Dev Workflows and Preview Workflows are out of scope. The reuse is
   `RunSavedAgentHandler`'s shape verbatim.

3. **Admission takes the existing lease and rejects before acceptance, inside one `BEGIN IMMEDIATE` transaction
   (D3, ruling R4-1), bounded per node and per principal (ruling R4-8).** `IIntegrationExecutionStore.AcceptAsync`
   opens its own `SqliteConnection`, begins an immediate transaction, re-reads the key row for revocation, counts the
   node's active executions and then the principal's, and only then inserts the session, the execution and the
   `execution.accepted` event. A full queue is answered `503` with `Retry-After: 5` and writes nothing. The reuse is
   `McpAgentRunStore.AdmitAsync`'s raw-ADO shape; the reason it is raw rather than EF is that `BEGIN IMMEDIATE` takes
   SQLite's write lock at statement one, so a concurrent accept blocks instead of reading the same count and admitting
   alongside it.

   ### Execution transitions (ruling R3-2)

   | From | To | When |
   |---|---|---|
   | `Accepted` | `Queued` | the execution waits for the node's single invocation lease |
   | `Accepted`, `Queued` | `Running` | the lease is held and the runner is about to be called |
   | `Running` | `Completed`, `Failed`, `Cancelled` | the run reported a terminal state |
   | `Accepted`, `Queued` | `Cancelled` | cancelled before the run started |
   | `Accepted`, `Queued` | `Failed` | rejected before the run started, with a `FailureCategory` from the list below |

   No other move is legal, and `Running` is never re-entered. Every move into `Completed`, `Failed` or `Cancelled` is
   made by `TryTerminalizeAsync` (ruling R5-4), which writes the status and the matching terminal event in one
   transaction; `UpdateStatusAsync` makes the non-terminal moves and nothing else.

   `FailureCategory` is a **closed** vocabulary of exactly ten values: `trigger-unavailable`, `cloud-model-rejected`,
   `capacity-rejected`, `restart`, `queue-full`, `shutdown`, `internal-failure`, plus three added in round 4:

   | Category | Ruling | Raised when |
   |---|---|---|
   | `approval-required` | R4-5 | an unattended run invoked an approval-gated tool, which cannot be answered; `Running → Failed` |
   | `queue-timeout` | R4-8 | a still-`Queued` execution exceeded `MaxQueueAgeSeconds` before the lease came free; `Queued → Failed` |
   | `session-policy` | R4-9 | a `CallerManaged` trigger resolved to an agent offering a tool outside `ToolCategory.ReadLocal`; rejected before the run started |

   A category outside those ten is a bug rather than an extension point. The column is content-free by contract and the
   UI renders the value directly.

4. **An integration invocation is unattended and fails closed (D4).** The runtime package carries
   `IsUnattended: true`; there is no auto-approve, and the MCP Agentic precedent is explicitly declined here. Ruling
   R4-5 makes "fail closed" mean an *audited* failure: approval-gated tools are offered **wrapped**, not stripped, so
   an unattended invocation of one raises `ApprovalUnavailableException` and terminalises the execution `Failed` with
   `FailureCategory = "approval-required"`, instead of the agent quietly finishing without the capability. Tool tiers
   are deferred to S5. The trigger editor's preflight warning is the operator-facing half of the same rule.

5. **A session is an `IntegrationSession` that owns a `NodeConversation`, discriminated by a new `Kind` column (D5).**
   `NodeConversationKind` is `chat` / `work-session` / `integration`; the two conversation **list** queries filter
   `kind = 'chat'`, by-id reads stay unfiltered, and the migration backfills existing work-session-owned conversations
   by joining `agent_work_sessions.conversation_id`. The reuse is `AgentWorkSession`'s shape minus tasks, findings and
   checkpoints, and the chat compaction path for session continuation.

6. **A POST is answered 202 or streamed as SSE by `Accept`, with resumable GET events, poll and cancel (D6).** The
   event envelope is `IntegrationStreamEvent(Type, Sequence, ExecutionId, SessionId, OccurredAtUtc, ContentType,
   Payload)`; `Sequence` is monotonic per execution and starts at 1 with `execution.accepted`. `Last-Event-ID` replays
   from an in-memory buffer, and a `410` sends the caller to the persisted-event poll. The reuse is
   `LocalModelProxyForwarder`'s streaming mechanics — `DisableBuffering()`, an idle watchdog for keepalive, and a
   caller abort that ends forwarding but never cancels the run.

7. **One built-in tool, `emit_output`, is the typed channel from the agent back to the caller (D7).** It is
   `ToolCategory.ReadLocal` with `RequiresApproval = false`, held out of every ordinary tool projection and unioned
   into `AllowedTools` by the integration coordinator alone. Each call produces one `external.output` event whose
   payload is forwarded verbatim, bounded by `MaxOutputBytes` per call and `MaxOutputBytesPerExecution` in aggregate.

8. **The external family carries a 1 MiB request-body limit (R1).** It is applied while the route is built, from
   configuration, and enforced twice: by setting `IHttpMaxRequestBodySizeFeature.MaxRequestBodySize` before the body is
   read, and by a bounded reader that answers `413` on overflow. `Content-Length` is never trusted alone.

9. **The work-session chat-list leak is a separate ticket (R2).** This ADR's `Kind` discriminator closes the leak for
   the chat list specifically; whether any other surface still exposes those conversations is tracked elsewhere.

10. **Ownership and request uniqueness key on a stable `PrincipalId`, never on a key instance (ruling R4-6).** A key is
    a credential, not an identity: rotating one, issuing a second, or splitting an ingest key from a read key must not
    strand a principal's sessions and in-flight executions. `IntegrationApiKey`, `IntegrationSession` and
    `IntegrationExecution` all carry `principal_id`; the request-uniqueness index is `(principal_id, request_id)`, so
    one integrator cannot preclaim another's request id; and every "is this yours?" question compares `PrincipalId`.
    `KeyPrefix` stays on the execution row and on the audit row as **audit metadata only** — it answers "which
    credential sent this", never "who owns this" — and nothing is looked up by it.

11. **V1 is explicitly loopback-only (ruling R4-10).** `LocalApiSecurityMiddleware` rejects every non-loopback peer and
    declares proxies unsupported, and `AllowNonLoopbackBind` only disables the startup bind guard rather than admitting
    a LAN peer, so there is no safe incremental LAN path to take: a direct LAN caller still gets 403. Remote exposure is
    therefore a **separate security architecture** — its own listener or route branch, its own TLS or trusted-proxy
    contract, its own client identity and its own network policy — and it is out of scope for V1 rather than a knob
    left unturned. A same-host tunnel is **unsupported** for integrations: it presents as loopback and so proxies the
    entire `/api/local/v1` surface, including unrelated and anonymous setup routes, which is a wider grant than any
    integration needs.

## Reuse decisions

| Copied rather than written | From |
|---|---|
| The headless executor: resolver → `LocalChatRuntimePackageBuilder` → `CreatePlain` → lease → `IInvocationRunner` | `RunSavedAgentHandler` |
| Idempotent accept: caller-supplied request id plus a request fingerprint, replay returns the existing row | `McpAgentRun.RequestId` / `RequestFingerprint` |
| The key scheme: 256-bit CSPRNG, digest-only storage, constant-time compare, prefix display, show-once | `McpServerApiKey` / `McpServerApiKeyService` |
| The SSE writer: `DisableBuffering()`, idle watchdog, caller-abort separated from upstream idle | `LocalModelProxyForwarder` |
| The event table: `Guid` id, monotonic `Sequence`, small encrypted `detail_json`, cascade FK | `DevWorkflowRunEvent` |
| Content-free audit rows sharing one table behind a discriminator | `AgentExecutionLog.RecordKind` |
| Session continuation through the chat compaction splice | `CompactionContextResolver` |
| The hard-bounded admission transaction: raw `SqliteConnection` + `BEGIN IMMEDIATE` | `McpAgentRunStore.AdmitAsync` |

## Consequences

**What this forecloses.** There are no concurrent integration executions in V1: one lease serialises them, and
`InvocationLifecycleTracker.cs:55` holds a single CTS. There is no resume of an in-flight generation across a restart —
the coordinator terminalises orphaned rows `Failed` / `restart` at startup. There is no IP allowlist, because there is
no non-loopback peer to allow.

**Acceptance is durable first (ruling R4-1).** One raw-connection `BEGIN IMMEDIATE` transaction counts the node's and
the principal's active executions, re-reads the key row for revocation, and inserts the session, the execution and the
`execution.accepted` event, then commits. **Only after that commit** does the caller create the owned
`NodeConversation` — at the `ConversationId` the session row already carries, minted before the transaction — and write
the seed message, because `INodeChatPersistenceService` is a singleton that opens its own scope per operation
(`NodeChatPersistenceWriter.cs:48-70`) and cannot join another transaction. A failure at either post-commit step
terminalises the execution `Failed` / `internal-failure` through the coordinator's ordinary path, which already treats a
missing conversation as `internal-failure`.

What that **removes** is worth stating plainly: an orphan `kind = integration` conversation with no owning session can
no longer be created, so the feature carries no orphan sweep, no `DeleteOrphanConversationsAsync` and no compensating
delete — and no honest-limits paragraph about `ChatRetentionOptions.Enabled` defaulting to `false` is needed for this
feature. The residue that remains is the mirror image and is smaller: a session row can point at a `conversation_id`
whose conversation was never created. Nothing reads it — the chat list filters on `kind`, and a purge keys on a
conversation row that does not exist — and a later continuation of that session fails the same `internal-failure` way,
deterministically. That is the accepted residue.

**A terminal status and its terminal event are written together or not at all (ruling R5-4).**
`TryTerminalizeAsync` performs the status CAS, the terminal `IntegrationExecutionEvent` insert at the reserved
sequence, the `LastSequence` and session watermarks, `EndedAtUtc` and the failure fields in one `SaveChanges`, and the
caller publishes to the stream only after it returns `true`. The split write it replaces — `UpdateStatusAsync`
committing the status first and the terminal event being appended afterwards — could be interrupted between the two by
a crash or a SQLite failure and leave a terminal row with **no** terminal event; the startup sweep only ever looks at
non-terminal rows, so that row would never be repaired, and a caller polling the persisted events would never see the
run end. The narrowing that buys it: `UpdateStatusAsync` no longer moves an execution to a terminal status at all, and
`FailNonTerminalAsync` — a bulk `UPDATE` that by construction writes no events — is gone, so there is exactly **one**
way for a run to end.

## Ruling record

Round-1 rulings (R1-1 … R1-15) stay in `Plans/external-integrations-2026-09-03/10-reconciliation.md` and are named
here by reference. The later rounds are reproduced because a slice landing months from now needs to know why the accept
path, the identity column, the sequence authority, the event set and the store's record-shaped surface look the way
they do.

### Round 2

| # | Rule | Owner |
|---|---|---|
| R2-1 | The event buffer moves to S1 and is the sole minter of `Sequence`. | S1 |
| R2-2 | Atomic accept is one store method, `AcceptAsync`; `CountActiveAsync` is dropped. | S0 |
| R2-3 | `IntegrationExecution.StopRequestedAtUtc` is the durable cancel marker. | S0 |
| R2-4 | Fingerprint separators are `0x1E` between the bound fields. | S1 |
| R2-5 | The queue channel is `Channel<Guid>` bounded with `FullMode.Wait`; no `DropWrite`. | S1 |
| R2-6 | A revoked key answers 401, not 403 — **superseded by R4-7**, which removes the accepted TOCTOU behind it. | S1 |
| R2-7 | The body limit is enforced by the Kestrel feature *and* a bounded reader. | S1 |
| R2-8 | State machine as an arrow — **superseded by R3-2's full table** above. | S1 |
| R2-9 | The persisted event set is nine types; the coordinator persists them; `AppendEventAsync` takes the caller's sequence. | S0–S3 |
| R2-10 | A 410 is decided before headers are written. | S2 |
| R2-11 | No rate-limit fallback: `.RequireRateLimiting` is never removed from a route. | S2 |
| R2-12 | Caller-managed sessions add no persistence of their own; they reuse the accept path. | S3 |
| R2-13 | `WorkSessionStepContextBound.ApplyAsync` gains a trailing optional `keepVerbatimExchanges`. | S3 |
| R2-14 | `emit_output` composes with the approval policy — **its byte-accounting unit is superseded by R3-5** (plaintext). | S3 |
| R2-15 | S4's filters and paging mirror the server's query parameters exactly. | S4 |
| R2-16 | The solution file is `XE-Local-AI-Engine.slnx` in every gate command. | all |
| R2-17 | R1-15 is cited in this ADR and this ruling table lives here. | S0, S1 |
| R2-18 | An orphan sweep with `DeleteOrphanConversationsAsync` — **superseded by R4-1**, which makes an orphan impossible and deletes the sweep. | S1 |
| R2-19 | `emit_output` persists through `AppendOutputEventAsync`. | S3 |
| R2-20 | The seed message id is the execution id. | S1, S3 |

### Round 3

| # | Rule | Owner |
|---|---|---|
| R3-1 | Restart recovery runs through the buffer: `TryCreate(id, LastSequence)` then a reserved terminal event. | S1 |
| R3-2 | The full transition table and the closed `FailureCategory` vocabulary above; **supersedes R2-8**. | S0–S4 |
| R3-3 | Terminal events have one producer, the coordinator; the mapper emits only assistant and tool events. | S1, S2 |
| R3-4 | The untracked sentinel is 0 and the buffer exposes `IsTracked`; the writer prechecks before any header. | S1, S2 |
| R3-5 | `IntegrationExecution.OutputBytes` holds **plaintext** UTF-8 bytes; **supersedes R2-14's unit**. | S0, S3 |
| R3-6 | A per-session `SemaphoreSlim(1,1)` guards session resolution through accept. | S3 |
| R3-7 | Store orderings are pinned (`LastActivityUtc DESC, Id DESC`; `ReceivedAtUtc DESC, Id DESC`); `TouchAsync` does not exist. | S0, S3 |
| R3-8 | Entities stay `internal`; store interfaces are `public` and speak only in records; **supersedes R2-2's parameter list**. | S0–S3 |
| R3-9 | `UpdateStatusAsync(IntegrationExecutionStatusUpdate, ct)` is the status CAS — **narrowed by R5-4** to non-terminal moves. | S0, S1 |
| R3-10 | S1 mechanics: build the accepted event before `AcceptAsync`; no orphan-sweep grace window. | S1 |
| R3-11 | S2 mechanics: one pending `MoveNextAsync` raced with a 15 s keepalive delay; stream count bounded. | S2 |
| R3-12 | S4 sends filters and ordering server-side and never re-sorts. | S4 |
| R3-13 | Plans close by internal closure files; no further Codex round. | all |
| R3-14 | Seam closures: writer precheck wording, positional status record, `AppendOutputEventAsync` shape, the four session methods S3 adds, drain failure terminalises. | S0–S3 |

### Round 4

| # | Rule | Owner |
|---|---|---|
| R4-1 | `AcceptAsync` is a raw `BEGIN IMMEDIATE` transaction and the accept order inverts; **supersedes R1-15 and deletes R2-18**. | S0, S1 |
| R4-2 | Durable-before-visible: `Reserve` → commit → `Publish` for `external.output` and the terminal events. | S1–S3 |
| R4-3 | The in-memory output tally is deleted; `OutputBytes` is the only authority. | S3 |
| R4-4 | A persisted-events external route lets a 410'd caller recover committed output. | S2 |
| R4-5 | Approval-gated tools are offered wrapped, not stripped; **supersedes the round-3 strip reading of D4**. | S1 |
| R4-6 | `PrincipalId` is the identity; `(principal_id, request_id)` is unique; **supersedes R1-3's global index**. | S0–S4 |
| R4-7 | The key row is re-read inside the accept transaction; **supersedes R2-6's accepted TOCTOU**. | S1 |
| R4-8 | Per-principal admission cap and a bounded queue age; the lease is taken before the capacity reservation. | S0, S1 |
| R4-9 | `CallerManaged` triggers are rejected for non-`ReadLocal` tools; prior outputs are framed into the context. | S1, S3 |
| R4-10 | V1 is explicitly loopback-only (Decision §11). | S0 |
| R4-11 | The option list is complete at fourteen members — **extended to fifteen by R5-7**. | S0 |
| R4-12 | The buffer API — **superseded by R5-6**, which adds `Abandon`. | S1–S3 |
| R4-13 | The claim-partitioned limiter is withdrawn; `NodeChatCreateConversationRequest` gains a caller-supplied id; `AcceptAsync`'s final signature. | S0, S1 |

### Round 5 (final)

| # | Rule | Owner |
|---|---|---|
| R5-1 | Every external route authorises on principal **and** the current key's trigger allowlist; either failure is the same masked 404. | S1–S3 |
| R5-2 | The lease wait is bounded by `MaxQueueAgeSeconds` through a linked CTS. | S1 |
| R5-3 | The buffer tracks pending reservations and gains `Abandon`; a reader never yields past the lowest pending one. | S1–S3 |
| R5-4 | `TryTerminalizeAsync` is the only terminal transition; `UpdateStatusAsync` is narrowed and `FailNonTerminalAsync` is retired. **Supersedes R3-9's scope.** | S0, S1 |
| R5-5 | Two rate-limit layers: a coarse per-IP route ceiling and a per-principal limiter inside the handlers. | S1 |
| R5-6 | The final buffer API, including `Abandon` and `LowestPendingReservation`. | S1–S3 |
| R5-7 | `IpRateLimitPerMinute` joins the options class, making it fifteen members. | S0 |
