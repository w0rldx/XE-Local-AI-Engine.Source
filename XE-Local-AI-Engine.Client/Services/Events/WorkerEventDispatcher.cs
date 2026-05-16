namespace XE_Local_AI_Engine.Client.Services.Events;

using System.Diagnostics.CodeAnalysis;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Models.Events;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Invocation.RuntimePackage;

[SuppressMessage("Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "Registered for the application lifetime; disposing the service provider owns singleton cleanup.")]
public sealed class WorkerEventDispatcher : IWorkerEventDispatcher
{
    private const string AadMismatchReason = "aad-mismatch";
    private const string RetiredKeyReason = "retired-key";

    private readonly Lazy<IHubMessageSender> _hubMessageSender;
    private readonly IInvocationHistory _invocationHistory;
    private readonly IInvocationRunner _invocationRunner;
    private readonly ILogger<WorkerEventDispatcher> _logger;
    private readonly INodeKeyRegistry _nodeKeyRegistry;
    private readonly SemaphoreSlim _remoteInvocationQueue = new(1, 1);
    private readonly IRuntimePackageEnvelopeAssembler _runtimePackageEnvelopeAssembler;
    private readonly object _syncRoot = new();

    public WorkerEventDispatcher(IInvocationRunner invocationRunner,
        IRuntimePackageEnvelopeAssembler runtimePackageEnvelopeAssembler,
        Lazy<IHubMessageSender> hubMessageSender,
        INodeKeyRegistry nodeKeyRegistry,
        IInvocationHistory invocationHistory,
        ILogger<WorkerEventDispatcher> logger)
    {
        _invocationRunner = invocationRunner ?? throw new ArgumentNullException(nameof(invocationRunner));
        _runtimePackageEnvelopeAssembler = runtimePackageEnvelopeAssembler ?? throw new ArgumentNullException(nameof(runtimePackageEnvelopeAssembler));
        _hubMessageSender = hubMessageSender ?? throw new ArgumentNullException(nameof(hubMessageSender));
        _nodeKeyRegistry = nodeKeyRegistry ?? throw new ArgumentNullException(nameof(nodeKeyRegistry));
        _invocationHistory = invocationHistory ?? throw new ArgumentNullException(nameof(invocationHistory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public event EventHandler<InvocationStateChangedEventArgs>? InvocationStateChanged;

    public InvocationState? CurrentInvocation { get; private set; }

    public async Task DispatchInvocationAssignedAsync(EncryptedRuntimePackageDto package)
    {
        ArgumentNullException.ThrowIfNull(package);

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
        catch (InvalidOperationException exception) when (IsAadMismatch(exception))
        {
            _logger.LogWarning(exception, "AAD mismatch during decrypt for message {MessageId}.", package.MessageId);
            await EmitInvocationKeyMismatchAsync(package.MessageId,
                AadMismatchReason,
                resolution.KeyIdUsed ?? resolution.RequestedKeyId).ConfigureAwait(false);
            return;
        }
        catch (InvalidOperationException exception) when (IsConfigHashMismatch(exception))
        {
            _logger.LogWarning(exception, "Config hash mismatch for message {MessageId}.", package.MessageId);
            await EmitEncryptedFailureAsync(package, "runtime-package-config-hash-mismatch", FailureCategory.HashMismatch).ConfigureAwait(false);
            return;
        }
        catch (InvalidOperationException exception) when (IsHistoryHashMismatch(exception))
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
            }).ConfigureAwait(false);
            return;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Assemble runtime package failed with unexpected exception. InvocationId={InvocationId}", package.InvocationId);
            throw;
        }

        using var invocationContext = context;
        var runtimePackage = context.Package;

        await _remoteInvocationQueue.WaitAsync().ConfigureAwait(false);

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

        return envelope.StorageMode switch
        {
            "PlainSync" when envelope.Plain is not null => DispatchPlainInvocationAsync(envelope.Plain),
            "EncryptedSync" when envelope.Encrypted is not null => DispatchInvocationAssignedAsync(envelope.Encrypted),
            _ => throw new InvalidOperationException($"InvocationAssignedV2 envelope was invalid for storage mode '{envelope.StorageMode}'.")
        };
    }

    public Task ReportInvocationAssignedAsync(RuntimePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        InvocationState snapshot;

        lock (_syncRoot)
        {
            if (IsInvocationActive(CurrentInvocation))
            {
                throw new InvalidOperationException($"Cannot assign local invocation {package.InvocationId} while invocation {CurrentInvocation!.InvocationId} is still active.");
            }

            CurrentInvocation = CreateInvocationState(package);
            snapshot = Clone(CurrentInvocation);
        }

        PublishStateChanged(snapshot);
        return Task.CompletedTask;
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
                snapshot = Clone(CurrentInvocation);
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

    public Task DispatchApprovalResolvedAsync(ApprovalResolvedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var currentInvocation = GetCurrentInvocationSnapshot();

        _logger.LogInformation("Received approval resolution for request {RequestId}. Approved: {Approved}",
            evt.RequestId,
            evt.Approved);

        _invocationRunner.ResolveApprovalResult(evt);

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

        InvocationState? snapshot = null;

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
            snapshot = Clone(CurrentInvocation);
        }

        _logger.LogInformation("Invocation {InvocationId} marked as cancelled.", evt.InvocationId);
        PublishStateChanged(snapshot);
        return Task.CompletedTask;
    }

    public Task ReportInvocationStreamChunkAsync(Guid invocationId, string chunk)
    {
        ArgumentException.ThrowIfNullOrEmpty(chunk);

        UpdateInvocation(invocationId,
            state =>
            {
                state.Status = InvocationStatus.Running;
                state.StreamedContent = string.Concat(state.StreamedContent, chunk);
                state.StreamedChunkCount++;
                return state;
            });

        return Task.CompletedTask;
    }

    public Task ReportInvocationThinkingChunkAsync(Guid invocationId, string chunk)
    {
        ArgumentException.ThrowIfNullOrEmpty(chunk);

        UpdateInvocation(invocationId,
            state =>
            {
                state.Status = InvocationStatus.Running;
                state.StreamedThinkingContent = string.Concat(state.StreamedThinkingContent, chunk);
                state.StreamedThinkingChunkCount++;
                return state;
            });

        return Task.CompletedTask;
    }

    public Task ReportInvocationCompletedAsync(Guid invocationId)
    {
        UpdateInvocation(invocationId,
            static state =>
            {
                state.Status = InvocationStatus.Completed;
                state.CompletedAt = DateTimeOffset.UtcNow;
                state.PendingApproval = null;
                state.PendingToolCalls = [];
                return state;
            });

        return Task.CompletedTask;
    }

    public Task ReportInvocationFailedAsync(Guid invocationId, string failureMessage, FailureCategory failureCategory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureMessage);

        UpdateInvocation(invocationId,
            state =>
            {
                state.Status = failureCategory == FailureCategory.Cancelled ? InvocationStatus.Cancelled : InvocationStatus.Failed;
                state.Error = failureMessage;
                state.FailureCategory = failureCategory;
                state.CompletedAt = DateTimeOffset.UtcNow;
                state.PendingApproval = null;
                state.PendingToolCalls = [];
                return state;
            });

        return Task.CompletedTask;
    }

    public Task ReportToolCallRequestedAsync(ToolCallRequestPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        UpdateInvocation(payload.InvocationId,
            state =>
            {
                state.PendingToolCalls =
                [
                    .. state.PendingToolCalls,
                    new InvocationToolCallState(payload.RequestId, payload.ToolName, payload.Parameters, DateTimeOffset.UtcNow)
                ];
                return state;
            });

        return Task.CompletedTask;
    }

    public Task ReportApprovalRequestedAsync(ApprovalRequestPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        UpdateInvocation(payload.InvocationId,
            state =>
            {
                state.PendingApproval = new InvocationApprovalState(payload.RequestId, payload.Description, DateTimeOffset.UtcNow);
                return state;
            });

        return Task.CompletedTask;
    }

    private async Task DispatchPlainInvocationAsync(RuntimePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        _logger.LogInformation("WorkerEventDispatcher handling plain InvocationAssignedV2. InvocationId={InvocationId} ConversationId={ConversationId}",
            package.InvocationId,
            package.ConversationId);

        using var context = InvocationExecutionContext.CreatePlain(package, Guid.Empty);

        await _remoteInvocationQueue.WaitAsync().ConfigureAwait(false);

        try
        {
            await RunQueuedInvocationAsync(context, package).ConfigureAwait(false);
        }
        finally
        {
            _ = _remoteInvocationQueue.Release();
        }
    }

    private async Task RunQueuedInvocationAsync(InvocationExecutionContext context, RuntimePackage runtimePackage)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(runtimePackage);

        InvocationState snapshot;

        lock (_syncRoot)
        {
            if (IsInvocationActive(CurrentInvocation))
            {
                _logger.LogWarning("Delaying invocation assignment for {InvocationId} because invocation {CurrentInvocationId} is still active.",
                    runtimePackage.InvocationId,
                    CurrentInvocation!.InvocationId);
            }

            CurrentInvocation = new InvocationState
            {
                InvocationId = runtimePackage.InvocationId,
                ConversationId = runtimePackage.ConversationId,
                Status = InvocationStatus.Assigned,
                StartedAt = DateTimeOffset.UtcNow,
                LastUpdatedAt = DateTimeOffset.UtcNow,
                ModelUsed = runtimePackage.ModelProfile
            };

            snapshot = Clone(CurrentInvocation);
        }

        _logger.LogInformation("Dispatched invocation assignment for {InvocationId}.", runtimePackage.InvocationId);
        PublishStateChanged(snapshot);

        await RunInvocationAsync(context).ConfigureAwait(false);
    }

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

    private async Task EmitInvocationKeyMismatchAsync(Guid messageId, string reason, string nodeKeyIdUsed)
    {
        _logger.LogWarning("Emitting invocation key mismatch for {MessageId}. Reason: {Reason}, key: {NodeKeyIdUsed}",
            messageId,
            reason,
            nodeKeyIdUsed);
        await _hubMessageSender.Value.SendInvocationKeyMismatchAsync(messageId, reason, nodeKeyIdUsed).ConfigureAwait(false);
    }

    private async Task EmitEncryptedFailureAsync(EncryptedRuntimePackageDto package, string error, FailureCategory failureCategory = FailureCategory.AgentRuntime)
    {
        await _hubMessageSender.Value.SendEncryptedFailedAsync(new EncryptedFailedEnvelopeV1
        {
            ConversationId = package.ConversationId,
            MessageId = package.MessageId,
            EpochVersion = package.EpochVersion,
            FailureCategory = failureCategory.ToString(),
            Error = error
        }).ConfigureAwait(false);
    }

    private static bool IsAadMismatch(InvalidOperationException exception)
    {
        return exception.Message.Contains("AAD", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsConfigHashMismatch(InvalidOperationException exception)
    {
        return exception.Message.Contains("runtime-package-config-hash-mismatch", StringComparison.Ordinal);
    }

    private static bool IsHistoryHashMismatch(InvalidOperationException exception)
    {
        return exception.Message.Contains("runtime-package-history-hash-mismatch", StringComparison.Ordinal);
    }

    private async Task RunInvocationAsync(InvocationExecutionContext context)
    {
        var package = context.Package;

        _logger.LogInformation("Starting invocation execution. InvocationId={InvocationId} ConversationId={ConversationId} Model={Model}",
            package.InvocationId,
            package.ConversationId,
            package.ModelProfile);

        UpdateInvocation(package.InvocationId,
            static state =>
            {
                state.Status = InvocationStatus.Running;
                return state;
            });

        try
        {
            await _invocationRunner.RunAsync(context).ConfigureAwait(false);

            UpdateInvocation(package.InvocationId,
                static state =>
                {
                    if (state.Status is InvocationStatus.Assigned or InvocationStatus.Running)
                    {
                        state.Status = InvocationStatus.Completed;
                        state.CompletedAt = DateTimeOffset.UtcNow;
                    }

                    return state;
                });

            _logger.LogInformation("Invocation execution completed successfully. InvocationId={InvocationId}", package.InvocationId);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Invocation {InvocationId} failed before execution completed.", package.InvocationId);

            UpdateInvocation(package.InvocationId,
                state =>
                {
                    state.Status = InvocationStatus.Failed;
                    state.Error = exception.Message;
                    state.FailureCategory = FailureCategory.Unexpected;
                    state.CompletedAt = DateTimeOffset.UtcNow;
                    return state;
                });
        }
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

        InvocationState? snapshot = null;

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
}
