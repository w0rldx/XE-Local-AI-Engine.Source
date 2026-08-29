namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.WorkSessions;

/// <summary>
///     Makes the node runs a crashed or restarted host left mid-flight dispatchable again, exactly once, at startup.
///     <para>
///         Registered AFTER <c>WorkSessionStartupReconciler</c> and BEFORE the dispatcher, and both halves of that
///         matter. A node run that resumes must not find its session still holding a half-written turn — the same
///         constraint the work-session reconciler documents one level down against the chat module — and the dispatcher
///         must not start admitting rows this has not judged yet.
///     </para>
///     <para>
///         It deliberately touches no RUN row. Runs auto-resume: a workflow run legitimately spans days, and requiring
///         an operator to restart every one of them after an engine restart would defeat the durability the feature
///         exists to provide.
///     </para>
/// </summary>
public sealed class DevWorkflowStartupReconciler : IHostedService
{
    private const string InterruptedReason = "The host restarted while the node run was in flight.";

    private readonly ILogger<DevWorkflowStartupReconciler> _logger;
    private readonly DevWorkflowOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;

    public DevWorkflowStartupReconciler(IServiceScopeFactory scopeFactory,
        IOptions<DevWorkflowOptions> options,
        ILogger<DevWorkflowStartupReconciler> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options.Value;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            // The services stay registered when the feature is off, so the guard is here rather than in the container.
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>();
        var sessions = scope.ServiceProvider.GetRequiredService<IWorkflowOwnedWorkSessionLifecycle>();

        // One query, and the rows come back carrying the status they held BEFORE the collapse — which is the useful
        // fact here, because what a node run was doing decides what re-running it costs.
        var reconciled = await store.ReconcileNonTerminalNodeRunsAsync(InterruptedReason, cancellationToken).ConfigureAwait(false);
        foreach (var nodeRun in reconciled)
        {
            await RepairAsync(store, sessions, nodeRun, cancellationToken).ConfigureAwait(false);
        }

        if (reconciled.Count > 0)
        {
            await EnforceAttemptBudgetAsync(store, reconciled, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Reconciled {Count} in-flight development workflow node run(s) after host startup.", reconciled.Count);
        }

        // Unconditional, unlike everything above it: an orphan is a session no node run references, so there is no
        // reconciled row that could lead to one.
        await SweepOrphanedWorkSessionsAsync(store,
                sessions,
                scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>(),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    ///     What the store could not know: whether re-running this node run costs an attempt, and whether it can be
    ///     re-run at all.
    ///     <para>
    ///         The store collapses every stranded row to <c>Pending</c> without touching <c>Attempt</c>, which is right
    ///         for the two cases that dominate — an agent whose session survives the restart on its own checkpoint, and
    ///         a queued row that was never dispatched. Neither is a failure, so neither is an attempt.
    ///     </para>
    /// </summary>
    private static async Task RepairAsync(IDevWorkflowStore store,
        IWorkflowOwnedWorkSessionLifecycle sessions,
        DevWorkflowReconciledNodeRun reconciled,
        CancellationToken cancellationToken)
    {
        var nodeRun = await store.GetNodeRunAsync(reconciled.NodeRunId, cancellationToken).ConfigureAwait(false);

        if (reconciled.NodeType is DevWorkflowNodeType.Tool or DevWorkflowNodeType.DevTask && reconciled.Status == DevWorkflowNodeRunStatus.Running)
        {
            // The sandbox process is gone and its workspace may be half-prepared, so the re-run is a real second
            // attempt and has to count against the node's budget — unlike an agent, whose session resumes from a
            // checkpoint it wrote itself.
            _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(nodeRun.RunId,
                                nodeRun.Id,
                                DevWorkflowVersions.Any,
                                DevWorkflowNodeRunStatus.Pending,
                                IncrementAttempt: true,
                                Outcome: DevWorkflowOutcomes.Interrupted),
                            cancellationToken)
                        .ConfigureAwait(false);
            return;
        }

        if (reconciled is not { NodeType: DevWorkflowNodeType.Agent, WorkSessionId: { } sessionId })
        {
            return;
        }

        try
        {
            _ = await sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkSessionNotFoundException)
        {
            // Deleted out from under the run. Nothing can resume it and a retry would only create a second session for
            // work whose transcript is already gone, so it goes to a human with the reason on the row.
            await BlockAsync(store,
                    nodeRun,
                    DevWorkflowFailureClasses.Configuration,
                    "The work session this node run was driving no longer exists, so the host restart could not resume it.",
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     The guard against a definition that restart-loops: without it, a node run the host keeps dying under would
    ///     spend an attempt on every boot forever.
    ///     <para>
    ///         The budget counts a run's RE-attempts — the sum of <c>Attempt − 1</c> over its node runs — so a graph
    ///         that has merely started has spent none of it however many nodes it declares.
    ///     </para>
    ///     <para>
    ///         <c>MaxNodeRunsPerRun</c> is deliberately NOT re-checked here. It is enforced where the rows are created,
    ///         and a run that somehow held more than it allows cannot be repaired by blocking every node run it has —
    ///         that would turn a bounded accounting error into a dead run.
    ///     </para>
    /// </summary>
    private async Task EnforceAttemptBudgetAsync(IDevWorkflowStore store,
        IReadOnlyList<DevWorkflowReconciledNodeRun> reconciled,
        CancellationToken cancellationToken)
    {
        var repaired = new List<DevWorkflowNodeRunSnapshot>(reconciled.Count);
        foreach (var row in reconciled)
        {
            repaired.Add(await store.GetNodeRunAsync(row.NodeRunId, cancellationToken).ConfigureAwait(false));
        }

        foreach (var group in repaired.GroupBy(static nodeRun => nodeRun.RunId))
        {
            var nodeRuns = await store.ListNodeRunsAsync(group.Key, cancellationToken).ConfigureAwait(false);
            var spent = nodeRuns.Sum(static nodeRun => nodeRun.Attempt - 1);
            if (spent < _options.MaxTotalAttempts)
            {
                continue;
            }

            _logger.LogWarning("Development workflow run {RunId} has spent {Spent} re-attempts, so its interrupted node runs need a human.", group.Key, spent);
            foreach (var nodeRun in group.Where(static nodeRun => nodeRun.Status == DevWorkflowNodeRunStatus.Pending))
            {
                await BlockAsync(store,
                        nodeRun,
                        DevWorkflowFailureClasses.BudgetExhausted,
                        $"This run has already spent {spent} re-attempts, which is as many re-attempts as this run allows.",
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    ///     Deletes every workflow-kind work session no node run references.
    ///     <para>
    ///         Such a session is unreachable by design: the owner surface refuses workflow-kind sessions to every
    ///         external caller, and a work-item delete can only release what its node runs point AT — so nothing else
    ///         will ever find it. Two ways to make one, and this closes both: a host death between creating a session
    ///         and attaching it, and a crash between a work item's rows committing and its sessions being released.
    ///     </para>
    ///     <para>
    ///         Startup only, and two queries — every workflow-kind session, and the ids node runs own. It is deliberately
    ///         NOT a per-tick sweep: a session created a millisecond ago and not yet attached is indistinguishable from
    ///         an orphan, and a sweep running concurrently with the executor would delete live work.
    ///     </para>
    ///     <para>
    ///         <b>A retry's superseded session is swept too</b>, and that is a real consequence rather than an oversight:
    ///         a re-attempt clears <c>WorkSessionId</c> so the fresh attempt does not resume a poisoned context, which
    ///         leaves the previous attempt's session owned by nothing and therefore indistinguishable here from the two
    ///         orphan shapes. Those transcripts are visible on the work-sessions page until the next restart, and they
    ///         leak forever without this. Narrow it to <c>session.Status == AgentWorkSessionStatus.Draft</c> if keeping
    ///         them wins — that keeps every driven session at the cost of no longer mopping up the delete residue.
    ///     </para>
    /// </summary>
    private async Task SweepOrphanedWorkSessionsAsync(IDevWorkflowStore store,
        IWorkflowOwnedWorkSessionLifecycle sessions,
        IAgentWorkSessionStore workSessions,
        CancellationToken cancellationToken)
    {
        var owned = (await store.ListOwnedWorkSessionIdsAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var orphans = (await workSessions.ListAsync(cancellationToken).ConfigureAwait(false))
                      .Where(session => session.Kind == AgentWorkSessionKind.Workflow && !owned.Contains(session.Id))
                      .Select(static session => session.Id)
                      .ToList();

        foreach (var orphan in orphans)
        {
            // Named, one line each: this is a workflow that lost a transcript, and a silent delete would erase the
            // evidence of the crash that made it along with the session.
            _logger.LogWarning("Deleting orphaned workflow work session {SessionId}, which no development workflow node run references.", orphan);
            try
            {
                await sessions.DeleteAsync(orphan, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is WorkSessionInvalidTransitionException or WorkSessionNotFoundException)
            {
                _logger.LogWarning(exception, "Orphaned workflow work session {SessionId} could not be deleted and has to be removed by hand.", orphan);
            }
        }
    }

    private static async Task BlockAsync(IDevWorkflowStore store,
        DevWorkflowNodeRunSnapshot nodeRun,
        string failureClass,
        string sanitizedReason,
        CancellationToken cancellationToken) =>
        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(nodeRun.RunId,
                            nodeRun.Id,
                            DevWorkflowVersions.Any,
                            DevWorkflowNodeRunStatus.Blocked,
                            PendingDecisionKind: DevWorkflowDecisionKind.Abandon,
                            FailureClass: failureClass,
                            TerminalReason: sanitizedReason,
                            WorkItemStatus: DevWorkflowWorkItemStatus.Blocked),
                        cancellationToken)
                    .ConfigureAwait(false);
}
