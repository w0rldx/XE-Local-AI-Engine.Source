# ADR 0008: External integrations invoke a saved agent through a keyed, loopback-only surface inside `/api/local/v1`

- **Status:** Accepted — by the repository owner (`w0rldx`) on 2026-09-03.
- **Date:** 2026-09-03
- **Scope:** How an external caller invokes a saved agent on this node, and where that surface lives. It changes no
  existing execution path: chat, the platform hub, benchmarks, the scheduler and inbound MCP keep the runners, the
  lease and the approval rules they have today.
- **Authority:** Decided by the maintainer on 2026-09-03, from the external-integrations assessment and its five
  read-only research lanes. The rulings that assessment produced are reproduced in full under **Ruling record**
  below, so this record stands on its own.

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
   | `session-policy` | R4-9 | **historical** — written before R6-1, when a `CallerManaged` trigger resolved to an agent offering a tool outside `ToolCategory.ReadLocal`; no longer produced, and kept only because those rows render it verbatim |

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

## Invariants the coordinator enforces

`IntegrationExecutionCoordinator` is the single consumer of the accept path's queue, the only component that runs an
execution and the only producer of an execution's terminal event. It drives the scheduler's `run-agent` seam —
`IAgentDefinitionResolver` → `ILocalChatRuntimePackageBuilder` → `InvocationExecutionContext.CreatePlain` →
`IInvocationRunner` — with `IsUnattended: true`, and diverges from it in three ways.

**The invocation lease is taken BEFORE the capacity reservation.** The scheduler decides capacity first and holds the
footprint reservation across the whole lease wait; reversing the pair keeps a queued integration run from failing a
concurrent interactive turn's capacity decision while it waits. The two are disposed in reverse acquisition order: a
leaked reservation wrongly rejects later spawns, and a throw from the reservation must not skip the lease and starve
every later run on the node.

**Approval-required tools are offered, never stripped.** The scheduler's strip logs a warning an operator eventually
reads; on an external surface it is silent degradation — the caller gets a plausible `Completed` from an agent that
quietly lost a capability its configuration says it has, and neither the caller nor the response can tell. Left in the
offer, `ToolApprovalCoordinator` sees `IsUnattended`, writes its unattended-unavailable audit row and fails the run by
name (decision 4, ruling R4-5). The runner classifies that refusal as `AgentRuntime` and surfaces its own fixed-shape
reason verbatim, so `ApprovalUnavailableException.UnattendedReasonPrefix` is what separates "this agent needs a
capability it cannot have unattended" from "something broke".

**The lease wait runs under the queue-age deadline**, so `MaxQueueAgeSeconds` bounds something a caller can observe. It
is checked three times: a cheap pre-check before the wait, the `CancelAfter` on the wait itself (ruling R5-2), and a
re-check after the lease is acquired — a lease granted at or past the deadline is a stale run, and the node's only
invocation slot must not be spent on a result nobody reads. The deadline token goes to the lease request and nowhere
else: the run must not inherit a token that expires mid-generation.

### The channel reader dispatches, it does not serialise

Awaiting the run inside the reader loop would hold the next id in the channel for the whole of the current run, and
every deadline control — the queue-age pre-check, the `CancelAfter`, the post-acquisition re-check — plus the sole
writer of `Queued` lives inside that method: a second execution would never reach `Queued`, never start measuring its
queue age, and only time out once the run ahead of it finished, so R5-2's bound would measure the wrong thing.
Dispatching instead lets every admitted execution wait on the invocation lease itself — a `SemaphoreSlim` granting in
wait order — which is what serialises the runs. The live-task count is bounded by the admission cap, because each task
holds a non-terminal row against it.

### An in-flight lease request is always settled

The lease semaphore is the node's ONE invocation slot, shared with chat, regeneration, the scheduler and the benchmark
executors. Disposing the deadline source does not cancel a pending `SemaphoreSlim` wait, so a frame unwinding past the
only `await` that would have taken ownership of the request cancels first, then awaits and disposes it: the wait unwinds
at once with no permit held, and in the race where the permit was granted a moment earlier the lease comes back and is
disposed. Never a detached task — the settle happens before the fault handler runs, or the row terminalises while the
slot is still held.

### The startup sweep reads the whole non-terminal set once

One unpaged read, and one pass over that snapshot. The filter takes a status set, so one read covers every non-terminal
status; an offset over that set cannot be made safe, because the set shrinks (rows this sweep closes, and rows the
already-listening cancel path closes mid-sweep) and grows (that same listener admits while the sweep runs), so an
advancing offset steps past an unseen row that shifted down behind the cursor and an offset that restarts at the top
never terminates under sustained admission churn. Loading it all is affordable by construction:
`IntegrationExecutionStore.AcceptAsync` counts the node-wide non-terminal rows inside its admission transaction and
refuses past `IntegrationOptions.MaxQueuedExecutions` (default 8, hard ceiling 1024). A stale snapshot is harmless: the
terminal transition is a status/version CAS over the non-terminal statuses, so a row that went terminal between the read
and its turn loses the CAS and the sweep writes nothing for it.

### The turn is built from the chat path's own parts

The compaction bound runs before the conversation is read, so the read sees the folded transcript; it projects what the
next turn would replay and folds only when that is over budget, and every no-op outcome (no local model, nothing
foldable) is non-fatal by design. The keep window is the CHAT window, not the work-session floor of two: a work-session
step rebuilds its state block from the database every step, so its transcript beyond the previous step is expendable,
while an integration session has no state block — its transcript IS the session state — so folding to two would delete
the continuation a caller-managed session exists to deliver. It is read from the chat compaction options rather than
written as a literal, so an operator who retunes chat retunes this too. A `CallerManaged` session additionally replays
its completed tool exchanges, so the projection counts them or the bound would measure a transcript smaller than the one
the turn sends; its excerpt cap is the same one the context builder applies, read from the same options.

The context comes from the SAME builder the chat send path uses, so a continued session replays exactly as a
conversation does. Its `selectedPath` is always null, because an integration conversation never regenerates and so has
no variant groups, and `imageContext` and `knowledgeContext` take their defaults: an execution has neither. The turn's
tool parts accumulate through the same primitive chat feeds — both paths observe the same event on the same invocation
filter, so a second accumulation would only be a place to diverge — and only the tool half is fed, because an
integration run streams no reasoning deltas to a persistence pump.

The runtime package differs from the scheduler's in three places: the conversation id is the session's OWNED one (a
throwaway `Guid` would break every by-conversation resolution downstream), the context is the session's history through
the same compaction splice chat uses, and `AllowedTools` is passed through unchanged.

### `emit_output` is unioned in at the `ask_user` seam

It is unioned into the offer AFTER the definition's offer ∩ `AllowedToolNames` intersection and BEFORE the agent is
constructed, the exact seam `ask_user` uses and for the same reason: delivering a result to the caller that started the
run is a property of running an integration execution, not a per-agent permission. Its approval flag is recomposed
through the node policy there, because that is the only place it can be — the offer provider hands out the raw declared
flag and consults no policy, and `InvocationToolResolver` reads the flag the offer carries rather than asking the policy
itself. A node that requires approval for `ReadLocal` therefore tightens this tool too and the run fails CLOSED at the
first call: an operator who declares that `ReadLocal` needs a human is not handed a silent exception on the one surface
reachable from outside the node.

### Orchestrators are refused, twice

V1 runs a saved SINGLE agent. The package carries no `OrchestrationSpec` — the scheduler's `run-agent` shape carries
none either — so `IInvocationRunner` would take its single-agent path and an orchestrator would report `Completed`
having run none of its participants, none of its routing and none of its handoffs. Orchestration is refused rather than
emulated: the offer `emit_output` is unioned into is the ROOT's, and it would have to be pushed across every participant
before an orchestrated integration run could be honest. The `Kind` is judged at save, again before the resolve, and
again on the resolver's own fresh read of the definition, because a definition's `Kind` can change under a trigger
between any two of them.

### The event drain precedes the terminal append

The stream mapper persists `tool.*` rows off a channel. Without an awaited drain one of them can land after the terminal
event, which a reader that stops on the terminal would never see; the drain also latches the handlers shut, so the
terminal append is provably the highest sequence for the execution. It runs under `CancellationToken.None`: past the
run, those rows carry sequences the ring has already published, so abandoning the write would leave a visible event with
no durable row behind it. An incomplete transcript is not a completed run, whatever the model did — the terminal is
still written, but it says `internal-failure` rather than the run's own status.

### The cancel primitive's fixed order

1. **Stamp the durable stop marker, so a restart cannot resurrect the run.** Written ONCE: a row that already carries
   one is answered without a second write, because every marker write bumps the version and a repeated cancel would
   drift it out from under the coordinator's bounded terminal retries until they were exhausted and the row stranded
   non-terminal. The marker write uses `NewStatus` equal to the current status, which makes it a pure marker write
   under the same compare-and-swap, so it cannot resurrect a row that terminalised a moment ago.
2. **Terminalise a row that has not started, in ONE transaction.** Whoever's CAS wins owns the terminal event and the
   one audit row; a loser appends nothing, because the coordinator won the `Queued → Running` race and will produce
   them itself. A CAS lost to an already-TERMINAL row is answered 409 rather than a 202 the caller would poll for a
   cancel that will never arrive.
3. **Signal the registered token on EVERY path**, whether step 2 won or lost and whatever the row reads. Signalling
   only for a running row leaves a queued execution in its lease wait until `MaxQueueAgeSeconds`, because the durable
   marker is only honoured at the post-lease re-check — which is not a cancel a caller can observe.

From the marker write down the cancel uses `CancellationToken.None`, for the same reason the coordinator does: a cancel
that has decided to stop a run must finish stamping and closing it even if the client that asked walks away.

### The prior-outputs block

A caller-managed continuation replays the session's committed `external.output` payloads back to the model as data.
Everything in the block is model-authored text, so all of it — the per-payload labels included — sits inside ONE
untrusted fence, and only the fixed preamble is outside it. The fence uses the SEEDED overload: the block is a stable
prefix of a multi-turn prompt, so a turn that adds no new output composes byte-identically and llama.cpp prompt/KV-cache
prefix reuse survives, while the server-secret seed keeps the closing marker unforgeable from inside a payload.

### `emit_output` is durable before visible

The order inside the handler is: read the execution's counter fresh, refuse over the cap, `Reserve` a sequence, commit
the row with it, and only then `Publish`. An `external.output` frame is an instruction a robot may act on, so a frame
published before its row commits could name a result absent from durable history, from the execution's counters, from
audit inspection and from restart recovery — and terminalising the run afterwards does not un-actuate anything the
caller already did.

Exactly one `Publish` or `Abandon` follows every successful `Reserve`. A reservation holds every reader of that
execution at its sequence, so an unresolved one is not a hole readers tolerate but a stall for the life of the entry.
An ABANDONED hole is legal: `Last-Event-ID` is a watermark, not a dense index.

Its `ToolCategory.ReadLocal` is a documented stretch. The category means "read-only, node-local, side-effect-free",
and the tool does write two rows — its own event and the execution's output counters — and hands bytes to the caller
that started the run. It touches no file, process or network and reaches nothing the caller did not already reach, but
it is not literally side-effect-free. A fifth `IntegrationEgress` category is declined for V1: it would change the
enum, every policy path that switches on it and the node-policy configuration surface, for one tool whose approval flag
the coordinator already recomposes through that same policy.

The security posture: the payload is opaque to the node — never parsed for meaning and never executed — and is bounded
per call and per execution. Its media type is validated at the trust boundary because it is echoed back in a
header-shaped field, not because the node interprets it. Every delivered call increments the audited execution row's
counters, and the acknowledgement handed back to the model never echoes the payload. The one path that flows a payload
back to a model is the later-turn replay of a caller-managed session, and that goes inside an untrusted-content fence.

### The accept path's ordering

Deduplication on `(principal_id, request_id)` runs BEFORE session resolution and before the input checks, inside the
per-session gate, which is still entered first and held through admission. That order is the whole point of
`requestId`: a retry happens exactly when the original 202 was lost, which is exactly when the original execution is
still running on the session it named. Resolving the session first answers such a retry with `SessionBusy` 409, and a
session closed since answers `SessionClosed` — both hiding the execution id the caller was retrying to learn. Nothing
in the dedup needs the session: the fingerprint covers the principal, the trigger name, the requested session id and
the raw body.

### The stream mapper's two halves

`IntegrationStreamEventMapper` is split on purpose. Its PURE half is the static methods: dispatcher arguments plus the
caller's cursor in, a draft or `null` out — no field, no buffer, no database, and therefore testable without a host.
Its PER-RUN half is an instance the coordinator builds inside its run scope and hangs on the one subscription lifetime
it already opens before the lease; that half owns the emit cursor, the debounce clock and the closed latch behind a
single lock, appends to the ring, and pumps `tool.*` rows to the store off a channel. The channel is unbounded and must
never drop: the chat sink may drop, because chat repairs a drop with a reconcile frame, and the ten integration event
types carry no such repair.

### Token usage is recorded like any other turn

The `AgentRunEnvelopeMetadata` is not optional: `SummarizeTokenUsageAsync` reads only kind-1 rows, so omitting it would
make an external surface the one path that can silently burn tokens invisibly, and the kind-3 audit row does not fill
that gap — it carries a terminal status and a latency, not the token columns. The envelope's trailing telemetry members
mirror `NodeChatInvocationPump.TerminalizeAsync`, because an integration run is the same turn measured the same way:
leaving them unset makes this the one surface whose rows carry no warm time, no tool-schema estimate and the LAST
round's tokens where every other row carries the turn's.

## Ruling record

All rounds are reproduced here, because a slice landing months from now needs to know why the accept path, the
identity column, the sequence authority, the event set and the store's record-shaped surface look the way they do.
Round 1 is condensed to one line per ruling; where a later round amended or superseded one, that round's row is the
current rule.

### Round 1

| # | Rule | Owner |
|---|---|---|
| R1-1 | Admin execution endpoints belong to S1 (`ListIntegrationExecutionsEndpoint`, `GetIntegrationExecutionEndpoint`, `CancelIntegrationExecutionEndpoint`); S2 keeps only `GetIntegrationExecutionEventsEndpoint` (`?sinceSeq=`); S3 owns the session endpoints. | S1, S2, S4 |
| R1-2 | Endpoint class names are the public contract — operationId is the camelCase class name minus `Endpoint`. The locked set is the fifteen `*IntegrationTrigger*`, `*IntegrationApiKey*`, `*IntegrationSession*` and `*IntegrationExecution*` endpoint classes in the host; the SDK derives its names from exactly those. | S1–S4 |
| R1-3 | Dedup is key-scoped and byte-exact: `RequestFingerprint = SHA-256(keyPrefix ‖ triggerName ‖ sessionId-or-empty ‖ raw UTF-8 body)`. Same `RequestId` + same fingerprint returns the existing 202 body; same `RequestId` + a different fingerprint or key prefix is a 409 with no details. Retries must resend an identical body; no JSON canonicalisation. **Separators amended by R2-4; the global `RequestId` index is superseded by R4-6.** | S0, S1 |
| R1-4 | Unauthorised = not found: a key whose allowlist excludes the trigger, or a session/execution belonging to another key prefix, gets 404 with the same body as "unknown", never 403, on every external route. **Amended by R2-6 — 403 appears nowhere; a revoked key is 401.** | S1, S2, S3 |
| R1-5 | Admission is one EF transaction, best-effort under SQLite's writer serialisation, with no compensating delete and the two-accepts-read-the-same-count race accepted for V1. **Superseded by R2-2 (one `AcceptAsync` store method) and then by R4-1 (raw `BEGIN IMMEDIATE`, "best-effort" withdrawn).** | S0, S1 |
| R1-6 | `IntegrationOptions` is the complete knob list: `MaxQueuedExecutions` 8 · `MaxRequestBodyBytes` 1 MiB (read at composition time from configuration, not from the options instance) · `MaxSeedBytes` 256 KiB · `EventBufferCapacity` 2048 events · `EventBufferMaxBytes` 4 MiB per execution · `MaxTrackedExecutions` 64 buffers (LRU-evict terminal first, then reject new attach with 503) · `EventBufferTtlAfterTerminal` 10 min · `MaxOutputBytes` 256 KiB per `emit_output` · `MaxOutputBytesPerExecution` 1 MiB · `RateLimitPerMinute` 600 · `ContextBudgetTokens` 12,000 (the R1-9 compaction bound; integration turns must never read `WorkSessionOptions`). **Extended by R4-11.** | S0–S3 |
| R1-7 | Sequence authority is the event buffer: `emit_output` calls `buffer.Append(...)`, which mints the event, and the row is persisted with that `Sequence`. Nothing else mints sequences; `IntegrationExecution.LastSequence` is updated from the buffer's value at persistence time. | S2, S3 |
| R1-8 | SSE frame field order is `event`, `data`, `id` — what `SseFormatter` emits. Irrelevant to the SSE spec, asserted by tests. | S2 |
| R1-9 | Integration-session compaction reuses `WorkSessionStepContextBound.ApplyAsync` with one optional `keepVerbatimExchanges` parameter (default 2, so the work-session call site is byte-identical); integration turns pass the chat window, 8. | S3 |
| R1-10 | The rate-limit 429 test lives in the existing `RateLimitPolicyTests`; the earlier "no test possible" claim is withdrawn. | S1, S2 |
| R1-11 | The approval warning is fail-closed: `requiresApproval = catalog.effectiveRequiresApproval \|\| agent.toolApprovals[name] === true`, and a tool missing from the catalog counts as requiring approval. The executions page polls unconditionally at the scheduler's cadence; `/integrations` gets an index route redirecting to triggers. | S4 |
| R1-12 | S0 owns the doc and test updates it invalidates: `docs/wiki/08-data-and-persistence.md` (encrypted-column table, entity inventory, migration timeline, `record_kind` sentence), the verbatim SQL copies in `AddConversationListIndexMigrationTests`, and `KeyHash` registered as a **required** encrypted property. | S0 |
| R1-13 | S1's handoff to S3 must not mention a package marker; the coordinator unions `GetIntegrationOutputOffer()` instead. | S1 |
| R1-14 | S2 fixes in-slice: capture the wakeup task inside the snapshot lock (`RunContinuationsAsynchronously`); a gap detected mid-stream ends the stream with a clean close — the writer emits nothing further and completes the response, and the caller re-attaches with `Last-Event-ID` to receive 410 and fall back to polling. No new event type; 410 only on attach. | S2 |
| R1-15 | Accept ordering: `INodeChatPersistenceService` is a singleton with its own scope per operation, so the seed cannot join the execution transaction — hence (1) create the owned conversation, (2) persist the seed, (3) one `SaveChanges` for session + execution + `execution.accepted`. A failure between them may orphan a `kind=integration` conversation, accepted for V1. **Superseded by R4-1, which commits first and mints the conversation after, so no orphan can exist.** | S0, S1 |

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

### Round 6

| # | Rule | Owner |
|---|---|---|
| R6-1 | Caller-managed sessions persist their tool parts through the chat primitive (`NodeChatPartAccumulator`) and replay them as function call/result content on continuation, so a continued turn can tell an action it performed from prose describing one. The `ReadLocal`-only restriction on caller-managed triggers is withdrawn at save, at accept and at run. **Supersedes R4-9(a)**; R4-9(b)'s prior-outputs framing stays, and R2-12 is amended to "no persistence of their OWN". | S6 |
