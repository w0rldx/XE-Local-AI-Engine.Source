namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The command surface over a development workflow run. Everything it does is validate, commit, signal, and answer
///     with what the rows now say — the dispatcher does the rest on its own clock.
/// </summary>
internal sealed class DevWorkflowRunService : IDevWorkflowRunService
{
    private readonly DevWorkflowOptions _options;
    private readonly IDevWorkflowDispatcherSignal _signal;
    private readonly IDevWorkflowStore _store;

    public DevWorkflowRunService(IDevWorkflowStore store, IDevWorkflowDispatcherSignal signal, IOptions<DevWorkflowOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
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

            // Signalled, not merely composed: the crash window is between this method's two store calls, and a run left
            // there needs a tick to finish starting rather than a wait for the next sweep.
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

        var run = await _store.StartRunAsync(new StartDevWorkflowRunCommand(operationId,
                                  workItemId,
                                  definitionId,
                                  definition.Version,
                                  definition.GraphHash,
                                  definition.GraphJson),
                              cancellationToken)
                              .ConfigureAwait(false);

        // Two calls, and the second is this method's rather than the dispatcher's: the caller's inputs live nowhere but
        // the entry node runs, so whoever holds them has to be the one that seeds them.
        _ = await _store.MaterializeNodeRunsAsync(new MaterializeDevWorkflowNodesCommand(run.Id,
                            DevWorkflowVersions.Any,
                            DevWorkflowOperationId.For(run.Id, string.Empty, attempt: 0, "materialize-graph"),
                            DevWorkflowRunSeeds.Compose(graph, workItem, inputsJson, _options.MaxNodeRunsPerRun)),
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
            // having since moved on because of it.
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

        // The same table the dispatcher settles against, so the endpoint cannot accept an answer the runtime would then
        // have to refuse — a Retry on an unanswered gate, say, which has no re-attempt to schedule.
        DevWorkflowStateMachine.EnsureLegal(nodeRun.Status, DevWorkflowStateMachine.TargetFor(decision), nodeRun.NodeKey);

        _ = await _store.RecordDecisionAsync(new RecordDevWorkflowDecisionCommand(runId,
                            Guid.NewGuid(),
                            nodeRunId,
                            DevWorkflowVersions.Any,
                            operationId,
                            decision,
                            comment,
                            payloadJson,
                            decidedBySubject),
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
        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        DevWorkflowStateMachine.EnsureLegal(run.Status, target);

        _ = await _store.TransitionRunAsync(new TransitionDevWorkflowRunCommand(runId, DevWorkflowVersions.Any, target, operationId), cancellationToken)
                        .ConfigureAwait(false);
        return await SignalAndComposeAsync(runId, cancellationToken).ConfigureAwait(false);
    }

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
