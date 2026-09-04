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
        // The operation id IS the run id. A start has no run row to key idempotency against yet, and inventing a second
        // identifier to correlate them would be a table nobody reads: a replayed start finds the run it created and
        // answers with it, while a genuinely second start of the same work item is refused by the live-run rule below.
        if (await TryReadAsync(operationId, cancellationToken).ConfigureAwait(false) is { } replayed)
        {
            // A replay has to be a replay of THIS request. A reused operation id naming a different work item or
            // definition is a caller bug, and answering it with another run's detail would hand out a run they never
            // asked for — so it reads as the conflict it is.
            if (replayed.WorkItemId != workItemId || replayed.DefinitionId != definitionId)
            {
                throw new DevWorkflowInvalidTransitionException($"Operation '{operationId}' already started a different run.");
            }

            // Signalled, not merely composed: a replay is what a caller sends when it never saw the first answer, and
            // the run it is asking about may still be waiting for its first tick.
            return await SignalAndComposeAsync(replayed.Id, cancellationToken).ConfigureAwait(false);
        }

        var workItem = await _store.GetWorkItemAsync(workItemId, cancellationToken).ConfigureAwait(false);
        var definition = await _store.GetDefinitionAsync(definitionId, cancellationToken).ConfigureAwait(false);
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
        var enabledRuleSets = await _store.ListEnabledRuleSetsAsync(cancellationToken).ConfigureAwait(false);

        // ONE call. The seeds carry the caller's inputs, which have no other home, so a run row that committed without
        // them would be a durable workflow quietly running a different request from the one that was asked.
        var run = await _store.StartRunAsync(new StartDevWorkflowRunCommand(operationId,
                                      workItemId,
                                      definitionId,
                                      definition.Version,
                                      definition.GraphHash,
                                      definition.GraphJson,
                                      DevWorkflowRunSeeds.Compose(graph, workItem, inputsJson, _options.MaxNodeRunsPerRun, enabledRuleSets)),
                                  cancellationToken)
                              .ConfigureAwait(false);

        return await SignalAndComposeAsync(run.Id, cancellationToken).ConfigureAwait(false);
    }

    public Task<DevWorkflowRunDetail> CancelAsync(Guid runId, Guid operationId, CancellationToken cancellationToken = default) =>
        CommandAsync(runId, operationId, DevWorkflowRunStatus.Cancelling, cancellationToken);

    public Task<DevWorkflowRunDetail> PauseAsync(Guid runId, Guid operationId, CancellationToken cancellationToken = default) =>
        CommandAsync(runId, operationId, DevWorkflowRunStatus.Pausing, cancellationToken);

    public async Task<DevWorkflowRunDetail> ResumeAsync(Guid runId, Guid operationId, CancellationToken cancellationToken = default)
    {
        // Ahead of the status check below for the same reason it runs ahead of the transition table in CommandAsync: a
        // resume that committed and was then retried is a replay, and by then the run it resumed is legitimately
        // Running — the one status this method refuses.
        if (await TryReplayAsync(runId, operationId, DevWorkflowRunStatus.Running, cancellationToken).ConfigureAwait(false) is { } replayed)
        {
            return replayed;
        }

        // Checked here rather than left to the transition table, which lets a run go back to Running from a human wait
        // BECAUSE the dispatcher's recomputation does exactly that. Only a paused run is one an operator can resume,
        // and answering "resumed" to a run that never stopped would be a lie about what the command did.
        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run.Status != DevWorkflowRunStatus.Paused)
        {
            throw new DevWorkflowInvalidTransitionException($"This run is {run.Status}, so there is nothing to resume.");
        }

        return await CommandAsync(runId, operationId, DevWorkflowRunStatus.Running, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteWorkItemAsync(Guid workItemId, CancellationToken cancellationToken = default)
    {
        // Reads the item first so an unknown id answers "not found" rather than "deleted nothing".
        _ = await _store.GetWorkItemAsync(workItemId, cancellationToken).ConfigureAwait(false);

        // Rows first, and everything external after. The live-run guard lives inside this transaction, so a delete
        // refused because a run started mid-flight cannot have destroyed that run's transcripts on the way to the
        // refusal — and the ids come back from the commit, so there is no page for a caller to walk or forget.
        var deleted = await _store.DeleteWorkItemAsync(workItemId, cancellationToken).ConfigureAwait(false);

        // Past this line the request's token is DELIBERATELY dropped. The rows that named these sessions and these
        // bytes have already committed, so a cancellation here undoes nothing — it only stops the cleanup partway, and
        // what it abandons is abandoned for good: the startup sweep takes only never-driven sessions, so it will not
        // collect a workflow session that ran, and nothing at all points at these bytes any more.
        //
        // Best-effort, and per item for the same reason: throwing would report a failure for a delete that in fact
        // succeeded, and one session the work-session family refuses must not cost the sessions after it and every
        // artifact directory behind them. What a failure leaves is a session or a directory nothing points at — never a
        // dangling reference, and removable by hand through the owner surface.
        foreach (var sessionId in deleted.WorkSessionIds)
        {
            try
            {
                await _sessions.DeleteAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
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
        await ComposeAsync(await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    public async Task<DevWorkflowDecisionResult> DecideAsync(Guid runId,
        Guid nodeRunId,
        Guid operationId,
        DevWorkflowDecisionKind decision,
        string? comment,
        string? payloadJson,
        string? decidedBySubject,
        CancellationToken cancellationToken = default)
    {
        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (await _store.FindDecisionByOperationAsync(runId, operationId, cancellationToken).ConfigureAwait(false) is { } recorded)
        {
            // A repeated POST answers with the decision it already recorded, not with a conflict about the node run
            // having since moved on because of it — but only if it IS the same act. A reused operation id naming a
            // different node run, a different answer or a different person would otherwise read as a success for a
            // decision nobody took, which is the one thing this audit trail exists to make impossible.
            if (recorded.NodeRunId != nodeRunId || recorded.Decision != decision || !string.Equals(recorded.DecidedBySubject, decidedBySubject, StringComparison.Ordinal))
            {
                throw new DevWorkflowInvalidTransitionException($"Operation '{operationId}' already recorded a different decision on this run.");
            }

            // Comment and payload are deliberately NOT compared: they are the free text around the act rather than the
            // act itself, and a client re-sending its request with a trimmed comment has still taken one decision.
            return new DevWorkflowDecisionResult(await ComposeAsync(run, cancellationToken).ConfigureAwait(false), recorded);
        }

        var nodeRun = await _store.GetNodeRunAsync(nodeRunId, cancellationToken).ConfigureAwait(false);
        if (nodeRun.RunId != runId)
        {
            throw new DevWorkflowNotFoundException($"Node run '{nodeRunId}' does not belong to run '{runId}'.");
        }

        if (nodeRun.Status is not (DevWorkflowNodeRunStatus.WaitingForApproval or DevWorkflowNodeRunStatus.Blocked))
        {
            // A node run that moved BECAUSE it was already answered is a different refusal from one that was never
            // waiting: the second click on a settled gate gets told what stands, rather than only that it failed. The
            // operation id cannot say this — a new one is a new human act, which is exactly the case being refused.
            var standing = (await _store.ListDecisionsAsync(runId, cancellationToken).ConfigureAwait(false))
                .LastOrDefault(decision => decision.NodeRunId == nodeRunId && decision.Attempt == nodeRun.Attempt);
            throw standing is not null
                ? new DevWorkflowGateAlreadyDecidedException($"Node run '{nodeRun.NodeKey}' was already decided {standing.Decision}.", standing.Decision)
                : new DevWorkflowInvalidTransitionException($"Node run '{nodeRun.NodeKey}' is {nodeRun.Status}, so there is nothing to decide on it.");
        }

        // The same rule the API advertises the answers from, so the endpoint cannot accept one it did not offer — a
        // Retry on an unanswered gate, say, which has no re-attempt to schedule even though the runtime's own reset
        // moves that row to the same place.
        if (!DevWorkflowStateMachine.IsDecidable(nodeRun.Status, decision))
        {
            throw new DevWorkflowInvalidTransitionException($"Node run '{nodeRun.NodeKey}' is {nodeRun.Status} and cannot be answered {decision}.");
        }

        if (decision == DevWorkflowDecisionKind.Retry)
        {
            // A human Retry ignores the NODE's attempt cap on purpose — that is what makes it an override — but the
            // run-wide budget still bounds it, or a definition nobody can fix becomes a person clicking Retry for ever.
            // The startup reconciler checks the same budget for the attempts a restart spends.
            //
            // This is the fast path and the friendly message, NOT the authority: it reads a count that several blocked
            // node runs answered in the same tick window would each read as unspent. The budget therefore travels on
            // the command and is admitted inside the transaction that records the decision, where the count is true.
            var spent = (await _store.ListNodeRunsAsync(runId, cancellationToken).ConfigureAwait(false)).Sum(static row => row.Attempt - 1);
            if (spent >= _options.MaxTotalAttempts)
            {
                throw new DevWorkflowInvalidTransitionException($"This run has already spent {spent} re-attempts, which is as many re-attempts as this run "
                                                                + "allows, so it cannot be retried again.");
            }
        }

        _ = await _store.RecordDecisionAsync(new RecordDevWorkflowDecisionCommand(runId,
                                Guid.NewGuid(),
                                nodeRunId,
                                DevWorkflowVersions.Any,
                                operationId,
                                decision,
                                comment,
                                payloadJson,
                                decidedBySubject,
                                decision == DevWorkflowDecisionKind.Retry ? _options.MaxTotalAttempts : null,

                                // The row this answer was validated against. Everything above read `nodeRun` outside
                                // the recording transaction, so the write re-checks the pair rather than trusting it.
                                nodeRun.Attempt,
                                nodeRun.Status),
                            cancellationToken)
                        .ConfigureAwait(false);

        var detail = await SignalAndComposeAsync(runId, cancellationToken).ConfigureAwait(false);
        var settled = await _store.FindDecisionByOperationAsync(runId, operationId, cancellationToken).ConfigureAwait(false)
                      ?? throw new DevWorkflowNotFoundException($"The decision recorded on run '{runId}' could not be read back.");
        return new DevWorkflowDecisionResult(detail, settled);
    }

    /// <summary>
    ///     A lifecycle command: legal from where the run stands, keyed by its operation id, and signalled after it
    ///     commits.
    ///     <para>
    ///         Written against the <c>Any</c> version sentinel deliberately. A command is an operator's intent and must
    ///         win over a status move the dispatcher decided a moment earlier — the version check exists to stop the
    ///         reverse, a recomputation overwriting a cancel that landed between its read and its write.
    ///     </para>
    /// </summary>
    private async Task<DevWorkflowRunDetail> CommandAsync(Guid runId, Guid operationId, DevWorkflowRunStatus target, CancellationToken cancellationToken)
    {
        if (await TryReplayAsync(runId, operationId, target, cancellationToken).ConfigureAwait(false) is { } replayed)
        {
            return replayed;
        }

        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        DevWorkflowStateMachine.EnsureLegal(run.Status, target);

        _ = await _store.TransitionRunAsync(new TransitionDevWorkflowRunCommand(runId, DevWorkflowVersions.Any, target, operationId), cancellationToken)
                        .ConfigureAwait(false);
        return await SignalAndComposeAsync(runId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     The run as it stands, when this operation id has already committed its command — and <see langword="null" />
    ///     when it has not.
    ///     <para>
    ///         Resolved BEFORE legality by every lifecycle verb. A command that committed and whose answer the client
    ///         never saw is retried against a run the dispatcher has meanwhile advanced — a cancel that has since
    ///         drained to <c>Cancelled</c>, a resume whose run is now <c>Running</c> — and judging that retry against
    ///         the status its own first attempt produced would answer a conflict to a caller that did exactly the right
    ///         thing. The store keeps the same promise one level down; this is that promise made visible to the verbs.
    ///     </para>
    ///     <para>
    ///         It is the replay of THIS verb or it is not a replay at all. An operation id names one act, so the same
    ///         id arriving on a different verb is a caller bug — and answering it with the run would report a cancel as
    ///         done while the run carried on, which is exactly the failure the decision replay's identity check exists
    ///         to prevent one method up.
    ///     </para>
    /// </summary>
    private async Task<DevWorkflowRunDetail?> TryReplayAsync(Guid runId,
        Guid operationId,
        DevWorkflowRunStatus target,
        CancellationToken cancellationToken)
    {
        if (await _store.FindOperationEventTypeAsync(runId, operationId, cancellationToken).ConfigureAwait(false) is not { } recorded)
        {
            return null;
        }

        var expected = EventTypeFor(target);
        if (!string.Equals(recorded, expected, StringComparison.Ordinal))
        {
            throw new DevWorkflowInvalidTransitionException($"Operation '{operationId}' already recorded '{recorded}' on this run, so it cannot also record '{expected}'.");
        }

        return await SignalAndComposeAsync(runId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     The event a lifecycle verb writes, which is what identifies that verb in the log.
    ///     <para>
    ///         A second spelling of the store's own status-to-event mapping rather than a shared constant, and kept
    ///         honest by the tests instead: a true replay of each verb must still read as a replay, so a drift here
    ///         fails those three immediately.
    ///     </para>
    /// </summary>
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
        return await ComposeAsync(await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    private async Task<DevWorkflowRunDetail> ComposeAsync(DevWorkflowRunSnapshot run, CancellationToken cancellationToken)
    {
        var nodeRuns = await _store.ListNodeRunsAsync(run.Id, cancellationToken).ConfigureAwait(false);
        return new DevWorkflowRunDetail(run,
            nodeRuns,
            nodeRuns.Count(static nodeRun => nodeRun.Status is DevWorkflowNodeRunStatus.WaitingForApproval or DevWorkflowNodeRunStatus.Blocked),

            // The same rule the store's list counters use: the first node run, in sequence order, that a human has to
            // act on — a gate awaiting its answer or a node awaiting intervention, since Blocked folds in. A narrower
            // reading here would make the list page and the detail page disagree about the same run.
            nodeRuns.Where(static nodeRun => nodeRun.Status is DevWorkflowNodeRunStatus.WaitingForApproval or DevWorkflowNodeRunStatus.Blocked)
                    .OrderBy(static nodeRun => nodeRun.Sequence)
                    .Select(static nodeRun => (Guid?)nodeRun.Id)
                    .FirstOrDefault());
    }

    private async Task<DevWorkflowRunSnapshot?> TryReadAsync(Guid runId, CancellationToken cancellationToken)
    {
        try
        {
            return await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        catch (DevWorkflowNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    ///     A graph with sandbox work in it needs a repository to do that work in, and the work item is where one is
    ///     bound. Checked at run start rather than at save: the same definition is legitimately reusable by a work item
    ///     that HAS a project, and a research-only workflow legitimately has none.
    /// </summary>
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
