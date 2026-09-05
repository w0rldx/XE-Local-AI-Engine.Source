namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Makes the node runs a crashed or restarted host left in flight judgeable again, exactly once, at startup.
///     <para>
///         Registered BEFORE the dispatcher, and that half matters on its own: hosted services start in registration
///         order, so the dispatcher's pumps must not begin admitting rows this has not judged yet. The work-session and
///         development-workflow reconcilers are registered ahead of both, because their modules are added first.
///     </para>
///     <para>
///         Exactly once survives a crash DURING recovery, because the collapse and every verdict that follows from it
///         commit together: this reads the interrupted rows, decides what each one costs, and hands those decisions to
///         the store to apply inside the one transaction that collapses them. A host that dies before that commit
///         leaves the rows as it found them, and the next boot judges them again from the same evidence.
///     </para>
///     <para>
///         <see cref="StartAsync" /> catches nothing, deliberately: a store that cannot be read at boot is a node whose
///         graph workflow rows would be admitted unjudged, and failing host start is the honest answer to that. It
///         matches the development-workflow reconciler this is modelled on.
///     </para>
///     <para>
///         The interrupted set is exactly <c>Queued ∪ Running</c>, which the store scopes and this never widens.
///         <c>WaitingForApproval</c> is a durable human wait rather than in-flight work, and a reconciler that took it
///         would destroy every pause on the node on every boot.
///     </para>
///     <para>
///         It touches no RUN row. The dispatcher sweeps every live run on its first pass — <c>PumpSweepAsync</c> runs
///         one sweep immediately rather than waiting an interval — so the run's own status is recomputed from the rows
///         this left behind without anything here writing to it.
///     </para>
/// </summary>
internal sealed class GraphWorkflowStartupReconciler : IHostedService
{
    private const string InterruptedReason = "The host restarted while the node run was in flight.";

    private const string UnjudgedReason = "Startup recovery could not settle this node run, because it kept changing while the host was starting.";

    /// <summary>
    ///     How many times recovery re-reads and re-judges before the last pass settles whatever is left. Bounded rather
    ///     than open-ended: a writer that keeps moving these rows is one this cannot outrace, and a startup that spins
    ///     on it never reaches the dispatcher.
    /// </summary>
    private const int RecoveryPasses = 3;

    private readonly ILogger<GraphWorkflowStartupReconciler> _logger;
    private readonly GraphWorkflowOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;

    public GraphWorkflowStartupReconciler(IServiceScopeFactory scopeFactory,
        IOptions<GraphWorkflowOptions> options,
        ILogger<GraphWorkflowStartupReconciler> logger)
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
            // The services stay registered when the feature is off, so the guard is here rather than in the container —
            // and it is BEFORE the scope, so a disabled node opens no scope and reads no row.
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();

        // One scope for every pass, which is the caller shape ReconcileNonTerminalNodeRunsAsync documents: it clears the
        // change tracker itself so a later pass cannot judge run rows its earlier one cached.
        await RecoverAsync(scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>(), cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    ///     The recovery itself, over a store the caller supplies. Separate from <see cref="StartAsync" /> so a test
    ///     drives a restart without a hosted-service lifecycle, and so the substitution a drift test needs is one
    ///     argument rather than a container.
    ///     <para>
    ///         Read, decide, write once — and only the rows that are still what they were when they were judged. A pass
    ///         that finds rows it cannot judge leaves those alone and goes round again, because collapsing an unjudged
    ///         row would strand it at <c>Pending</c> with nothing left to decide what re-running it costs. The last pass
    ///         settles what is left instead of walking away from it: there is no <c>Blocked</c> state in v1 to park a
    ///         row in, and "the next boot" is not something anybody schedules.
    ///     </para>
    /// </summary>
    internal async Task RecoverAsync(IGraphWorkflowStore store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);

        var recovered = 0;
        var remaining = await store.ListInterruptedNodeRunsAsync(cancellationToken).ConfigureAwait(false);
        for (var pass = 1; pass <= RecoveryPasses && remaining.Count > 0; pass++)
        {
            var verdicts = ComposeVerdicts(remaining);
            var unjudged = pass == RecoveryPasses
                ? new GraphWorkflowUnjudgedNodeRunSettlement(GraphWorkflowFailureClass.Interrupted, UnjudgedReason)
                : null;
            var reconciled = await store.ReconcileNonTerminalNodeRunsAsync(InterruptedReason, verdicts, unjudged, cancellationToken).ConfigureAwait(false);
            recovered += reconciled.Count;
            _logger.LogInformation("Graph workflow startup recovery pass {Pass} of {Passes} judged {Judged} in-flight node run(s) and reconciled {Reconciled}.",
                pass,
                RecoveryPasses,
                remaining.Count,
                reconciled.Count);
            remaining = await store.ListInterruptedNodeRunsAsync(cancellationToken).ConfigureAwait(false);
        }

        if (remaining.Count > 0)
        {
            // Only reachable when something stranded these AFTER the settling pass looked, which makes them its rows
            // rather than ours: whatever is writing them is running, and blocking another writer's live work would be
            // the worse mistake.
            _logger.LogWarning("{Count} graph workflow node run(s) went in flight while startup recovery was finishing, so recovery left them alone.", remaining.Count);
        }

        if (recovered > 0)
        {
            _logger.LogInformation("Reconciled {Count} in-flight graph workflow node run(s) after host startup.", recovered);
        }
    }

    /// <summary>
    ///     What the store cannot know: whether the work behind each interrupted row can be picked up where it stopped.
    ///     <para>
    ///         The store collapses every stranded row to <c>Pending</c> without touching <c>Attempt</c>, which is the
    ///         whole verdict for the two cases that dominate — a <c>Queued</c> row that was never dispatched, and a
    ///         <c>Running</c> inline node, which is a pure function of rows the crash did not change. Neither is a
    ///         failure, so neither is an attempt, and neither needs a repair on top of the collapse.
    ///     </para>
    ///     <para>
    ///         A <c>Running</c> <c>Agent</c> or <c>Tool</c> row is the exception, and it is failed rather than resumed:
    ///         the work was an in-process task with no durable handle, so its partial output died with the host. This
    ///         never re-attempts it. The dispatcher's retry stage does, on its first tick, if and only if the node and
    ///         run budgets allow — which is why the class written here is the plain <c>Interrupted</c> rather than
    ///         anything <c>GraphWorkflowFailures.Classify</c> would decide from a graph this deliberately never parses.
    ///     </para>
    ///     <para>
    ///         The <c>Tool</c> half of that arm is S2's: S1 registers no Tool lane, so nothing can leave such a row
    ///         behind and no S1 test can reach it. It is written now because the verdict and the reason for it are the
    ///         Agent arm's exactly, and a kind added to this table later than its lane is a kind nobody adds at all.
    ///     </para>
    /// </summary>
    private IReadOnlyList<GraphWorkflowNodeRunVerdict> ComposeVerdicts(IReadOnlyList<GraphWorkflowReconciledNodeRun> interrupted)
    {
        var verdicts = new List<GraphWorkflowNodeRunVerdict>(interrupted.Count);
        foreach (var nodeRun in interrupted)
        {
            var failed = nodeRun is { Status: GraphWorkflowNodeRunStatus.Running, Kind: GraphWorkflowNodeKind.Agent or GraphWorkflowNodeKind.Tool };
            _logger.LogDebug("Graph workflow node run {NodeRunId} ({NodeKey}, {Kind}) was left {Status} on run {RunId} and is judged {Verdict}.",
                nodeRun.NodeRunId,
                nodeRun.NodeKey,
                nodeRun.Kind,
                nodeRun.Status,
                nodeRun.RunId,
                failed ? "Failed(Interrupted)" : "Pending");
            verdicts.Add(new GraphWorkflowNodeRunVerdict(nodeRun.NodeRunId,
                nodeRun.Status,
                nodeRun.Attempt,
                failed
                    ?
                    [
                        new TransitionGraphWorkflowNodeRunCommand(nodeRun.RunId,
                            nodeRun.NodeRunId,
                            GraphWorkflowVersions.Any,
                            GraphWorkflowNodeRunStatus.Failed,
                            FailureClass: GraphWorkflowFailureClass.Interrupted,
                            TerminalReason: InterruptedReason)
                    ]
                    : []));
        }

        return verdicts;
    }
}
