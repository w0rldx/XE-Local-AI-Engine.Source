namespace XE_Local_AI_Engine.Client.Services.Events.Implementation;

using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Models.Events;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Invocation.RuntimePackage;

[SuppressMessage("Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "Registered for the application lifetime; disposing the service provider owns singleton cleanup.")]
/// <summary>
///     Represents worker event dispatcher.
/// </summary>
public sealed partial class WorkerEventDispatcher : IWorkerEventDispatcher
{
    private const string AadMismatchReason = "aad-mismatch";
    private const string RetiredKeyReason = "retired-key";

    private readonly Lazy<IHubMessageSender> _hubMessageSender;
    private readonly IInvocationHistory _invocationHistory;
    private readonly IInvocationRunner _invocationRunner;
    private readonly ILogger<WorkerEventDispatcher> _logger;
    private readonly INodeKeyRegistry _nodeKeyRegistry;
    private readonly SemaphoreSlim _remoteInvocationQueue = new(initialCount: 1, maxCount: 1);
    private readonly INodeChatRemotePersistenceCoordinator _remotePersistenceCoordinator;
    private readonly IRuntimePackageEnvelopeAssembler _runtimePackageEnvelopeAssembler;

    // Cancelled when the worker stops accepting remote invocations (drain), so a remote assignment still
    // BLOCKED on the invocation slot is abandoned instead of waiting forever — the previously uncancelable
    // `_remoteInvocationQueue.WaitAsync()` on the two remote paths could hang a draining node indefinitely. A running
    // invocation (past the wait) is unaffected: it runs under its own token, not this one.
    [SuppressMessage("Sonar",
        "S2930:\"IDisposables\" should be \"Dispose\"d",
        Justification =
            "App-lifetime singleton (see the CA1001 suppression on the type). It is Cancel()-only — no CancelAfter timer and its WaitHandle is never accessed — so it holds no unmanaged resource to reclaim before process exit.")]
    private readonly CancellationTokenSource _shutdownCts = new();

    private readonly Lock _syncRoot = new();
    private bool _isAcceptingRemoteInvocations = true;

    public WorkerEventDispatcher(IInvocationRunner invocationRunner,
        IRuntimePackageEnvelopeAssembler runtimePackageEnvelopeAssembler,
        Lazy<IHubMessageSender> hubMessageSender,
        INodeKeyRegistry nodeKeyRegistry,
        IInvocationHistory invocationHistory,
        INodeChatRemotePersistenceCoordinator remotePersistenceCoordinator,
        ILogger<WorkerEventDispatcher> logger)
    {
        _invocationRunner = invocationRunner ?? throw new ArgumentNullException(nameof(invocationRunner));
        _runtimePackageEnvelopeAssembler = runtimePackageEnvelopeAssembler ?? throw new ArgumentNullException(nameof(runtimePackageEnvelopeAssembler));
        _hubMessageSender = hubMessageSender ?? throw new ArgumentNullException(nameof(hubMessageSender));
        _nodeKeyRegistry = nodeKeyRegistry ?? throw new ArgumentNullException(nameof(nodeKeyRegistry));
        _invocationHistory = invocationHistory ?? throw new ArgumentNullException(nameof(invocationHistory));
        _remotePersistenceCoordinator = remotePersistenceCoordinator ?? throw new ArgumentNullException(nameof(remotePersistenceCoordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public event EventHandler<InvocationStateChangedEventArgs>? InvocationStateChanged;

    public event EventHandler<ToolCallLifecycleChangedEventArgs>? ToolCallLifecycleChanged;

    public event EventHandler<TurnNoticeChangedEventArgs>? TurnNoticeChanged;

    public event EventHandler<ApprovalRequestedChangedEventArgs>? ApprovalRequestedChanged;

    public event EventHandler<UserQuestionRequestedChangedEventArgs>? UserQuestionRequestedChanged;

    // The live invocation, mutated in place only under _syncRoot. Its StreamedContent/StreamedThinkingContent now
    // materialize from an immutable append-only accumulator, so an off-lock read is memory-safe (though it may observe a
    // transient value mid-append) — see IWorkerEventDispatcher.CurrentInvocation. Internal callers already hold _syncRoot
    // when they touch it; GetCurrentInvocationSnapshot returns a locked clone for anyone who needs a consistent copy.
    public InvocationState? CurrentInvocation { get; private set; }

    public bool IsAcceptingRemoteInvocations
    {
        get
        {
            lock (_syncRoot)
            {
                return _isAcceptingRemoteInvocations;
            }
        }
    }

    public void StopAcceptingRemoteInvocations()
    {
        lock (_syncRoot)
        {
            if (!_isAcceptingRemoteInvocations)
            {
                return;
            }

            _isAcceptingRemoteInvocations = false;
        }

        // Release any remote assignment still WAITING for the slot: at drain it must not start (it has not yet acquired
        // the slot). Fired once (guarded by the flag transition above). Cancelled outside the lock so continuations do
        // not run under it.
        _shutdownCts.Cancel();
        _logger.LogInformation("WorkerEventDispatcher stopped accepting new remote invocation assignments for shutdown drain.");
    }

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

    public Task ReportInvocationStreamChunkAsync(Guid invocationId, string chunk)
    {
        ArgumentException.ThrowIfNullOrEmpty(chunk);

        UpdateInvocation(invocationId,
            state =>
            {
                state.Status = InvocationStatus.Running;
                state.AppendStreamedContent(chunk);
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
                state.AppendStreamedThinkingContent(chunk);
                state.StreamedThinkingChunkCount++;
                return state;
            });

        return Task.CompletedTask;
    }

    public Task ReportInvocationPhaseAsync(Guid invocationId, InvocationRuntimePhase phase)
    {
        // The cold-load phases (PreparingRuntime/LoadingModel) fire BEFORE the stream-idle watchdog is armed, so a
        // legitimate load is visible instead of an apparent hang. A no-op when the id is not the current invocation.
        UpdateInvocation(invocationId,
            state =>
            {
                state.RuntimePhase = phase;
                return state;
            });

        return Task.CompletedTask;
    }

    public Task ReportInvocationCompletedAsync(Guid invocationId, int? inputTokens = null, int? outputTokens = null, int? totalTokens = null, int? reasoningTokens = null,
        long? generationDurationMs = null, string? finishReason = null, InvocationThroughput? throughput = null)
    {
        UpdateInvocation(invocationId,
            state =>
            {
                state.Status = InvocationStatus.Completed;
                state.CompletedAt = DateTimeOffset.UtcNow;
                state.InputTokens = inputTokens;
                state.OutputTokens = outputTokens;
                state.TotalTokens = totalTokens;
                state.ReasoningTokens = reasoningTokens;
                state.GenerationDurationMs = generationDurationMs;
                state.FinishReason = finishReason;
                state.Throughput = throughput;
                state.PendingApproval = null;
                state.PendingQuestion = null;
                state.PendingToolCalls = [];
                return state;
            });

        return Task.CompletedTask;
    }

    public Task ReportInvocationFailedAsync(Guid invocationId, string failureMessage, FailureCategory failureCategory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureMessage);

        if (failureCategory != FailureCategory.Cancelled)
        {
            NodeMetrics.InvocationFailedTotal.Add(delta: 1, new KeyValuePair<string, object?>("source", failureCategory.ToString()));
        }

        UpdateInvocation(invocationId,
            state =>
            {
                state.Status = failureCategory == FailureCategory.Cancelled ? InvocationStatus.Cancelled : InvocationStatus.Failed;
                state.Error = failureMessage;
                state.FailureCategory = failureCategory;
                state.CompletedAt = DateTimeOffset.UtcNow;
                state.PendingApproval = null;
                state.PendingQuestion = null;
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

    public Task ReportToolCallLifecycleAsync(ToolCallLifecyclePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        ToolCallLifecycleChanged?.Invoke(this, new ToolCallLifecycleChangedEventArgs(payload));

        return Task.CompletedTask;
    }

    public Task ReportTurnNoticeAsync(TurnNoticePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        TurnNoticeChanged?.Invoke(this, new TurnNoticeChangedEventArgs(payload));

        return Task.CompletedTask;
    }

    public Task ReportApprovalLifecycleAsync(ApprovalLifecyclePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        // Fold the runner's session-scope answer onto the pending-approval slot the preceding
        // ReportApprovalRequestedAsync recorded. It cannot ride that call: ApprovalRequestPayload is the platform-hub
        // contract and carries no such field. Without this the reconnect replay had nothing to send and the browser
        // fell back to the tool catalog — the exact fallback that offered a session scope for the skill tools.
        if (payload.SessionScopeEligible is { } sessionScopeEligible)
        {
            UpdateInvocation(payload.InvocationId,
                state =>
                {
                    if (state.PendingApproval is { } approval && string.Equals(approval.RequestId, payload.RequestId, StringComparison.Ordinal))
                    {
                        state.PendingApproval = approval with
                        {
                            SessionScopeEligible = sessionScopeEligible
                        };
                    }

                    return state;
                });
        }

        ApprovalRequestedChanged?.Invoke(this, new ApprovalRequestedChangedEventArgs(payload));

        return Task.CompletedTask;
    }

    public Task ReportUserQuestionAsync(UserQuestionLifecyclePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        // Record on the invocation state FIRST, then fan out. The state write is what a reconnecting browser is
        // replayed from, so doing it first means a client that attaches in the gap still sees the pending question
        // rather than missing both the live event and the snapshot.
        UpdateInvocation(payload.InvocationId,
            state =>
            {
                state.PendingQuestion = new InvocationUserQuestionState(payload.RequestId,
                    payload.CallId,
                    payload.ToolName,
                    payload.Questions,
                    DateTimeOffset.UtcNow);
                return state;
            });

        UserQuestionRequestedChanged?.Invoke(this, new UserQuestionRequestedChangedEventArgs(payload));

        return Task.CompletedTask;
    }

    public Task DispatchUserQuestionAnsweredAsync(UserQuestionAnsweredEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        // Content-free log: the request id only. The answers are the operator's words and never reach the log.
        _logger.LogInformation("Received user-question answers for request {RequestId}.", evt.RequestId);

        _invocationRunner.ResolveUserQuestionResult(evt);

        UpdateCurrentInvocation(state =>
        {
            if (state.PendingQuestion is not null
                && string.Equals(state.PendingQuestion.RequestId, evt.RequestId, StringComparison.Ordinal))
            {
                state.PendingQuestion = null;
            }
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

        try
        {
            await _remoteInvocationQueue.WaitAsync(_shutdownCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Abandoning queued remote invocation {InvocationId}: the worker is draining for shutdown and will not start new queued work.", package.InvocationId);
            return;
        }

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

            snapshot = CurrentInvocation.Clone();
        }

        _logger.LogInformation("Dispatched invocation assignment for {InvocationId}.", runtimePackage.InvocationId);
        PublishStateChanged(snapshot);

        await RunInvocationWithRemotePersistenceAsync(context, runtimePackage).ConfigureAwait(false);
    }

    /// <summary>
    ///     Runs a platform-served invocation while persisting its chat content to node SQLite with Origin=Remote.
    ///     The dispatcher stays thin: it opens a persistence session (ensure-conversation + user/assistant rows),
    ///     fans this invocation's <see cref="InvocationStateChanged" /> deltas into the shared pump via the session,
    ///     then terminalizes. All persistence translation lives in the coordinator/pump, not here.
    /// </summary>
    private async Task RunInvocationWithRemotePersistenceAsync(InvocationExecutionContext context, RuntimePackage runtimePackage)
    {
        NodeChatRemotePersistenceSession? session;

        try
        {
            session = await _remotePersistenceCoordinator.BeginAsync(runtimePackage, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Persistence is best-effort relative to the agent run: never block/fail a platform invocation
            // because the node-local mirror could not be written. Run without persistence in that case.
            _logger.LogError(exception, "Failed to begin remote persistence for invocation {InvocationId}; running without node-local persistence.", runtimePackage.InvocationId);
            await RunInvocationAsync(context).ConfigureAwait(false);
            return;
        }

        if (session is null)
        {
            // The assistant row reached a terminal status before it could be marked streaming (e.g. an early cancel), so
            // there is nothing to persist into. Run the invocation without the node-local mirror rather than driving the
            // pump against a terminal row.
            _logger.LogInformation("Remote persistence session not opened for invocation {InvocationId} (assistant row already terminal); running without node-local persistence.",
                runtimePackage.InvocationId);
            await RunInvocationAsync(context).ConfigureAwait(false);
            return;
        }

        var stateChannel = Channel.CreateUnbounded<InvocationState>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        void OnInvocationStateChanged(object? _, InvocationStateChangedEventArgs args)
        {
            if (args.State.InvocationId == runtimePackage.InvocationId)
            {
                stateChannel.Writer.TryWrite(args.State);
            }
        }

        InvocationStateChanged += OnInvocationStateChanged;
        var persistenceTask = DrainRemotePersistenceAsync(session, stateChannel.Reader, runtimePackage.InvocationId);

        try
        {
            await RunInvocationAsync(context).ConfigureAwait(false);
        }
        finally
        {
            InvocationStateChanged -= OnInvocationStateChanged;
            stateChannel.Writer.TryComplete();
            await persistenceTask.ConfigureAwait(false);
        }
    }

    private async Task DrainRemotePersistenceAsync(NodeChatRemotePersistenceSession session,
        ChannelReader<InvocationState> stateReader,
        Guid invocationId)
    {
        var terminalPersisted = false;

        try
        {
            await foreach (var state in stateReader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                terminalPersisted = await session.ApplyAsync(state, CancellationToken.None).ConfigureAwait(false);
                if (terminalPersisted)
                {
                    break;
                }
            }

            if (!terminalPersisted)
            {
                // The run ended without a terminal state reaching us (process/stream loss). Terminalize the
                // node-local mirror as interrupted so it does not hang in a non-terminal state.
                await session.TerminalizeInterruptedAsync(false).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Remote persistence drain failed for invocation {InvocationId}.", invocationId);
        }
    }

    private async Task EmitInvocationKeyMismatchAsync(Guid messageId, string reason, string nodeKeyIdUsed)
    {
        _logger.LogWarning("Emitting invocation key mismatch for {MessageId}. Reason: {Reason}, key: {NodeKeyIdUsed}",
            messageId,
            reason,
            nodeKeyIdUsed);
        await _hubMessageSender.Value.SendInvocationKeyMismatchAsync(messageId, reason, nodeKeyIdUsed, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task EmitEncryptedFailureAsync(EncryptedRuntimePackageDto package, string error, FailureCategory failureCategory = FailureCategory.AgentRuntime)
    {
        if (failureCategory == FailureCategory.HashMismatch)
        {
            NodeMetrics.EnvelopeHashMismatchTotal.Add(delta: 1, new KeyValuePair<string, object?>("reason", error));
        }

        await _hubMessageSender.Value.SendEncryptedFailedAsync(new EncryptedFailedEnvelopeV1
        {
            ConversationId = package.ConversationId,
            MessageId = package.MessageId,
            EpochVersion = package.EpochVersion,
            FailureCategory = failureCategory.ToString(),
            Error = error
        }, CancellationToken.None).ConfigureAwait(false);
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
            await _invocationRunner.RunAsync(context, CancellationToken.None).ConfigureAwait(false);

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

    /// <summary>
    ///     TEST-ONLY: clears <see cref="CurrentInvocation" /> back to null under the dispatcher's lock.
    ///     Production never resets the slot (it is only ever assigned), so e2e tests that share a single
    ///     <see cref="WorkerEventDispatcher" /> via <c>PerTestSession</c> use this to stop a completed
    ///     chat's invocation from leaking into the Invocations empty-state assertions. Exposed to the e2e
    ///     test assembly via <c>InternalsVisibleTo</c>; not part of the public contract.
    /// </summary>
    internal void ResetForTests()
    {
        lock (_syncRoot)
        {
            CurrentInvocation = null;
        }
    }
}
