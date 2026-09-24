namespace XE_Local_AI_Engine.Client.Services.Integrations;

using System.Diagnostics;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Terminalization half of <see cref="IntegrationExecutionCoordinator" />: the status/version CAS that closes an
///     execution row exactly once, and the paths that reach it.
/// </summary>
/// <remarks>
///     It holds the CAS retry loop, the stop-marker reconciliation, the audit row carried into the same transaction,
///     and the pre-run, fault and stranded entry points.
/// </remarks>
internal sealed partial class IntegrationExecutionCoordinator
{
    /// <summary>
    ///     The last resort for a dispatched execution whose every attempt escaped its own handler: re-read the row and
    ///     close it through the ordinary fault path.
    /// </summary>
    /// <remarks>
    ///     The admission slot it holds is released by that close, and its caller gets a terminal event instead of
    ///     silence.
    /// </remarks>
    private async Task TerminalizeStrandedAsync(Guid executionId)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IIntegrationExecutionStore>();
            var row = await store.GetByIdAsync(executionId, CancellationToken.None);
            if (row is null || !NonTerminalStatuses.Contains(row.Status))
            {
                return;
            }

            var context = new ExecutionRunContext(store, row);
            await TerminalizeFromFaultAsync(context,
                IntegrationExecutionStatus.Failed,
                IntegrationFailureCategories.InternalFailure,
                "The execution could not be dispatched.");
        }
        catch (Exception exception)
        {
            // Everything else already failed; the next restart sweep is genuinely the last line here.
            _logger.LogError(exception, "Integration execution {ExecutionId} could not be terminalized after its dispatch faults.", executionId);
        }
    }

    /// <summary>
    ///     Terminalizes a row that never reached <c>Running</c>. A legal <c>Accepted|Queued -> Failed</c> edge for
    ///     exactly the pre-run rejections; an ordinary failure does NOT have to pass through <c>Running</c> first.
    /// </summary>
    private Task TerminalizeBeforeRunAsync(ExecutionRunContext context, string failureCategory, string failureSummary) =>
        TerminalizeAsync(context, BeforeRunStatuses, IntegrationExecutionStatus.Failed, failureCategory, failureSummary);

    /// <summary>
    ///     The safety net for a throw anywhere in the pipeline: re-read the row so the CAS carries a version that is
    ///     actually current, and terminalize whatever non-terminal status it is in.
    /// </summary>
    private async Task TerminalizeFromFaultAsync(ExecutionRunContext context,
        IntegrationExecutionStatus status,
        string? failureCategory,
        string? failureSummary)
    {
        try
        {
            var row = await context.Store.GetByIdAsync(context.ExecutionId, CancellationToken.None);
            if (row is null || !NonTerminalStatuses.Contains(row.Status))
            {
                return;
            }

            if (!_buffer.IsTracked(context.ExecutionId) && !_buffer.TryCreate(context.ExecutionId, row.LastSequence))
            {
                _logger.LogWarning("The event buffer refused an entry for integration execution {ExecutionId}; it stays non-terminal for the next restart.", context.ExecutionId);
                return;
            }

            context.Version = row.Version;

            // The re-read is this path's own first look at the row, so it honours a marker the caller's outcome
            // predates before it ever attempts a CAS.
            var (marked, markedCategory, markedSummary) = HonourStopMarker(row, status, failureCategory, failureSummary);
            _ = await TerminalizeAsync(context, NonTerminalStatuses, marked, markedCategory, markedSummary);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Integration execution {ExecutionId} could not be terminalized after a fault.", context.ExecutionId);
        }
    }

    /// <summary>
    ///     The ONE terminal shape: reserve a sequence privately, commit the status and the event together, and only
    ///     then publish.
    /// </summary>
    /// <remarks>
    ///     A lost CAS or a throw abandons the reservation: an unresolved one is not a hole readers tolerate but a stall that
    ///     parks every reader on this execution until the entry is evicted. Whoever's CAS returns <see langword="true" /> owns the
    ///     terminal artefacts — the published event and the one kind-3 audit row — so a queued cancel cannot produce two cancelled
    ///     events and two audit rows. The retries are bounded rather than single because each cancel bumps the row's version
    ///     without terminalizing it, and every reload re-runs <see cref="HonourStopMarker" />.
    /// </remarks>
    private async Task<bool> TerminalizeAsync(ExecutionRunContext context,
        IReadOnlySet<IntegrationExecutionStatus> expectedStatuses,
        IntegrationExecutionStatus status,
        string? failureCategory,
        string? failureSummary)
    {
        var outcome = (Status: status, FailureCategory: failureCategory, FailureSummary: failureSummary);

        for (var attempt = 1; attempt <= MaxTerminalAttempts; attempt++)
        {
            if (await TryTerminalizeOnceAsync(context, expectedStatuses, outcome.Status, outcome.FailureCategory, outcome.FailureSummary))
            {
                return true;
            }

            var fresh = await context.Store.GetByIdAsync(context.ExecutionId, CancellationToken.None);
            if (fresh is null || !expectedStatuses.Contains(fresh.Status) || fresh.Version == context.Version)
            {
                // Gone, already terminal, or a loss no reload explains: retrying the same version would only lose the
                // same way.
                return false;
            }

            context.Version = fresh.Version;
            outcome = HonourStopMarker(fresh, outcome.Status, outcome.FailureCategory, outcome.FailureSummary);
        }

        return false;
    }

    /// <summary>
    ///     A durable stop marker outranks the FAILURE a terminal path came with: the cancel primitive stamps it first,
    ///     so the row has an accepted 202 behind it and closing it as a failure shows a fault the caller never caused.
    /// </summary>
    /// <remarks>
    ///     A <c>Completed</c> outcome is the one exception and is left alone: its assistant turn is persisted and its
    ///     <c>external.output</c> payloads are committed, so a row reading <c>Cancelled</c> over them would contradict
    ///     every artefact the caller can read. The cancel endpoint's 202 says the stop was REQUESTED, never that it
    ///     arrived in time.
    /// </remarks>
    private static (IntegrationExecutionStatus Status, string? FailureCategory, string? FailureSummary) HonourStopMarker(IntegrationExecutionSnapshot row,
        IntegrationExecutionStatus status,
        string? failureCategory,
        string? failureSummary) =>
        row.StopRequestedAtUtc is not null && status is not (IntegrationExecutionStatus.Cancelled or IntegrationExecutionStatus.Completed)
            ? (IntegrationExecutionStatus.Cancelled, null, null)
            : (status, failureCategory, failureSummary);

    private async Task<bool> TryTerminalizeOnceAsync(ExecutionRunContext context,
        IReadOnlySet<IntegrationExecutionStatus> expectedStatuses,
        IntegrationExecutionStatus status,
        string? failureCategory,
        string? failureSummary)
    {
        var eventType = status switch
        {
            IntegrationExecutionStatus.Completed => IntegrationStreamEventTypes.ExecutionCompleted,
            IntegrationExecutionStatus.Cancelled => IntegrationStreamEventTypes.ExecutionCancelled,
            _ => IntegrationStreamEventTypes.ExecutionFailed
        };

        var endedAtUtc = NowUnixMilliseconds();

        // ONE payload for both writes below. A terminal frame with a null payload tells an integrator nothing about
        // why the run ended, and the row would then carry a reason the stream never gave.
        var payload = status switch
        {
            // The run's own duration when the invocation reported one; otherwise the wall time since the request was
            // admitted, which includes the queue wait but is never absent.
            IntegrationExecutionStatus.Completed => IntegrationTerminalPayload.Completion(context.TotalTokens,
                context.RunDurationMs ?? Math.Max(val1: 0L, endedAtUtc - context.ReceivedAtUtc)),
            // `execution.cancelled` carries no payload by contract: a cancel is an outcome, not a failure.
            IntegrationExecutionStatus.Cancelled => (JsonElement?)null,
            _ => IntegrationTerminalPayload.Failure(failureCategory, failureSummary)
        };

        var sequence = _buffer.Reserve(context.ExecutionId);
        var published = false;
        try
        {
            // Terminal writes never carry the run's cancellation token: a shutdown must still be able to close the row.
            var won = await context.Store.TryTerminalizeAsync(new IntegrationTerminalizeCommand
                {
                    ExecutionId = context.ExecutionId,
                    ExpectedVersion = context.Version,
                    ExpectedStatuses = expectedStatuses,
                    NewStatus = status,
                    Sequence = sequence,
                    EventType = eventType,
                    EndedAtUtc = endedAtUtc,
                    FailureCategory = failureCategory,
                    FailureSummary = failureSummary,
                    EventDetailJson = payload?.GetRawText(),
                    Audit = BuildAudit(context, status, endedAtUtc)
                },
                CancellationToken.None);
            if (!won)
            {
                return false;
            }

            _buffer.Publish(new IntegrationStreamEvent
            {
                Type = eventType,
                Sequence = sequence,
                ExecutionId = context.ExecutionId,
                SessionId = context.SessionId,
                OccurredAtUtc = endedAtUtc,
                ContentType = null,
                Payload = payload
            });
            published = true;
            context.Version++;
            return true;
        }
        finally
        {
            if (!published)
            {
                _buffer.Abandon(context.ExecutionId, sequence);
            }
        }
    }

    /// <summary>
    ///     The ONE kind-3 audit row per execution, carried INTO the terminal command so the store inserts it in the
    ///     same transaction as the status and the terminal event.
    /// </summary>
    /// <remarks>
    ///     A separate write after the terminal commits cannot be recovered: every later terminalization rejects an
    ///     already-terminal row, so one swallowed failure loses the audit row for good. Content-free by contract — ids,
    ///     a trigger name, a credential prefix and a terminal status.
    /// </remarks>
    private static IntegrationInvocationAuditInput BuildAudit(ExecutionRunContext context, IntegrationExecutionStatus status, long endedAtUtc) =>
        new()
        {
            InvocationId = context.InvocationId,
            RequestId = context.RequestId,
            TriggerName = context.TriggerName,
            KeyPrefix = context.KeyPrefix,
            TargetAgentDefinitionId = context.TargetAgentDefinitionId,
            TerminalStatus = status switch
            {
                IntegrationExecutionStatus.Completed => NodeChatMessageStatusValues.Completed,
                IntegrationExecutionStatus.Cancelled => NodeChatMessageStatusValues.Cancelled,
                _ => NodeChatMessageStatusValues.Failed
            },
            TraceId = Activity.Current?.TraceId.ToString(),
            LatencyMs = Math.Max(val1: 0L, endedAtUtc - context.ReceivedAtUtc)
        };
}
