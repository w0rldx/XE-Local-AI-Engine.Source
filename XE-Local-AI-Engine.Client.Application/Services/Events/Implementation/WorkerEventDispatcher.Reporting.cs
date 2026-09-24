namespace XE_Local_AI_Engine.Client.Services.Events.Implementation;

using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;

public sealed partial class WorkerEventDispatcher
{
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
                // Stamped on a REAL transition only: the browser renders elapsed cold-load time from this value, so a re-report of the same
                // phase must not restart its clock. The injected clock matches UpdateInvocation's own LastUpdatedAt stamp.
                if (state.RuntimePhase != phase)
                {
                    state.RuntimePhaseChangedAtUtc = _timeProvider.GetUtcNow();
                }

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
                state.CompletedAt = _timeProvider.GetUtcNow();
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

    public Task ReportToolSchemaTokensAsync(Guid invocationId, long? toolSchemaTokens, int? maxToolSchemaTokens)
    {
        // A no-op when the id is not the current invocation, exactly like the phase report above.
        UpdateInvocation(invocationId,
            state =>
            {
                state.ToolSchemaTokens = toolSchemaTokens;
                state.MaxToolSchemaTokens = maxToolSchemaTokens;
                return state;
            });

        return Task.CompletedTask;
    }

    public Task ReportTurnTelemetryAsync(Guid invocationId, long? modelReadinessMs, TurnUsageTotals? usage)
    {
        // A no-op when the id is not the current invocation, exactly like the reports above.
        UpdateInvocation(invocationId,
            state =>
            {
                state.ModelReadinessMs = modelReadinessMs;
                // The TURN's summed usage, kept apart from the state's InputTokens/OutputTokens/... which ReportInvocationCompletedAsync fills
                // with the LAST round's counts. Both are persisted, to different rows: cost onto the envelope, occupancy onto the message.
                state.TurnInputTokens = usage?.InputTokens;
                state.TurnOutputTokens = usage?.OutputTokens;
                state.TurnTotalTokens = usage?.TotalTokens;
                state.TurnReasoningTokens = usage?.ReasoningTokens;
                return state;
            });

        return Task.CompletedTask;
    }

    public Task ReportTurnContextWindowAsync(Guid invocationId, int contextCapacityTokens, int reservedOutputTokens)
    {
        // A no-op when the id is not the current invocation, exactly like the reports above.
        UpdateInvocation(invocationId,
            state =>
            {
                state.ContextCapacityTokens = contextCapacityTokens;
                state.ReservedOutputTokens = reservedOutputTokens;
                return state;
            });

        return Task.CompletedTask;
    }

    public Task ReportEffortDispatchAsync(Guid invocationId, string dispatchedTier, string authoredEffort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dispatchedTier);
        ArgumentException.ThrowIfNullOrWhiteSpace(authoredEffort);

        // A no-op when the id is not the current invocation, exactly like the reports above.
        UpdateInvocation(invocationId,
            state =>
            {
                state.DispatchedTier = dispatchedTier;
                state.AuthoredEffort = authoredEffort;
                return state;
            });

        return Task.CompletedTask;
    }

    public Task ReportServedModelAsync(Guid invocationId, string modelUsed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelUsed);

        // A no-op when the id is not the current invocation, exactly like the reports above.
        UpdateInvocation(invocationId,
            state =>
            {
                state.ModelUsed = modelUsed;
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
                state.CompletedAt = _timeProvider.GetUtcNow();
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
                    new InvocationToolCallState
                    {
                        RequestId = payload.RequestId,
                        ToolName = payload.ToolName,
                        Parameters = payload.Parameters,
                        RequestedAt = _timeProvider.GetUtcNow()
                    }
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
                state.PendingApproval = new InvocationApprovalState
                {
                    RequestId = payload.RequestId,
                    Description = payload.Description,
                    RequestedAt = _timeProvider.GetUtcNow()
                };
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

        // Fold the runner's session-scope answer onto the pending-approval slot ReportApprovalRequestedAsync recorded; it cannot ride that
        // call, because ApprovalRequestPayload is the platform-hub contract. Without it the replay falls back to the tool catalog.
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

        // Record on the invocation state FIRST, then fan out: the state write is what a reconnecting browser is replayed from, so a client
        // that attaches in the gap still sees the pending question rather than missing both the live event and the snapshot.
        UpdateInvocation(payload.InvocationId,
            state =>
            {
                state.PendingQuestion = new InvocationUserQuestionState
                {
                    RequestId = payload.RequestId,
                    CallId = payload.CallId,
                    ToolName = payload.ToolName,
                    Questions = payload.Questions,
                    RequestedAt = _timeProvider.GetUtcNow()
                };
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
}
