# Streaming budget redesign — delta-only protocol, O(delta) persistence, bounded streams, disconnect grace

> **Status: implemented — historical design record.** This plan shipped on 2026-08-07 in commit `606dba14`
> ("feat(chat): streaming budget — delta-only protocol, O(delta) persistence, bounded channels, disconnect grace").
> D1/D2 are `ChatStreamEventTypes.AssistantDelta` and `ChatStreamEventTypes.AssistantSnapshot` in
> `NodeChatStreamDtos.cs`; D3's growth-triggered flush is `PartialFlushPolicy`; the budget knobs are
> `ChatStreamBudgetOptions`; D6's grace deadline is `WorkerNodeOptions.DetachedGraceSeconds` (default 300)
> with `DetachedInvocationReaper`. The body below is kept unchanged as the design record — read it for the
> rationale, not as outstanding work.

- **Status:** Implemented (2026-08-07, `606dba14`) — see the banner above. Originally recorded as "Design, ready to implement. No code in this document has been written."
- **Date:** 2026-08-07
- **Source revision:** `c5a03752` (worktree branch `fix/inference-open-findings-2`)
- **Closes:** [v2 audit](../audits/2026-08-07-ai-inference-stack-performance-audit-v2.md) §3.3 (quadratic persistence), §3.4 (full-snapshot deltas), §2 correction 1 (watchdog re-arm with nobody attached); [v1 audit](../audits/2026-08-07-ai-inference-stack-performance-audit.md) P0 (bounded channels, subscriber-aware disconnect policy). Also folds in the v2 side-find: `NodeChatRegenerationService` never subscribes `ApprovalRequestedChanged`.
- **Not in scope:** §3.1 (residency cap), §3.2 (`--cache-ram`), §3.5, §3.6. Those are llama.cpp-side and share no files with this work.

## 0. Decisions at a glance

| # | Decision | Rationale |
|---|---|---|
| D1 | Live `assistant-delta` carries **only** the delta plus a character offset. Full text moves to a new `assistant-snapshot` event and stays on terminals. | Removes the O(n²) wire/serialization amplifier of §3.4. |
| D2 | The legacy `content`/`reasoning` fields are **deleted from the delta path outright** — no dual-read window. The desktop app ships frontend and backend from one build, so there is no version skew to tolerate. | Keeping both is what makes the amplifier survive the change. |
| D3 | Persistence uses a **growth-triggered flush cadence** — flush when the message has grown by ≥20% since the last flush, or a 2 s ceiling elapsed. **No append-only journal, and no read-free flush path.** | Turns per-turn write volume from quadratic to ~6×n with one predicate, no schema migration, no second source of truth for message content, no new AEAD surface, no change to `NodeChatMessageCommands` at all. §2.3 rejects the journal; §2.4 drops the read-free path now that a sibling lane is fixing the read itself. |
| D4 | The SSE emit cadence is **decoupled** from the persistence cadence (25 Hz emit; growth-triggered persist). | Without this, D3 would make the UI update every two seconds. |
| D5 | Bounded stream queue with a **whole-stream reconcile** on overflow, not per-event dropping. Deltas are coalesced **at the producer** (the pump), never in the queue. | A per-kind drop policy risks silently losing an approval; one repair path (`ResumeMessage`) already exists and serves overflow, offset gaps, and oversized replay alike. |
| D6 | A detached run keeps running under a **grace deadline** (`DetachedGraceSeconds`, default 300, `0` disables). Human-wait re-arm keeps today's semantics **only while attached**. | Closes §2 correction 1: a disconnected park currently buys `MaxPendingToolCallAge + InvocationTimeout` ≈ 15 min *per park*, holding the llama-server lease. |

§7 records the four settled decisions; nothing is left open.

---

## 1. Delta-only live protocol (§3.4)

### 1.1 Today

`ChatStreamEventMapper.MessageEvent` populates `Content` and `Reasoning` from the persisted row on **every** event it builds, including `AssistantDelta`. `InvocationResumeRegistry.ToEvent` does the same from `InvocationState.StreamedContent`. The client cannot ignore them: `NodeChatStreamState.applyNodeChatStreamEvent` computes `content` as `event.content ?? existing + delta` — the snapshot is *preferred*. So the full accumulated text is serialized, sent, and re-assigned on every frame.

### 1.2 Wire contract after the change

Two new fields on `ChatStreamEvent` (`NodeChatStreamDtos.cs`), both trailing-optional:

```
long? ContentOffset      // character index in the accumulated content at which Delta begins
long? ReasoningOffset    // character index in the accumulated reasoning at which ReasoningDelta begins
```

Offsets are **.NET `string` indices**, i.e. UTF-16 code units. This is deliberate: `NodeChatPumpCursor` and `InvocationResumeRegistry.ResumeCoreAsync` already slice with `state.StreamedContent[cursor.Content.Length..]`, and JavaScript `String.length` is the same code-unit space, so client and server agree on the index without any conversion. A delta may split a surrogate pair — it already can today, and rendering concatenates before display, so nothing changes.

One new event type on `ChatStreamEventTypes`:

```
AssistantSnapshot = "assistant-snapshot"
```

Per-event payload rules:

| Event type | `delta` / `reasoningDelta` | `contentOffset` / `reasoningOffset` | `content` / `reasoning` |
|---|---|---|---|
| `assistant-delta` | **set** | **set** | **null — never populated** |
| `assistant-snapshot` (new) | null | set (= length of the carried text) | **set** (authoritative replacement) |
| `assistant-completed` / `-cancelled` / `-failed` / `-interrupted` | null | null | set (one frame per turn; cost is irrelevant) |
| `assistant-pending` / `-queued` / `-streaming` / `-phase` / `-notice` | null | null | null |
| `user-message-persisted` | null | null | set (the user's own text, once) |
| `tool-call-*`, `approval-requested`, `question-requested` | null | null | null |

`assistant-snapshot` is emitted in exactly three situations:

1. **Resume replay.** `InvocationResumeRegistry.ResumeCoreAsync` currently opens with `ToEvent(ChatStreamEventTypes.AssistantDelta, snapshot, …)` — a delta-typed event carrying full content and no delta. That becomes an `assistant-snapshot`. Its own comment already describes it as "a pure SNAPSHOT event"; the type name now matches the contract.
2. **Gap repair**, when the client detects a discontinuity (§1.4).
3. **Queue overflow**, when the sink could not enqueue and the stream must resynchronize (§3.4).

Cases 2 and 3 both reach the client through the *existing* `ResumeMessage` hub call, which already emits case 1 as its first frame. So there is no new server-push mechanism: gap repair and overflow repair are both "tear down the subscription, call `ResumeMessage`", which the adapter already does on transport error.

### 1.3 Backend changes

**`ChatStreamEventMapper.MessageEvent`** — split into two methods so the "which fields does this event carry" question is answered by the type system rather than by a caller's discipline:

- `MessageEvent(type, correlation, message, timestampMs, sequence, inputTokens, outputTokens, totalTokens, reasoningTokens)` — lifecycle and terminal events. Keeps populating `Content`/`Reasoning` from the row. The `delta`/`reasoningDelta` parameters are **removed** from this overload.
- `DeltaEvent(correlation, timestampMs, sequence, contentDelta, reasoningDelta, contentOffset, reasoningOffset)` — new. Builds an `assistant-delta` with `Status = NodeChatMessageStatusValues.Streaming` and no `Content`/`Reasoning`/`Model`/token fields. Note it takes **no `NodeChatPersistedMessageDto`**: a delta frame no longer needs a database row at all. That is what makes D4 possible.
- `SnapshotEvent(conversationId, messageId, requestId, content, reasoning, timestampMs, sequence)` — new. Used by the resume registry.

**`ChatInvocationStatePump.PumpAsync`** — `PersistPartialAsync` today does one thing: persist, then emit from the persisted row. Split it into `EmitDeltaAsync` (no I/O) and `PersistPartialAsync` (no SSE), each on its own cadence. Two cursors, both plain `NodeChatPumpCursor` values:

- `emitCursor` — how much has been *sent* to the client. Advances on every emitted frame.
- `persistCursor` — how much has been *written*. The existing `cursor`, unchanged in meaning.

`EmitDeltaAsync(latest)` computes `contentDelta = latest.StreamedContent[emitCursor.Content.Length..]` and `contentOffset = emitCursor.Content.Length`, same for reasoning, calls `parts.AppendReasoning(reasoningDelta, deltaSequence)` (unchanged — the ordered `parts[]` accumulator must still see every reasoning delta), writes `ChatStreamEventMapper.DeltaEvent(...)`, and advances `emitCursor`. It is gated by an emit debounce of `EmitDebounceMs` (default 40 ms → ≤25 frames/s), with the same two exceptions the persistence path already has: the first delta and any terminal emit immediately.

The terminal path is unchanged in shape but must first flush any un-emitted tail: before writing the terminal event, call `EmitDeltaAsync(latest)` unconditionally so the client's accumulated text equals the terminal's `content` and the two agree. (Belt and braces: the terminal carries the full text anyway, so a client that missed the tail still converges. The explicit tail-emit exists so the *common* path never needs the terminal's snapshot to correct it.)

**`InvocationResumeRegistry.ResumeCoreAsync`** — the opening replay becomes `SnapshotEvent`; the live loop's `ToEvent(ChatStreamEventTypes.AssistantDelta, state, sequence++, contentDelta, reasoningDelta)` becomes a `DeltaEvent` carrying `lastContent.Length` / `lastReasoning.Length` as the offsets (which the method already computes as slice bases). The terminal emits keep `ToEvent` and its full content. `ToEvent` itself must **stop** populating `StreamedContent`/`StreamedThinkingContent` for the delta case — after this change `ToEvent` is only used for terminals, so it keeps them unconditionally and simply loses its `delta`/`reasoningDelta` parameters.

### 1.4 Frontend changes

**`NodeChatStreamState.ts`** — the load-bearing line is the snapshot-preferred merge in `applyNodeChatStreamEvent`:

```
const content = event.content ?? `${existing?.content ?? ""}${event.delta ?? ""}`;
```

Replace with a type-driven merge. Concretely, hoist a small helper above the assistant-message construction:

- If the event type is `assistantDelta`: `content = (existing?.content ?? "") + (event.delta ?? "")`. **Never** read `event.content` on this branch.
- If the event type is `assistantSnapshot` or a terminal: `content = event.content ?? existing?.content ?? ""`.
- Otherwise: `content = existing?.content ?? ""`.

Reasoning follows the identical three-way rule against `event.reasoningDelta` / `event.reasoning`.

`nextReasoningParts` keeps both branches: the `event.reasoningDelta` branch serves live deltas, and the `else if (reasoning && reasoningSegments.length === 0)` reseed branch now serves `assistant-snapshot` and terminals (its comment already says "terminal/resume rehydrate" — that stays accurate).

`nodeChatStreamEventTypes` gains `assistantSnapshot: "assistant-snapshot"`. `assistant-snapshot` must **not** be added to `terminalStatusForEvent` and must not be treated as terminal anywhere; it is a mid-stream state replacement. Its `status` on the wire is `streaming`, so `normalizeStatus` already resolves it correctly.

`mergeToolEntry` and the tool/notice/prompt paths are untouched by this lane — they never carried `content` in the first place.

**`NodeChatStreamTypes.ts`** — the hand-written `NodeChatStreamEventDto` gains `contentOffset?: number` and `reasoningOffset?: number`. There is no OpenAPI regeneration for the stream DTO (the file's own header records that the SignalR stream has no generated equivalent), so this is a plain hand edit.

**Gap detection and repair — `NodeChatAdapter.signalRStream`.** The adapter is the right home: it already owns subscription lifecycle and the resume machinery, and it can detect a gap from offsets alone without holding any text.

Add two counters alongside `lastPushedSequence`:

```
let nextContentOffset: number | undefined;
let nextReasoningOffset: number | undefined;
```

In `pushEvent`, before forwarding:

- On `assistant-snapshot` or a terminal: reset both to the length of the carried `content` / `reasoning` (treat absent as `0`).
- On `assistant-delta`: if `nextContentOffset !== undefined && event.contentOffset !== nextContentOffset` (same test for reasoning), this is a gap or an overlap → **repair**. Otherwise advance `nextContentOffset = event.contentOffset + (event.delta?.length ?? 0)` and forward the event.

`repair()` is three lines and reuses what is already there:

```
activeSubscription?.dispose();
activeSubscription = undefined;
subscribe("ResumeMessage", [invocationId], true);
```

The existing `resumeSequenceBase` rebase makes the resumed stream's restarted numbering contiguous past `lastPushedSequence`, and the resume stream's first frame is the `assistant-snapshot` that resets the offsets and replaces the client's text. If `invocationId` is not yet latched, repair is impossible — drop the event and let the turn's terminal (which carries the full text) converge the state; log to the console breadcrumb trail.

An **overlap** (`event.contentOffset < nextContentOffset`) is the benign duplicate-replay case and is handled by the same repair path rather than by a partial-slice heuristic. It should not occur once sequence dedupe in `guardNodeChatStream` is doing its job; treating it as a gap keeps one code path.

**`NodeChatStreamGuard.ts`** — no change is required by this lane. It orders by `sequence`, and the server still mints one sequence per emitted frame with no skips (deltas are coalesced *before* a sequence is minted — see §3.2). This is a hard constraint on Lane B: **the sink must never drop an event that has already consumed a sequence number without triggering a reconcile**, because the guard would then stall on the missing sequence until end-of-stream.

---

## 2. O(delta) persistence (§3.3)

### 2.1 The chosen mechanism: growth-triggered flush cadence

**One change**, in the pump's flush predicate. Nothing in `NodeChatMessageCommands` moves.

> **Read-side note (2026-08-07).** A sibling lane is landing a targeted single-message query for `NodeChatPersistenceSql.ReadMessageAsync`, which today calls `ReadMessagesAsync` and loads plus AEAD-decrypts *every message in the conversation* to return one — on the per-flush path. Assume that fix is in. What remains quadratic in output length, and what this section addresses, is the per-flush **re-serialize + re-encrypt + rewrite of the full accumulated content and metadata** (plus the now-single-message decrypt on the way in) — roughly five O(n) passes per flush. See §2.4 for why that fix removes the case for a read-free flush path, and §2.3 for why it strengthens rather than weakens the case against a journal.

`ChatInvocationStatePump` today flushes when `timeProvider.GetElapsedTime(lastPartialFlushTimestamp) >= PartialFlushDebounceInterval` (fixed 100 ms). Replace the predicate with a new static helper `PartialFlushPolicy.ShouldFlush(persistedChars, pendingChars, elapsed, options)`:

```
if (pendingChars == 0)                          return false;   // nothing advanced
if (elapsed < MinIntervalMs)                    return false;   // never faster than today's cadence
if (pendingChars >= max(MinGrowthChars,
                        persistedChars * GrowthFraction))  return true;
if (elapsed >= MaxIntervalMs)                   return true;    // slow streams still checkpoint
return false;
```

`persistedChars = persistCursor.Content.Length + persistCursor.Reasoning.Length`; `pendingChars` is the same measure on the incoming state minus the cursor. The two existing unconditional-flush cases stay: the **first** partial (`!hasFlushedPartial`) and any **terminal**.

Defaults: `MinIntervalMs = 100`, `MaxIntervalMs = 2000`, `GrowthFraction = 0.20`, `MinGrowthChars = 512`.

Why this is the fix and not a constant-factor dodge: the amplifier is "rewrite `n` bytes to append `d` bytes". Bounding `d ≥ 0.20·n` bounds the rewrite-to-append ratio at 6:1 regardless of `n`, so total bytes rewritten across a turn of `n` characters is `≤ 6n` on the growth-triggered branch — linear. The `MaxIntervalMs` ceiling reintroduces a quadratic term for streams slow enough that 2 s of output is under 20% of the message, but with a 30× smaller constant. Worked example, matching the audit's framing: a 120 KB response at ~50 tok/s over 600 s. Today: ~6000 flushes averaging 60 KB ≈ **360 MB** each of decrypt, JSON, encrypt and SQLite write. After: the growth trigger dominates below ~1.2 KB/flush-window and the 2 s ceiling above it, giving ~300 flushes averaging ~60 KB ≈ **18 MB**. Twenty-fold, from a predicate.

The write path in `NodeChatMessageCommands` and the `INodeChatInvocationPump` surface are **untouched** by this lane. `NodeChatPumpFlushResult.Persisted` is simply no longer read on the local path — the pump does not need a persisted DTO to build a delta event (§1.3) — and stays as-is for the platform path.

### 2.2 Crash-resume path

Unchanged, and that is the point.

- **Graceful interruption** (stream ends with no terminal state): `ChatInvocationStatePump` already flushes `pendingPartialState` before `TerminalizeInterruptedStreamAsync`. Nothing is lost. The larger flush window makes `pendingPartialState` larger, not less reliable.
- **User cancel**: today's documented trade-off stands — the cancelled row terminalizes from `persistCursor`, dropping up to one window of tail tokens, because the cancellation token is already tripped and a re-flush would throw. The window grows from ~100 ms to ≤2 s of output (~400 characters at 50 tok/s). A cancelled turn is discarded output; this is the same trade-off the existing comment already accepts, at a larger but still bounded size.
- **Hard crash / power loss**: `NodeChatRestartRecoveryService.RecoverInterruptedMessagesAsync` terminalizes every `pending`/`queued`/`streaming` assistant row to `Interrupted` at next launch, preserving whatever content was last flushed. Resume therefore reads exactly one thing after a crash: **the message row**. There is no second store to reconcile, no journal to replay, no ordinal-continuity check, and no partially-written append to classify. Loss bound: **≤ `MaxIntervalMs` of output**, i.e. ~400 characters at default settings.

### 2.3 Encryption story, and why the journal is rejected

**Encryption is unchanged.** No new key, no new AAD, no new envelope. The flush continues to write one `content` blob and one `metadata_json` blob through `NodeChatDbContext.EncryptMessageContent` / `EncryptMessageMetadata`, i.e. `NodeChatContentProtection.Protect` over `NodePayloadProtector.Encrypt` with `AAD = conversationId ‖ messageId ‖ columnName ‖ "v1"` and a fresh random nonce per write. Nothing about the at-rest format, the read-both legacy-plaintext path, or the migration changes.

That is the decisive argument against the append-only journal. A journal would need:

1. A new table plus an EF migration, and a new retention/purge rule tied to conversation delete.
2. A **per-append AEAD** whose AAD binds the append's ordinal, or the ordinal authenticated inside the plaintext — otherwise appends can be reordered or duplicated within a message without detection. `NodePayloadProtector.BuildAssociatedData` has no ordinal slot today, so this means either a new protector overload (a new AAD schema — a cryptographic change) or an ordinal-in-plaintext convention that every reader must enforce. Both are new security-relevant surface for a performance fix.
3. A compaction step at terminalize, and a crash path that must decide what a torn final append means.
4. Worst of all: **message content would live in two places mid-turn.** Every reader — the conversation GET, branching, compaction, revision listing, export, the memory-extraction hook — would have to know whether to consult the journal, and any one that forgets silently renders a truncated turn. That is a correctness liability paid forever to remove a constant factor.

Both mechanisms are asymptotically the same order for total write volume once the growth trigger is in place (`O(n)` vs `O(n)`). The journal buys a smaller crash-loss window — ~0 instead of ~400 characters — on a partial assistant turn that the application already treats as disposable. It is not worth items 1–4.

The sibling lane's `ReadMessageAsync` fix **strengthens** this conclusion rather than complicating it. The single largest absolute term in the old per-flush cost was not the message at all — it was decrypting every *other* message in the conversation, which grows with conversation length independently of the turn. Removing that leaves a residue proportional to the turn's own output, which is exactly the term a growth-triggered cadence bounds. The journal was never the right tool for the read-side cost, and the read-side cost is now gone.

If a future measurement shows the 2 s ceiling dominating on a real workload, the cheap next step is to scale the ceiling with size (`MaxIntervalMs = clamp(MinIntervalMs × (1 + chars/32768), 100, 5000)`) — one line, still no journal.

### 2.4 Considered and dropped: the read-free flush path

An earlier revision of this design also proposed skipping the flush's `ReadMessageAsync` entirely, by caching the placeholder-immutable metadata fields in a `NodeChatPartialFlushContext` and adding a second write path to `NodeChatMessageCommands`. **Dropped**, on the sibling lane's news.

The arithmetic: with the whole-conversation read fixed, skipping the remaining single-message read saves roughly one of the five O(n) passes per flush, call it 20–40%. The growth-triggered cadence already removes ~95% of the flushes. Stacking the read-free path on top buys about 1.3× on a cost that is already a twentieth of what it was — in exchange for a new record type, a new optional parameter threaded through `INodeChatInvocationPump`, a second code path through the guarded UPDATE that must independently preserve the transition guard and the metadata round-trip, and a new outcome type whose rejection branch does the read anyway.

That is a lot of new surface in the one place where a mistake silently corrupts persisted message attribution. Skip it; revisit only if `chat_partial_flush_chars_total / output chars` (§5) still exceeds the ≤10 target after Lane A lands, which would mean the ceiling — not the read — is dominating, and §2.3's one-line ceiling scaling is the cheaper answer anyway.

Bonus: dropping it removes four files from Lane A's ownership, including `NodeChatMessageCommands.cs` — which the sibling read-side lane is also editing.

---

## 3. Bounded channels and the stream budget (v1 P0)

### 3.1 Today

`NodeChatStreamService.SendMessageCoreAsync` and `NodeChatRegenerationService.RegenerateCoreAsync` each create two `Channel.CreateUnbounded` instances with six and five concurrent producers respectively. On client disconnect the SSE `await foreach` exits — but the producers keep writing, with **no reader**, for the remainder of the run. Every event, tool result, and (today) full content snapshot is retained until the iterator's `finally` completes. `InvocationResumeRegistry.LiveInvocation.Subscribe` creates an unbounded channel per subscriber with no cap on subscriber count.

### 3.2 Where coalescing happens: at the producer, never in the queue

The only high-volume event kind is `assistant-delta`, and it has exactly **one** producer: the pump. §1.3's `EmitDebounceMs` (40 ms) caps delta frames at ≤25/s independent of token rate, on top of the existing burst drain (`while (!IsTerminal(latest.Status) && stateReader.TryRead(out var queued)) latest = queued`) which already collapses a token burst into one frame.

Coalescing there rather than in the queue has one decisive property: **a coalesced delta consumes exactly one sequence number**, minted at emit time. Nothing is merged after a sequence has been assigned, so the client's `guardNodeChatStream` never sees a hole. Coalescing inside a queue would have to merge already-numbered frames, which either strands `guardNodeChatStream` waiting on a vanished sequence or requires a `throughSequence` range field and a guard change — complexity bought for nothing, since the producer-side cap already bounds the rate.

Consequently the "which kinds may coalesce, which must never drop" table collapses to:

| Kind | Policy |
|---|---|
| `assistant-delta` | Coalesced at the producer (40 ms window + state-burst drain). Never dropped selectively. |
| terminal, `approval-requested`, `question-requested`, `tool-call-requested`/`-completed`, `assistant-phase`, `assistant-notice`, lifecycle status | Never coalesced, never dropped selectively. If one of these cannot be enqueued, the whole stream reconciles (§3.4). |

### 3.3 The sink

Replace the raw `ChannelWriter<ChatStreamEvent>` threaded through the pump, the runner task, and the five event handlers with one new type, `ChatStreamEventSink` (new file, `Services/Chat/Implementation/ChatStreamEventSink.cs`), behind an interface `IChatStreamEventSink` (new file) so the pump's signature does not depend on the concrete budget implementation:

```
ValueTask WriteAsync(ChatStreamEvent e, CancellationToken ct);   // ordered, for awaitable producers
bool TryWrite(ChatStreamEvent e);                                // for the sync event handlers
IAsyncEnumerable<ChatStreamEvent> ReadAllAsync(CancellationToken ct);
void Detach();                                                   // the SSE consumer is gone
void Complete();
```

Internals:

- One `Channel<ChatStreamEvent>` created **bounded** with `capacity = QueueCapacity` (default 2048), `SingleReader = true`, `SingleWriter = false`, `FullMode = BoundedChannelFullMode.DropWrite`. `DropWrite` — not `Wait` — is mandatory: the pump writes events *and* owns persistence, so blocking a write would stall the persistence loop and violate the existing "the run keeps going and the pump persists its real terminal" invariant that `SendMessageCoreAsync`'s finally-block comment is built on.
- A byte budget alongside the count: an `Interlocked` char counter incremented on enqueue and decremented on dequeue, capped at `MaxQueuedChars` (default 1 048 576). A tool result can be megabytes on its own, so a count cap alone is not a memory bound.
- A `_reconcileNeeded` flag, set whenever an enqueue is refused for either reason.
- `Detach()` latches a `_detached` flag; every subsequent `WriteAsync`/`TryWrite` becomes a no-op returning success. **It never throws and never completes the channel**, because the pump's `catch (Exception) when (!terminalPersisted)` would otherwise interpret a `ChannelClosedException` as a persistence fault and terminalize the row `Failed`. Detaching is the fix for the retention leak: after the browser goes away the run continues, persistence continues, and the event stream is simply discarded — the persisted row plus `InvocationResumeRegistry` are the recovery surface, exactly as they are for a reload today.

### 3.4 Overflow → reconcile

When `_reconcileNeeded` is set, the reader emits a single `assistant-reconcile` event (new `ChatStreamEventTypes.AssistantReconcile = "assistant-reconcile"`, no payload beyond the correlation and a sequence) ahead of the next drained item, then clears the flag. The client handles it in `NodeChatAdapter.signalRStream` with the *same* `repair()` used for an offset gap: dispose the subscription, re-subscribe via `ResumeMessage`, receive an `assistant-snapshot`, continue. `NodeChatStreamState` needs no branch for it — the adapter consumes it and does not forward it.

At 25 frames/s a 2048-deep queue represents ~80 s of consumer lag before overflow; a consumer that far behind should resynchronize rather than replay 80 s of history. Reaching the cap is expected to be effectively unreachable in practice and is instrumented accordingly (§5).

### 3.5 Resume-path bounds

In `InvocationResumeRegistry.LiveInvocation`:

- `Subscribe` rejects beyond `MaxSubscribersPerInvocation` (default 4) by throwing `InvalidOperationException`; `ResumeAsync` surfaces it the same way it surfaces "not resumable" today, and the hub returns the error to that client only. Existing subscribers are unaffected.
- Per-subscriber channels become bounded at `QueueCapacity` with `DropWrite`, with the same reconcile latch — a resume consumer that falls behind gets an `assistant-reconcile` and re-resumes.
- Replay snapshot cap: in `ResumeCoreAsync`, if `snapshot.StreamedContent.Length + snapshot.StreamedThinkingContent.Length > MaxReplaySnapshotChars` (default 1 048 576), emit `assistant-reconcile` **instead of** the opening `assistant-snapshot` and end the resume stream. The client falls back to refetching the persisted conversation, which holds the same text and costs one request. This avoids inventing a truncation semantic for the snapshot.
- The existing `MaxRecordedToolEvents` (256) and `MaxRecordedNoticeEvents` (64) caps are already correct and stay.

### 3.6 Options

One options record, `ChatStreamBudgetOptions` (new file, `Client.Application/Models/`), `SectionName = "Chat:StreamBudget"`:

| Key | Default | Meaning |
|---|---|---|
| `Chat:StreamBudget:QueueCapacity` | `2048` | Max buffered events per stream (live and per resume subscriber). |
| `Chat:StreamBudget:MaxQueuedChars` | `1048576` | Max buffered characters per stream. |
| `Chat:StreamBudget:EmitDebounceMs` | `40` | Minimum spacing between live delta frames. |
| `Chat:StreamBudget:MaxSubscribersPerInvocation` | `4` | Concurrent resume consumers per invocation. |
| `Chat:StreamBudget:MaxReplaySnapshotChars` | `1048576` | Above this, resume reconciles instead of replaying. |
| ~~`Chat:StreamBudget:DetachedGraceSeconds`~~ | `300` | §4. `0` = never cancel (today's behavior). **Not bound here** — operator-editable, so it is a stored node setting read through `INodeRuntimeSettings` (**§7.1**). Listed for the default only. |
| `Chat:StreamBudget:PartialFlushMinIntervalMs` | `100` | §2.1(a). |
| `Chat:StreamBudget:PartialFlushMaxIntervalMs` | `2000` | §2.1(a). |
| `Chat:StreamBudget:PartialFlushGrowthFraction` | `0.20` | §2.1(a). |
| `Chat:StreamBudget:PartialFlushMinGrowthChars` | `512` | §2.1(a). |

Nine related knobs in one record rather than two records split by concern: they are all "how much of a streaming turn may be buffered or deferred", they are tuned together, and one section is one thing to find. The tenth, `DetachedGraceSeconds`, lives elsewhere because it is the only one an operator edits through the UI (§7.1) — the rest are appsettings-only tuning.

---

## 4. Subscriber-aware disconnect grace (§2 correction 1, v1 P0)

### 4.1 The problem, precisely

`InvocationRunner.SetInvocationDeadline(parkedOnHuman: true)` re-points the whole-turn `CancelAfter` to `_maxPendingToolCallAge + _invocationTimeout` — 10 min + 5 min by default — and does so **per park**, up to `MaximumToolIterationsPerRequest` times. Nothing in that path knows whether a human is attached. A browser that disconnected while an approval card was on screen leaves a run holding the collision-slot lease, the llama-server process, and the whole transcript for ~15 minutes per park, waiting for an answer that cannot arrive.

`SendMessageCoreAsync`'s finally-block `DECISION` comment deliberately declines to free the slot on SSE unsubscribe, and correctly warns that doing so from there would resurrect the interrupted-terminal bug. The grace deadline is the "explicit disconnect→cancel path distinct from this SSE unsubscribe" that comment asks for.

### 4.2 Attachment tracking

New singleton `InvocationAttachmentTracker` implementing `IInvocationAttachmentTracker` (two new files under `Services/Invocation/`):

```
IDisposable Attach(Guid invocationId);       // ref-counted; Dispose detaches
bool IsAttached(Guid invocationId);
IReadOnlyCollection<(Guid InvocationId, DateTimeOffset DetachedAtUtc)> ListDetached();
event EventHandler<InvocationAttachmentChangedEventArgs>? AttachmentChanged;
```

A `ConcurrentDictionary<Guid, Entry>` where `Entry` holds a count and a `DetachedAtUtc` stamped by `TimeProvider` when the count reaches zero and cleared when it rises above zero. Entries are removed when the invocation terminalizes (subscribe `IWorkerEventDispatcher.InvocationStateChanged` for terminal statuses, mirroring how `InvocationResumeRegistry.OnInvocationStateChanged` releases its own entries).

**Where `Attach` is called: `LocalChatHub`, and only there.** All four stream entry points — `SendMessage`, `RegenerateMessage`, `ResumeMessage`, `ResumeConversation` — return `IAsyncEnumerable<ChatStreamEvent>` from that one file. The hub wraps each returned enumerable in a small local iterator that latches the invocation id from the first event's `RequestId` (known up front for `SendMessage` and `ResumeMessage`; server-minted and latched for `RegenerateMessage` and `ResumeConversation`, exactly as `NodeChatAdapter.signalRStream` latches it client-side), calls `Attach` on the latch, and disposes the handle in its `finally`. One file, one hook, no per-service plumbing, and it covers every attach path uniformly including ones added later.

### 4.3 The reaper

New hosted service `DetachedInvocationReaper` (new file under `Services/Invocation/`). A `PeriodicTimer` at 5 s (fixed; not worth a knob) walks `ListDetached()` and, for each entry older than `DetachedGraceSeconds`, calls `IInvocationRunner.CancelDetached(invocationId)`. Disabled entirely when `DetachedGraceSeconds == 0`.

`CancelDetached` is a new method on `InvocationRunner`, identical to `Cancel` except it sets `_requestedCancellationOrigin = CancellationOrigin.DetachedGraceExpired` (new enum member `= 4`) so the failure classification and the logs distinguish it from a user stop.

What expiry does, and why nothing new is needed to do it: cancelling the invocation's CTS unwinds `InvocationRunner.RunAsync`, which propagates to `RunInvocationAsync`'s `catch (OperationCanceledException)` → `ReportInvocationFailedAsync(..., FailureCategory.Cancelled)`; the pump sees the terminal state and terminalizes the row `Cancelled`; `RunInvocationAsync`'s `finally` disposes the collision-slot **lease**; `InvocationResumeRegistry.OnInvocationStateChanged` removes the live entry. Lease release, persistence terminalization, and invocation-state marking all fall out of the existing machinery. The reaper adds a trigger, not a teardown path.

### 4.4 Interaction with the existing watchdogs

`SetInvocationDeadline` becomes subscriber-aware. Add a `bool _parkedOnHuman` field guarded by the existing `_syncRoot`, set by the two park sites and cleared by their `finally`s, and change the re-arm to:

```
var attached = _attachmentTracker.IsAttached(_currentInvocationId.Value);
cts.CancelAfter(parkedOnHuman && attached
    ? _maxPendingToolCallAge + _invocationTimeout
    : _invocationTimeout);
```

- **Attached park:** byte-identical to today. `MaxPendingToolCallAge` remains the real cap on operator thinking time, which is the property the existing doc comment establishes and must not regress.
- **Detached park:** the turn falls back to a plain `InvocationTimeout` backstop, and the reaper's grace (300 s default) normally fires first.
- **Re-attach during a park** (the common case — the user reloads while an approval card is up): `InvocationAttachmentTracker.AttachmentChanged` fires; `InvocationRunner` subscribes and re-applies `SetInvocationDeadline(_parkedOnHuman)` for the current invocation, so a re-attached park gets the full `MaxPendingToolCallAge` back from the moment of re-attach. Without this hook a reload would inherit whatever a detached park left behind.
- The per-wait `approvalTimeoutCancellationTokenSource.CancelAfter(_maxPendingToolCallAge)` and `questionTimeoutCancellationTokenSource.CancelAfter(_maxPendingToolCallAge)` linked sources are **untouched**. They remain the independent per-wait cap, and the "not answered" sentinel result they produce is unchanged.
- Relationship to SignalR's own reconnect: the grace clock starts on *stream* detach, which follows transport loss by the time it takes the hub to tear down the enumerator. `DetachedGraceSeconds` must therefore comfortably exceed the client's automatic-reconnect window; at 300 s it does by a wide margin. This is the reason for a generous default rather than the 30–60 s a pure resource argument would suggest.

---

## 5. Observability

New counters on `NodeMetrics` (content-free, matching the conventions already there):

| Metric | Kind | Tags | Why |
|---|---|---|---|
| `chat_stream_enqueue_dropped_total` | Counter | `reason` = `queue_capacity` \| `queue_bytes` | Should stay at zero; non-zero means a real consumer stall. |
| `chat_stream_reconcile_total` | Counter | `reason` = `queue_overflow` \| `replay_cap` \| `offset_gap` | The offset-gap tag is client-reported via the existing error-snapshot breadcrumb trail, not the server meter. |
| `chat_stream_detached_invocations` | UpDownCounter | — | The "leases held without subscribers" gauge v1 asked for. |
| `chat_detached_invocation_reaped_total` | Counter | — | How often the grace deadline actually fires. |
| `chat_partial_flush_total` | Counter | — | Flush count per turn; the direct before/after evidence for §2. |
| `chat_partial_flush_chars_total` | Counter | — | Characters rewritten. `chars_total / output_chars` is the amplification ratio. Acceptance target, reconciled after Lane A's derivation: the growth branch is bounded by `n·(1+2g)/g` = **7** at the shipped `g = 0.2` (flush series `n·(1+g)/g` plus the terminal's full rewrite), so ≤10 holds wherever growth governs. A slow stream governed by the 2 s ceiling (§7.2, decided) can exceed 10 by design — that trade bought the crash-loss bound; the §2.4 revisit trigger (ceiling scaling) is the recorded answer if live ratios show the ceiling dominating in practice. |

---

## 6. Lanes, ownership, and verification

Four lanes. **A and C are the coupled pair** (protocol change; both halves ship in one build — see D2). **B depends on A** because both edit `ChatInvocationStatePump`; do not run them concurrently. **D is independent of all three** and can start immediately.

### Lane A — backend protocol and flush cadence

Delivers §1.3 and §2.1. Ships independently of B and D; must merge together with C.

Files owned:
- `Client.Application/Services/Chat/NodeChatStreamDtos.cs` — home of both `ChatStreamEvent` (add `ContentOffset`, `ReasoningOffset`) and `ChatStreamEventTypes` (add `AssistantSnapshot`, `AssistantReconcile`)
- `Client.Application/Services/Chat/Implementation/ChatStreamEventMapper.cs` (`MessageEvent` split, new `DeltaEvent`, `SnapshotEvent`)
- `Client.Application/Services/Chat/Implementation/ChatInvocationStatePump.cs` (emit/persist split, dual cursors, flush-context capture)
- `Client.Application/Services/Chat/Implementation/PartialFlushPolicy.cs` (**new**)
- `Client.Application/Services/Chat/Implementation/InvocationResumeRegistry.cs` (snapshot type, delta offsets)
- `Client.Application/Models/ChatStreamBudgetOptions.cs` (**new** — A creates it with the four flush knobs; B adds the five budget knobs)

Explicitly **not** owned by A, per §2.4: `NodeChatMessageCommands.cs`, `NodeChatInvocationPump.cs`, `INodeChatInvocationPump.cs`, `NodeChatPumpDtos.cs`, `NodeChatPersistenceSql.cs`. The persistence write path is unchanged, so A does not collide with the sibling read-side lane.
- `Client.Application/DependencyInjection/Modules/AddNodeChatStreamBudgetExtensions.cs` (**new**, options binding only)

Unit tests:
- `ChatStreamEventMapperTests`: `DeltaEvent` leaves `Content`/`Reasoning` null and sets both offsets; `SnapshotEvent` sets content and offsets equal to the text lengths; terminal `MessageEvent` still carries full content.
- `ChatInvocationStatePumpTests`: emitted delta offsets are contiguous across a multi-chunk turn (`offset[k+1] == offset[k] + delta[k].length`); a terminal after a partial emits the tail delta before the terminal; emit cadence is bounded by `EmitDebounceMs` while the persist cadence is not.
- New `PartialFlushPolicyTests`: growth trigger fires at exactly 20%; `MinGrowthChars` floors it for a short message; `MaxIntervalMs` fires for a slow stream; `MinIntervalMs` suppresses a fast one; a turn of `n` characters produces `≤ 6n` rewritten characters in a simulated drive.
- `InvocationResumeRegistryTests`: the first replayed event is `assistant-snapshot`; subsequent live events are `assistant-delta` with correct offsets; the terminal still carries full content.

Live-Aspire scenario: **long response with a throttled consumer, reconnect twice.** Send a prompt producing ≥50 KB, throttle the browser to a slow profile, drop and restore the connection twice mid-stream. Assert: rendered text matches the persisted row exactly at terminal; `chat_partial_flush_chars_total / output chars ≤ 10`; per-frame wire payload for `assistant-delta` is bounded (does not grow with message length).

### Lane B — stream budget and sink

Delivers §3, plus fold-in §4 of the task brief (the regenerate approval subscription). **Starts after A merges.**

Files owned:
- `Client.Application/Services/Chat/Implementation/ChatStreamEventSink.cs` (**new**)
- `Client.Application/Services/Chat/IChatStreamEventSink.cs` (**new**)
- `Client.Application/Services/Chat/Implementation/NodeChatStreamService.cs` (sink instead of `Channel`; `Detach()` in the SSE-loop `finally`, before awaiting the tasks)
- `Client.Application/Services/Chat/Implementation/NodeChatRegenerationService.cs` (same, **plus the missing `ApprovalRequestedChanged` subscription**)
- `Client.Application/Services/Chat/Implementation/InvocationResumeRegistry.cs` — *conflicts with Lane A; B rebases onto A's version*
- `Client.Application/Common/Telemetry/NodeMetrics.cs`
- `Client.Application/Models/ChatStreamBudgetOptions.cs` (adds the five budget knobs to A's record; `DetachedGraceSeconds` is not one of them — §7.1)
- `Client.Application/DependencyInjection/Modules/AddNodeChatStreamBudgetExtensions.cs` (sink registration)

The regenerate fold-in is a verbatim port of `NodeChatStreamService.OnApprovalRequestedChanged`: the same handler body, subscribed and unsubscribed alongside the existing four. Today a regenerated turn that hits an approval-gated tool parks with no card ever reaching the browser — the same class of bug the existing `OnUserQuestionRequestedChanged` comment in that file was added to fix, for the sibling event. Update that file's stale "Five producers write this channel" comment to six.

Unit tests:
- New `ChatStreamEventSinkTests`: enqueue past `QueueCapacity` drops and latches reconcile; enqueue past `MaxQueuedChars` does the same; the reader emits exactly one `assistant-reconcile` per latch; after `Detach()` writes are no-ops that never throw and never complete the channel; `Complete()` still drains buffered items.
- `NodeChatRegenerationServiceTests`: an `ApprovalRequestedChanged` for the run's request id reaches the stream; one for a different id does not; the handler is detached on every exit path including a pre-task throw.
- `InvocationResumeRegistryTests`: the `MaxSubscribersPerInvocation + 1`-th `ResumeAsync` throws while existing subscribers keep streaming; an oversized snapshot yields `assistant-reconcile` and ends the stream.

Live-Aspire scenario: **long response, throttled consumer, disconnect mid-stream and stay away.** Assert the run reaches its terminal, the persisted row is complete, managed heap does not grow with the abandoned stream (take a heap snapshot before and after the disconnected tail), and `chat_stream_enqueue_dropped_total` is zero for a merely-slow consumer.

### Lane C — frontend merge and repair

Delivers §1.4. Develops in parallel with A; **merges with A** (a C-only merge would render nothing, since the server would still be sending snapshots the client no longer reads — and vice versa).

Files owned:
- `Client.React/src/features/chat/api/NodeChatStreamState.ts`
- `Client.React/src/features/chat/api/NodeChatAdapter.ts`
- `Client.React/src/features/chat/models/NodeChatStreamTypes.ts`
- their `__tests__` siblings

Unit tests (Vitest):
- `NodeChatStreamState`: an `assistant-delta` carrying a stale `content` field is ignored and the delta is appended (the direct regression test for D2); an `assistant-snapshot` replaces content wholesale; a terminal replaces content; reasoning follows the same three rules; `parts[]` interleave is unchanged across a delta→tool→delta sequence.
- `NodeChatAdapter`: a delta whose `contentOffset` skips ahead triggers exactly one `ResumeMessage` re-subscription; an overlapping offset does too; a contiguous stream never does; `assistant-reconcile` triggers repair and is not forwarded downstream; offsets reset correctly from a snapshot and from a terminal.

Live-Aspire scenario: shares Lane A's (they merge together). Additionally verify by hand that a mid-turn browser reload re-renders the in-flight turn with no duplicated or missing text.

### Lane D — disconnect grace and watchdog

Delivers §4. Fully independent; can merge before, between, or after the others.

Files owned:
- `Client.Application/Services/Invocation/IInvocationAttachmentTracker.cs` (**new**)
- `Client.Application/Services/Invocation/Implementation/InvocationAttachmentTracker.cs` (**new**)
- `Client.Application/Services/Invocation/Implementation/DetachedInvocationReaper.cs` (**new**)
- `Client.Application/Services/Invocation/Implementation/InvocationRunner.cs` (`CancelDetached`, `CancellationOrigin.DetachedGraceExpired`, `_parkedOnHuman`, subscriber-aware `SetInvocationDeadline`, `AttachmentChanged` handler)
- `Client/Hubs/LocalChatHub.cs` (attach/detach wrapper)
- `Client.Application/DependencyInjection/Modules/AddNodeInvocationAttachmentExtensions.cs` (**new**)

Plus the stored-node-setting chain for `DetachedGraceSeconds` — see **§7.1** for that file list, its OpenAPI-regen requirement, and its own collision warning. D therefore does **not** depend on Lane A's `ChatStreamBudgetOptions` at all: it reads the grace through `INodeRuntimeSettings`, so the two lanes share no file.

Unit tests:
- New `InvocationAttachmentTrackerTests`: ref-counting across concurrent attaches; `DetachedAtUtc` stamped only on the zero transition and cleared on re-attach; terminal removes the entry; `TimeProvider`-driven, no real clock.
- New `DetachedInvocationReaperTests`: cancels exactly once past the grace; does not cancel an attached run; does not cancel a run detached for less than the grace; disabled at `0`.
- `InvocationRunnerTests`: `SetInvocationDeadline(parkedOnHuman: true)` while **attached** re-arms to `MaxPendingToolCallAge + InvocationTimeout` (pins today's behavior); while **detached** re-arms to `InvocationTimeout` only; a re-attach during a park restores the full park budget; the per-wait `CancelAfter(_maxPendingToolCallAge)` is untouched in all three cases.

Live-Aspire scenario: **disconnect during an approval park.** Start an agent turn that calls an approval-gated tool, wait for the card, close the browser tab without answering. Assert: the run is cancelled within `DetachedGraceSeconds` (not 15 minutes); the llama-server collision-slot lease is released (a second chat send succeeds immediately afterwards); the row terminalizes `Cancelled` with no error text; `chat_detached_invocation_reaped_total` incremented by one. Then repeat, but **reload the page** within the grace instead of closing it: assert the approval card re-renders, the run is *not* reaped, and answering it lets the turn complete.

### Cross-lane live scenario (run once, after all four merge)

**Crash mid-turn resume.** Start a long response, kill the node process (`SIGKILL`, not a graceful stop) at ~50% of the output, restart. Assert: `NodeChatRestartRecoveryService` terminalizes the row `Interrupted`; the rendered content on reload equals the last flushed content; the lost tail is under `PartialFlushMaxIntervalMs` of output; a run envelope exists for the row.

### Pre-existing edits in this worktree

At the time of writing, `fix/inference-open-findings-2` already carries uncommitted edits from sibling audit lanes to files this design claims: `NodeChatStreamService.cs` (Lane B), and `InvocationRunner.cs` plus the four node-settings files listed in §7.1 (Lane D). Neither lane may assume a clean base for any of them — rebase onto whatever lands first and re-read the surrounding region before editing, particularly `InvocationRunner`'s `_syncRoot`-guarded state, where Lane D adds two fields.

### Build gate for every backend lane

`dotnet build XE-Local-AI-Engine.slnx --configuration Release`. A green Debug build is not verification — the analyzer wall is Release-only, and a bare `TODO`/`FIXME` in a C# comment fails it (Sonar S1135 + warnings-as-errors). `pnpm run lint` is the only typecheck for Lane C; a green E2E run does not typecheck the frontend.

---

## 7. Decisions (settled 2026-08-07)

No open questions remain. All four are settled; implement as written.

1. **`DetachedGraceSeconds` = 300, operator-editable, `0` = never cancel.** Operator decision. `0` remains supported and means today's behavior (a detached run is bounded only by the whole-invocation watchdog). Because it is operator-editable it is a **stored node setting**, not an appsettings-only key — see §7.1 for the plumbing this adds to Lane D.
2. **`PartialFlushMaxIntervalMs` = 2000.** Operator decision. The ~2 s crash-loss window on a partial turn (§2.2) is accepted.
3. **Subscriber cap rejects, it does not evict.** §3.5 as written: the 5th concurrent resume subscriber gets an `InvalidOperationException`; existing subscribers are unaffected. Revisit only if multi-window usage turns out to be real.
4. **`assistant-reconcile` stays silent.** §3.4 as written: the adapter consumes it and re-resumes; no toast. `chat_stream_reconcile_total` (§5) is the signal that it is happening.

### 7.1 What decision 1 adds to Lane D

Surfacing the knob next to `MaxPendingToolCallAgeMinutes` means following that setting's existing stored-node-setting chain rather than binding a plain options key. `DetachedGraceSeconds` therefore **moves out of `ChatStreamBudgetOptions`** (§3.6's table row is now informational only — the row stays for the default, but the value is read through `INodeRuntimeSettings`). Precedence matches every sibling getter: **stored > `WorkerNode:DetachedGraceSeconds` > seed 300**.

Additional Lane D file ownership, mirroring `MaxPendingToolCallAgeMinutes` one-for-one:

- `Client.Application/Services/NodeSettings/StoredNodeSettings.cs` (field + normalize; clamp negatives to `0`)
- `Client.Application/Services/NodeSettings/INodeRuntimeSettings.cs` + `Implementation/NodeRuntimeSettings.cs` (`GetDetachedGraceSecondsAsync` + the sync twin — the reaper's timer callback is structurally sync)
- `Client.Application/Configuration/WorkerNodeOptions.cs` + `Validation/WorkerNodeOptionsValidator.cs`
- `Client/Endpoints/NodeSettings/V1/Dtos/NodeSettingsEndpointDtos.cs`, `Mappers/NodeSettingsEndpointDtoMapper.cs`, `Validators/NodeSettingsEndpointValidators.cs`
- `Client.React/src/features/node-settings/models/NodeSettingsFieldsModel.ts` (+ `.test.ts`) and `components/NodeSettingsFieldsCard.tsx`
- `Tests/Testing/Builders/StubNodeRuntimeSettings.cs`

Two traps this pulls in, both already paid for once in this repo:

- **The endpoint DTO change requires an OpenAPI regen**, and the regen must run with `XE_LAUNCH_MODE=desktop` or desktop-only endpoints are silently dropped from the generated client. The React side reads the setting through `core/api/generated`, so the regen is not optional.
- **The reaper must not cache the value in a singleton field.** `INodeRuntimeSettings`' own header records that capturing a stored setting in a singleton is exactly what silently required a node restart before an edit took effect (F-001/F-025). Read it per tick; the read is an `IMemoryCache` hit through `CachedNodeSettingsStore`.

Added Lane D tests: `StoredNodeSettingsNormalizeTests` (negative clamps to `0`, unset falls back to seed 300); `NodeRuntimeSettingsTests` (stored > config > seed precedence); `NodeSettingsFieldsModel.test.ts` (the field round-trips); and a `DetachedInvocationReaperTests` case asserting a **mid-run settings edit takes effect on the next tick** — the direct regression test for the F-001/F-025 trap.

**Collision warning:** four of these files — `StoredNodeSettings.cs`, `NodeSettingsFieldsModel.ts`, `NodeSettingsFieldsModel.test.ts`, `NodeSettingsFieldsCard.tsx` — already carry uncommitted sibling-lane edits in this worktree (see "Pre-existing edits" above). Lane D rebases; it does not assume a clean base for any of them.
