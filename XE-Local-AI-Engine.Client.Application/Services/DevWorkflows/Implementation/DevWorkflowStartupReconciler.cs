namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

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
///         Exactly once survives a crash DURING recovery, because the collapse and every verdict that follows from it
///         commit together: this reads the interrupted rows, decides what each one costs, and hands those decisions to
///         the store to apply inside the one transaction that collapses them. A host that dies before that commit
///         leaves the rows as it found them, and the next boot judges them again from the same evidence; one that dies
///         after it finds nothing left to judge. Neither can spend a second attempt on one interruption.
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

        // Read first, decide everything, then write once. Judging the rows before anything is committed is what makes
        // recovery all-or-nothing: a crash during startup leaves them exactly as the dead host left them, so the next
        // boot judges the same rows again rather than finding repaired-looking rows nothing will ever finish repairing.
        var interrupted = await store.ListInterruptedNodeRunsAsync(cancellationToken).ConfigureAwait(false);
        var repairs = await ComposeRepairsAsync(store, sessions, interrupted, cancellationToken).ConfigureAwait(false);
        var reconciled = await store.ReconcileNonTerminalNodeRunsAsync(InterruptedReason, repairs, cancellationToken).ConfigureAwait(false);
        if (reconciled.Count > 0)
        {
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
    ///     What the store could not know: whether re-running each interrupted node run costs an attempt, whether it can
    ///     be re-run at all, and whether its run has any re-attempts left to spend on it.
    ///     <para>
    ///         The store collapses every stranded row to <c>Pending</c> without touching <c>Attempt</c>, which is right
    ///         for the two cases that dominate — an agent whose session survives the restart on its own checkpoint, and
    ///         a queued row that was never dispatched. Neither is a failure, so neither is an attempt.
    ///     </para>
    ///     <para>
    ///         Every verdict is composed as a transition the collapse commits WITH it, in this order, so a row that both
    ///         spends its attempt and then exhausts its run's budget records both — the re-attempt and the intervention
    ///         it ran into — exactly as two separate writes did before they had to be atomic.
    ///     </para>
    ///     <para>
    ///         The budget counts a run's RE-attempts — the sum of <c>Attempt − 1</c> over its node runs, plus the ones
    ///         this pass is about to spend — so a graph that has merely started has spent none of it however many nodes
    ///         it declares. It is the guard against a definition that restart-loops: without it, a node run the host
    ///         keeps dying under would spend an attempt on every boot forever. <c>MaxNodeRunsPerRun</c> is deliberately
    ///         NOT re-checked: it is enforced where the rows are created, and a run that somehow held more than it
    ///         allows cannot be repaired by blocking every node run it has — that would turn a bounded accounting error
    ///         into a dead run.
    ///     </para>
    /// </summary>
    private async Task<IReadOnlyList<TransitionDevWorkflowNodeRunCommand>> ComposeRepairsAsync(IDevWorkflowStore store,
        IWorkflowOwnedWorkSessionLifecycle sessions,
        IReadOnlyList<DevWorkflowReconciledNodeRun> interrupted,
        CancellationToken cancellationToken)
    {
        var repairs = new List<TransitionDevWorkflowNodeRunCommand>();
        var reattempted = new HashSet<Guid>();
        var blocked = new HashSet<Guid>();

        foreach (var nodeRun in interrupted)
        {
            if (nodeRun.NodeType is DevWorkflowNodeType.Tool or DevWorkflowNodeType.DevTask && nodeRun.Status == DevWorkflowNodeRunStatus.Running)
            {
                // The sandbox process is gone and its workspace may be half-prepared, so the re-run is a real second
                // attempt and has to count against the node's budget — unlike an agent, whose session resumes from a
                // checkpoint it wrote itself.
                repairs.Add(new TransitionDevWorkflowNodeRunCommand(nodeRun.RunId,
                    nodeRun.NodeRunId,
                    DevWorkflowVersions.Any,
                    DevWorkflowNodeRunStatus.Pending,
                    IncrementAttempt: true,
                    Outcome: DevWorkflowOutcomes.Interrupted));
                _ = reattempted.Add(nodeRun.NodeRunId);
                continue;
            }

            if (nodeRun is not { NodeType: DevWorkflowNodeType.Agent, WorkSessionId: { } sessionId })
            {
                continue;
            }

            try
            {
                _ = await sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
            }
            catch (WorkSessionNotFoundException)
            {
                // Deleted out from under the run. Nothing can resume it and a retry would only create a second session
                // for work whose transcript is already gone, so it goes to a human with the reason on the row.
                repairs.Add(Block(nodeRun,
                    DevWorkflowFailureClasses.Configuration,
                    "The work session this node run was driving no longer exists, so the host restart could not resume it."));
                _ = blocked.Add(nodeRun.NodeRunId);
            }
        }

        foreach (var group in interrupted.GroupBy(static nodeRun => nodeRun.RunId))
        {
            var nodeRuns = await store.ListNodeRunsAsync(group.Key, cancellationToken).ConfigureAwait(false);
            var spent = nodeRuns.Sum(static nodeRun => nodeRun.Attempt - 1) + group.Count(nodeRun => reattempted.Contains(nodeRun.NodeRunId));
            if (spent < _options.MaxTotalAttempts)
            {
                continue;
            }

            _logger.LogWarning("Development workflow run {RunId} has spent {Spent} re-attempts, so its interrupted node runs need a human.", group.Key, spent);
            repairs.AddRange(group.Where(nodeRun => !blocked.Contains(nodeRun.NodeRunId))
                                  .Select(nodeRun => Block(nodeRun,
                                      DevWorkflowFailureClasses.BudgetExhausted,
                                      $"This run has already spent {spent} re-attempts, which is as many re-attempts as this run allows.")));
        }

        return repairs;
    }

    /// <summary>
    ///     Deletes every workflow-kind work session that no node run references AND that was never driven.
    ///     <para>
    ///         That is precisely what a host death between <c>CreateAsync</c> and <c>AttachWorkSessionAsync</c> leaves
    ///         behind, and it is unreachable by design: the owner surface refuses workflow-kind sessions to every
    ///         external caller, a work-item delete can only release what its node runs point AT, and the next tick
    ///         creates a fresh session rather than finding this one. Never started, it holds no transcript to lose.
    ///     </para>
    ///     <para>
    ///         <c>Draft</c> is what makes that last sentence true, and it is load-bearing rather than tidiness. A
    ///         re-attempt clears <c>WorkSessionId</c> so the fresh attempt cannot resume a poisoned context, which
    ///         leaves the PREVIOUS attempt's session owned by nothing as well — but that one RAN, and its transcript is
    ///         the evidence of an attempt the event log keeps only the id of. Auditability is this module's pillar, so
    ///         a driven session is never swept. A work-item delete interrupted before it released its sessions
    ///         therefore leaves recoverable orphans rather than being mopped up here: rare, kind-scoped, and deletable
    ///         through the owner surface.
    ///     </para>
    ///     <para>
    ///         Startup only, and two queries — every workflow-kind session, and the ids node runs own. It is deliberately
    ///         NOT a per-tick sweep: a session created a millisecond ago and not yet attached is indistinguishable from
    ///         an orphan, and a sweep running concurrently with the executor would delete live work.
    ///     </para>
    /// </summary>
    private async Task SweepOrphanedWorkSessionsAsync(IDevWorkflowStore store,
        IWorkflowOwnedWorkSessionLifecycle sessions,
        IAgentWorkSessionStore workSessions,
        CancellationToken cancellationToken)
    {
        var owned = (await store.ListOwnedWorkSessionIdsAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var orphans = (await workSessions.ListAsync(cancellationToken).ConfigureAwait(false))
                      .Where(session => session is { Kind: AgentWorkSessionKind.Workflow, Status: AgentWorkSessionStatus.Draft } && !owned.Contains(session.Id))
                      .Select(static session => session.Id)
                      .ToList();

        foreach (var orphan in orphans)
        {
            // Named, one line each: a session lost this way is a crash that happened, and deleting it silently would
            // erase the evidence along with the row.
            _logger.LogWarning("Deleting orphaned workflow work session {SessionId}, which was never driven and which no development workflow node run references.",
                orphan);
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

    private static TransitionDevWorkflowNodeRunCommand Block(DevWorkflowReconciledNodeRun nodeRun, string failureClass, string sanitizedReason) =>
        new(nodeRun.RunId,
            nodeRun.NodeRunId,
            DevWorkflowVersions.Any,
            DevWorkflowNodeRunStatus.Blocked,
            PendingDecisionKind: DevWorkflowDecisionKind.Abandon,
            FailureClass: failureClass,
            TerminalReason: sanitizedReason,
            WorkItemStatus: DevWorkflowWorkItemStatus.Blocked);
}
