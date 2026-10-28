namespace XE_Local_AI_Engine.Client.Services.Integrations;

using System.Text;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.Integrations.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Graph half of <see cref="IntegrationExecutionCoordinator" />: a <c>Graph</c> target starts a saved graph workflow with
///     origin <c>Integration</c>, follows the run by polling, and maps its events onto the existing stream types
///     (ADR 0008).
/// </summary>
/// <remarks>
///     No invocation lease and no capacity decision here: the graph dispatcher takes both for each node it runs. The seed
///     turn the accept path persisted is the run's input document, verbatim. Node start and finish become
///     <c>tool.started</c>/<c>tool.completed</c> carrying the node key, kind and status, never node output; the End output
///     becomes one <c>external.output</c>; the run's end becomes the one terminal event.
/// </remarks>
internal sealed partial class IntegrationExecutionCoordinator
{
    /// <summary>The structural reason a definition's trigger sources refused the start; a summary under <c>trigger-unavailable</c>, whose vocabulary is closed.</summary>
    internal const string GraphTriggerSourceRefusedSummary =
        "graph-trigger-source-refused: the target workflow does not accept runs started by the integration API; the operator can add Integration to its trigger sources.";

    private const string GraphOutputContentType = "application/json";

    /// <summary>How often a followed run is re-read: polling, not a push seam; the graph hub publishes to browsers only.</summary>
    private static readonly TimeSpan GraphRunPollInterval = TimeSpan.FromMilliseconds(250);

    private static readonly JsonSerializerOptions GraphEventJsonOptions = JsonSerializerOptions.Web;

    private async Task ExecuteGraphAsync(IServiceProvider services,
        ExecutionRunContext context,
        IntegrationExecutionSnapshot execution,
        IntegrationSessionSnapshot session,
        IntegrationTriggerSnapshot trigger,
        CancellationToken runToken,
        CancellationToken cancelToken,
        CancellationToken stoppingToken)
    {
        var definitionId = trigger.TargetGraphWorkflowDefinitionId.GetValueOrDefault();

        // The audit row's target column carries the graph definition for a graph target.
        context.Describe(trigger.Name, definitionId);

        if (execution.Status == IntegrationExecutionStatus.Cancelled)
        {
            return;
        }

        if (execution.StopRequestedAtUtc is not null)
        {
            await TerminalizeAsync(context, BeforeRunStatuses, IntegrationExecutionStatus.Cancelled, failureCategory: null, failureSummary: null);
            return;
        }

        // A run started while the dispatcher is switched off would sit Pending, holding this principal's admission slot.
        if (!await services.GetRequiredService<INodeRuntimeSettings>().GetGraphWorkflowsEnabledAsync(runToken))
        {
            await TerminalizeBeforeRunAsync(context, IntegrationFailureCategories.TriggerUnavailable, "Graph workflows are turned off on this node.");
            return;
        }

        var conversation = await services.GetRequiredService<INodeChatPersistenceService>().GetConversationForTurnAsync(session.ConversationId, runToken);
        var seed = conversation?.Messages.FirstOrDefault(message => message.MessageId == execution.Id);
        if (seed is null)
        {
            await TerminalizeBeforeRunAsync(context, IntegrationFailureCategories.InternalFailure, "The execution's owned conversation or seed turn is missing.");
            return;
        }

        var runs = services.GetRequiredService<IGraphWorkflowRunService>();
        GraphWorkflowRunDetail started;
        try
        {
            // The execution id is the request id: a dispatch retry replays the same run instead of starting a second one. Not the run token: a cancel
            // landing mid-start must find the run and cancel it (below), not abandon a run that already committed.
            started = await runs.StartAsync(definitionId,
                execution.Id,
                seed.Content,
                definitionVersion: null,
                new GraphWorkflowRunOrigin
                {
                    Source = GraphWorkflowTriggerSource.Integration,
                    PrincipalId = execution.PrincipalId,
                    KeyPrefix = execution.KeyPrefix
                },
                stoppingToken);
        }
        catch (GraphWorkflowRunConflictException)
        {
            // With no definition version passed, the start's only conflict is the definition's accepted trigger sources.
            await TerminalizeBeforeRunAsync(context, IntegrationFailureCategories.TriggerUnavailable, GraphTriggerSourceRefusedSummary);
            return;
        }
        catch (GraphWorkflowNotFoundException)
        {
            await TerminalizeBeforeRunAsync(context, IntegrationFailureCategories.TriggerUnavailable, "The trigger's target workflow no longer exists.");
            return;
        }
        catch (GraphWorkflowValidationException)
        {
            await TerminalizeBeforeRunAsync(context, IntegrationFailureCategories.TriggerUnavailable, "The trigger's target workflow refused the start as not runnable.");
            return;
        }

        var runId = started.Run.Id;
        try
        {
            if (!await MoveGraphExecutionToRunningAsync(context, runs, runId))
            {
                return;
            }

            var executionStarted = _buffer.Append(context.ExecutionId, session.Id, IntegrationStreamEventTypes.ExecutionStarted, contentType: null, payload: null);
            await AppendDurableAsync(context, executionStarted, CancellationToken.None);

            await FollowGraphRunAsync(context, runs, started, session.Id, cancelToken, stoppingToken);
        }
        catch (Exception)
        {
            // The fault handler is about to tell the caller this execution failed: the run must not go on firing actions behind that answer.
            await CancelGraphRunAsync(runs, runId);
            throw;
        }
    }

    /// <summary>The Running CAS, stamping the run id twice: as the graph run column and as the audit correlation.</summary>
    /// <returns><see langword="false" /> when the row was closed instead, after cancelling the run it started.</returns>
    private async Task<bool> MoveGraphExecutionToRunningAsync(ExecutionRunContext context, IGraphWorkflowRunService runs, Guid runId)
    {
        if (await context.Store.UpdateStatusAsync(new IntegrationExecutionStatusUpdate
                {
                    ExecutionId = context.ExecutionId,
                    ExpectedVersion = context.Version,
                    ExpectedStatuses = BeforeRunStatuses,
                    NewStatus = IntegrationExecutionStatus.Running,
                    StartedAtUtc = NowUnixMilliseconds(),
                    InvocationId = runId,
                    GraphWorkflowRunId = runId
                },
                CancellationToken.None))
        {
            context.Version++;
            context.InvocationId = runId;
            return true;
        }

        // A cancel stamped its marker (or closed the row) between the start and this CAS: the run it started must not outlive it.
        await CancelGraphRunAsync(runs, runId);
        var reloaded = await context.Store.GetByIdAsync(context.ExecutionId, CancellationToken.None);
        if (reloaded is null || !NonTerminalStatuses.Contains(reloaded.Status))
        {
            return false;
        }

        context.Version = reloaded.Version;
        if (reloaded.StopRequestedAtUtc is not null)
        {
            await TerminalizeAsync(context, BeforeRunStatuses, IntegrationExecutionStatus.Cancelled, failureCategory: null, failureSummary: null);
            return false;
        }

        await TerminalizeFromFaultAsync(context,
            IntegrationExecutionStatus.Failed,
            IntegrationFailureCategories.InternalFailure,
            $"The execution could not be moved to Running from {reloaded.Status}.");
        return false;
    }

    /// <summary>
    ///     Polls the run until it is terminal, mapping every new event in sequence order, then writes the End output and the
    ///     terminal. A cancel from the API cancels the run and keeps following it to its own end.
    /// </summary>
    private async Task FollowGraphRunAsync(ExecutionRunContext context,
        IGraphWorkflowRunService runs,
        GraphWorkflowRunDetail started,
        Guid sessionId,
        CancellationToken cancelToken,
        CancellationToken stoppingToken)
    {
        var runId = started.Run.Id;
        var kinds = started.NodeRuns.ToDictionary(static nodeRun => nodeRun.NodeKey, static nodeRun => nodeRun.Kind, StringComparer.Ordinal);
        var startedNodes = new HashSet<string>(StringComparer.Ordinal);
        var afterSeq = 0L;
        var cancelForwarded = false;

        while (true)
        {
            if (cancelToken.IsCancellationRequested && !cancelForwarded)
            {
                cancelForwarded = true;
                await CancelGraphRunAsync(runs, runId);
            }

            // Status first, events second: a terminal status read here guarantees the drain below sees every event before it.
            var detail = await runs.GetRunAsync(runId, stoppingToken);
            GraphWorkflowRunEventPage page;
            do
            {
                page = await runs.ListEventsAsync(runId, afterSeq, stoppingToken);
                foreach (var runEvent in page.Events)
                {
                    afterSeq = runEvent.Seq;
                    if (MapGraphEvent(runEvent, kinds, startedNodes) is { } draft)
                    {
                        var appended = _buffer.Append(context.ExecutionId, sessionId, draft.Type, draft.ContentType, draft.Payload);
                        await AppendDurableAsync(context, appended, CancellationToken.None);
                    }
                }
            } while (page.Events.Count > 0);

            if (GraphWorkflowStateMachine.IsTerminal(detail.Run.Status))
            {
                await FinishGraphAsync(context, detail.Run, sessionId);
                return;
            }

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, cancelForwarded ? CancellationToken.None : cancelToken);
            try
            {
                await Task.Delay(GraphRunPollInterval, _timeProvider, wait.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // The cancel arrived mid-wait; the next pass forwards it.
            }
        }
    }

    /// <summary>
    ///     Node start and finish only, as <c>tool.*</c> frames carrying the node key, kind and status — never the node's
    ///     output. A finish is mapped only for a node seen starting, so a node cancelled while pending stays silent.
    /// </summary>
    internal static IntegrationStreamEventDraft? MapGraphEvent(GraphWorkflowRunEventSnapshot runEvent,
        IReadOnlyDictionary<string, GraphWorkflowNodeKind> kinds,
        ISet<string> startedNodes)
    {
        ArgumentNullException.ThrowIfNull(runEvent);
        ArgumentNullException.ThrowIfNull(kinds);
        ArgumentNullException.ThrowIfNull(startedNodes);

        if (runEvent.NodeKey is not { } nodeKey)
        {
            return null;
        }

        var kind = kinds.TryGetValue(nodeKey, out var known) ? known.ToString() : null;
        if (runEvent.EventType == GraphWorkflowEventTypes.NodeStarted)
        {
            _ = startedNodes.Add(nodeKey);
            return new IntegrationStreamEventDraft
            {
                Type = IntegrationStreamEventTypes.ToolStarted,
                ContentType = null,
                Payload = JsonSerializer.SerializeToElement(new
                {
                    name = nodeKey,
                    nodeKey,
                    kind,
                    status = nameof(GraphWorkflowNodeRunStatus.Running)
                }, GraphEventJsonOptions)
            };
        }

        GraphWorkflowNodeRunStatus? finished = runEvent.EventType switch
        {
            GraphWorkflowEventTypes.NodeCompleted => GraphWorkflowNodeRunStatus.Succeeded,
            GraphWorkflowEventTypes.NodeFailed => GraphWorkflowNodeRunStatus.Failed,
            GraphWorkflowEventTypes.NodeCancelled => GraphWorkflowNodeRunStatus.Cancelled,
            _ => null
        };
        if (finished is not { } status || !startedNodes.Remove(nodeKey))
        {
            return null;
        }

        return new IntegrationStreamEventDraft
        {
            Type = IntegrationStreamEventTypes.ToolCompleted,
            ContentType = null,
            Payload = JsonSerializer.SerializeToElement(new
            {
                name = nodeKey,
                nodeKey,
                kind,
                status = status.ToString(),
                ok = status == GraphWorkflowNodeRunStatus.Succeeded
            }, GraphEventJsonOptions)
        };
    }

    private async Task FinishGraphAsync(ExecutionRunContext context, GraphWorkflowRunSnapshot run, Guid sessionId)
    {
        var status = run.Status switch
        {
            GraphWorkflowRunStatus.Completed => IntegrationExecutionStatus.Completed,
            GraphWorkflowRunStatus.Cancelled => IntegrationExecutionStatus.Cancelled,
            _ => IntegrationExecutionStatus.Failed
        };
        var failed = status == IntegrationExecutionStatus.Failed;
        var failureCategory = failed ? IntegrationFailureCategories.InternalFailure : null;
        var failureSummary = failed ? $"The workflow run failed ({run.FailureClass})." : null;

        if (run is { StartedAtUtc: { } startedAt, CompletedAtUtc: { } completedAt })
        {
            context.RunDurationMs = Math.Max(val1: 0L, completedAt - startedAt);
        }

        if (status == IntegrationExecutionStatus.Completed && run.OutputJson is { } output && !await DeliverGraphOutputAsync(context, sessionId, output))
        {
            status = IntegrationExecutionStatus.Failed;
            failureCategory = IntegrationFailureCategories.InternalFailure;
            failureSummary = "The workflow's End output is larger than this node delivers to an integration caller.";
        }

        if (!await TerminalizeAsync(context, RunningOnly, status, failureCategory, failureSummary))
        {
            await TerminalizeFromFaultAsync(context, status, failureCategory, failureSummary);
        }
    }

    /// <summary>The End output as one <c>external.output</c>, in the same envelope and under the same caps <c>emit_output</c> uses.</summary>
    /// <returns><see langword="false" /> when the output is over a cap and nothing was delivered.</returns>
    private async Task<bool> DeliverGraphOutputAsync(ExecutionRunContext context, Guid sessionId, string outputJson)
    {
        using var output = JsonDocument.Parse(outputJson);
        var payload = output.RootElement.Clone();
        var detailJson = JsonSerializer.Serialize(new
        {
            contentType = GraphOutputContentType,
            payload
        }, GraphEventJsonOptions);
        if (Encoding.UTF8.GetByteCount(detailJson) > _options.MaxOutputBytes)
        {
            return false;
        }

        var sequence = _buffer.Reserve(context.ExecutionId);
        var occurredAtUtc = NowUnixMilliseconds();
        bool recorded;
        try
        {
            recorded = await context.Store.AppendOutputEventAsync(new IntegrationEventAppend
                {
                    EventId = Guid.NewGuid(),
                    ExecutionId = context.ExecutionId,
                    Sequence = sequence,
                    EventType = IntegrationStreamEventTypes.ExternalOutput,
                    DetailJson = detailJson,
                    OccurredAtUtc = occurredAtUtc
                },
                _options.MaxOutputBytesPerExecution,
                CancellationToken.None);
        }
        catch (Exception)
        {
            _buffer.Abandon(context.ExecutionId, sequence);
            throw;
        }

        if (!recorded)
        {
            _buffer.Abandon(context.ExecutionId, sequence);
            return false;
        }

        _buffer.Publish(new IntegrationStreamEvent
        {
            Type = IntegrationStreamEventTypes.ExternalOutput,
            Sequence = sequence,
            ExecutionId = context.ExecutionId,
            SessionId = sessionId,
            OccurredAtUtc = occurredAtUtc,
            ContentType = GraphOutputContentType,
            Payload = payload
        });
        return true;
    }

    private async Task CancelGraphRunAsync(IGraphWorkflowRunService runs, Guid runId)
    {
        try
        {
            _ = await runs.CancelAsync(runId, CancellationToken.None);
        }
        catch (GraphWorkflowRunConflictException)
        {
            // Already terminal: the follow loop reads its own end.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Cancelling graph workflow run {RunId} for an integration execution failed.", runId);
        }
    }

    private static Task AppendDurableAsync(ExecutionRunContext context, IntegrationStreamEvent appended, CancellationToken cancellationToken) =>
        context.Store.AppendEventAsync(new IntegrationEventAppend
        {
            EventId = Guid.NewGuid(),
            ExecutionId = context.ExecutionId,
            Sequence = appended.Sequence,
            EventType = appended.Type,
            DetailJson = appended.Payload?.GetRawText(),
            OccurredAtUtc = appended.OccurredAtUtc
        }, cancellationToken);
}
