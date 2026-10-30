namespace XE_Local_AI_Engine.Client.Services.Events.Implementation;

using System.Diagnostics;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;

public sealed partial class WorkerEventDispatcher
{
    private InvocationState CreateInvocationState(RuntimePackage runtimePackage)
    {
        ArgumentNullException.ThrowIfNull(runtimePackage);

        return new InvocationState
        {
            InvocationId = runtimePackage.InvocationId,
            ConversationId = runtimePackage.ConversationId,
            // Capture the W3C trace id of the ambient (request/hub) activity so the monitor can surface a copyable correlation id. The
            // pre-spawn spans are children of that activity and share the id, so the row links straight to the run's exported trace; a default (all-zero) id is absent.
            TraceId = Activity.Current is { } activity && activity.TraceId != default ? activity.TraceId.ToString() : null,
            Status = InvocationStatus.Assigned,
            StartedAt = _timeProvider.GetUtcNow(),
            LastUpdatedAt = _timeProvider.GetUtcNow(),
            ModelUsed = runtimePackage.ModelProfile,
            StreamedThinkingContent = string.Empty,
            StreamedThinkingChunkCount = 0
        };
    }

    // Caller must hold _syncRoot. A live turn by id, else an ended one whose lease is still out, else the last assigned one when it is that id.
    private InvocationState? FindState(Guid invocationId)
    {
        if (_live.TryGetValue(invocationId, out var state) || _ended.TryGetValue(invocationId, out state))
        {
            return state;
        }

        return CurrentInvocation?.InvocationId == invocationId ? CurrentInvocation : null;
    }

    // Applies to the live turn the predicate picks, else to CurrentInvocation only when it matches too; returns the target's pre-update clone, or null.
    private InvocationState? UpdateMatching(Func<InvocationState, bool> match, Action<InvocationState> update)
    {
        InvocationState? before;
        InvocationState? snapshot;

        lock (_syncRoot)
        {
            var target = _live.Values.FirstOrDefault(match) ?? (CurrentInvocation is { } last && match(last) ? last : null);
            if (target is null)
            {
                return null;
            }

            before = target.Clone();
            update(target);
            target.LastUpdatedAt = _timeProvider.GetUtcNow();
            snapshot = target.Clone();
        }

        PublishStateChanged(snapshot);
        return before;
    }

    private void UpdateInvocation(Guid invocationId, Func<InvocationState, InvocationState> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        InvocationState snapshot;

        lock (_syncRoot)
        {
            if (FindState(invocationId) is not { } state)
            {
                // Neither live nor the last assigned turn. An update applies against the matching id only, and a dropped terminal
                // would silently leave the message non-terminal, so probe the dropped status here to surface it.
                var dropped = update(new InvocationState
                {
                    InvocationId = invocationId,
                    ConversationId = Guid.Empty
                });

                if (NodeChatInvocationPump.IsTerminal(dropped.Status))
                {
                    _logger.LogWarning("Dropped a terminal {Status} update for invocation {InvocationId} because it is not live. The terminal will not be persisted via this slot.",
                        dropped.Status,
                        invocationId);
                }

                return;
            }

            var updated = update(state);
            updated.LastUpdatedAt = _timeProvider.GetUtcNow();
            if (CurrentInvocation?.InvocationId == invocationId)
            {
                CurrentInvocation = updated;
            }

            if (NodeChatInvocationPump.IsTerminal(updated.Status) && _live.Remove(invocationId))
            {
                // Kept until its lease is released, so a holder that just ended still reads as the holder and late updates still land.
                _ended[invocationId] = updated;
            }
            else if (_live.ContainsKey(invocationId))
            {
                _live[invocationId] = updated;
            }
            else if (_ended.ContainsKey(invocationId))
            {
                _ended[invocationId] = updated;
            }

            snapshot = updated.Clone();
        }

        PublishStateChanged(snapshot);
    }

    // Called once by a lease's dispose: frees the slot only while that lease's invocation holds it, and drops a turn that never reported a terminal.
    private void ReleaseLease(Guid invocationId)
    {
        var release = false;
        lock (_syncRoot)
        {
            if (_slotHolderId == invocationId)
            {
                _slotHolderId = null;
                SignalSlotChanged();
                release = true;
            }

            _ = _live.Remove(invocationId);
            _ = _ended.Remove(invocationId);
        }

        if (release)
        {
            _ = _invocationQueue.Release();
        }
    }

    public bool ReleaseSlotForPark(Guid invocationId)
    {
        InvocationState? snapshot;
        lock (_syncRoot)
        {
            if (_slotHolderId != invocationId)
            {
                return false;
            }

            _slotHolderId = null;
            SignalSlotChanged();
            snapshot = FindState(invocationId)?.Clone();
        }

        _ = _invocationQueue.Release();
        if (snapshot is not null)
        {
            PublishStateChanged(snapshot);
        }

        return true;
    }

    public async Task ReacquireSlotAsync(Guid invocationId, CancellationToken cancellationToken)
    {
        await _invocationQueue.WaitAsync(cancellationToken);

        // Nothing that can throw sits between the wait and this assignment, or a cancel in the gap would leak the permit.
        InvocationState? snapshot;
        lock (_syncRoot)
        {
            _slotHolderId = invocationId;
            SignalSlotChanged();
            snapshot = FindState(invocationId)?.Clone();
        }

        if (snapshot is not null)
        {
            PublishStateChanged(snapshot);
        }
    }

    // Caller must hold _syncRoot. Its continuations run asynchronously, so completing it under the lock runs no waiter here.
    private void SignalSlotChanged()
    {
        var changed = _slotChanged;
        _slotChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = changed.TrySetResult();
    }

    private void PublishStateChanged(InvocationState state)
    {
        // Every caller hands in a fresh Clone of the live invocation (never the mutable CurrentInvocation) and nothing downstream mutates the
        // snapshot, so the recorder and the event share this one immutable snapshot rather than cloning again per streamed chunk.
        _invocationHistory.Record(state);
        Volatile.Read(ref InvocationStateChanged)?.Invoke(this, new InvocationStateChangedEventArgs(state));
    }

    /// <summary>
    ///     A local run's claim on the shared invocation slot. Disposing it releases the slot only while its invocation is the slot
    ///     holder, so the next queued invocation can proceed. Dispose is idempotent.
    /// </summary>
    private sealed class LocalInvocationLease : IAsyncDisposable
    {
        private readonly Guid _invocationId;
        private readonly WorkerEventDispatcher _owner;
        private int _disposed;

        public LocalInvocationLease(WorkerEventDispatcher owner, Guid invocationId)
        {
            _owner = owner;
            _invocationId = invocationId;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, value: 1) == 0)
            {
                _owner.ReleaseLease(_invocationId);
            }

            return ValueTask.CompletedTask;
        }
    }
}
