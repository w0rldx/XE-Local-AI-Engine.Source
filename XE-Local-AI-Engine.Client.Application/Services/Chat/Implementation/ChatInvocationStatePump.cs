namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Threading.Channels;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     Fans the shared invocation pump's persisted results out as SSE <see cref="ChatStreamEvent" />s for a local
///     response, shared by the send and regenerate paths so their cadences and terminal handling stay identical.
/// </summary>
/// <remarks>
///     <see cref="INodeChatInvocationPump" /> owns all persistence; this only orders its output on the caller's
///     <see cref="NodeChatStreamSequence" />. EMITTING and PERSISTING run on separate cadences and cursors: a frame
///     is a pure delta needing no database row, while a flush waits until the message has GROWN enough to be worth
///     rewriting (<see cref="PartialFlushPolicy" />). Events leave through an <see cref="IChatStreamEventSink" />,
///     whose bounded queue NEVER makes a write wait, or the run's terminal would stall behind a lagging consumer.
/// </remarks>
public sealed class ChatInvocationStatePump
{
    // Error text stamped on the row when the persistence pump itself faults — distinct from a
    // generation-side failure so a persistence fault is traceable on the terminalized row.
    private const string PumpFaultError = "local-chat-persistence-failed";

    // Optional so the many direct constructions in tests keep the shipped defaults without threading options through.
    private readonly ChatStreamBudgetOptions _options;
    private readonly INodeChatInvocationPump _invocationPump;
    private readonly TimeProvider _timeProvider;

    public ChatInvocationStatePump(INodeChatInvocationPump invocationPump,
        TimeProvider timeProvider,
        IOptions<ChatStreamBudgetOptions>? options = null)
    {
        _invocationPump = invocationPump;
        _timeProvider = timeProvider;
        _options = options?.Value ?? new ChatStreamBudgetOptions();
    }

    public async Task PumpAsync(ChannelReader<InvocationState> stateReader,
        IChatStreamEventSink eventSink,
        NodeChatMessageCorrelation correlation,
        string? requestedModel,
        NodeChatStreamSequence sequence,
        NodeChatPartAccumulator parts,
        Action<InvocationState, NodeChatPumpTerminalResult>? onTerminal,
        CancellationToken cancellationToken,
        // KB sources that grounded this turn, computed up front by the send path so they land on the terminal row's
        // metadata_json. Null or empty for a turn that used no knowledge base, preserving any persisted sources.
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
        // The last runtime phase surfaced to the client, so a pre-first-token transition is emitted once per distinct
        // phase. Coalescing keeps the newest snapshot per burst, so this tracks the CURRENT phase, not every one.
        InvocationRuntimePhase? lastEmittedPhase = null;
        // The freshest content-bearing snapshots the cadences deferred. Retained so a graceful end-of-stream that never
        // delivers a terminal still writes (and sends) the tail rather than a stale cursor.
        InvocationState? pendingPartialState = null;
        InvocationState? pendingEmitState = null;

        // Sends one snapshot's growth as a delta-only AssistantDelta, sliced out of the snapshot at the emit cursor
        // with no I/O. Each state carries the FULL accumulation, so the latest snapshot alone is still contiguous.
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
            lastEmitTimestamp = _timeProvider.GetTimestamp();
            hasEmitted = true;

            var deltaSequence = sequence.Next();
            // The reasoning delta and its SSE event share this emit-time sequence while tool parts stamp their own, so
            // order at a tool boundary can shift by one emit window — which feeding from the EMIT path keeps small.
            parts.AppendReasoning(reasoningDelta, deltaSequence);

            await eventSink.WriteAsync(ChatStreamEventMapper.DeltaEvent(correlation,
                    NowUnixMilliseconds(),
                    deltaSequence,
                    contentDelta,
                    reasoningDelta,
                    contentOffset,
                    reasoningOffset),
                cancellationToken);
        }

        // Persists one snapshot's content/reasoning delta. Emits nothing — the client was already fed by
        // EmitDeltaAsync on its own cadence, and a persisted row is no longer needed to build a delta frame.
        async Task PersistPartialAsync(InvocationState snapshotToFlush)
        {
            var flush = await _invocationPump.FlushDeltaAsync(correlation, snapshotToFlush, persistCursor, cancellationToken);
            persistCursor = flush.Cursor;

            if (flush.Persisted is null)
            {
                return;
            }

            lastPartialFlushTimestamp = _timeProvider.GetTimestamp();
            hasFlushedPartial = true;
        }

        try
        {
            await foreach (var state in stateReader.ReadAllAsync(cancellationToken))
            {
                // Coalesce a burst: drain the backlog and keep only the newest snapshot, never draining past a
                // terminal, so a burst of per-token states collapses into a single flush without losing content.
                var latest = state;
                while (!NodeChatInvocationPump.IsTerminal(latest.Status) && stateReader.TryRead(out var queued))
                {
                    latest = queued;
                }

                var isTerminal = NodeChatInvocationPump.IsTerminal(latest.Status);

                // A pre-first-token phase transition is a content-free AssistantPhase event, emitted only for a
                // non-terminal state whose PHASE changed; RuntimePhaseChangedAtUtc moves only when the phase does.
                if (!isTerminal && latest.RuntimePhase is { } runtimePhase && runtimePhase != lastEmittedPhase)
                {
                    lastEmittedPhase = runtimePhase;
                    await eventSink.WriteAsync(ChatStreamEventMapper.PhaseEvent(correlation, runtimePhase, NowUnixMilliseconds(), sequence.Next(), latest.RuntimePhaseChangedAtUtc), cancellationToken);
                }

                // Send first, on the fast cadence: the first delta emits immediately, and a terminal emits its tail
                // unconditionally so the client's text already equals the terminal's and never needs correcting.
                if (isTerminal
                    || !hasEmitted
                    || _timeProvider.GetElapsedTime(lastEmitTimestamp) >= emitDebounceInterval)
                {
                    pendingEmitState = null;
                    await EmitDeltaAsync(latest);
                }
                else
                {
                    pendingEmitState = latest;
                }

                // Persist second, on the slow cadence: a flush rewrites the whole message, so it waits on GROWTH rather
                // than a clock. A terminal and the first partial flush immediately; between flushes state is deferred.
                if (isTerminal
                    || !hasFlushedPartial
                    || PartialFlushPolicy.ShouldFlush(persistCursor.Content.Length + persistCursor.Reasoning.Length,
                        latest.StreamedContent.Length - persistCursor.Content.Length + (latest.StreamedThinkingContent.Length - persistCursor.Reasoning.Length),
                        _timeProvider.GetElapsedTime(lastPartialFlushTimestamp),
                        _options))
                {
                    pendingPartialState = null;
                    await PersistPartialAsync(latest);
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
                    var terminal = await _invocationPump.TerminalizeAsync(correlation, latest, requestedModel, snapshot, sources);
                    terminalPersisted = true;

                    // Post-run adaptive memory: the just-persisted terminal goes to the fire-and-forget extraction hook
                    // before the SSE write, and the hook never blocks or throws into the pump.
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
                        CancellationToken.None);
                    break;
                }
            }

            if (!terminalPersisted)
            {
                // Send, then flush, any tail the two cadences deferred, so the interrupted terminal is written from the
                // freshest content and the deferred reasoning tail still reaches parts[], which the emit path owns.
                if (pendingEmitState is not null)
                {
                    await EmitDeltaAsync(pendingEmitState);
                }

                if (pendingPartialState is not null)
                {
                    await PersistPartialAsync(pendingPartialState);
                }

                await TerminalizeInterruptedStreamAsync(eventSink,
                    correlation,
                    sequence.Next(),
                    persistCursor,
                    cancellationToken.IsCancellationRequested);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !terminalPersisted)
        {
            // Deliberate trade-off: a cancelled message terminalizes from the last-persisted cursor, so a cancel drops
            // up to one flush window of tail tokens. Re-flushing cannot work with the token already tripped.
            await TerminalizeInterruptedStreamAsync(eventSink,
                correlation,
                sequence.Next(),
                persistCursor,
                wasCancelled: true);
        }
        catch (Exception) when (!terminalPersisted)
        {
            // A persistence fault would otherwise propagate while the finally ends the writer NORMALLY, leaving the row
            // streaming until the next reconcile. The atomic status guard makes this Failed terminalize a safe no-op.
            await TerminalizeFaultedStreamAsync(eventSink, correlation, requestedModel, persistCursor, parts, sequence.Next(), sources);
            throw;
        }
        finally
        {
            eventSink.Complete();
        }
    }

    // Terminalizes the row Failed from the last-persisted cursor after a pump fault and emits the Failed SSE. A throw
    // from the terminalize itself is swallowed: the caller rethrows the ORIGINAL fault and recovery backstops the row.
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
            // A synthetic Failed state carries the last-persisted content so the row keeps whatever streamed before the
            // fault. No live InvocationState exists here, so tokens and duration are null and the model is the request's.
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
            var terminal = await _invocationPump.TerminalizeAsync(correlation, faultedState, requestedModel, snapshot, sources);

            await eventSink.WriteAsync(ChatStreamEventMapper.MessageEvent(terminal.EventType, correlation, terminal.Persisted, NowUnixMilliseconds(), sequence), CancellationToken.None);
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
        var terminal = await _invocationPump.TerminalizeInterruptedAsync(correlation, cursor, wasCancelled);

        await eventSink.WriteAsync(ChatStreamEventMapper.MessageEvent(terminal.EventType, correlation, terminal.Persisted, NowUnixMilliseconds(), sequence), CancellationToken.None);
    }

    private long NowUnixMilliseconds()
    {
        return _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
    }
}
