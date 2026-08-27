namespace XE_Local_AI_Engine.Client.Services.Events.Implementation;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Models.Events;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Invocation;

public sealed partial class WorkerEventDispatcher
{
    public async Task DispatchInvocationAssignedAsync(EncryptedRuntimePackageDto package)
    {
        ArgumentNullException.ThrowIfNull(package);

        if (!IsAcceptingRemoteInvocations)
        {
            _logger.LogInformation("Ignoring remote InvocationAssigned because shutdown drain is active. InvocationId={InvocationId}", package.InvocationId);
            return;
        }

        _logger.LogInformation("WorkerEventDispatcher handling InvocationAssigned. InvocationId={InvocationId} ConversationId={ConversationId} MessageId={MessageId} EpochVersion={EpochVersion}",
            package.InvocationId,
            package.ConversationId,
            package.MessageId,
            package.EpochVersion);

        NodeKeyResolution resolution;

        try
        {
            var activeKeyId = _nodeKeyRegistry.ActiveKeyId;
            _logger.LogInformation("Resolving node key. ActiveKeyId={ActiveKeyId}", activeKeyId);
            resolution = _nodeKeyRegistry.Resolve(activeKeyId);
            _logger.LogInformation("Node key resolved. Status={Status} IsResolved={IsResolved}", resolution.Status, resolution.IsResolved);
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogWarning(exception, "No active node key is available to decrypt message {MessageId}.", package.MessageId);
            return;
        }

        if (!resolution.IsResolved || resolution.PrivateKey is null)
        {
            if (resolution.Status == NodeKeyLookupStatus.RetiredExpired)
            {
                _logger.LogWarning("Active node key retired/expired for message {MessageId}. KeyId={KeyId}", package.MessageId, resolution.KeyIdUsed ?? resolution.RequestedKeyId);
                await EmitInvocationKeyMismatchAsync(package.MessageId,
                    RetiredKeyReason,
                    resolution.KeyIdUsed ?? resolution.RequestedKeyId).ConfigureAwait(false);
                return;
            }

            throw new InvalidOperationException("No active node key is available to decrypt the invocation envelope.");
        }

        InvocationExecutionContext context;

        try
        {
            _logger.LogInformation("Assembling runtime package from encrypted envelope. InvocationId={InvocationId}", package.InvocationId);
            context = _runtimePackageEnvelopeAssembler.Assemble(package);
        }
        catch (InvalidOperationException exception) when (EncryptedPackageFailureClassifier.IsAadMismatch(exception))
        {
            _logger.LogWarning(exception, "AAD mismatch during decrypt for message {MessageId}.", package.MessageId);
            await EmitInvocationKeyMismatchAsync(package.MessageId,
                AadMismatchReason,
                resolution.KeyIdUsed ?? resolution.RequestedKeyId).ConfigureAwait(false);
            return;
        }
        catch (InvalidOperationException exception) when (EncryptedPackageFailureClassifier.IsConfigHashMismatch(exception))
        {
            _logger.LogWarning(exception, "Config hash mismatch for message {MessageId}.", package.MessageId);
            await EmitEncryptedFailureAsync(package, "runtime-package-config-hash-mismatch", FailureCategory.HashMismatch).ConfigureAwait(false);
            return;
        }
        catch (InvalidOperationException exception) when (EncryptedPackageFailureClassifier.IsHistoryHashMismatch(exception))
        {
            _logger.LogWarning(exception, "History hash mismatch for message {MessageId}.", package.MessageId);
            await EmitEncryptedFailureAsync(package, "runtime-package-history-hash-mismatch", FailureCategory.HashMismatch).ConfigureAwait(false);
            return;
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogError(exception, "Assemble runtime package failed with unhandled exception. InvocationId={InvocationId}", package.InvocationId);
            await _hubMessageSender.Value.SendInvocationFailedAsync(new InvocationFailedPayload
            {
                InvocationId = package.InvocationId,
                MessageId = package.MessageId,
                Error = "runtime-package-assemble-failed",
                FailureCategory = nameof(FailureCategory.AgentRuntime)
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Assemble runtime package failed with unexpected exception. InvocationId={InvocationId}", package.InvocationId);
            throw;
        }

        using var invocationContext = context;
        var runtimePackage = context.Package;

        try
        {
            await _remoteInvocationQueue.WaitAsync(_shutdownCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Abandoning queued remote invocation {InvocationId}: the worker is draining for shutdown and will not start new queued work.", runtimePackage.InvocationId);
            return;
        }

        try
        {
            await RunQueuedInvocationAsync(context, runtimePackage).ConfigureAwait(false);
        }
        finally
        {
            _ = _remoteInvocationQueue.Release();
        }
    }

    public Task DispatchInvocationAssignedV2Async(InvocationAssignedEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!IsAcceptingRemoteInvocations)
        {
            _logger.LogInformation("Ignoring remote InvocationAssignedV2 because shutdown drain is active. StorageMode={StorageMode}", envelope.StorageMode);
            return Task.CompletedTask;
        }

        return envelope.StorageMode switch
        {
            "PlainSync" when envelope.Plain is not null => DispatchPlainInvocationAsync(envelope.Plain),
            "EncryptedSync" when envelope.Encrypted is not null => DispatchInvocationAssignedAsync(envelope.Encrypted),
            _ => throw new InvalidOperationException($"InvocationAssignedV2 envelope was invalid for storage mode '{envelope.StorageMode}'.")
        };
    }

    public async Task<IAsyncDisposable> ReportInvocationAssignedAsync(RuntimePackage package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);

        // Queue the local turn behind any in-flight invocation (local or remote) using the SAME slot the
        // remote dispatch paths hold, instead of throwing when busy. The slot is held until the returned lease
        // is disposed (when the local run terminates), so local and platform invocations stay mutually
        // exclusive. Cancelling the local turn while it is still queued aborts the wait here.
        await _remoteInvocationQueue.WaitAsync(cancellationToken).ConfigureAwait(false);

        InvocationState snapshot;

        lock (_syncRoot)
        {
            CurrentInvocation = CreateInvocationState(package);
            snapshot = CurrentInvocation.Clone();
        }

        PublishStateChanged(snapshot);
        return new LocalInvocationLease(_remoteInvocationQueue);
    }

    public Task DispatchToolCallResultAsync(ToolCallResultEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var currentInvocation = GetCurrentInvocationSnapshot();
        var matchingPendingToolCall = currentInvocation?.PendingToolCalls.Any(pendingToolCall =>
            string.Equals(pendingToolCall.RequestId, evt.RequestId, StringComparison.Ordinal)) == true;

        _logger.LogInformation("Received tool call result. RequestId={RequestId} HasError={HasError} ResultLength={ResultLength} CurrentInvocationId={CurrentInvocationId}",
            evt.RequestId,
            !string.IsNullOrWhiteSpace(evt.Error),
            evt.Result.Length,
            currentInvocation?.InvocationId);

        if (currentInvocation is null)
        {
            _logger.LogWarning("Tool call result arrived with no current invocation tracked. RequestId={RequestId}", evt.RequestId);
        }
        else if (!matchingPendingToolCall)
        {
            _logger.LogWarning("Tool call result did not match any pending tool call. RequestId={RequestId} CurrentInvocationId={CurrentInvocationId} PendingToolCallCount={PendingToolCallCount}",
                evt.RequestId,
                currentInvocation.InvocationId,
                currentInvocation.PendingToolCalls.Count);
        }

        _invocationRunner.ResolveToolCallResult(evt);

        UpdateCurrentInvocation(state =>
        {
            state.PendingToolCalls = [.. state.PendingToolCalls.Where(pendingToolCall => !string.Equals(pendingToolCall.RequestId, evt.RequestId, StringComparison.Ordinal))];
            state.LastToolCallResult = new InvocationToolCallResultState(evt.RequestId,
                string.IsNullOrWhiteSpace(evt.Error),
                evt.Result,
                evt.Error,
                DateTimeOffset.UtcNow);
        });

        _logger.LogDebug("Tool call result processing finished. RequestId={RequestId}", evt.RequestId);

        return Task.CompletedTask;
    }

    public Task DispatchDisconnectRequestedAsync(DisconnectRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var currentInvocation = GetCurrentInvocationSnapshot();

        _logger.LogWarning("Received disconnect request. Reason={Reason} CurrentInvocationId={CurrentInvocationId}",
            evt.Reason,
            currentInvocation?.InvocationId);

        if (currentInvocation is null)
        {
            _logger.LogInformation("Disconnect request received while no invocation was active. Reason={Reason}", evt.Reason);
        }

        _invocationRunner.CancelAll();

        InvocationState? snapshot = null;

        lock (_syncRoot)
        {
            if (IsInvocationActive(CurrentInvocation))
            {
                CurrentInvocation!.Status = InvocationStatus.Cancelled;
                CurrentInvocation.Error = evt.Reason;
                CurrentInvocation.FailureCategory = FailureCategory.Cancelled;
                CurrentInvocation.CompletedAt = DateTimeOffset.UtcNow;
                snapshot = CurrentInvocation.Clone();
            }
        }

        if (snapshot is not null)
        {
            _logger.LogInformation("Disconnect request marked invocation as cancelled. InvocationId={InvocationId}", snapshot.InvocationId);
            PublishStateChanged(snapshot);
        }
        else
        {
            _logger.LogDebug("Disconnect request completed without invocation state changes. Reason={Reason}", evt.Reason);
        }

        return Task.CompletedTask;
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

            state.LastApprovalResolution = new InvocationApprovalResolutionState(evt.RequestId, evt.Approved, DateTimeOffset.UtcNow);
        });

        _logger.LogDebug("Approval resolution processing finished. RequestId={RequestId}", evt.RequestId);

        return Task.CompletedTask;
    }

    public Task DispatchInvocationCancelledAsync(InvocationCancelledEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var currentInvocation = GetCurrentInvocationSnapshot();

        _logger.LogInformation("Received invocation cancellation. InvocationId={InvocationId} Reason={Reason} CurrentInvocationId={CurrentInvocationId}",
            evt.InvocationId,
            evt.Reason,
            currentInvocation?.InvocationId);

        _invocationRunner.Cancel(evt.InvocationId);

        InvocationState? snapshot;

        lock (_syncRoot)
        {
            if (CurrentInvocation?.InvocationId != evt.InvocationId)
            {
                _logger.LogDebug("Ignoring cancellation for {InvocationId} because it does not match the current invocation {CurrentInvocationId}.",
                    evt.InvocationId,
                    CurrentInvocation?.InvocationId);

                return Task.CompletedTask;
            }

            CurrentInvocation.Status = InvocationStatus.Cancelled;
            CurrentInvocation.Error = evt.Reason;
            CurrentInvocation.FailureCategory = FailureCategory.Cancelled;
            CurrentInvocation.CompletedAt = DateTimeOffset.UtcNow;
            snapshot = CurrentInvocation.Clone();
        }

        _logger.LogInformation("Invocation {InvocationId} marked as cancelled.", evt.InvocationId);
        PublishStateChanged(snapshot);
        return Task.CompletedTask;
    }
}
