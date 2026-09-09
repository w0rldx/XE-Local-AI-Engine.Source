namespace XE_Local_AI_Engine.Client.Services.Integrations;

using System.Diagnostics;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Terminalization half of <see cref="IntegrationExecutionCoordinator" />: the status/version CAS that closes an
///     execution row exactly once, its retry loop, the stop-marker reconciliation, the audit row carried into the same
///     transaction, and the pre-run, fault and stranded entry points that reach it.
/// </summary>
internal sealed partial class IntegrationExecutionCoordinator
{
    /// <summary>
    ///     The last resort for a dispatched execution whose every attempt escaped its own handler: re-read the row and
    ///     close it through the ordinary fault path, so the admission slot it holds is released and its caller gets a
    ///     terminal event instead of silence.
    /// </summary>
    private async Task TerminalizeStrandedAsync(Guid executionId)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IIntegrationExecutionStore>();
            var row = await store.GetByIdAsync(executionId, CancellationToken.None).ConfigureAwait(false);
            if (row is null || !NonTerminalStatuses.Contains(row.Status))
            {
                return;
            }

            var context = new ExecutionRunContext(store, row);
            await TerminalizeFromFaultAsync(context,
                    IntegrationExecutionStatus.Failed,
                    IntegrationFailureCategories.InternalFailure,
                    "The execution could not be dispatched.")
                .ConfigureAwait(false);
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
            var row = await context.Store.GetByIdAsync(context.ExecutionId, CancellationToken.None).ConfigureAwait(false);
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
            _ = await TerminalizeAsync(context, NonTerminalStatuses, marked, markedCategory, markedSummary).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Integration execution {ExecutionId} could not be terminalized after a fault.", context.ExecutionId);
        }
    }

    /// <summary>
    ///     The ONE terminal shape: reserve a sequence privately, commit the status and the event together, and only
    ///     then publish. A lost CAS or a throw abandons the reservation — an unresolved one is not a hole readers
    ///     tolerate, it is a stall that parks every reader on this execution until the entry is evicted.
    ///     <para>
    ///         Whoever's CAS returns <see langword="true" /> owns the terminal artefacts: the published event and the
    ///         one kind-3 audit row. A caller that finds the row already terminal publishes nothing and audits nothing,
    ///         so a queued cancel cannot produce two cancelled events and two audit rows.
    ///     </para>
    ///     <para>
    ///         The retries exist because the cancel path stamps its durable stop marker through a NON-terminal status
    ///         update, which bumps the row's version without terminalizing it. Without them a coordinator holding the
    ///         pre-marker version would lose its CAS and leave the row stuck — and ONE retry is not enough, because a
    ///         second cancel landing inside the window bumps the version again and exhausts it.
    ///     </para>
    ///     <para>
    ///         Every reload also honours the marker it finds: a stop marker stamped after this outcome was chosen
    ///         outranks it, so a pre-run rejection racing an accepted cancel writes <c>Cancelled</c> rather than the
    ///         failure the caller never asked for.
    ///     </para>
    /// </summary>
    private async Task<bool> TerminalizeAsync(ExecutionRunContext context,
        IReadOnlySet<IntegrationExecutionStatus> expectedStatuses,
        IntegrationExecutionStatus status,
        string? failureCategory,
        string? failureSummary)
    {
        var outcome = (Status: status, FailureCategory: failureCategory, FailureSummary: failureSummary);

        for (var attempt = 1; attempt <= MaxTerminalAttempts; attempt++)
        {
            if (await TryTerminalizeOnceAsync(context, expectedStatuses, outcome.Status, outcome.FailureCategory, outcome.FailureSummary).ConfigureAwait(false))
            {
                return true;
            }

            var fresh = await context.Store.GetByIdAsync(context.ExecutionId, CancellationToken.None).ConfigureAwait(false);
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
    ///     A durable stop marker outranks the FAILURE a terminal path came with. The cancel primitive stamps it before
    ///     it does anything else, so a row carrying one has an accepted 202 behind it: closing it as a failure shows
    ///     the caller a fault it did not cause and never asked about.
    ///     <para>
    ///         A <c>Completed</c> outcome is the one exception, and is left alone. The generation had already produced
    ///         its answer: its assistant turn is persisted, its <c>external.output</c> payloads are committed, and a
    ///         row reading <c>Cancelled</c> over them would contradict every artefact the caller can read. The 202 the
    ///         cancel endpoint returns says the stop was REQUESTED, never that it arrived in time.
    ///     </para>
    /// </summary>
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
            var won = await context.Store.TryTerminalizeAsync(new IntegrationTerminalizeCommand(context.ExecutionId,
                                           context.Version,
                                           expectedStatuses,
                                           status,
                                           sequence,
                                           eventType,
                                           endedAtUtc,
                                           failureCategory,
                                           failureSummary,
                                           payload?.GetRawText(),
                                           BuildAudit(context, status, endedAtUtc)),
                                       CancellationToken.None)
                                   .ConfigureAwait(false);
            if (!won)
            {
                return false;
            }

            _buffer.Publish(new IntegrationStreamEvent(eventType,
                sequence,
                context.ExecutionId,
                context.SessionId,
                endedAtUtc,
                ContentType: null,
                payload));
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
    ///     same transaction as the status and the terminal event. It used to be a separate write after the terminal
    ///     committed and its failures were swallowed, which lost the row for good: every later terminalization rejects
    ///     an already-terminal row, so nothing could write it afterwards. Content-free by contract: ids, a trigger
    ///     name, a credential prefix and a terminal status.
    /// </summary>
    private static IntegrationInvocationAuditInput BuildAudit(ExecutionRunContext context, IntegrationExecutionStatus status, long endedAtUtc) =>
        new(context.InvocationId,
            context.RequestId,
            context.TriggerName,
            context.KeyPrefix,
            context.TargetAgentDefinitionId,
            status switch
            {
                IntegrationExecutionStatus.Completed => NodeChatMessageStatusValues.Completed,
                IntegrationExecutionStatus.Cancelled => NodeChatMessageStatusValues.Cancelled,
                _ => NodeChatMessageStatusValues.Failed
            },
            Activity.Current?.TraceId.ToString(),
            Math.Max(val1: 0L, endedAtUtc - context.ReceivedAtUtc));
}
