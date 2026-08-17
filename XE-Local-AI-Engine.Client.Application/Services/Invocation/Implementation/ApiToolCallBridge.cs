namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Events;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     The request/response bridge for API-side (platform) tool calls: registers the call, optionally runs the
///     approval round-trip, ships the request over the hub, and waits for the result event that releases it. Shares
///     the one <see cref="PendingToolCallRegistry" /> with <see cref="ToolApprovalCoordinator" /> and
///     <see cref="InvocationRunner" />, so a call registered here is visible to the approval resolve, the runner's
///     cancel/drain path, and the stale sweep alike.
///     <para>
///         A singleton for the same reason the coordinator is: the result arrives on a different call stack than the
///         turn waiting for it, and the stale sweep runs from a background service.
///     </para>
/// </summary>
public sealed class ApiToolCallBridge
{
    // The SAME dictionary instance the runner and ToolApprovalCoordinator hold (see PendingToolCallRegistry).
    private readonly ConcurrentDictionary<string, PendingToolCall> _pendingToolCalls;

    // The effective tool-result wait budget for each active invocation, seeded from the package's
    // ToolCallTimeoutSeconds when RunAsync starts. ExecuteApiToolCallAsync (which only carries the invocation id) reads
    // it here so a package-scoped tool timeout wins over the node-global _maxPendingToolCallAge; absent an entry (a
    // tool call outside an active invocation) it falls back to the node-global age.
    private readonly ConcurrentDictionary<Guid, TimeSpan> _toolResultTimeoutsByInvocation = new();

    private readonly Lazy<IWorkerEventDispatcher> _eventDispatcher;

    private readonly Lazy<IHubMessageSender> _hubSender;

    private readonly TimeSpan _maxPendingToolCallAge;

    public ApiToolCallBridge(Lazy<IHubMessageSender> hubSender,
        Lazy<IWorkerEventDispatcher> eventDispatcher,
        PendingToolCallRegistry pendingToolCallRegistry,
        INodeRuntimeSettings runtimeSettings)
    {
        _hubSender = hubSender ?? throw new ArgumentNullException(nameof(hubSender));
        _eventDispatcher = eventDispatcher ?? throw new ArgumentNullException(nameof(eventDispatcher));
        ArgumentNullException.ThrowIfNull(pendingToolCallRegistry);
        _pendingToolCalls = pendingToolCallRegistry.Calls;
        ArgumentNullException.ThrowIfNull(runtimeSettings);
        _maxPendingToolCallAge = TimeSpan.FromMinutes(runtimeSettings.GetMaxPendingToolCallAgeMinutes());
    }

    public Task<string> ExecuteApiToolCallAsync(Guid invocationId,
        string toolName,
        string parameters,
        CancellationToken cancellationToken = default)
    {
        // Default to the approval-gated path; the per-tool overload below is what BuildInvocationTools wires in,
        // passing the tool's RequiresApproval flag so non-approval tools auto-execute.
        return ExecuteApiToolCallAsync(invocationId, toolName, parameters, requiresApproval: true, cancellationToken);
    }

    public async Task<string> ExecuteApiToolCallAsync(Guid invocationId,
        string toolName,
        string parameters,
        bool requiresApproval,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(parameters);

        var requestId = Guid.NewGuid().ToString("N");
        var approvalCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resultCompletion = new TaskCompletionSource<ToolCallResultEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingToolCall = new PendingToolCall(invocationId, DateTimeOffset.UtcNow, approvalCompletion, resultCompletion);
        var sender = _hubSender.Value;
        var dispatcher = _eventDispatcher.Value;

        if (!_pendingToolCalls.TryAdd(requestId, pendingToolCall))
        {
            throw new InvalidOperationException("Failed to register pending tool call.");
        }

        // Tracks whether the Requested lifecycle phase was emitted so the timeout/cancel catch paths can emit a
        // matching Completed (IsError=true) exactly once. The React UI only clears a tool card on Completed, so a
        // timed-out tool without this would stay stuck in requesting/waiting forever.
        var requestedLifecycleEmitted = false;

        try
        {
            var payload = new ToolCallRequestPayload
            {
                InvocationId = invocationId,
                RequestId = requestId,
                ToolName = toolName,
                Parameters = parameters
            };

            // Approval gating: only tools that opt in (RequiresApproval) run the approval round-trip. All beta
            // tools ship as non-approval, so this branch is dormant today but keeps the wiring in place for a
            // future approval UI.
            if (requiresApproval)
            {
                var approvalPayload = new ApprovalRequestPayload
                {
                    InvocationId = invocationId,
                    RequestId = requestId,
                    Description = $"Tool '{toolName}' requested with parameters: {parameters}"
                };

                await sender.SendApprovalRequestAsync(approvalPayload, cancellationToken).ConfigureAwait(false);
                await dispatcher.ReportApprovalRequestedAsync(approvalPayload).ConfigureAwait(false);

                // Surface the pending approval on the LOCAL chat stream. This API-tool path emits its
                // tool-call-requested lifecycle only AFTER approval, so the browser has no card yet — the CallId is the
                // request id, and the reducer creates the waiting card from this event.
                await dispatcher.ReportApprovalLifecycleAsync(new ApprovalLifecyclePayload
                {
                    InvocationId = invocationId,
                    RequestId = requestId,
                    CallId = requestId,
                    ToolName = toolName,
                    Description = approvalPayload.Description
                }).ConfigureAwait(false);

                using var approvalTimeoutCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                approvalTimeoutCancellationTokenSource.CancelAfter(_maxPendingToolCallAge);

                var approved = await approvalCompletion.Task.WaitAsync(approvalTimeoutCancellationTokenSource.Token).ConfigureAwait(false);
                if (!approved)
                {
                    throw new WorkerToolCallException(toolName, "Tool call was rejected by the user.");
                }
            }

            await sender.SendToolCallRequestAsync(payload,
                cancellationToken).ConfigureAwait(false);
            await dispatcher.ReportToolCallRequestedAsync(payload).ConfigureAwait(false);
            await dispatcher.ReportToolCallLifecycleAsync(new ToolCallLifecyclePayload
            {
                InvocationId = invocationId,
                ToolCallId = requestId,
                ToolName = toolName,
                Phase = ToolCallLifecyclePhase.Requested,
                Arguments = parameters,
                RequiresApproval = requiresApproval
            }).ConfigureAwait(false);
            requestedLifecycleEmitted = true;

            // The tool-RESULT wait honours the active package's ToolCallTimeoutSeconds (falling back to the node-global
            // age when the call is not tied to an active invocation). The approval wait above intentionally keeps the
            // node-global age: it bounds a human decision, not tool execution, so it must not shrink to the short
            // per-tool budget.
            using var timeoutCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellationTokenSource.CancelAfter(ResolveToolResultTimeout(invocationId));

            var result = await resultCompletion.Task.WaitAsync(timeoutCancellationTokenSource.Token).ConfigureAwait(false);
            var isError = !string.IsNullOrWhiteSpace(result.Error);

            await dispatcher.ReportToolCallLifecycleAsync(new ToolCallLifecyclePayload
            {
                InvocationId = invocationId,
                ToolCallId = requestId,
                ToolName = toolName,
                Phase = ToolCallLifecyclePhase.Completed,
                Result = isError ? result.Error : result.Result,
                IsError = isError
            }).ConfigureAwait(false);

            if (isError)
            {
                throw new WorkerToolCallException(toolName, result.Error!);
            }

            return result.Result;
        }
        catch (TimeoutException timeoutException)
        {
            await TryEmitTimeoutCompletedLifecycleAsync(dispatcher, requestedLifecycleEmitted, invocationId, requestId, toolName, timeoutException.Message).ConfigureAwait(false);
            throw new WorkerToolCallException(toolName, timeoutException.Message, timeoutException);
        }
        catch (OperationCanceledException operationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            const string TimeoutReason = "Tool call timed out waiting for a result.";
            await TryEmitTimeoutCompletedLifecycleAsync(dispatcher, requestedLifecycleEmitted, invocationId, requestId, toolName, TimeoutReason).ConfigureAwait(false);
            throw new WorkerToolCallException(toolName, TimeoutReason, operationCanceledException);
        }
        finally
        {
            _pendingToolCalls.TryRemove(requestId, out _);
        }
    }

    public void CleanupStaleToolCalls(TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow - maxAge;

        foreach (var pendingToolCall in _pendingToolCalls)
        {
            if (pendingToolCall.Value.CreatedAt >= cutoff)
            {
                continue;
            }

            if (_pendingToolCalls.TryRemove(pendingToolCall.Key, out var removedPendingToolCall))
            {
                var timeoutException = new TimeoutException("Tool call timed out during cleanup.");
                removedPendingToolCall.ApprovalCompletion.TrySetException(timeoutException);
                removedPendingToolCall.ResultCompletion.TrySetException(timeoutException);
            }
        }
    }

    // Seeded by InvocationRunner.RunAsync from the package's resolved TurnPolicy, and cleared again when the turn
    // ends. ExecuteApiToolCallAsync only carries the invocation id, so this is how a package-scoped tool timeout reaches
    // it at all.
    public void SetToolResultTimeout(Guid invocationId, TimeSpan toolResultTimeout)
    {
        _toolResultTimeoutsByInvocation[invocationId] = toolResultTimeout;
    }

    public void ClearToolResultTimeout(Guid invocationId)
    {
        _toolResultTimeoutsByInvocation.TryRemove(invocationId, out _);
    }

    private TimeSpan ResolveToolResultTimeout(Guid invocationId)
    {
        return _toolResultTimeoutsByInvocation.TryGetValue(invocationId, out var timeout)
            ? timeout
            : _maxPendingToolCallAge;
    }

    // Mirrors the normal Completed lifecycle emission for the timeout/cancel rethrow paths, emitting Completed with
    // IsError=true so a tool card the UI parked on Requested gets cleared instead of spinning forever. Skips when no
    // Requested was emitted (e.g. a timeout during the approval wait), so Completed never fires without a Requested.
    private static async Task TryEmitTimeoutCompletedLifecycleAsync(IWorkerEventDispatcher dispatcher,
        bool requestedLifecycleEmitted,
        Guid invocationId,
        string requestId,
        string toolName,
        string error)
    {
        if (!requestedLifecycleEmitted)
        {
            return;
        }

        await dispatcher.ReportToolCallLifecycleAsync(new ToolCallLifecyclePayload
        {
            InvocationId = invocationId,
            ToolCallId = requestId,
            ToolName = toolName,
            Phase = ToolCallLifecyclePhase.Completed,
            Result = error,
            IsError = true
        }).ConfigureAwait(false);
    }
}
