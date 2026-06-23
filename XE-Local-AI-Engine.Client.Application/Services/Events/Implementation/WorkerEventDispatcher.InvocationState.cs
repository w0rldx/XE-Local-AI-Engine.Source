namespace XE_Local_AI_Engine.Client.Services.Events.Implementation;

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
            Status = state.Status,
            StreamedContent = state.StreamedContent,
            StreamedChunkCount = state.StreamedChunkCount,
            StreamedThinkingContent = state.StreamedThinkingContent,
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
        _invocationHistory.Record(state);
        Volatile.Read(ref InvocationStateChanged)?.Invoke(this, new InvocationStateChangedEventArgs(Clone(state)));
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
