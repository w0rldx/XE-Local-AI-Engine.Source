namespace XE_Local_AI_Engine.Client.Services.Events.Implementation;

using System.Diagnostics;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;

public sealed partial class WorkerEventDispatcher
{
    private static InvocationState Clone(InvocationState state)
    {
        return new InvocationState
        {
            InvocationId = state.InvocationId,
            ConversationId = state.ConversationId,
            TraceId = state.TraceId,
            Status = state.Status,
            RuntimePhase = state.RuntimePhase,
            // Copy the immutable accumulators by REFERENCE (O(1)); reading state.StreamedContent here would materialize
            // the whole response every chunk (the O(n^2) hot-path cost this snapshot design removes).
            ContentAccumulator = state.ContentAccumulator,
            StreamedChunkCount = state.StreamedChunkCount,
            ThinkingAccumulator = state.ThinkingAccumulator,
            StreamedThinkingChunkCount = state.StreamedThinkingChunkCount,
            StartedAt = state.StartedAt,
            LastUpdatedAt = state.LastUpdatedAt,
            CompletedAt = state.CompletedAt,
            Error = state.Error,
            FailureCategory = state.FailureCategory,
            ModelUsed = state.ModelUsed,
            InputTokens = state.InputTokens,
            OutputTokens = state.OutputTokens,
            TotalTokens = state.TotalTokens,
            ReasoningTokens = state.ReasoningTokens,
            GenerationDurationMs = state.GenerationDurationMs,
            PendingApproval = state.PendingApproval,
            LastApprovalResolution = state.LastApprovalResolution,
            PendingToolCalls = [.. state.PendingToolCalls],
            LastToolCallResult = state.LastToolCallResult
        };
    }

    private static InvocationState CreateInvocationState(RuntimePackage runtimePackage)
    {
        ArgumentNullException.ThrowIfNull(runtimePackage);

        return new InvocationState
        {
            InvocationId = runtimePackage.InvocationId,
            ConversationId = runtimePackage.ConversationId,
            // Capture the W3C trace id of the ambient (request/hub) activity so the invocation monitor can surface a
            // copyable correlation id. The pre-spawn spans (AUD4-23) start as children of this same activity, so they
            // share this trace id — the monitor row therefore links straight to the run's exported trace. A default
            // (all-zero) id is treated as absent.
            TraceId = Activity.Current is { } activity && activity.TraceId != default ? activity.TraceId.ToString() : null,
            Status = InvocationStatus.Assigned,
            StartedAt = DateTimeOffset.UtcNow,
            LastUpdatedAt = DateTimeOffset.UtcNow,
            ModelUsed = runtimePackage.ModelProfile,
            StreamedThinkingContent = string.Empty,
            StreamedThinkingChunkCount = 0
        };
    }

    private static bool IsInvocationActive(InvocationState? state)
    {
        return state is not null && state.Status is InvocationStatus.Assigned or InvocationStatus.Running;
    }

    private InvocationState? GetCurrentInvocationSnapshot()
    {
        lock (_syncRoot)
        {
            return CurrentInvocation is null ? null : Clone(CurrentInvocation);
        }
    }

    private void UpdateCurrentInvocation(Action<InvocationState> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        InvocationState? snapshot;

        lock (_syncRoot)
        {
            if (CurrentInvocation is null)
            {
                return;
            }

            update(CurrentInvocation);
            CurrentInvocation.LastUpdatedAt = DateTimeOffset.UtcNow;
            snapshot = Clone(CurrentInvocation);
        }

        if (snapshot is not null)
        {
            PublishStateChanged(snapshot);
        }
    }

    private void UpdateInvocation(Guid invocationId, Func<InvocationState, InvocationState> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        InvocationState? snapshot = null;

        lock (_syncRoot)
        {
            if (CurrentInvocation?.InvocationId != invocationId)
            {
                // The current slot was replaced (or cleared) before this update arrived. A dropped terminal here
                // would silently leave the message non-terminal; we apply the update against the matching id only,
                // so probe the dropped status outside the lock to surface terminal drops for diagnosis.
                var dropped = update(new InvocationState
                {
                    InvocationId = invocationId,
                    ConversationId = CurrentInvocation?.ConversationId ?? Guid.Empty
                });

                if (NodeChatInvocationPump.IsTerminal(dropped.Status))
                {
                    _logger.LogWarning(
                        "Dropped a terminal {Status} update for invocation {InvocationId} because the current invocation is {CurrentInvocationId}. The terminal will not be persisted via this slot.",
                        dropped.Status,
                        invocationId,
                        CurrentInvocation?.InvocationId);
                }

                return;
            }

            CurrentInvocation = update(CurrentInvocation);
            CurrentInvocation.LastUpdatedAt = DateTimeOffset.UtcNow;
            snapshot = Clone(CurrentInvocation);
        }

        if (snapshot is not null)
        {
            PublishStateChanged(snapshot);
        }
    }

    private void PublishStateChanged(InvocationState state)
    {
        // Every caller already hands us a fresh Clone of the live invocation (never the mutable CurrentInvocation),
        // and nothing downstream mutates the snapshot — the history buffer only stores it and the event consumers only
        // read it. So the recorder and the event share this one immutable snapshot instead of cloning a second time
        // (which also re-copied PendingToolCalls) on every streamed chunk.
        _invocationHistory.Record(state);
        Volatile.Read(ref InvocationStateChanged)?.Invoke(this, new InvocationStateChangedEventArgs(state));
    }

    /// <summary>
    ///     Holds the shared invocation slot for the duration of a local run. Disposing it releases the slot so the
    ///     next queued invocation (local or remote) can proceed. Release is idempotent.
    /// </summary>
    private sealed class LocalInvocationLease(SemaphoreSlim queue) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, value: 1) == 0)
            {
                _ = queue.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
