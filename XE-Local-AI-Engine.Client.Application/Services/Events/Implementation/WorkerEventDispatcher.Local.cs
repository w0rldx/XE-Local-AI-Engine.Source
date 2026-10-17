namespace XE_Local_AI_Engine.Client.Services.Events.Implementation;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;

public sealed partial class WorkerEventDispatcher
{
    public async Task<IAsyncDisposable> ReportInvocationAssignedAsync(RuntimePackage package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);

        // Queue the turn behind any in-flight invocation instead of throwing when busy. The slot is held until the returned lease is disposed
        // (when the run terminates), so concurrent turns stay mutually exclusive; cancelling while still queued aborts the wait here.
        await _invocationQueue.WaitAsync(cancellationToken);

        InvocationState? snapshot = null;

        lock (_syncRoot)
        {
            // A second assignment of a live id would let the first turn's lease pass the holder check and free the new holder's slot.
            if (!_live.ContainsKey(package.InvocationId) && !_ended.ContainsKey(package.InvocationId))
            {
                CurrentInvocation = CreateInvocationState(package);
                _live[package.InvocationId] = CurrentInvocation;
                _slotHolderId = package.InvocationId;
                snapshot = CurrentInvocation.Clone();
            }
        }

        if (snapshot is null)
        {
            _ = _invocationQueue.Release();
            throw new InvalidOperationException($"Invocation {package.InvocationId} is already live.");
        }

        PublishStateChanged(snapshot);
        return new LocalInvocationLease(this, package.InvocationId);
    }

    public Task DispatchApprovalResolvedAsync(ApprovalResolvedEvent evt, ApprovalScope scope = ApprovalScope.Once)
    {
        ArgumentNullException.ThrowIfNull(evt);

        _logger.LogInformation("Received approval resolution for request {RequestId}. Approved: {Approved} Scope: {Scope}",
            evt.RequestId,
            evt.Approved,
            scope);

        _invocationRunner.ResolveApprovalResult(evt, scope);

        // The turn whose pending approval carries this request id, which need not be the last one assigned.
        var target = UpdateMatching(state => string.Equals(state.PendingApproval?.RequestId, evt.RequestId, StringComparison.Ordinal),
            state =>
            {
                if (state.PendingApproval is not null
                    && string.Equals(state.PendingApproval.RequestId, evt.RequestId, StringComparison.Ordinal))
                {
                    state.PendingApproval = null;
                }

                state.LastApprovalResolution = new InvocationApprovalResolutionState
                {
                    RequestId = evt.RequestId,
                    Approved = evt.Approved,
                    ResolvedAt = _timeProvider.GetUtcNow()
                };
            });

        if (target is null)
        {
            _logger.LogInformation("Approval resolution arrived for request {RequestId}, which no live invocation is waiting on; nothing was updated.", evt.RequestId);
        }

        _logger.LogDebug("Approval resolution processing finished. RequestId={RequestId}", evt.RequestId);

        return Task.CompletedTask;
    }
}
