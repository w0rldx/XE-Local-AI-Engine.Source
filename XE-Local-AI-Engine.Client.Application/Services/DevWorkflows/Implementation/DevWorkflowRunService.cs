namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.WorkSessions;

/// <summary>
///     The command surface over a development workflow run. Everything it does is validate, commit, signal, and answer
///     with what the rows now say — the dispatcher does the rest on its own clock.
/// </summary>
internal sealed class DevWorkflowRunService : IDevWorkflowRunService
{
    private readonly IDevWorkflowArtifactBlobStore _blobs;
    private readonly ILogger<DevWorkflowRunService> _logger;
    private readonly DevWorkflowOptions _options;
    private readonly IWorkflowOwnedWorkSessionLifecycle _sessions;
    private readonly IDevWorkflowDispatcherSignal _signal;
    private readonly IDevWorkflowStore _store;

    public DevWorkflowRunService(IDevWorkflowStore store,
        IDevWorkflowDispatcherSignal signal,
        IWorkflowOwnedWorkSessionLifecycle sessions,
        IDevWorkflowArtifactBlobStore blobs,
        IOptions<DevWorkflowOptions> options,
        ILogger<DevWorkflowRunService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _blobs = blobs ?? throw new ArgumentNullException(nameof(blobs));
        _options = options.Value;
    }

    public async Task<DevWorkflowRunDetail> StartAsync(Guid workItemId,
        Guid definitionId,
        string? inputsJson,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        // The operation id IS the run id: a start has no run row to key idempotency against, and a second identifier
        // would be a table nobody reads. A genuinely second start of one work item is refused by the live-run rule.
        if (await TryReadAsync(operationId, cancellationToken) is { } replayed)
        {
            // A replay has to be a replay of THIS request: a reused operation id naming a different work item or
            // definition is a caller bug, and answering it would hand out a run nobody asked for.
            if (replayed.WorkItemId != workItemId || replayed.DefinitionId != definitionId)
            {
                throw new DevWorkflowInvalidTransitionException($"Operation '{operationId}' already started a different run.");
            }

            // Signalled, not merely composed: a replay is what a caller sends when it never saw the first answer, and
            // the run it is asking about may still be waiting for its first tick.
            return await SignalAndComposeAsync(replayed.Id, cancellationToken);
        }

        var workItem = await _store.GetWorkItemAsync(workItemId, cancellationToken);
        var definition = await _store.GetDefinitionAsync(definitionId, cancellationToken);
        if (definition.Archived)
        {
            throw new DevWorkflowValidationException($"Definition '{definition.Name}' is archived, so no new run can start from it.");
        }

        // Validated here as well as at save time, because an agent definition can be deleted in between — and because
        // the definition endpoints that will validate on save do not exist yet.
        var graph = DevWorkflowGraph.Parse(definition.GraphJson);
        EnsureRepositoryBound(graph, workItem);

        // Read once for the whole run: every seed's policy resolution is decided from this one list, so two nodes of the
        // same run can never disagree about which rule sets were live when it started.
        var enabledRuleSets = await _store.ListEnabledRuleSetsAsync(cancellationToken);

        // ONE call. The seeds carry the caller's inputs, which have no other home, so a run row that committed without
        // them would be a durable workflow quietly running a different request from the one that was asked.
        var run = await _store.StartRunAsync(new StartDevWorkflowRunCommand
            {
                RunId = operationId,
                WorkItemId = workItemId,
                DefinitionId = definitionId,
                DefinitionVersion = definition.Version,
                DefinitionGraphHash = definition.GraphHash,
                GraphJson = definition.GraphJson,
                NodeRuns = DevWorkflowRunSeeds.Compose(graph, workItem, inputsJson, _options.MaxNodeRunsPerRun, enabledRuleSets)
            },
            cancellationToken);

        return await SignalAndComposeAsync(run.Id, cancellationToken);
    }

    public Task<DevWorkflowRunDetail> CancelAsync(Guid runId, Guid operationId, CancellationToken cancellationToken = default) =>
        CommandAsync(runId, operationId, DevWorkflowRunStatus.Cancelling, cancellationToken);

    public Task<DevWorkflowRunDetail> PauseAsync(Guid runId, Guid operationId, CancellationToken cancellationToken = default) =>
        CommandAsync(runId, operationId, DevWorkflowRunStatus.Pausing, cancellationToken);

    public async Task<DevWorkflowRunDetail> ResumeAsync(Guid runId, Guid operationId, CancellationToken cancellationToken = default)
    {
        // Ahead of the status check below for the reason it runs ahead of CommandAsync's transition table: a resume
        // that committed and was retried is a replay, and by then its run is legitimately Running — what this refuses.
        if (await TryReplayAsync(runId, operationId, DevWorkflowRunStatus.Running, cancellationToken) is { } replayed)
        {
            return replayed;
        }

        // Checked here, not left to the transition table, which allows Running from a human wait BECAUSE the
        // dispatcher does that. Answering "resumed" to a run that never stopped would misreport what the command did.
        var run = await _store.GetRunAsync(runId, cancellationToken);
        if (run.Status != DevWorkflowRunStatus.Paused)
        {
            throw new DevWorkflowInvalidTransitionException($"This run is {run.Status}, so there is nothing to resume.");
        }

        return await CommandAsync(runId, operationId, DevWorkflowRunStatus.Running, cancellationToken);
    }

    public async Task DeleteWorkItemAsync(Guid workItemId, CancellationToken cancellationToken = default)
    {
        // Reads the item first so an unknown id answers "not found" rather than "deleted nothing".
        _ = await _store.GetWorkItemAsync(workItemId, cancellationToken);

        // Rows first, everything external after: the live-run guard is inside this transaction, so a delete refused
        // because a run started mid-flight destroyed none of its transcripts. The commit returns the ids to clean up.
        var deleted = await _store.DeleteWorkItemAsync(workItemId, cancellationToken);

        // Past this line the token is DELIBERATELY dropped and each step is best-effort per item: the rows have
        // committed. See docs/wiki/25-dev-workflows.md ("The application service seams").
        foreach (var sessionId in deleted.WorkSessionIds)
        {
            try
            {
                await _sessions.DeleteAsync(sessionId, CancellationToken.None);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Work session {SessionId} outlived the work item that owned it and has to be removed by hand.", sessionId);
            }
        }

        foreach (var runId in deleted.RunIds)
        {
            try
            {
                _blobs.DeleteRun(runId);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception,
                    "The artifact bytes of development workflow run {RunId} outlived the work item that owned it and have to be removed by hand.",
                    runId);
            }
        }
    }

    public async Task<DevWorkflowRunDetail> GetAsync(Guid runId, CancellationToken cancellationToken = default) =>
        await ComposeAsync(await _store.GetRunAsync(runId, cancellationToken), cancellationToken);

    public async Task<DevWorkflowDecisionResult> DecideAsync(Guid runId,
        Guid nodeRunId,
        Guid operationId,
        DevWorkflowDecisionKind decision,
        string? comment,
        string? payloadJson,
        string? decidedBySubject,
        CancellationToken cancellationToken = default)
    {
        var run = await _store.GetRunAsync(runId, cancellationToken);
        if (await _store.FindDecisionByOperationAsync(runId, operationId, cancellationToken) is { } recorded)
        {
            // A repeated POST answers with the decision it already recorded rather than a conflict — but only if it
            // IS the same act, or a reused id would read as a success for a decision nobody took.
            if (recorded.NodeRunId != nodeRunId || recorded.Decision != decision || !string.Equals(recorded.DecidedBySubject, decidedBySubject, StringComparison.Ordinal))
            {
                throw new DevWorkflowInvalidTransitionException($"Operation '{operationId}' already recorded a different decision on this run.");
            }

            // Comment and payload are deliberately NOT compared: they are the free text around the act rather than the
            // act itself, and a client re-sending its request with a trimmed comment has still taken one decision.
            return new DevWorkflowDecisionResult
            {
                Detail = await ComposeAsync(run, cancellationToken),
                Decision = recorded
            };
        }

        var nodeRun = await _store.GetNodeRunAsync(nodeRunId, cancellationToken);
        if (nodeRun.RunId != runId)
        {
            throw new DevWorkflowNotFoundException($"Node run '{nodeRunId}' does not belong to run '{runId}'.");
        }

        if (nodeRun.Status is not (DevWorkflowNodeRunStatus.WaitingForApproval or DevWorkflowNodeRunStatus.Blocked))
        {
            // A node run that moved BECAUSE it was answered is a different refusal from one that was never waiting:
            // the second click on a settled gate is told what stands. A new operation id is a new act, not a replay.
            var standing = (await _store.ListDecisionsAsync(runId, cancellationToken))
                .LastOrDefault(decision => decision.NodeRunId == nodeRunId && decision.Attempt == nodeRun.Attempt);
            throw standing is not null
                ? new DevWorkflowGateAlreadyDecidedException($"Node run '{nodeRun.NodeKey}' was already decided {standing.Decision}.", standing.Decision)
                : new DevWorkflowInvalidTransitionException($"Node run '{nodeRun.NodeKey}' is {nodeRun.Status}, so there is nothing to decide on it.");
        }

        // The same rule the API advertises the answers from, so the endpoint cannot accept one it did not offer — a
        // Retry on an unanswered gate, which has no attempt to schedule though the runtime's reset moves it there.
        if (!DevWorkflowStateMachine.IsDecidable(nodeRun.Status, decision))
        {
            throw new DevWorkflowInvalidTransitionException($"Node run '{nodeRun.NodeKey}' is {nodeRun.Status} and cannot be answered {decision}.");
        }

        if (decision == DevWorkflowDecisionKind.Retry)
        {
            // A human Retry overrides the NODE's cap, but the run-wide budget still bounds it. This is the friendly
            // message, NOT the authority: the budget is admitted inside the transaction that records the decision.
            var spent = (await _store.ListNodeRunsAsync(runId, cancellationToken)).Sum(static row => row.Attempt - 1);
            if (spent >= _options.MaxTotalAttempts)
            {
                throw new DevWorkflowInvalidTransitionException($"This run has already spent {spent} re-attempts, which is as many re-attempts as this run "
                                                                + "allows, so it cannot be retried again.");
            }
        }

        _ = await _store.RecordDecisionAsync(new RecordDevWorkflowDecisionCommand
            {
                RunId = runId,
                DecisionId = Guid.NewGuid(),
                NodeRunId = nodeRunId,
                ExpectedVersion = DevWorkflowVersions.Any,
                OperationId = operationId,
                Decision = decision,
                Comment = comment,
                PayloadJson = payloadJson,
                DecidedBySubject = decidedBySubject,
                MaxTotalAttempts = decision == DevWorkflowDecisionKind.Retry ? _options.MaxTotalAttempts : null,
                // The row this answer was validated against. Everything above read `nodeRun` outside
                // the recording transaction, so the write re-checks the pair rather than trusting it.
                ExpectedAttempt = nodeRun.Attempt,
                ExpectedStatus = nodeRun.Status
            },
            cancellationToken);

        var detail = await SignalAndComposeAsync(runId, cancellationToken);
        var settled = await _store.FindDecisionByOperationAsync(runId, operationId, cancellationToken)
                      ?? throw new DevWorkflowNotFoundException($"The decision recorded on run '{runId}' could not be read back.");
        return new DevWorkflowDecisionResult
        {
            Detail = detail,
            Decision = settled
        };
    }

    /// <summary>A lifecycle command: legal from where the run stands, keyed by its operation id, signalled on commit.</summary>
    /// <remarks>
    ///     Written against the <c>Any</c> version sentinel deliberately: an operator's intent must win over a status
    ///     move the dispatcher decided a moment earlier, the version check existing to stop the reverse.
    ///     See docs/wiki/25-dev-workflows.md ("The application service seams").
    /// </remarks>
    private async Task<DevWorkflowRunDetail> CommandAsync(Guid runId, Guid operationId, DevWorkflowRunStatus target, CancellationToken cancellationToken)
    {
        if (await TryReplayAsync(runId, operationId, target, cancellationToken) is { } replayed)
        {
            return replayed;
        }

        var run = await _store.GetRunAsync(runId, cancellationToken);
        DevWorkflowStateMachine.EnsureLegal(run.Status, target);

        _ = await _store.TransitionRunAsync(new TransitionDevWorkflowRunCommand
        {
            RunId = runId,
            ExpectedVersion = DevWorkflowVersions.Any,
            TargetStatus = target,
            OperationId = operationId
        }, cancellationToken);
        return await SignalAndComposeAsync(runId, cancellationToken);
    }

    /// <summary>The run as it stands when this operation id has already committed its command, else null.</summary>
    /// <remarks>
    ///     Resolved BEFORE legality by every lifecycle verb; the store keeps the same promise one level down, and this
    ///     is that promise made visible to the verbs. It is the replay of THIS verb or it is not a replay at all.
    ///     See docs/wiki/25-dev-workflows.md ("The application service seams").
    /// </remarks>
    private async Task<DevWorkflowRunDetail?> TryReplayAsync(Guid runId,
        Guid operationId,
        DevWorkflowRunStatus target,
        CancellationToken cancellationToken)
    {
        if (await _store.FindOperationEventTypeAsync(runId, operationId, cancellationToken) is not { } recorded)
        {
            return null;
        }

        var expected = EventTypeFor(target);
        if (!string.Equals(recorded, expected, StringComparison.Ordinal))
        {
            throw new DevWorkflowInvalidTransitionException($"Operation '{operationId}' already recorded '{recorded}' on this run, so it cannot also record '{expected}'.");
        }

        return await SignalAndComposeAsync(runId, cancellationToken);
    }

    /// <summary>The event a lifecycle verb writes, which is what identifies that verb in the log.</summary>
    /// <remarks>
    ///     A second spelling of the store's own status-to-event mapping rather than a shared constant, kept honest by
    ///     the tests instead: a true replay of each verb must still read as a replay, so a drift here fails them.
    /// </remarks>
    private static string EventTypeFor(DevWorkflowRunStatus target) =>
        target switch
        {
            DevWorkflowRunStatus.Cancelling => DevWorkflowEventTypes.RunCancelled,
            DevWorkflowRunStatus.Pausing => DevWorkflowEventTypes.RunPaused,

            // Running reaches here only from ResumeAsync, and a run that never started cannot be resumed — so the
            // store's first-start branch (run.started) is unreachable from this surface.
            _ => DevWorkflowEventTypes.RunResumed
        };

    /// <summary>
    ///     Signals AFTER the commit, which is the whole of the runtime's obligation to the dispatcher: without it a
    ///     fresh run would sit visibly <c>Pending</c> until the next sweep, for no reason a reader could see.
    /// </summary>
    private async Task<DevWorkflowRunDetail> SignalAndComposeAsync(Guid runId, CancellationToken cancellationToken)
    {
        _signal.Signal(runId);
        return await ComposeAsync(await _store.GetRunAsync(runId, cancellationToken), cancellationToken);
    }

    private async Task<DevWorkflowRunDetail> ComposeAsync(DevWorkflowRunSnapshot run, CancellationToken cancellationToken)
    {
        var nodeRuns = await _store.ListNodeRunsAsync(run.Id, cancellationToken);
        return new DevWorkflowRunDetail
        {
            Run = run,
            NodeRuns = nodeRuns,
            PendingDecisionCount = nodeRuns.Count(static nodeRun => nodeRun.Status is DevWorkflowNodeRunStatus.WaitingForApproval or DevWorkflowNodeRunStatus.Blocked),
            // The store's list counters' own rule: the first node run in sequence order a human has to act on, a gate
            // or a Blocked node alike. A narrower reading would make the list and detail pages disagree on one run.
            BlockingGateNodeRunId = nodeRuns.Where(static nodeRun => nodeRun.Status is DevWorkflowNodeRunStatus.WaitingForApproval or DevWorkflowNodeRunStatus.Blocked)
                                            .OrderBy(static nodeRun => nodeRun.Sequence)
                                            .Select(static nodeRun => (Guid?)nodeRun.Id)
                                            .FirstOrDefault()
        };
    }

    private async Task<DevWorkflowRunSnapshot?> TryReadAsync(Guid runId, CancellationToken cancellationToken)
    {
        try
        {
            return await _store.GetRunAsync(runId, cancellationToken);
        }
        catch (DevWorkflowNotFoundException)
        {
            return null;
        }
    }

    /// <summary>A graph with sandbox work in it needs a repository, and the work item is where one is bound.</summary>
    /// <remarks>
    ///     Checked at run start rather than at save: the same definition is legitimately reusable by a work item that
    ///     HAS a project, and a research-only workflow legitimately has none.
    /// </remarks>
    private static void EnsureRepositoryBound(DevWorkflowGraph graph, DevWorkflowWorkItemSnapshot workItem)
    {
        if (workItem.DevelopmentProjectId is not null)
        {
            return;
        }

        var repositoryBound = graph.Nodes.Values.Where(static node => node.NodeType is DevWorkflowNodeType.Tool or DevWorkflowNodeType.DevTask)
                                   .Select(static node => node.NodeKey)
                                   .OrderBy(static key => key, StringComparer.Ordinal)
                                   .ToList();
        if (repositoryBound.Count > 0)
        {
            throw new DevWorkflowValidationException($"This workflow runs commands in a repository ({string.Join(", ", repositoryBound)}), "
                                                     + "so the work item has to name the development project they run against.");
        }
    }
}
