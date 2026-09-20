namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.WorkSessions;

/// <summary>
///     Makes the node runs a crashed or restarted host left mid-flight dispatchable again, exactly once, at startup.
/// </summary>
/// <remarks>
///     Registered AFTER <c>WorkSessionStartupReconciler</c> and BEFORE the dispatcher, and both halves matter: a
///     node run that resumes must not find its session holding a half-written turn, and the dispatcher must not admit
///     rows this has not judged. Exactly once survives a crash DURING recovery, because the collapse and every
///     verdict commit together. It touches no RUN row: runs auto-resume. See
///     docs/wiki/25-dev-workflows.md ("Restart recovery").
/// </remarks>
public sealed class DevWorkflowStartupReconciler : IHostedService
{
    private const string InterruptedReason = "The host restarted while the node run was in flight.";

    private const string UnjudgedReason = "Startup recovery could not settle this node run, because it kept changing while the host was starting.";

    /// <summary>
    ///     How many times recovery re-reads and re-judges before the last pass settles whatever is left.
    /// </summary>
    /// <remarks>
    ///     Bounded rather than open-ended: a writer that keeps moving these rows is one this cannot outrace, and a
    ///     startup that spins on it never reaches the dispatcher.
    /// </remarks>
    private const int RecoveryPasses = 3;

    /// <summary>
    ///     How many times the loop may run in total, counting the passes a concurrent operator <c>Retry</c> refused.
    /// </summary>
    /// <remarks>
    ///     A refused pass writes nothing — the collapse rolls back whole — so counting it as one of the
    ///     <see cref="RecoveryPasses" /> would spend a pass on a judgement never applied, and a refusal on the LAST
    ///     one would throw the settling pass away with it, leaving the rows Queued/Running, the pair of states nothing
    ///     downstream recovers. A cap rather than an unbounded retry, because a writer that refuses every pass is one
    ///     this cannot outrace either.
    /// </remarks>
    private const int RecoveryAttempts = RecoveryPasses + 2;

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

        // Read, decide, write once — and only the rows still what they were when judged. A pass that finds rows it cannot judge leaves them alone and goes round again, because
        // collapsing an unjudged row would strand it at Pending with nothing left to decide what re-running it costs.
        var recovered = 0;
        var pass = 1;
        var attempts = 0;
        var remaining = await store.ListInterruptedNodeRunsAsync(cancellationToken);
        while (pass <= RecoveryPasses && attempts < RecoveryAttempts && remaining.Count > 0)
        {
            attempts++;
            var verdicts = await ComposeVerdictsAsync(store, sessions, remaining, cancellationToken);

            // The last pass settles what it could not judge instead of walking away: nothing downstream picks those rows up, because the dispatcher admits Pending rows and follows
            // Running AGENT ones, so a stranded Tool row wedges its run for good. The settlement is decided against the live row inside that transaction, so drift cannot reach it.
            var unjudged = pass == RecoveryPasses
                ? new DevWorkflowUnjudgedNodeRunBlock { FailureClass = DevWorkflowFailureClasses.Interrupted, SanitizedReason = UnjudgedReason }
                : null;
            try
            {
                recovered += (await store.ReconcileNonTerminalNodeRunsAsync(InterruptedReason, verdicts, unjudged, cancellationToken)).Count;
                pass++;
            }
            catch (DevWorkflowRetryBudgetExceededException refused)
            {
                // A human Retry committed while these verdicts were composed, so an attempt this pass admitted is one the run no longer has. The collapse rolls back whole, which makes
                // another pass the whole recovery. The pass counter deliberately does NOT advance: nothing was written, and `attempts` is what bounds this instead.
                _logger.LogWarning(refused, "Startup recovery pass {Pass} was refused its re-attempt budget, so it is being re-judged against the decision it did not see.", pass);
            }

            remaining = await store.ListInterruptedNodeRunsAsync(cancellationToken);
        }

        if (remaining.Count > 0 && pass <= RecoveryPasses)
        {
            // The cap stopped us: every attempt, the settling one included, was refused its budget by a writer that never stopped. These rows stay Queued or Running, the one outcome
            // nothing downstream repairs, so each run is named here rather than left for whoever notices it has stopped moving.
            foreach (var stranded in remaining.GroupBy(static nodeRun => nodeRun.RunId))
            {
                _logger.LogWarning("Startup recovery gave up after {Attempts} refused attempts, so development workflow run {RunId} keeps {Count} unsettled node run(s) "
                                   + "that need a human: {NodeRuns}.",
                    attempts,
                    stranded.Key,
                    stranded.Count(),
                    string.Join(", ", stranded.Select(static nodeRun => $"{nodeRun.NodeKey} ({nodeRun.Status})")));
            }
        }
        else if (remaining.Count > 0)
        {
            // Only reachable when something stranded these AFTER the settling pass looked, which makes them its rows rather than ours: whatever is writing them is running, and
            // blocking another writer's live work would be the worse mistake.
            _logger.LogWarning("{Count} development workflow node run(s) went in flight while startup recovery was finishing, so recovery left them alone.",
                remaining.Count);
        }

        if (recovered > 0)
        {
            _logger.LogInformation("Reconciled {Count} in-flight development workflow node run(s) after host startup.", recovered);
        }

        // Unconditional, unlike everything above it: an orphan is a session no node run references, so there is no reconciled row that could lead to one.
        await SweepOrphanedWorkSessionsAsync(store,
                sessions,
                scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>(),
                cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    ///     What the store could not know: whether re-running each interrupted node run costs an attempt, whether it
    ///     can be re-run at all, and whether its run has any re-attempts left to spend on it.
    /// </summary>
    /// <remarks>
    ///     The store collapses every stranded row to <c>Pending</c> without touching <c>Attempt</c>, right for the two
    ///     dominant cases — an agent resuming on its own checkpoint, and a queued row never dispatched — neither a
    ///     failure. Each verdict carries the row as observed plus the transitions the collapse commits WITH it, so a
    ///     row can spend its attempt and then block on the budget it exhausted. The budget is <c>Attempt − 1</c>
    ///     summed over the run, handed out in node-key order; what it does not reach blocks WITHOUT an attempt.
    /// </remarks>
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
                _ = await sessions.GetAsync(sessionId, cancellationToken);
            }
            catch (WorkSessionNotFoundException)
            {
                // Deleted out from under the run. Nothing can resume it, and a retry would only create a second session for work whose transcript is gone, so it goes to a human.
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
            var nodeRuns = await store.ListNodeRunsAsync(group.Key, cancellationToken);

            // TWO counts. `spent` is what the run made and is what the trailing sweep decides on; `promised` adds the reservations a Retry recorded before the crash left, which no
            // sum over Attempt can see (FU3-4 race B). Only ADMISSION uses `promised` — widening the sweep sends every interrupted Agent row, which costs no attempt, to a human.
            var spent = nodeRuns.Sum(static nodeRun => nodeRun.Attempt - 1);
            var promised = DevWorkflowRetryPolicy.Promised(nodeRuns, await store.ListDecisionsAsync(group.Key, cancellationToken));

            // The re-attempts this run can still afford, handed out in a fixed order so the same boot always admits the same rows. Otherwise several interrupted sandbox node runs each
            // take an attempt they were never all entitled to, and the run spends more than it allows by the width of its fan-out.
            var affordable = Math.Max(0, _options.MaxTotalAttempts - promised);
            var interruptedSandbox = group.Where(static nodeRun => nodeRun.NodeType is DevWorkflowNodeType.Tool or DevWorkflowNodeType.DevTask
                                                                   && nodeRun.Status == DevWorkflowNodeRunStatus.Running)
                                          .OrderBy(static nodeRun => nodeRun.NodeKey, StringComparer.Ordinal)
                                          .ThenBy(static nodeRun => nodeRun.NodeRunId)
                                          .ToList();

            // A row already AT its own cap is no candidate for the run's budget: recovery increments the attempt of every row it admits, and this one has none left to be given —
            // without the check a 3-of-3 row resets to Pending at 4 (FU3-4). Blocked with its OWN reason, so nobody reads it as the run-wide budget.
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
                // The sandbox process is gone and its workspace may be half-prepared, so the re-run is a real second attempt and counts against the node's budget — unlike an agent,
                // whose session resumes from a checkpoint it wrote itself.
                Repair(repairs,
                    nodeRun,
                    new TransitionDevWorkflowNodeRunCommand
                    {
                        RunId = nodeRun.RunId,
                        NodeRunId = nodeRun.NodeRunId,
                        ExpectedVersion = DevWorkflowVersions.Any,
                        TargetStatus = DevWorkflowNodeRunStatus.Pending,
                        IncrementAttempt = true,
                        Outcome = DevWorkflowOutcomes.Interrupted,
                        // `affordable` above is a check-then-write: the run service can record a human Retry while these verdicts are composed. The budget rides on the
                        // command so the collapse admits it under the writer lock, and a refusal rolls the pass back for StartAsync to re-judge.
                        MaxTotalAttempts = _options.MaxTotalAttempts
                    });
            }

            foreach (var nodeRun in sandboxed.Skip(admitted))
            {
                // Nothing left to pay for this one's re-run, so it is handed over UNSPENT: an attempt recorded here would be one the run never had, and the row would read as having
                // tried again when it never did.
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
            .. interrupted.Select(nodeRun => new DevWorkflowNodeRunVerdict
            {
                NodeRunId = nodeRun.NodeRunId,
                ObservedStatus = nodeRun.Status,
                ObservedAttempt = nodeRun.Attempt,
                ObservedWorkSessionId = nodeRun.WorkSessionId,
                Repairs = repairs.TryGetValue(nodeRun.NodeRunId, out var composed) ? composed : []
            })
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
    /// </summary>
    /// <remarks>
    ///     That is what a host death between <c>CreateAsync</c> and <c>AttachWorkSessionAsync</c> leaves behind, and
    ///     it is unreachable otherwise. <c>Draft</c> is load-bearing: a re-attempt clears <c>WorkSessionId</c>, so the
    ///     PREVIOUS attempt's session is owned by nothing too — but that one RAN, and its transcript is evidence the
    ///     event log keeps only the id of, so a driven session is never swept. Startup only, never per-tick: a session
    ///     created a millisecond ago is indistinguishable from an orphan, and a concurrent sweep deletes live work.
    /// </remarks>
    private async Task SweepOrphanedWorkSessionsAsync(IDevWorkflowStore store,
        IWorkflowOwnedWorkSessionLifecycle sessions,
        IAgentWorkSessionStore workSessions,
        CancellationToken cancellationToken)
    {
        var owned = (await store.ListOwnedWorkSessionIdsAsync(cancellationToken)).ToHashSet();
        var orphans = (await workSessions.ListAsync(cancellationToken))
                      .Where(session => session is { Kind: AgentWorkSessionKind.Workflow, Status: AgentWorkSessionStatus.Draft } && !owned.Contains(session.Id))
                      .Select(static session => session.Id)
                      .ToList();

        foreach (var orphan in orphans)
        {
            // Named, one line each: a session lost this way is a crash that happened, and deleting it silently would erase the evidence along with the row.
            _logger.LogWarning("Deleting orphaned workflow work session {SessionId}, which was never driven and which no development workflow node run references.",
                orphan);
            try
            {
                await sessions.DeleteAsync(orphan, cancellationToken);
            }
            catch (Exception exception) when (exception is WorkSessionInvalidTransitionException or WorkSessionNotFoundException)
            {
                _logger.LogWarning(exception, "Orphaned workflow work session {SessionId} could not be deleted and has to be removed by hand.", orphan);
            }
        }
    }

    private static TransitionDevWorkflowNodeRunCommand Block(DevWorkflowReconciledNodeRun nodeRun, string failureClass, string sanitizedReason) =>
        new()
        {
            RunId = nodeRun.RunId,
            NodeRunId = nodeRun.NodeRunId,
            ExpectedVersion = DevWorkflowVersions.Any,
            TargetStatus = DevWorkflowNodeRunStatus.Blocked,
            PendingDecisionKind = DevWorkflowDecisionKind.Abandon,
            FailureClass = failureClass,
            TerminalReason = sanitizedReason,
            WorkItemStatus = DevWorkflowWorkItemStatus.Blocked
        };
}
