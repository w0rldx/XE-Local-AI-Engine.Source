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

    private const string UnjudgedReason = "Startup recovery could not settle this node run, because it kept changing while the host was starting.";

    /// <summary>
    ///     How many times recovery re-reads and re-judges before the last pass settles whatever is left. Bounded rather
    ///     than open-ended: a writer that keeps moving these rows is one this cannot outrace, and a startup that spins
    ///     on it never reaches the dispatcher.
    /// </summary>
    private const int RecoveryPasses = 3;

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

        // Read, decide, write once — and only the rows that are still what they were when they were judged. A pass that
        // finds rows it cannot judge (a second process moved one, or stranded one after the read) leaves those alone and
        // goes round again, because collapsing an unjudged row would strand it at Pending with nothing left to decide
        // what re-running it costs.
        var recovered = 0;
        var remaining = await store.ListInterruptedNodeRunsAsync(cancellationToken).ConfigureAwait(false);
        for (var pass = 1; pass <= RecoveryPasses && remaining.Count > 0; pass++)
        {
            var verdicts = await ComposeVerdictsAsync(store, sessions, remaining, cancellationToken).ConfigureAwait(false);

            // The last pass settles what it could not judge instead of walking away from it, because nothing downstream
            // would pick those rows up: the dispatcher admits Pending rows and follows Running AGENT ones, so a stranded
            // Tool row left behind wedges its run for good — "the next boot" is not something anybody schedules. The
            // settlement is decided against the live row inside that transaction, so the drift that caused this cannot
            // reach it.
            var unjudged = pass == RecoveryPasses
                ? new DevWorkflowUnjudgedNodeRunBlock(DevWorkflowFailureClasses.Interrupted, UnjudgedReason)
                : null;
            try
            {
                recovered += (await store.ReconcileNonTerminalNodeRunsAsync(InterruptedReason, verdicts, unjudged, cancellationToken).ConfigureAwait(false)).Count;
            }
            catch (DevWorkflowRetryBudgetExceededException refused)
            {
                // A human Retry committed while these verdicts were being composed, so an attempt this pass admitted
                // is one the run no longer has. The collapse rolls back whole, which is what makes another pass the
                // whole of the recovery: it re-reads the decisions and composes the Block instead. The last pass
                // settles whatever is still unjudged, so this cannot loop.
                _logger.LogWarning(refused, "Startup recovery pass {Pass} was refused its re-attempt budget, so it is being re-judged against the decision it did not see.", pass);
            }

            remaining = await store.ListInterruptedNodeRunsAsync(cancellationToken).ConfigureAwait(false);
        }

        if (remaining.Count > 0)
        {
            // Only reachable when something stranded these AFTER the settling pass looked, which makes them its rows
            // rather than ours: whatever is writing them is running, and blocking another writer's live work would be
            // the worse mistake.
            _logger.LogWarning("{Count} development workflow node run(s) went in flight while startup recovery was finishing, so recovery left them alone.",
                remaining.Count);
        }

        if (recovered > 0)
        {
            _logger.LogInformation("Reconciled {Count} in-flight development workflow node run(s) after host startup.", recovered);
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
    ///         Every verdict carries the row as it was observed, plus the transitions the collapse commits WITH it, in
    ///         order — so a row that both spends its attempt and then exhausts its run's budget records both, the
    ///         re-attempt and the intervention it ran into, exactly as two separate writes did before they had to be
    ///         atomic.
    ///     </para>
    ///     <para>
    ///         The budget counts a run's RE-attempts — the sum of <c>Attempt − 1</c> over its node runs — so a graph that
    ///         has merely started has spent none of it however many nodes it declares. What is left is handed out to the
    ///         interrupted sandbox node runs in node-key order, and the ones it does not reach are blocked WITHOUT an
    ///         attempt: several interrupted siblings each taking one would spend more than the run allows by the width
    ///         of its fan-out, which is precisely the restart loop this budget exists to stop. <c>MaxNodeRunsPerRun</c>
    ///         is deliberately NOT re-checked: it is enforced where the rows are created, and a run that somehow held
    ///         more than it allows cannot be repaired by blocking every node run it has — that would turn a bounded
    ///         accounting error into a dead run.
    ///     </para>
    /// </summary>
    private async Task<IReadOnlyList<DevWorkflowNodeRunVerdict>> ComposeVerdictsAsync(IDevWorkflowStore store,
        IWorkflowOwnedWorkSessionLifecycle sessions,
        IReadOnlyList<DevWorkflowReconciledNodeRun> interrupted,
        CancellationToken cancellationToken)
    {
        var repairs = new Dictionary<Guid, List<TransitionDevWorkflowNodeRunCommand>>();
        var blocked = new HashSet<Guid>();

        foreach (var nodeRun in interrupted)
        {
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
                Repair(repairs,
                    nodeRun,
                    Block(nodeRun,
                        DevWorkflowFailureClasses.Configuration,
                        "The work session this node run was driving no longer exists, so the host restart could not resume it."));
                _ = blocked.Add(nodeRun.NodeRunId);
            }
        }

        foreach (var group in interrupted.GroupBy(static nodeRun => nodeRun.RunId))
        {
            var nodeRuns = await store.ListNodeRunsAsync(group.Key, cancellationToken).ConfigureAwait(false);

            // TWO counts, deliberately. `spent` is what the run has actually made — the sum the trailing sweep below
            // has always decided on. `promised` adds the reservations: a Retry recorded before the crash and never
            // applied has spent no attempt for a sum over Attempt to see, but the dispatcher turns it into one on its
            // first tick after this (FU3-4 race B).
            //
            // Only the ADMISSION decision uses `promised`. Widening the trailing sweep with it would block rows that
            // cost nothing — an interrupted Agent row resumes from a checkpoint it wrote itself and is charged no
            // attempt, so a single unapplied Retry sitting on the last slot would send every one of them to a human at
            // boot for a slot none of them wanted.
            var spent = nodeRuns.Sum(static nodeRun => nodeRun.Attempt - 1);
            var promised = DevWorkflowRetryPolicy.Promised(nodeRuns, await store.ListDecisionsAsync(group.Key, cancellationToken).ConfigureAwait(false));

            // The re-attempts this run can still afford, handed out in a fixed order so the same boot always admits the
            // same rows. Several interrupted sandbox node runs otherwise each take an attempt they were never all
            // entitled to, and the run ends up having spent more than it allows by however wide its fan-out is.
            var affordable = Math.Max(0, _options.MaxTotalAttempts - promised);
            var interruptedSandbox = group.Where(static nodeRun => nodeRun.NodeType is DevWorkflowNodeType.Tool or DevWorkflowNodeType.DevTask
                                                                   && nodeRun.Status == DevWorkflowNodeRunStatus.Running)
                                          .OrderBy(static nodeRun => nodeRun.NodeKey, StringComparer.Ordinal)
                                          .ThenBy(static nodeRun => nodeRun.NodeRunId)
                                          .ToList();

            // A row already AT its own cap is not a candidate for the run's budget at all: recovery increments the
            // attempt of every row it admits, and this one has no attempt left to be given. The live path refuses the
            // same row before every automatic re-attempt; recovery bypassed that check entirely and reset a 3-of-3 row
            // to Pending at 4 (FU3-4). Blocked with its OWN reason, so nobody reads it as the run-wide budget.
            var atCap = interruptedSandbox.Where(static nodeRun => nodeRun.Attempt >= nodeRun.MaxAttempts).ToList();
            var sandboxed = interruptedSandbox.Where(static nodeRun => nodeRun.Attempt < nodeRun.MaxAttempts).ToList();
            var admitted = Math.Min(affordable, sandboxed.Count);
            spent += admitted;
            var exhausted = $"This run has already spent {spent} re-attempts, which is as many re-attempts as this run allows.";

            foreach (var nodeRun in atCap)
            {
                Repair(repairs,
                    nodeRun,
                    Block(nodeRun,
                        DevWorkflowFailureClasses.BudgetExhausted,
                        $"This node has already been attempted {nodeRun.Attempt} times, which is as many as it allows, so the host restart could not re-run it."));
                _ = blocked.Add(nodeRun.NodeRunId);
            }

            foreach (var nodeRun in sandboxed.Take(admitted))
            {
                // The sandbox process is gone and its workspace may be half-prepared, so the re-run is a real second
                // attempt and has to count against the node's budget — unlike an agent, whose session resumes from a
                // checkpoint it wrote itself.
                Repair(repairs,
                    nodeRun,
                    new TransitionDevWorkflowNodeRunCommand(nodeRun.RunId,
                        nodeRun.NodeRunId,
                        DevWorkflowVersions.Any,
                        DevWorkflowNodeRunStatus.Pending,
                        IncrementAttempt: true,
                        Outcome: DevWorkflowOutcomes.Interrupted,

                        // `affordable` above is a check-then-write like every other one this pass replaced: the run
                        // service can be recording a human Retry while these verdicts are being composed. The budget
                        // rides on the command so the collapse admits it under the writer lock, and a refusal rolls
                        // the whole pass back for StartAsync to re-judge from the decision it did not see.
                        MaxTotalAttempts: _options.MaxTotalAttempts));
            }

            foreach (var nodeRun in sandboxed.Skip(admitted))
            {
                // Nothing left to pay for this one's re-run, so it is handed over UNSPENT: an attempt recorded here
                // would be one the run never had, and the row would read as having tried again when it never did.
                Repair(repairs, nodeRun, Block(nodeRun, DevWorkflowFailureClasses.BudgetExhausted, exhausted));
                _ = blocked.Add(nodeRun.NodeRunId);
            }

            if (spent < _options.MaxTotalAttempts)
            {
                continue;
            }

            _logger.LogWarning("Development workflow run {RunId} has spent {Spent} re-attempts, so its interrupted node runs need a human.", group.Key, spent);
            foreach (var nodeRun in group.Where(nodeRun => !blocked.Contains(nodeRun.NodeRunId)))
            {
                Repair(repairs, nodeRun, Block(nodeRun, DevWorkflowFailureClasses.BudgetExhausted, exhausted));
            }
        }

        return
        [
            .. interrupted.Select(nodeRun => new DevWorkflowNodeRunVerdict(nodeRun.NodeRunId,
                nodeRun.Status,
                nodeRun.Attempt,
                nodeRun.WorkSessionId,
                repairs.TryGetValue(nodeRun.NodeRunId, out var composed) ? composed : []))
        ];
    }

    /// <summary>Repairs apply in the order they are added, which is what lets one row spend its attempt and then block on the budget it just exhausted.</summary>
    private static void Repair(Dictionary<Guid, List<TransitionDevWorkflowNodeRunCommand>> repairs,
        DevWorkflowReconciledNodeRun nodeRun,
        TransitionDevWorkflowNodeRunCommand command)
    {
        if (!repairs.TryGetValue(nodeRun.NodeRunId, out var composed))
        {
            composed = [];
            repairs[nodeRun.NodeRunId] = composed;
        }

        composed.Add(command);
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
