namespace XE_Local_AI_Engine.Client.Services.Events.Implementation;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;

public sealed partial class WorkerEventDispatcher
{
    public async Task<IAsyncDisposable> ReportInvocationAssignedAsync(RuntimePackage package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);

        // Queue the turn behind any in-flight invocation instead of throwing when busy. The slot is held until the
        // returned lease is disposed (when the run terminates), so concurrent turns stay mutually exclusive.
        // Cancelling the turn while it is still queued aborts the wait here.
        await _invocationQueue.WaitAsync(cancellationToken);

        InvocationState snapshot;

        lock (_syncRoot)
        {
            CurrentInvocation = CreateInvocationState(package);
            snapshot = CurrentInvocation.Clone();
        }

        PublishStateChanged(snapshot);
        return new LocalInvocationLease(_invocationQueue);
    }

    public Task DispatchApprovalResolvedAsync(ApprovalResolvedEvent evt, ApprovalScope scope = ApprovalScope.Once)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var currentInvocation = GetCurrentInvocationSnapshot();

        _logger.LogInformation("Received approval resolution for request {RequestId}. Approved: {Approved} Scope: {Scope}",
            evt.RequestId,
            evt.Approved,
            scope);

        _invocationRunner.ResolveApprovalResult(evt, scope);

        if (currentInvocation is null)
        {
            _logger.LogWarning("Approval resolution arrived with no current invocation tracked. RequestId={RequestId}", evt.RequestId);
        }
        else if (currentInvocation.PendingApproval is null)
        {
            _logger.LogWarning("Approval resolution arrived with no pending approval tracked. RequestId={RequestId} CurrentInvocationId={CurrentInvocationId}",
                evt.RequestId,
                currentInvocation.InvocationId);
        }
        else if (!string.Equals(currentInvocation.PendingApproval.RequestId, evt.RequestId, StringComparison.Ordinal))
        {
            _logger.LogWarning("Approval resolution request id did not match pending approval. RequestId={RequestId} PendingRequestId={PendingRequestId} CurrentInvocationId={CurrentInvocationId}",
                evt.RequestId,
                currentInvocation.PendingApproval.RequestId,
                currentInvocation.InvocationId);
        }

        UpdateCurrentInvocation(state =>
        {
            if (state.PendingApproval is not null
                && string.Equals(state.PendingApproval.RequestId, evt.RequestId, StringComparison.Ordinal))
            {
                state.PendingApproval = null;
            }

            state.LastApprovalResolution = new InvocationApprovalResolutionState { RequestId = evt.RequestId, Approved = evt.Approved, ResolvedAt = _timeProvider.GetUtcNow() };
        });

        _logger.LogDebug("Approval resolution processing finished. RequestId={RequestId}", evt.RequestId);

        return Task.CompletedTask;
    }
}
