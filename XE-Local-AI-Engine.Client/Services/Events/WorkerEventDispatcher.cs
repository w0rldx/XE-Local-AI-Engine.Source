namespace XE_Local_AI_Engine.Client.Services.Events;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Models.Events;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Invocation.RuntimeEnvelope;

public sealed class WorkerEventDispatcher : IWorkerEventDispatcher
{
    private const string AadMismatchReason = "aad-mismatch";
    private const string RetiredKeyReason = "retired-key";

    private readonly Lazy<IHubMessageSender> _hubMessageSender;
    private readonly IInvocationRunner _invocationRunner;
    private readonly ILogger<WorkerEventDispatcher> _logger;
    private readonly INodeKeyRegistry _nodeKeyRegistry;
    private readonly IRuntimePackageEnvelopeAssembler _runtimePackageEnvelopeAssembler;
    private readonly object _syncRoot = new();

    public WorkerEventDispatcher(IInvocationRunner invocationRunner,
        IRuntimePackageEnvelopeAssembler runtimePackageEnvelopeAssembler,
        Lazy<IHubMessageSender> hubMessageSender,
        INodeKeyRegistry nodeKeyRegistry,
        ILogger<WorkerEventDispatcher> logger)
    {
        _invocationRunner = invocationRunner ?? throw new ArgumentNullException(nameof(invocationRunner));
        _runtimePackageEnvelopeAssembler = runtimePackageEnvelopeAssembler ?? throw new ArgumentNullException(nameof(runtimePackageEnvelopeAssembler));
        _hubMessageSender = hubMessageSender ?? throw new ArgumentNullException(nameof(hubMessageSender));
        _nodeKeyRegistry = nodeKeyRegistry ?? throw new ArgumentNullException(nameof(nodeKeyRegistry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public event EventHandler<InvocationStateChangedEventArgs>? InvocationStateChanged;

    public InvocationState? CurrentInvocation { get; private set; }

    public async Task DispatchInvocationAssignedAsync(EncryptedRuntimePackageDto package)
    {
        ArgumentNullException.ThrowIfNull(package);

        _logger.LogInformation(
            "WorkerEventDispatcher handling InvocationAssigned. InvocationId={InvocationId} ConversationId={ConversationId} MessageId={MessageId} EpochVersion={EpochVersion}",
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
            await EmitEncryptedFailureAsync(package, "runtime-package-config-hash-mismatch").ConfigureAwait(false);
            return;
        }
        catch (InvalidOperationException exception) when (IsHistoryHashMismatch(exception))
        {
            _logger.LogWarning(exception, "History hash mismatch for message {MessageId}.", package.MessageId);
            await EmitEncryptedFailureAsync(package, "runtime-package-history-hash-mismatch").ConfigureAwait(false);
            return;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Assemble runtime package failed. InvocationId={InvocationId}", package.InvocationId);
            throw;
        }

        using var _ = context;
        var runtimePackage = context.Package;

        InvocationState snapshot;

        lock (_syncRoot)
        {
            if (IsInvocationActive(CurrentInvocation))
            {
                _logger.LogWarning("Ignoring invocation assignment for {InvocationId} because invocation {CurrentInvocationId} is still active.",
                    runtimePackage.InvocationId,
                    CurrentInvocation!.InvocationId);

                return;
            }

            CurrentInvocation = new InvocationState
            {
                InvocationId = runtimePackage.InvocationId,
                ConversationId = runtimePackage.ConversationId,
                Status = InvocationStatus.Assigned,
                StartedAt = DateTimeOffset.UtcNow,
                ModelUsed = runtimePackage.ModelProfile
            };

            snapshot = Clone(CurrentInvocation);
        }

        _logger.LogInformation("Dispatched invocation assignment for {InvocationId}.", runtimePackage.InvocationId);
        PublishStateChanged(snapshot);

        await RunInvocationAsync(context).ConfigureAwait(false);
    }

    public Task DispatchToolCallResultAsync(ToolCallResultEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        _logger.LogInformation("Received tool call result for request {RequestId}.", evt.RequestId);
        _invocationRunner.ResolveToolCallResult(evt);
        return Task.CompletedTask;
    }

    public Task DispatchDisconnectRequestedAsync(DisconnectRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        _logger.LogInformation("Received disconnect request: {Reason}", evt.Reason);
        _invocationRunner.CancelAll();

        InvocationState? snapshot = null;

        lock (_syncRoot)
        {
            if (IsInvocationActive(CurrentInvocation))
            {
                CurrentInvocation!.Status = InvocationStatus.Cancelled;
                CurrentInvocation.Error = evt.Reason;
                snapshot = Clone(CurrentInvocation);
            }
        }

        if (snapshot is not null)
        {
            PublishStateChanged(snapshot);
        }

        return Task.CompletedTask;
    }

    public Task DispatchApprovalResolvedAsync(ApprovalResolvedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        _logger.LogInformation("Received approval resolution for request {RequestId}. Approved: {Approved}",
            evt.RequestId,
            evt.Approved);

        return Task.CompletedTask;
    }

    public Task DispatchInvocationCancelledAsync(InvocationCancelledEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        _invocationRunner.Cancel(evt.InvocationId);

        InvocationState? snapshot = null;

        lock (_syncRoot)
        {
            if (CurrentInvocation?.InvocationId != evt.InvocationId)
            {
                _logger.LogDebug("Ignoring cancellation for {InvocationId} because it does not match the current invocation.",
                    evt.InvocationId);

                return Task.CompletedTask;
            }

            CurrentInvocation.Status = InvocationStatus.Cancelled;
            CurrentInvocation.Error = evt.Reason;
            snapshot = Clone(CurrentInvocation);
        }

        _logger.LogInformation("Invocation {InvocationId} marked as cancelled.", evt.InvocationId);
        PublishStateChanged(snapshot);
        return Task.CompletedTask;
    }

    private static InvocationState Clone(InvocationState state)
    {
        return new InvocationState
        {
            InvocationId = state.InvocationId,
            ConversationId = state.ConversationId,
            Status = state.Status,
            StreamedContent = state.StreamedContent,
            StartedAt = state.StartedAt,
            Error = state.Error,
            ModelUsed = state.ModelUsed
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

    private async Task EmitEncryptedFailureAsync(EncryptedRuntimePackageDto package, string error)
    {
        await _hubMessageSender.Value.SendEncryptedFailedAsync(new EncryptedFailedEnvelopeV1
        {
            ConversationId = package.ConversationId,
            MessageId = package.MessageId,
            EpochVersion = package.EpochVersion,
            FailureCategory = nameof(FailureCategory.AgentRuntime),
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
                    }

                    return state;
                });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Invocation {InvocationId} failed before execution completed.", package.InvocationId);

            UpdateInvocation(package.InvocationId,
                state =>
                {
                    state.Status = InvocationStatus.Failed;
                    state.Error = exception.Message;
                    return state;
                });
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
            snapshot = Clone(CurrentInvocation);
        }

        if (snapshot is not null)
        {
            PublishStateChanged(snapshot);
        }
    }

    private void PublishStateChanged(InvocationState state)
    {
        Volatile.Read(ref InvocationStateChanged)?.Invoke(this, new InvocationStateChangedEventArgs(Clone(state)));
    }
}
