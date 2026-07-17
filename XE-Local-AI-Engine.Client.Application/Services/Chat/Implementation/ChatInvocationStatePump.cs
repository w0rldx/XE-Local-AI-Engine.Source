namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Threading.Channels;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     Fans the shared invocation pump's persisted results out as SSE <see cref="ChatStreamEvent" />s for a local
///     response. Owned by both the send and regenerate paths so the debounced flush cadence, the burst-coalescing, and
///     the interrupted-terminal handling stay identical between them. The pump itself (<see cref="INodeChatInvocationPump" />)
///     owns all persistence; this only translates its output into the ordered stream events, sharing the caller's
///     <see cref="NodeChatStreamSequence" /> with the streaming-transition event so every event stays monotonically ordered.
/// </summary>
public sealed class ChatInvocationStatePump(INodeChatInvocationPump invocationPump, TimeProvider timeProvider)
{
    // Mid-stream partial flushes (the per-chunk DB read-modify-write + AssistantDelta SSE) are debounced to at most one
    // per this window so a fast local model does not drive one persistence round-trip per token. Terminal/error states
    // always flush immediately, so the final content is never delayed; a crash mid-stream therefore loses at most one
    // window of streamed tokens, which is the same crash-consistency bound the per-chunk flush already accepted.
    private static readonly TimeSpan PartialFlushDebounceInterval = TimeSpan.FromMilliseconds(100);

    public async Task PumpAsync(ChannelReader<InvocationState> stateReader,
        ChannelWriter<ChatStreamEvent> eventWriter,
        NodeChatMessageCorrelation correlation,
        string? requestedModel,
        NodeChatStreamSequence sequence,
        NodeChatPartAccumulator parts,
        Action<InvocationState, NodeChatPumpTerminalResult>? onTerminal,
        CancellationToken cancellationToken)
    {
        var cursor = NodeChatPumpCursor.Empty;
        var terminalPersisted = false;
        var hasFlushedPartial = false;
        var lastPartialFlushTimestamp = 0L;
        // The last runtime phase surfaced to the client, so a pre-first-token phase transition is emitted once per
        // distinct phase (AUD4-20: the "Loading model…" indicator). Coalescing keeps the newest snapshot per burst, so
        // this tracks the CURRENT phase, not every intermediate one — which is exactly what the indicator needs.
        InvocationRuntimePhase? lastEmittedPhase = null;
        // The freshest content-bearing snapshot the debounce deferred without persisting. Retained so a graceful
        // end-of-stream that never delivers a terminal still writes the tail rather than a stale cursor.
        InvocationState? pendingPartialState = null;

        // Persists one snapshot's content/reasoning delta and fans it out as an AssistantDelta. Returns whether a
        // delta advanced. Each InvocationState carries the FULL accumulated content/reasoning, so flushing only the
        // latest snapshot after coalescing still captures everything between the cursor and that snapshot.
        async Task<bool> PersistPartialAsync(InvocationState snapshotToFlush)
        {
            var flush = await invocationPump.FlushDeltaAsync(correlation, snapshotToFlush, cursor, cancellationToken).ConfigureAwait(false);
            cursor = flush.Cursor;

            if (flush.Persisted is null)
            {
                return false;
            }

            lastPartialFlushTimestamp = timeProvider.GetTimestamp();
            hasFlushedPartial = true;
            var deltaSequence = sequence.Next();
            // The reasoning delta and its SSE event share this flush-time sequence. Tool parts stamp their own
            // sequence synchronously when the tool lifecycle fires on the separate event channel, so debouncing the
            // reasoning flush can push a reasoning segment behind a tool part that streamed just after it — widening
            // the pre-existing reasoning<->tool interleave skew to at most one debounce window. This is display-only:
            // the reasoning text and the tool parts are all retained; only their relative order at a tool boundary can
            // shift within that window.
            parts.AppendReasoning(flush.ReasoningDelta, deltaSequence);

            await eventWriter.WriteAsync(ChatStreamEventMapper.MessageEvent(ChatStreamEventTypes.AssistantDelta,
                    correlation,
                    flush.Persisted,
                    NowUnixMilliseconds(),
                    deltaSequence,
                    flush.ContentDelta,
                    flush.ReasoningDelta),
                cancellationToken).ConfigureAwait(false);
            return true;
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
                    await eventWriter.WriteAsync(ChatStreamEventMapper.PhaseEvent(correlation, runtimePhase, NowUnixMilliseconds(), sequence.Next()), cancellationToken).ConfigureAwait(false);
                }

                // Terminal/error flushes immediately (the final delta + reasoning tail must not be delayed); the first
                // partial also flushes immediately so the first token is visible without waiting a window. Between
                // windows the snapshot is deferred so it is not lost if the stream ends without a terminal.
                if (isTerminal
                    || !hasFlushedPartial
                    || timeProvider.GetElapsedTime(lastPartialFlushTimestamp) >= PartialFlushDebounceInterval)
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
                    var terminal = await invocationPump.TerminalizeAsync(correlation, latest, requestedModel, snapshot).ConfigureAwait(false);
                    terminalPersisted = true;

                    // Post-run adaptive memory: hand the just-persisted terminal to the (background, fire-and-forget)
                    // extraction hook before the SSE write so the run context is captured immediately. The hook never
                    // blocks or throws into the pump (it only schedules work on its own scope).
                    onTerminal?.Invoke(latest, terminal);

                    await eventWriter.WriteAsync(ChatStreamEventMapper.MessageEvent(terminal.EventType,
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
                // Flush any tail the debounce deferred so the interrupted terminal is written from the freshest
                // content rather than a stale cursor.
                if (pendingPartialState is not null)
                {
                    await PersistPartialAsync(pendingPartialState).ConfigureAwait(false);
                }

                await TerminalizeInterruptedStreamAsync(eventWriter,
                    correlation,
                    sequence.Next(),
                    cursor,
                    cancellationToken.IsCancellationRequested).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !terminalPersisted)
        {
            // Deliberate trade-off: the cancelled message is terminalized from the last-persisted cursor, NOT from a
            // debounce-deferred pendingPartialState, so a user cancel can drop up to one debounce window of tail
            // tokens off the cancelled turn. Re-flushing here is not an option — the cancellation token is already
            // tripped, so FlushDeltaAsync/WriteAsync would just throw again. Accepted because a cancelled turn is
            // discarded output anyway, and this stays within the same one-window crash-consistency bound.
            await TerminalizeInterruptedStreamAsync(eventWriter,
                correlation,
                sequence.Next(),
                cursor,
                wasCancelled: true).ConfigureAwait(false);
        }
        finally
        {
            eventWriter.TryComplete();
        }
    }

    private async Task TerminalizeInterruptedStreamAsync(ChannelWriter<ChatStreamEvent> eventWriter,
        NodeChatMessageCorrelation correlation,
        long sequence,
        NodeChatPumpCursor cursor,
        bool wasCancelled)
    {
        var terminal = await invocationPump.TerminalizeInterruptedAsync(correlation, cursor, wasCancelled).ConfigureAwait(false);

        await eventWriter.WriteAsync(ChatStreamEventMapper.MessageEvent(terminal.EventType, correlation, terminal.Persisted, NowUnixMilliseconds(), sequence), CancellationToken.None)
                         .ConfigureAwait(false);
    }

    private long NowUnixMilliseconds()
    {
        return timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
    }
}
