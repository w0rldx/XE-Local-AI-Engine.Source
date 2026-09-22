namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Makes the node runs a crashed or restarted host left in flight judgeable again, exactly once, at startup.
/// </summary>
/// <remarks>
///     Registered BEFORE the dispatcher, whose pumps must not begin admitting rows this has not judged; hosted
///     services start in registration order, and the work-session and development-workflow reconcilers precede both
///     because their modules are added first. <see cref="StartAsync" /> catches nothing, deliberately: a store that
///     cannot be read at boot is a node whose rows would be admitted unjudged, and failing host start is the honest
///     answer. What it judges, what a verdict costs and why no run row moves: docs/wiki/21-graph-workflows.md ("Restart").
/// </remarks>
internal sealed class GraphWorkflowStartupReconciler : IHostedService
{
    private const string InterruptedReason = "The host restarted while the node run was in flight.";

    private const string UnjudgedReason = "Startup recovery could not settle this node run, because it kept changing while the host was starting.";

    /// <summary>How many times recovery re-reads and re-judges before the last pass settles whatever is left.</summary>
    /// <remarks>
    ///     Bounded rather than open-ended: a writer that keeps moving these rows is one this cannot outrace, and a
    ///     startup that spins on it never reaches the dispatcher.
    /// </remarks>
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
        await RecoverAsync(scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>(), cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>The recovery itself, over a store the caller supplies.</summary>
    /// <remarks>
    ///     Separate from <see cref="StartAsync" /> so a test drives a restart without a hosted-service lifecycle, and
    ///     so a drift test substitutes one argument rather than a container. Read, decide, write once, and only the
    ///     rows still what they were when judged: a pass that finds rows it cannot judge leaves those alone and goes
    ///     round again, because collapsing an unjudged row would strand it at <c>Pending</c> with nothing left to
    ///     decide what re-running costs. The last pass settles what is left: no status parks a row, and nobody schedules "the next boot".
    /// </remarks>
    internal async Task RecoverAsync(IGraphWorkflowStore store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);

        var recovered = 0;
        var remaining = await store.ListInterruptedNodeRunsAsync(cancellationToken);
        for (var pass = 1; pass <= RecoveryPasses && remaining.Count > 0; pass++)
        {
            var verdicts = ComposeVerdicts(remaining);
            var unjudged = pass == RecoveryPasses
                ? new GraphWorkflowUnjudgedNodeRunSettlement { FailureClass = GraphWorkflowFailureClass.Interrupted, SanitizedReason = UnjudgedReason }
                : null;
            var reconciled = await store.ReconcileNonTerminalNodeRunsAsync(InterruptedReason, verdicts, unjudged, cancellationToken);
            recovered += reconciled.Count;
            _logger.LogInformation("Graph workflow startup recovery pass {Pass} of {Passes} judged {Judged} in-flight node run(s) and reconciled {Reconciled}.",
                pass,
                RecoveryPasses,
                remaining.Count,
                reconciled.Count);
            remaining = await store.ListInterruptedNodeRunsAsync(cancellationToken);
        }

        if (remaining.Count > 0)
        {
            // Only reachable when something stranded these AFTER the settling pass looked, which makes them that writer's
            // rows: whatever is writing them is running, and blocking another writer's live work would be the worse mistake.
            _logger.LogWarning("{Count} graph workflow node run(s) went in flight while startup recovery was finishing, so recovery left them alone.", remaining.Count);
        }

        if (recovered > 0)
        {
            _logger.LogInformation("Reconciled {Count} in-flight graph workflow node run(s) after host startup.", recovered);
        }
    }

    /// <summary>
    ///     What the store cannot know: whether the work behind each interrupted row can be picked up where it stopped.
    /// </summary>
    /// <remarks>
    ///     The store collapses every stranded row to <c>Pending</c> without touching <c>Attempt</c>, the whole verdict
    ///     for the two cases that dominate — a <c>Queued</c> row never dispatched, and a <c>Running</c> inline node, a
    ///     pure function of rows the crash did not change. Neither is a failure, so neither costs an attempt. The
    ///     <c>Tool</c> arm is written beside the <c>Agent</c> one because its verdict and reason are identical, and a
    ///     kind added later than its lane is a kind nobody adds. It writes the plain class, never <c>GraphWorkflowFailures.Classify</c>'s: docs/wiki/21-graph-workflows.md ("Restart").
    /// </remarks>
    private IReadOnlyList<GraphWorkflowNodeRunVerdict> ComposeVerdicts(IReadOnlyList<GraphWorkflowReconciledNodeRun> interrupted)
    {
        var verdicts = new List<GraphWorkflowNodeRunVerdict>(interrupted.Count);
        foreach (var nodeRun in interrupted)
        {
            var failed = nodeRun is { Status: GraphWorkflowNodeRunStatus.Running, Kind: GraphWorkflowNodeKind.Agent or GraphWorkflowNodeKind.Tool or GraphWorkflowNodeKind.LlmCall };
            _logger.LogDebug("Graph workflow node run {NodeRunId} ({NodeKey}, {Kind}) was left {Status} on run {RunId} and is judged {Verdict}.",
                nodeRun.NodeRunId,
                nodeRun.NodeKey,
                nodeRun.Kind,
                nodeRun.Status,
                nodeRun.RunId,
                failed ? "Failed(Interrupted)" : "Pending");
            verdicts.Add(new GraphWorkflowNodeRunVerdict
            {
                NodeRunId = nodeRun.NodeRunId,
                ObservedStatus = nodeRun.Status,
                ObservedAttempt = nodeRun.Attempt,
                Repairs = failed
                    ?
                    [
                        new TransitionGraphWorkflowNodeRunCommand
                        {
                            RunId = nodeRun.RunId,
                            NodeRunId = nodeRun.NodeRunId,
                            ExpectedVersion = GraphWorkflowVersions.Any,
                            TargetStatus = GraphWorkflowNodeRunStatus.Failed,
                            FailureClass = GraphWorkflowFailureClass.Interrupted,
                            TerminalReason = InterruptedReason
                        }
                    ]
                    : []
            });
        }

        return verdicts;
    }
}
