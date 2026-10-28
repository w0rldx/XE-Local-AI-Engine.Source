namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed partial class GraphWorkflowStore
{
    public async Task<IReadOnlyList<GraphWorkflowReconciledNodeRun>> ListInterruptedNodeRunsAsync(CancellationToken cancellationToken = default) =>
    [
        .. await InterruptedNodeRuns().AsNoTracking()
                                      .Select(entity => new GraphWorkflowReconciledNodeRun
                                      {
                                          NodeRunId = entity.Id,
                                          RunId = entity.RunId,
                                          NodeKey = entity.NodeKey,
                                          Kind = entity.Kind,
                                          Status = entity.Status,
                                          Attempt = entity.Attempt
                                      })
                                      .ToListAsync(cancellationToken)
    ];

    public async Task<IReadOnlyList<GraphWorkflowReconciledNodeRun>> ReconcileNonTerminalNodeRunsAsync(string sanitizedReason,
        IReadOnlyList<GraphWorkflowNodeRunVerdict> verdicts,
        GraphWorkflowUnjudgedNodeRunSettlement? unjudged = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sanitizedReason);
        ArgumentNullException.ThrowIfNull(verdicts);

        // Checked by hand rather than left to ToDictionary, which would throw naming neither the node run nor what a caller did wrong: two verdicts about one row
        // are two answers to the same question, and picking one is not this store's call.
        if (verdicts.GroupBy(static verdict => verdict.NodeRunId).FirstOrDefault(static group => group.Count() > 1) is { } duplicate)
        {
            throw new ArgumentException($"Node run '{duplicate.Key}' was judged more than once in one reconcile pass.", nameof(verdicts));
        }

        // Every pass reads the world afresh. A caller that reconciles more than once holds one scope, and the identity map from its earlier pass would hand back run
        // rows as they stood BEFORE whatever moved these node runs — so this would allocate watermarks that are already taken.
        _dbContext.ChangeTracker.Clear();

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var stranded = await InterruptedNodeRuns().ToListAsync(cancellationToken);
            if (stranded.Count == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return [];
            }

            var runIds = stranded.Select(static entity => entity.RunId).Distinct().ToList();
            var runs = await _dbContext.GraphWorkflowRuns.Where(entity => runIds.Contains(entity.Id))
                                       .ToDictionaryAsync(static entity => entity.Id, cancellationToken);
            var judged = verdicts.ToDictionary(static verdict => verdict.NodeRunId);
            var reconciled = new List<GraphWorkflowReconciledNodeRun>(stranded.Count);
            var repairs = new List<NodeRunRepair>();
            var startedBefore = new Dictionary<Guid, long?>();
            var invocationBefore = new Dictionary<Guid, Guid?>();
            foreach (var nodeRun in stranded)
            {
                if (!runs.TryGetValue(nodeRun.RunId, out var run))
                {
                    continue;
                }

                // A row the caller did not judge, or judged as something it no longer is, is left exactly where it is: collapsing it would strand it at Pending with
                // nobody left to decide what re-running it costs. The caller reads again next pass — or ends it with `unjudged`, decided off the row in front of us.
                var matched = judged.TryGetValue(nodeRun.Id, out var verdict)
                              && verdict.ObservedStatus == nodeRun.Status
                              && verdict.ObservedAttempt == nodeRun.Attempt;
                if (!matched && unjudged is null)
                {
                    continue;
                }

                // The status BEFORE the collapse is the informative one: it says whether the node run was merely
                // admitted or actually mid-execution. Where it lands is always Pending.
                reconciled.Add(new GraphWorkflowReconciledNodeRun
                {
                    NodeRunId = nodeRun.Id,
                    RunId = nodeRun.RunId,
                    NodeKey = nodeRun.NodeKey,
                    Kind = nodeRun.Kind,
                    Status = nodeRun.Status,
                    Attempt = nodeRun.Attempt
                });

                // Re-dispatchable means clean: a row sitting at Pending must not carry a terminal reason, or a reader
                // takes "the engine restarted" for this attempt's outcome. The reason is on the node.interrupted event.
                startedBefore[nodeRun.Id] = nodeRun.StartedAtUtc;
                invocationBefore[nodeRun.Id] = nodeRun.InvocationId;
                ResetToPending(nodeRun);
                _ = AddEvent(run, GraphWorkflowEventTypes.NodeInterrupted, nodeRun.NodeKey, ReasonDetail(sanitizedReason));
                run.Version++;
                repairs.AddRange(matched
                    ? verdict!.Repairs.Select(command => new NodeRunRepair(run, command))
                    : [new NodeRunRepair(run, SettleUnjudged(nodeRun, unjudged!))]);
            }

            foreach (var (run, command) in repairs)
            {
                EnsureVersion(run, command.ExpectedVersion);
                var outcome = await ApplyNodeRunTransitionAsync(run, command, cancellationToken);
                if (outcome.EventType is { } eventType)
                {
                    // Always, for a node-run move: only the run-level cancel settle records no event, and nothing
                    // reconciles one.
                    _ = AddEvent(run, eventType, outcome.NodeKey, outcome.DetailJson);
                }

                run.Version++;
            }

            // The reset clears the start and invocation for a re-dispatch; a row its repair settled instead keeps those of the attempt the restart cut short.
            foreach (var nodeRun in stranded.Where(row => IsTerminal(row.Status) && startedBefore.ContainsKey(row.Id)))
            {
                nodeRun.StartedAtUtc = startedBefore[nodeRun.Id];
                nodeRun.InvocationId ??= invocationBefore[nodeRun.Id];
            }

            _ = await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return reconciled;
        }
        catch
        {
            await RollbackAsync(transaction);
            throw;
        }
    }

    /// <summary>
    ///     A re-attempt, and a restart collapse, both start from a clean slate.
    /// </summary>
    /// <remarks>
    ///     The timestamps, or a reader sees the row running since its first try; the failure fields, or it reports the
    ///     previous attempt's outcome while it runs again. What that attempt failed with is already on its own event.
    /// </remarks>
    private static void ResetToPending(GraphWorkflowNodeRun nodeRun)
    {
        nodeRun.Status = GraphWorkflowNodeRunStatus.Pending;
        nodeRun.StartedAtUtc = null;
        nodeRun.CompletedAtUtc = null;
        nodeRun.FailureClass = GraphWorkflowFailureClass.None;
        nodeRun.Error = null;
        nodeRun.PendingDecisionKind = null;
        nodeRun.InvocationId = null;
    }

    /// <summary>Where a settling pass puts a node run it could not judge: failed with the caller's class, costing no attempt.</summary>
    private static TransitionGraphWorkflowNodeRunCommand SettleUnjudged(GraphWorkflowNodeRun nodeRun, GraphWorkflowUnjudgedNodeRunSettlement unjudged) =>
        new()
        {
            RunId = nodeRun.RunId,
            NodeRunId = nodeRun.Id,
            ExpectedVersion = GraphWorkflowVersions.Any,
            TargetStatus = GraphWorkflowNodeRunStatus.Failed,
            FailureClass = unjudged.FailureClass,
            TerminalReason = unjudged.SanitizedReason
        };

    /// <summary>
    ///     The node runs a host death stranded, shared by the read and the write so the set the caller judged cannot
    ///     differ from the set the collapse takes.
    /// </summary>
    /// <remarks>
    ///     Only <c>Queued</c> and <c>Running</c> lost an executor: <c>Pending</c> was never dispatched, and
    ///     <c>WaitingForApproval</c> is a durable human wait a restart does not invalidate.
    /// </remarks>
    private IOrderedQueryable<GraphWorkflowNodeRun> InterruptedNodeRuns() =>
        _dbContext.GraphWorkflowNodeRuns.Where(entity => InterruptedStatuses.Contains(entity.Status))
                  .OrderBy(entity => entity.RunId)
                  .ThenBy(entity => entity.NodeKey);

    /// <summary>One repair a reconciliation queues: the run it belongs to and the node-run transition to apply.</summary>
    private readonly record struct NodeRunRepair(GraphWorkflowRun Run, TransitionGraphWorkflowNodeRunCommand Command);
}
