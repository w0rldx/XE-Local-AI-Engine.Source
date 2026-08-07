namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Threading.Channels;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     Fans the shared invocation pump's persisted results out as SSE <see cref="ChatStreamEvent" />s for a local
///     response. Owned by both the send and regenerate paths so the flush cadence, the burst-coalescing, and
///     the interrupted-terminal handling stay identical between them. The pump itself (<see cref="INodeChatInvocationPump" />)
///     owns all persistence; this only translates its output into the ordered stream events, sharing the caller's
///     <see cref="NodeChatStreamSequence" /> with the streaming-transition event so every event stays monotonically ordered.
///     <para>
///         EMITTING and PERSISTING run on separate cadences, tracked by separate cursors. An emitted frame is a pure
///         delta (<see cref="ChatStreamEventMapper.DeltaEvent" />) that needs no database row, so the client can be fed
///         at ~25 frames/s while persistence flushes only when the message has GROWN enough to be worth rewriting
///         (<see cref="PartialFlushPolicy" />). Coupling them is what previously forced one full-message rewrite per
///         100 ms; decoupling them is what keeps a 2 s flush window from becoming a 2 s UI stall.
///     </para>
///     <para>
///         Events leave through an <see cref="IChatStreamEventSink" /> rather than a raw channel writer, so the queue
///         is bounded and a write NEVER waits. That matters here specifically: this pump owns persistence as well as
///         emission, so a write blocking on a lagging consumer would stall the database writes the run's real terminal
///         depends on. The sink drops instead, and repairs the whole stream rather than the frame.
///     </para>
/// </summary>
public sealed class ChatInvocationStatePump(INodeChatInvocationPump invocationPump,
    TimeProvider timeProvider,
    IOptions<ChatStreamBudgetOptions>? options = null)
{
    // Error text stamped on the row when the persistence pump itself faults — distinct from a
    // generation-side failure so a persistence fault is traceable on the terminalized row.
    private const string PumpFaultError = "local-chat-persistence-failed";

    // Optional so the many direct constructions in tests keep the shipped defaults without threading options through.
    private readonly ChatStreamBudgetOptions _options = options?.Value ?? new ChatStreamBudgetOptions();

    public async Task PumpAsync(ChannelReader<InvocationState> stateReader,
        IChatStreamEventSink eventSink,
        NodeChatMessageCorrelation correlation,
        string? requestedModel,
        NodeChatStreamSequence sequence,
        NodeChatPartAccumulator parts,
        Action<InvocationState, NodeChatPumpTerminalResult>? onTerminal,
        CancellationToken cancellationToken,
        // KB sources that grounded this turn, computed up front by the send path before generation and
        // captured here so they land on the terminal row's metadata_json. Null/empty for turns that used no knowledge
        // base (e.g. the regenerate path, which passes none) — which preserves any existing persisted sources.
        IReadOnlyList<NodeChatMessageSource>? sources = null)
    {
        // How much has been WRITTEN, and how much has been SENT. They advance independently — see the class remarks.
        var persistCursor = NodeChatPumpCursor.Empty;
        var emitCursor = NodeChatPumpCursor.Empty;
        var terminalPersisted = false;
        var hasFlushedPartial = false;
        var hasEmitted = false;
        var lastPartialFlushTimestamp = 0L;
        var lastEmitTimestamp = 0L;
        var emitDebounceInterval = TimeSpan.FromMilliseconds(_options.EmitDebounceMs);
        // The last runtime phase surfaced to the client, so a pre-first-token phase transition is emitted once per
        // distinct phase (AUD4-20: the "Loading model…" indicator). Coalescing keeps the newest snapshot per burst, so
        // this tracks the CURRENT phase, not every intermediate one — which is exactly what the indicator needs.
        InvocationRuntimePhase? lastEmittedPhase = null;
        // The freshest content-bearing snapshots the cadences deferred. Retained so a graceful end-of-stream that never
        // delivers a terminal still writes (and sends) the tail rather than a stale cursor.
        InvocationState? pendingPartialState = null;
        InvocationState? pendingEmitState = null;

        // Sends one snapshot's content/reasoning growth as a delta-only AssistantDelta. No I/O at all: the delta is
        // sliced straight out of the snapshot at the emit cursor, so a frame costs the delta, not the message. Each
        // InvocationState carries the FULL accumulated content/reasoning, so emitting only the latest snapshot after
        // coalescing still sends everything between the cursor and that snapshot as one contiguous delta.
        async Task EmitDeltaAsync(InvocationState snapshotToEmit)
        {
            var content = snapshotToEmit.StreamedContent;
            var reasoning = snapshotToEmit.StreamedThinkingContent;
            var contentOffset = emitCursor.Content.Length;
            var reasoningOffset = emitCursor.Reasoning.Length;
            var contentDelta = content.Length > contentOffset ? content[contentOffset..] : null;
            var reasoningDelta = reasoning.Length > reasoningOffset ? reasoning[reasoningOffset..] : null;

            if (contentDelta is null && reasoningDelta is null)
            {
                return;
            }

            emitCursor = new NodeChatPumpCursor(content, reasoning);
            lastEmitTimestamp = timeProvider.GetTimestamp();
            hasEmitted = true;

            var deltaSequence = sequence.Next();
            // The reasoning delta and its SSE event share this emit-time sequence. Tool parts stamp their own
            // sequence synchronously when the tool lifecycle fires on the separate event channel, so deferring the
            // reasoning append can push a reasoning segment behind a tool part that streamed just after it — a
            // pre-existing reasoning<->tool interleave skew bounded by one emit window. This is display-only: the
            // reasoning text and the tool parts are all retained; only their relative order at a tool boundary can
            // shift within that window. The accumulator is fed from the EMIT path, not the persist path, precisely so
            // this window stays the 40 ms emit cadence rather than the far slower flush cadence.
            parts.AppendReasoning(reasoningDelta, deltaSequence);

            await eventSink.WriteAsync(ChatStreamEventMapper.DeltaEvent(correlation,
                    NowUnixMilliseconds(),
                    deltaSequence,
                    contentDelta,
                    reasoningDelta,
                    contentOffset,
                    reasoningOffset),
                cancellationToken).ConfigureAwait(false);
        }

        // Persists one snapshot's content/reasoning delta. Emits nothing — the client was already fed by
        // EmitDeltaAsync on its own cadence, and a persisted row is no longer needed to build a delta frame.
        async Task PersistPartialAsync(InvocationState snapshotToFlush)
        {
            var flush = await invocationPump.FlushDeltaAsync(correlation, snapshotToFlush, persistCursor, cancellationToken).ConfigureAwait(false);
            persistCursor = flush.Cursor;

            if (flush.Persisted is null)
            {
                return;
            }

            lastPartialFlushTimestamp = timeProvider.GetTimestamp();
            hasFlushedPartial = true;
        }

        try
        {
            await foreach (var state in stateReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                // Coalesce a burst: when the runner produces states faster than we persist, they queue on the
                // channel. Drain the backlog and keep only the newest snapshot (never draining past a terminal), so
                // a burst of per-token states collapses into a single flush without losing content.
                var latest = state;
                while (!NodeChatInvocationPump.IsTerminal(latest.Status) && stateReader.TryRead(out var queued))
                {
                    latest = queued;
                }

                var isTerminal = NodeChatInvocationPump.IsTerminal(latest.Status);

                // Surface a pre-first-token runtime-phase transition (preparing/loading/generating) as a content-free
                // AssistantPhase event so the client renders "Loading model…" during a cold load. Emitted before the
                // content flush and only for a non-terminal state whose phase changed; the warm wait between "loading"
                // and "generating" is where the indicator earns its keep.
                if (!isTerminal && latest.RuntimePhase is { } runtimePhase && runtimePhase != lastEmittedPhase)
                {
                    lastEmittedPhase = runtimePhase;
                    await eventSink.WriteAsync(ChatStreamEventMapper.PhaseEvent(correlation, runtimePhase, NowUnixMilliseconds(), sequence.Next()), cancellationToken).ConfigureAwait(false);
                }

                // Send first, on the fast cadence. The first delta emits immediately so the first token is visible
                // without waiting a window, and a terminal emits its tail unconditionally so the client's accumulated
                // text already equals the terminal's content by the time the terminal lands (the terminal carries the
                // full text as a backstop, but the common path must never need it to correct anything).
                if (isTerminal
                    || !hasEmitted
                    || timeProvider.GetElapsedTime(lastEmitTimestamp) >= emitDebounceInterval)
                {
                    pendingEmitState = null;
                    await EmitDeltaAsync(latest).ConfigureAwait(false);
                }
                else
                {
                    pendingEmitState = latest;
                }

                // Persist second, on the slow cadence. A flush rewrites the whole message, so it waits until the
                // message has GROWN enough to be worth rewriting rather than running on a fixed clock — see
                // PartialFlushPolicy. Terminal/error and the first partial still flush immediately; between flushes the
                // snapshot is deferred so it is not lost if the stream ends without a terminal.
                if (isTerminal
                    || !hasFlushedPartial
                    || PartialFlushPolicy.ShouldFlush(persistCursor.Content.Length + persistCursor.Reasoning.Length,
                        latest.StreamedContent.Length - persistCursor.Content.Length + (latest.StreamedThinkingContent.Length - persistCursor.Reasoning.Length),
                        timeProvider.GetElapsedTime(lastPartialFlushTimestamp),
                        _options))
                {
                    pendingPartialState = null;
                    await PersistPartialAsync(latest).ConfigureAwait(false);
                }
                else
                {
                    pendingPartialState = latest;
                }

                if (isTerminal)
                {
                    // An empty snapshot (a plain-text turn with no reasoning/tools) is passed as null so the persisted
                    // parts are left untouched rather than overwritten with an empty interleave.
                    var snapshot = parts.HasParts ? parts.Snapshot() : null;
                    var terminal = await invocationPump.TerminalizeAsync(correlation, latest, requestedModel, snapshot, sources).ConfigureAwait(false);
                    terminalPersisted = true;

                    // Post-run adaptive memory: hand the just-persisted terminal to the (background, fire-and-forget)
                    // extraction hook before the SSE write so the run context is captured immediately. The hook never
                    // blocks or throws into the pump (it only schedules work on its own scope).
                    onTerminal?.Invoke(latest, terminal);

                    await eventSink.WriteAsync(ChatStreamEventMapper.MessageEvent(terminal.EventType,
                            correlation,
                            terminal.Persisted,
                            NowUnixMilliseconds(),
                            sequence.Next(),
                            inputTokens: latest.InputTokens,
                            outputTokens: latest.OutputTokens,
                            totalTokens: latest.TotalTokens,
                            reasoningTokens: latest.ReasoningTokens),
                        CancellationToken.None).ConfigureAwait(false);
                    break;
                }
            }

            if (!terminalPersisted)
            {
                // Send, then flush, any tail the two cadences deferred, so the interrupted terminal is written from the
                // freshest content rather than a stale cursor — and so the deferred reasoning tail still reaches
                // parts[], which the emit path owns.
                if (pendingEmitState is not null)
                {
                    await EmitDeltaAsync(pendingEmitState).ConfigureAwait(false);
                }

                if (pendingPartialState is not null)
                {
                    await PersistPartialAsync(pendingPartialState).ConfigureAwait(false);
                }

                await TerminalizeInterruptedStreamAsync(eventSink,
                    correlation,
                    sequence.Next(),
                    persistCursor,
                    cancellationToken.IsCancellationRequested).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !terminalPersisted)
        {
            // Deliberate trade-off: the cancelled message is terminalized from the last-persisted cursor, NOT from a
            // deferred pendingPartialState, so a user cancel can drop up to one flush window of tail tokens off the
            // cancelled turn. Re-flushing here is not an option — the cancellation token is already tripped, so
            // FlushDeltaAsync/WriteAsync would just throw again. Accepted because a cancelled turn is discarded output
            // anyway. The growth-triggered cadence widens this window from ~100 ms to at most
            // PartialFlushMaxIntervalMs of output (~400 characters at 50 tok/s), which is the same trade-off at a
            // larger but still bounded size.
            await TerminalizeInterruptedStreamAsync(eventSink,
                correlation,
                sequence.Next(),
                persistCursor,
                wasCancelled: true).ConfigureAwait(false);
        }
        catch (Exception) when (!terminalPersisted)
        {
            // A FlushDeltaAsync/TerminalizeAsync exception (a persistence fault, not a user cancel — those
            // are handled above) would otherwise propagate while the finally only TryComplete()s the writer as a NORMAL
            // end, leaving the row streaming until the next restart's recovery reconcile. Idempotently terminalize the
            // row Failed and emit the Failed terminal SSE, then rethrow so the caller cancels the run and surfaces the
            // fault. The NodeChatMessageTransitions atomic `AND status IN (...)` guard makes this Failed terminalize a
            // no-op over any real terminal that committed concurrently, so a late fault-terminalize can never overwrite
            // a genuine outcome.
            await TerminalizeFaultedStreamAsync(eventSink, correlation, requestedModel, persistCursor, parts, sequence.Next(), sources).ConfigureAwait(false);
            throw;
        }
        finally
        {
            eventSink.Complete();
        }
    }

    // Terminalizes the row Failed after a persistence-pump fault from the last-persisted cursor and emits the
    // Failed terminal SSE. Best-effort: if the terminalize itself throws (the persistence layer is likely down, which
    // caused the original fault), it is swallowed here — the caller still rethrows the ORIGINAL fault, and the
    // restart-recovery reconcile is the backstop for the row.
    private async Task TerminalizeFaultedStreamAsync(IChatStreamEventSink eventSink,
        NodeChatMessageCorrelation correlation,
        string? requestedModel,
        NodeChatPumpCursor cursor,
        NodeChatPartAccumulator parts,
        long sequence,
        IReadOnlyList<NodeChatMessageSource>? sources = null)
    {
        try
        {
            // A synthetic Failed state carries the last-persisted content/reasoning so the terminal row keeps whatever
            // streamed before the fault. There is no live InvocationState in this catch — tokens/duration are unknown and
            // left null; the model attribution falls back to the requested model.
            var faultedState = new InvocationState
            {
                InvocationId = correlation.RequestId,
                ConversationId = correlation.ConversationId,
                Status = InvocationStatus.Failed,
                StreamedContent = cursor.Content,
                StreamedThinkingContent = cursor.Reasoning,
                Error = PumpFaultError,
                FailureCategory = FailureCategory.Unexpected
            };

            var snapshot = parts.HasParts ? parts.Snapshot() : null;
            var terminal = await invocationPump.TerminalizeAsync(correlation, faultedState, requestedModel, snapshot, sources).ConfigureAwait(false);

            await eventSink.WriteAsync(ChatStreamEventMapper.MessageEvent(terminal.EventType, correlation, terminal.Persisted, NowUnixMilliseconds(), sequence), CancellationToken.None)
                           .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Swallowed deliberately: nothing more can be persisted or emitted here. The caller rethrows the original
            // fault so the run is cancelled and the fault surfaced; restart recovery reconciles the row on next launch.
        }
    }

    private async Task TerminalizeInterruptedStreamAsync(IChatStreamEventSink eventSink,
        NodeChatMessageCorrelation correlation,
        long sequence,
        NodeChatPumpCursor cursor,
        bool wasCancelled)
    {
        var terminal = await invocationPump.TerminalizeInterruptedAsync(correlation, cursor, wasCancelled).ConfigureAwait(false);

        await eventSink.WriteAsync(ChatStreamEventMapper.MessageEvent(terminal.EventType, correlation, terminal.Persisted, NowUnixMilliseconds(), sequence), CancellationToken.None)
                       .ConfigureAwait(false);
    }

    private long NowUnixMilliseconds()
    {
        return timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
    }
}
