namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>What a lane came back with, in the four terms the retry decision is made on.</summary>
internal sealed class DevWorkflowFailure
{
    /// <summary>The closed failure-class token that says why.</summary>
    public required string FailureClass { get; init; }

    /// <summary>What an operator is shown. Already sanitized by whoever produced it.</summary>
    public required string SanitizedReason { get; init; }

    /// <summary>The node's output document, which a routed retry hands to the node it re-runs.</summary>
    public required string OutputJson { get; init; }

    /// <summary>The event outcome, for the two cases the status alone cannot express.</summary>
    public string? Outcome { get; init; }
}

/// <summary>
///     Where a failed node run's next move is decided: re-attempt it, re-run the upstream node that produced what it
///     was judging, or stand it down for a human.
/// </summary>
/// <remarks>
///     One class rather than a branch in each executor, because the agent lane and the sandbox lane must answer this
///     identically, and because the cross-node fix loop reaches rows neither lane owns. Every write goes through the
///     store inside the dispatcher's serialized tick, exactly as the executors' own settles do. It holds one piece of
///     non-authoritative state: when a re-attempt may be admitted, for the nodes that ask for a delay. See
///     docs/wiki/25-dev-workflows.md ("The retry policy").
/// </remarks>
internal sealed class DevWorkflowRetryPolicy
{
    /// <summary>camelCase, matching every other document this product puts on a wire.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     The failure classes another attempt can answer. The three that are absent are absent on evidence: a
    ///     <c>Configuration</c> or <c>Policy</c> refusal produces the byte-identical answer next time, and
    ///     <c>BudgetExhausted</c> is already the answer to having tried.
    /// </summary>
    private static readonly HashSet<string> RetryableFailureClasses = new(StringComparer.Ordinal)
    {
        DevWorkflowFailureClasses.ProviderError,
        DevWorkflowFailureClasses.Timeout,
        DevWorkflowFailureClasses.Interrupted,
        DevWorkflowFailureClasses.ToolCommandFailed,
        DevWorkflowFailureClasses.Internal
    };

    /// <summary>
    ///     When a re-attempt may be admitted, for the node runs whose node asks for a delay. Keyed by node run, each
    ///     entry naming its run so a run that ends mid-delay can be forgotten in one call.
    /// </summary>
    /// <remarks>
    ///     ponytail: in memory, so a restart re-admits immediately, which is the answer rather than a gap in it. A
    ///     delay is a CUSHION, never a bound — the bounds are <c>Attempt</c> on the row and the run's total, both
    ///     durable — so early re-admission can only shorten a wait, in the one situation that already cost more
    ///     wall-clock than any delay a definition asks for. <c>node.retry.scheduled</c> carries <c>delayUntil</c>, so
    ///     the log says what was promised; the upgrade path is to re-read that event at startup, not to add a column.
    /// </remarks>
    private readonly ConcurrentDictionary<Guid, ScheduledRetry> _notBefore = new();

    private readonly ILogger<DevWorkflowRetryPolicy> _logger;

    private readonly DevWorkflowOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;

    public DevWorkflowRetryPolicy(IServiceScopeFactory scopeFactory,
        IOptions<DevWorkflowOptions> options,
        TimeProvider timeProvider,
        ILogger<DevWorkflowRetryPolicy> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options.Value;
    }

    /// <summary>How many re-attempts are waiting on a clock. Instrumentation: the only way to assert nothing accumulates.</summary>
    internal int ScheduledRetryCount => _notBefore.Count;

    /// <summary>
    ///     Whether a re-attempt's delay has passed. Answers <see langword="true" /> for every node run that never asked
    ///     for one, which is almost all of them, and forgets the entry once it has been honoured.
    /// </summary>
    public bool IsReady(Guid nodeRunId)
    {
        if (!_notBefore.TryGetValue(nodeRunId, out var scheduled))
        {
            return true;
        }

        if (_timeProvider.GetUtcNow() < scheduled.NotBefore)
        {
            return false;
        }

        _ = _notBefore.TryRemove(nodeRunId, out _);
        return true;
    }

    /// <summary>Drops what this run had promised itself, because it will never ask again.</summary>
    /// <remarks>
    ///     Called wherever the dispatcher forgets a run's parsed graph, at the same moments and for the same reason: a
    ///     run cancelled or failed while a node run waits out a delay never reaches <see cref="IsReady" /> again, so
    ///     the entry would sit here until the process restarted. A PAUSED run is deliberately not forgotten — it is
    ///     coming back and its cushions stand. Deleting a work item needs nothing of its own: the store refuses a
    ///     delete while any run of the item is non-terminal, so a deletable run has already been through here.
    /// </remarks>
    public void Forget(Guid runId)
    {
        foreach (var (nodeRunId, scheduled) in _notBefore)
        {
            if (scheduled.RunId == runId)
            {
                _ = _notBefore.TryRemove(nodeRunId, out _);
            }
        }
    }

    /// <summary>
    ///     Settles a node run whose work failed: another attempt at it, another attempt at the node it was judging, or
    ///     a stand-down for a human.
    /// </summary>
    /// <remarks>
    ///     A run that is CANCELLING is exempt: it has been told to stop, and re-attempting anything under it would be
    ///     the runtime resurrecting work an operator asked it to abandon, so such a failure settles <c>Failed</c> and
    ///     the drain takes it from there. A run that is PAUSING is not exempt — a re-attempt lands the row at
    ///     <c>Pending</c>, which is exactly where the pause drain parks work anyway.
    /// </remarks>
    public async Task<int> SettleFailureAsync(IDevWorkflowStore store,
        DevWorkflowGraph graph,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        DevWorkflowFailure failure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(nodeRun);
        ArgumentNullException.ThrowIfNull(nodeRuns);
        ArgumentNullException.ThrowIfNull(failure);

        if (run.Status == DevWorkflowRunStatus.Cancelling)
        {
            return await FailAsync(store, run, nodeRun, nodeRuns, failure, cancellationToken);
        }

        if (!graph.Nodes.TryGetValue(nodeRun.NodeKey, out var node))
        {
            // The run's pinned graph no longer declares it, so there is no attempt cap and no retry target to read.
            return await BlockAsync(store,
                    run,
                    nodeRun,
                    DevWorkflowFailureClasses.Configuration,
                    $"The run's graph no longer declares node '{nodeRun.NodeKey}', so this failure cannot be retried.",
                    failure.OutputJson,
                    cancellationToken);
        }

        if (!IsRetryable(node, failure.FailureClass, nodeRun.Attempt))
        {
            // No attempt is spent: nothing was tried again, and a row reading attempt 2 would say one was.
            return await BlockAsync(store, run, nodeRun, failure.FailureClass, failure.SanitizedReason, failure.OutputJson, cancellationToken);
        }

        return node.RetryTarget is { } retryTarget
            ? await RouteAsync(store, graph, run, node, retryTarget, nodeRun, nodeRuns, failure, cancellationToken)
            : await ReAttemptSameNodeAsync(store, run, node, nodeRun, nodeRuns, failure, cancellationToken);
    }

    /// <summary>
    ///     <c>Internal</c> is retryable exactly once: an executor that threw something nobody predicted may have hit a
    ///     transient, but a second identical throw is a defect.
    /// </summary>
    /// <remarks>
    ///     Spending a node's whole attempt budget on a defect only delays the human who has to read the log. A
    ///     DECOMPOSING node's <c>Configuration</c> failure is retryable once for the same reason, and is the one named
    ///     exception to <c>Configuration</c> being non-retryable: what wrote the unusable task package can rewrite it,
    ///     and the re-attempt carries the complaint into its objective. Scoped to the node rather than the failure
    ///     because the class is all a lane hands over; the cost is one spent attempt, and the answer after it a human.
    /// </remarks>
    private static bool IsRetryable(DevWorkflowGraphNode node, string failureClass, int attempt) =>
        (RetryableFailureClasses.Contains(failureClass) && (failureClass != DevWorkflowFailureClasses.Internal || attempt < 2))
        || (failureClass == DevWorkflowFailureClasses.Configuration && node.Materialization is not null && attempt < 2);

    /// <summary>The same node again — a same-node retry, bounded by the node's cap and the run's budget.</summary>
    private async Task<int> ReAttemptSameNodeAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        DevWorkflowGraphNode node,
        DevWorkflowNodeRunSnapshot nodeRun,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        DevWorkflowFailure failure,
        CancellationToken cancellationToken)
    {
        if (nodeRun.Attempt >= nodeRun.MaxAttempts)
        {
            return await BlockAsync(store,
                    run,
                    nodeRun,
                    failure.FailureClass,
                    $"{failure.SanitizedReason} It has now failed {nodeRun.Attempt} times, which is as many attempts as this node allows.",
                    failure.OutputJson,
                    cancellationToken);
        }

        if (await PromisedAsync(store, run.Id, nodeRuns, cancellationToken) + 1 > _options.MaxTotalAttempts)
        {
            return await BlockAsync(store, run, nodeRun, DevWorkflowFailureClasses.BudgetExhausted, BudgetExhausted(failure), failure.OutputJson, cancellationToken);
        }

        // The next attempt is told what the last one came to, read off the failure in hand rather than the row the Pending write is about to clear; the helper strips any earlier
        // priorFailure so rounds replace rather than nest. NO priorFailureNode here — see the remarks on PriorFailure for why this node's own key there reads as a cross-node rejection.
        try
        {
            return await ReAttemptAsync(store,
                    run,
                    nodeRun,
                    node.RetryDelaySeconds,
                    DetailFor(nodeRun, failure),
                    failure.Outcome ?? DevWorkflowOutcomes.Failed,
                    cancellationToken,
                    PriorFailure(nodeRun.InputJson, fromNodeKey: null, fromAttempt: null, failure.OutputJson));
        }
        catch (DevWorkflowRetryBudgetExceededException refused)
        {
            // The budget check the store takes under its writer lock refused this attempt: a human Retry committed between the PromisedAsync pre-check above and this write, spending
            // the slot it was counting on. The pre-check stays as the cheap fast path; THIS is the authority, and its answer is the same block the pre-check would have written.
            _logger.LogInformation(refused,
                "Development workflow run {RunId} could not re-attempt '{NodeKey}': the run's re-attempt budget was spent under the write.",
                run.Id,
                nodeRun.NodeKey);
            return await BlockAsync(store, run, nodeRun, DevWorkflowFailureClasses.BudgetExhausted, BudgetExhausted(failure), failure.OutputJson, cancellationToken);
        }
    }

    /// <summary>
    ///     The cross-node fix loop: the named upstream target re-runs with this failure in its inputs, and every node
    ///     run downstream of it re-runs with it — the one that failed included, being a descendant by the parse rule.
    /// </summary>
    /// <remarks>
    ///     The reset set is ALL descendants rather than the path back to the failure, because a <c>Succeeded</c>
    ///     sibling holds an answer about an implementation that no longer exists. Leaving it would be a stale result
    ///     presented as a current one, and a re-run that was not needed is the cheaper mistake.
    /// </remarks>
    private async Task<int> RouteAsync(IDevWorkflowStore store,
        DevWorkflowGraph graph,
        DevWorkflowRunSnapshot run,
        DevWorkflowGraphNode node,
        string retryTarget,
        DevWorkflowNodeRunSnapshot nodeRun,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        DevWorkflowFailure failure,
        CancellationToken cancellationToken)
    {
        var byKey = nodeRuns.ToDictionary(static row => row.NodeKey, StringComparer.Ordinal);

        // The caller's row, not the tick's opening copy: a lane may have caught its row up to Running since, and the reset has to judge the status it is actually moving from.
        byKey[nodeRun.NodeKey] = nodeRun;
        if (!byKey.TryGetValue(retryTarget, out var target))
        {
            // Declared and validated as an ancestor at parse, so the row exists in every run this build materializes. Reaching here means graph and rows disagree — nothing to guess at.
            return await BlockAsync(store,
                    run,
                    nodeRun,
                    DevWorkflowFailureClasses.Configuration,
                    $"This node routes its failures to '{retryTarget}', which this run has no node run for.",
                    failure.OutputJson,
                    cancellationToken);
        }

        var reset = graph.Descendants(retryTarget)
                         .Select(byKey.GetValueOrDefault)
                         .OfType<DevWorkflowNodeRunSnapshot>()

                         // A row that has not started needs no reset: it will judge the new round when it is admitted,
                         // and an attempt recorded on it would be one the run never made.
                         .Where(static row => row.Status != DevWorkflowNodeRunStatus.Pending)
                         .OrderBy(static row => row.NodeKey, StringComparer.Ordinal)
                         .ToList();

        if (target.Attempt >= target.MaxAttempts)
        {
            return await BlockAsync(store,
                    run,
                    nodeRun,
                    DevWorkflowFailureClasses.BudgetExhausted,
                    $"{failure.SanitizedReason} Node '{retryTarget}' has already been attempted {target.Attempt} times, which is as many as it allows.",
                    failure.OutputJson,
                    cancellationToken);
        }

        // GRAPH-C4-4: this node's own fix loop, bounded by what the definition said; absent means no cap (ruling D9), a parse-time default silently tightening every stored definition.
        // Attempt is the right base and needs no column, an operator Retry is subtracted from it, and two nodes routing to one target over-attributes — which errs toward blocking.
        if (node.MaxLoopIterations is { } maxLoopIterations)
        {
            var decisions = await store.ListDecisionsAsync(run.Id, cancellationToken);
            var loops = nodeRun.Attempt - 1 - decisions.Count(decision => decision.NodeRunId == nodeRun.Id && decision.Decision == DevWorkflowDecisionKind.Retry);
            if (loops >= maxLoopIterations)
            {
                return await BlockAsync(store,
                        run,
                        nodeRun,
                        DevWorkflowFailureClasses.BudgetExhausted,
                        $"{failure.SanitizedReason} This node's fix loop has been re-run {loops} {(loops == 1 ? "time" : "times")}, which is as many as it allows "
                        + "(invariant GRAPH-C4-4).",
                        failure.OutputJson,
                        cancellationToken);
            }
        }

        // The WHOLE cascade has to fit, not just the target's own attempt: admitting a fan-out one attempt at a time is how a run spends more re-attempts than it allows by the width of
        // its graph. The same accounting the startup reconciler does, for the same reason.
        var cost = reset.Count + 1;
        if (await PromisedAsync(store, run.Id, nodeRuns, cancellationToken) + cost > _options.MaxTotalAttempts)
        {
            return await BlockAsync(store, run, nodeRun, DevWorkflowFailureClasses.BudgetExhausted, BudgetExhausted(failure), failure.OutputJson, cancellationToken);
        }

        // Composed before anything is touched, so an illegal move is refused while the run still stands where it did. The target is built LAST so the event log reads decision, then the
        // answers being discarded, then the node being re-run — the order a person reconstructs the round in.
        var moves = new List<(TransitionDevWorkflowNodeRunCommand Command, Guid NodeRunId, DateTimeOffset? DelayUntil)>(reset.Count + 1);
        foreach (var row in reset)
        {
            // Only the node that failed ended failed. The rest are re-run because the answer they gave is about to describe something gone, and stamping "failed" would say they broke.
            var (command, delayUntil) = row.Id == nodeRun.Id
                ? ReAttempt(run, row, delaySeconds: 0, DetailFor(row, failure), failure.Outcome ?? DevWorkflowOutcomes.Failed, inputJson: null)
                : ReAttempt(run,
                    row,
                    delaySeconds: 0,
                    new RetryDetail
                    {
                        Attempt = row.Attempt,
                        FailureClass = failure.FailureClass,
                        Reason = $"Node '{retryTarget}' is being re-attempted because '{nodeRun.NodeKey}' failed, so this node run's result no longer describes it.",
                        DelayUntil = null
                    },
                    outcome: null,
                    inputJson: null);
            moves.Add((command, row.Id, delayUntil));
        }

        var (targetCommand, targetDelay) = ReAttempt(run,
            target,
            node.RetryDelaySeconds,
            new RetryDetail { Attempt = target.Attempt, FailureClass = failure.FailureClass, Reason = $"Re-attempted because '{nodeRun.NodeKey}' failed.", DelayUntil = null },
            outcome: null,
            PriorFailure(target.InputJson, nodeRun.NodeKey, nodeRun.Attempt, failure.OutputJson));
        moves.Add((targetCommand, target.Id, targetDelay));

        // Quiesce EVERY superseded row before the transaction opens: a rollback cannot undo a stopped session, and a lane still driving one would settle it back off the discarded
        // answer — an Any join can leave the target itself live. Asked AGAIN here, because the first check ran before the moves were composed and the refusal below lands too late.
        if (await PromisedAsync(store, run.Id, nodeRuns, cancellationToken) + cost > _options.MaxTotalAttempts)
        {
            return await BlockAsync(store, run, nodeRun, DevWorkflowFailureClasses.BudgetExhausted, BudgetExhausted(failure), failure.OutputJson, cancellationToken);
        }

        foreach (var row in reset.Where(row => row.Id != nodeRun.Id))
        {
            await QuiesceAsync(row, cancellationToken);
        }

        await QuiesceAsync(target, cancellationToken);

        // ONE transaction for the routing event and every reset under it. A row at a time leaves a crash window where the failed check is Pending again while the gate approval beside it
        // still reads Succeeded, which startup recovery never judges. All or nothing leaves the failure recorded instead, and the next sweep re-derives and re-routes it.
        var route = new RouteDevWorkflowRetryCommand
        {
            Route = new AppendDevWorkflowEventCommand
        {
            RunId = run.Id,
            ExpectedVersion = DevWorkflowVersions.Any,
            EventType = DevWorkflowEventTypes.NodeRetryRouted,
            NodeRunId = nodeRun.Id,
            OperationId = DevWorkflowOperationId.For(run.Id, nodeRun.NodeKey, nodeRun.Attempt, "retry-routed"),
            Outcome = failure.Outcome ?? DevWorkflowOutcomes.Failed,
            DetailJson = JsonSerializer.Serialize(new RoutedDetail { From = nodeRun.NodeKey, To = retryTarget, FailureClass = failure.FailureClass, Reason = failure.SanitizedReason }, JsonOptions)
        },
            Resets = [.. moves.Select(static move => move.Command)],
            MaxTotalAttempts = _options.MaxTotalAttempts
        };
        try
        {
            await RouteOnceMoreOnAClashAsync(store, run, nodeRun, retryTarget, route, cancellationToken);
        }
        catch (DevWorkflowRetryBudgetExceededException refused)
        {
            // A budget refusal past BOTH pre-checks: a human Retry committed inside the transaction's own window. Warning, not Information, because the lanes above are already stopped
            // and this answer does not reset them — unlike a concurrency clash, a refusal has no next route to redo them, so the rows named here are the ones a human has to look at.
            _logger.LogWarning(refused,
                "Development workflow run {RunId} could not route '{NodeKey}' back to '{RetryTarget}': the run's re-attempt budget was spent inside the write, "
                + "after node run(s) {QuiescedNodeKeys} had already been asked to stop for it. They are left as their own lanes settle them.",
                run.Id,
                nodeRun.NodeKey,
                retryTarget,
                string.Join(", ", reset.Where(row => row.Id != nodeRun.Id).Select(static row => row.NodeKey).Append(target.NodeKey)));
            return await BlockAsync(store, run, nodeRun, DevWorkflowFailureClasses.BudgetExhausted, BudgetExhausted(failure), failure.OutputJson, cancellationToken);
        }

        // After the commit: a cushion for a re-attempt that did not commit would hold back a row nothing reset.
        foreach (var (_, nodeRunId, delayUntil) in moves)
        {
            Cushion(run.Id, nodeRunId, delayUntil);
        }

        return moves.Count + 1;
    }

    /// <summary>Writes the route, and on a lost race asks EXACTLY once more before giving up on this tick.</summary>
    /// <remarks>
    ///     The lanes this route supersedes are already stopped by the time the write is attempted and a clash rolls
    ///     that write back whole, so leaving it to the next sweep would leave cancelled attempts with nothing reset —
    ///     and the DevTask lane reads a cancelled attempt as a cancellation rather than a round to redo. The SAME
    ///     command is re-sent: every part carries <see cref="DevWorkflowVersions.Any" />, so the operation id makes a
    ///     lost answer a replay. Twice and no further; a third ask is a writer that is not going away.
    /// </remarks>
    private async Task RouteOnceMoreOnAClashAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        string retryTarget,
        RouteDevWorkflowRetryCommand route,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await store.RouteRetryAsync(route, cancellationToken);
            return;
        }
        catch (DevWorkflowConcurrencyException clash)
        {
            _logger.LogDebug(clash,
                "Development workflow run {RunId} lost a race routing '{NodeKey}' back to '{RetryTarget}', so the route is being asked once more.",
                run.Id,
                nodeRun.NodeKey,
                retryTarget);
        }

        try
        {
            _ = await store.RouteRetryAsync(route, cancellationToken);
        }
        catch (DevWorkflowConcurrencyException persistent)
        {
            _logger.LogWarning(persistent,
                "Development workflow run {RunId} lost the race routing '{NodeKey}' back to '{RetryTarget}' twice, so the lanes it stopped are left for the next sweep to re-derive and route again.",
                run.Id,
                nodeRun.NodeKey,
                retryTarget);
            throw;
        }
    }

    /// <summary>
    ///     Stops the lane work a node run is about to lose, before the transaction that takes away the only row that
    ///     could ever have settled it.
    /// </summary>
    /// <remarks>
    ///     Without this a fix loop orphans live work: an agent re-attempt clears its <c>WorkSessionId</c>, so the
    ///     session keeps the node's one invocation slot with nothing pointing at it and the fresh attempt queues
    ///     behind what it supersedes; a dev-task row holds Dev Mode's one-active-attempt rule the same way. Sessions
    ///     are ASKED to stop, never deleted, because a superseded session RAN and is audit evidence; the tool lane
    ///     discards, since a stale registry entry would refuse the next attempt. Its own scope: the lanes are scoped.
    /// </remarks>
    private async Task QuiesceAsync(DevWorkflowNodeRunSnapshot nodeRun, CancellationToken cancellationToken)
    {
        if (nodeRun.Status is not (DevWorkflowNodeRunStatus.Running or DevWorkflowNodeRunStatus.Queued))
        {
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        switch (nodeRun)
        {
            case { NodeType: DevWorkflowNodeType.Tool }:
                await scope.ServiceProvider.GetRequiredService<DevWorkflowToolExecutor>().DiscardAsync(nodeRun.Id);
                break;

            case { NodeType: DevWorkflowNodeType.DevTask }:
                _ = await scope.ServiceProvider.GetRequiredService<DevWorkflowDevTaskExecutor>()
                               .StopAttemptAsync(nodeRun, cancel: true, cancellationToken);
                break;

            case { NodeType: DevWorkflowNodeType.Agent, WorkSessionId: { } sessionId }:
                await scope.ServiceProvider.GetRequiredService<DevWorkflowAgentExecutor>().StopAsync(sessionId, cancel: true, cancellationToken);
                break;

            default:
                // An inline node holds nothing: it is Running only for the width of the tick that admitted it, and a
                // human gate's wait is a durable row rather than work.
                break;
        }
    }

    /// <summary>Moves one node run back to <c>Pending</c> for another attempt.</summary>
    /// <remarks>
    ///     <c>ClearWorkSession</c> travels with EVERY re-attempt, agent node or not: a retry that resumed the session
    ///     that just failed would resume the context that failed with it, and the release also stops the fresh attempt
    ///     being settled straight back off the old session's answer. The failure fields are cleared by the store, so
    ///     the event this writes is the only record of what is being re-attempted — which is why it carries its own
    ///     detail rather than the reason.
    /// </remarks>
    private async Task<int> ReAttemptAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        int delaySeconds,
        RetryDetail detail,
        string? outcome,
        CancellationToken cancellationToken,
        string? inputJson = null)
    {
        var (command, delayUntil) = ReAttempt(run, nodeRun, delaySeconds, detail, outcome, inputJson);
        _ = await store.TransitionNodeRunAsync(command, cancellationToken);
        Cushion(run.Id, nodeRun.Id, delayUntil);
        return 1;
    }

    /// <summary>
    ///     The one re-attempt move, composed but not written: the fix loop needs every move it makes in hand before it
    ///     writes any of them, because they go to the store as one transaction.
    /// </summary>
    private (TransitionDevWorkflowNodeRunCommand Command, DateTimeOffset? DelayUntil) ReAttempt(DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        int delaySeconds,
        RetryDetail detail,
        string? outcome,
        string? inputJson)
    {
        var delayUntil = delaySeconds > 0 ? _timeProvider.GetUtcNow().AddSeconds(delaySeconds) : (DateTimeOffset?)null;
        DevWorkflowStateMachine.EnsureLegal(nodeRun.Status, DevWorkflowNodeRunStatus.Pending, nodeRun.NodeKey);
        return (new TransitionDevWorkflowNodeRunCommand
        {
            RunId = run.Id,
            NodeRunId = nodeRun.Id,
            ExpectedVersion = DevWorkflowVersions.Any,
            TargetStatus = DevWorkflowNodeRunStatus.Pending,
            InputJson = inputJson,
            DetailJson = JsonSerializer.Serialize(detail with
            {
                DelayUntil = delayUntil?.ToUnixTimeMilliseconds()
            }, JsonOptions),
            IncrementAttempt = true,
            ClearWorkSession = true,
            Outcome = outcome,
            // The run-wide budget travels WITH the write, so the store re-checks it under the writer lock instead of trusting the caller's earlier read (FU3-4). Inert on a
            // reset inside a route: those go through the route's own transaction, which admits the whole cascade once rather than each reset separately.
            MaxTotalAttempts = _options.MaxTotalAttempts
        }, delayUntil);
    }

    /// <summary>
    ///     Records or clears when a re-attempt may be admitted.
    /// </summary>
    /// <remarks>
    ///     Written for EVERY re-attempt, the ones asking for no delay included: a fix-loop reset of a row already
    ///     waiting on a clock must not inherit the previous attempt's, and the removal is also what keeps the map from
    ///     accumulating an entry per delayed retry for the life of the process.
    /// </remarks>
    private void Cushion(Guid runId, Guid nodeRunId, DateTimeOffset? delayUntil)
    {
        if (delayUntil is { } notBefore)
        {
            _notBefore[nodeRunId] = new ScheduledRetry { RunId = runId, NotBefore = notBefore };
        }
        else
        {
            _ = _notBefore.TryRemove(nodeRunId, out _);
        }
    }

    /// <summary>Stands the node run down for a human, blocking its work item in the same transaction.</summary>
    private static async Task<int> BlockAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        string failureClass,
        string sanitizedReason,
        string outputJson,
        CancellationToken cancellationToken)
    {
        DevWorkflowStateMachine.EnsureLegal(nodeRun.Status, DevWorkflowNodeRunStatus.Blocked, nodeRun.NodeKey);
        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand
        {
            RunId = run.Id,
            NodeRunId = nodeRun.Id,
            ExpectedVersion = DevWorkflowVersions.Any,
            TargetStatus = DevWorkflowNodeRunStatus.Blocked,
            PendingDecisionKind = DevWorkflowDecisionKind.Abandon,
            OutputJson = outputJson,
            FailureClass = failureClass,
            TerminalReason = sanitizedReason,
            WorkItemStatus = DevWorkflowWorkItemStatus.Blocked
        },
                           cancellationToken);
        return 1;
    }

    /// <summary>The pre-B3 settle, kept for the one run status that must not re-attempt: one being cancelled.</summary>
    private static async Task<int> FailAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        DevWorkflowFailure failure,
        CancellationToken cancellationToken)
    {
        DevWorkflowStateMachine.EnsureLegal(nodeRun.Status, DevWorkflowNodeRunStatus.Failed, nodeRun.NodeKey);
        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand
        {
            RunId = run.Id,
            NodeRunId = nodeRun.Id,
            ExpectedVersion = DevWorkflowVersions.Any,
            TargetStatus = DevWorkflowNodeRunStatus.Failed,
            OutputJson = failure.OutputJson,
            FailureClass = failure.FailureClass,
            TerminalReason = failure.SanitizedReason,
            Outcome = failure.Outcome,
            WorkItemStatus = DevWorkflowStateMachine.WorkItemStatusAfter(run.Status, nodeRuns, nodeRun.Id, DevWorkflowNodeRunStatus.Failed)
        },
                           cancellationToken);
        return 1;
    }

    /// <summary>
    ///     The re-attempts this run has already made or promised — <c>Σ(Attempt − 1)</c> plus the recorded operator
    ///     retries that have not become an attempt yet.
    /// </summary>
    /// <remarks>
    ///     Deliberately the same count the store admits a human <c>Retry</c> against, because it is the same budget:
    ///     an automatic re-attempt and an operator's are both re-attempts of this run, and <c>MaxTotalAttempts</c> is
    ///     the one bound over both. Counting the reservations is what stops an automatic retry from spending an
    ///     attempt a person has already been promised in the same tick window.
    /// </remarks>
    private static async Task<int> PromisedAsync(IDevWorkflowStore store,
        Guid runId,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        CancellationToken cancellationToken) =>
        Promised(nodeRuns, await store.ListDecisionsAsync(runId, cancellationToken));

    /// <summary>
    ///     The formula itself, shared with <see cref="DevWorkflowStartupReconciler" /> rather than restated there.
    /// </summary>
    /// <remarks>
    ///     Restating it lets restart recovery count only <c>Σ(Attempt − 1)</c> and hand an interrupted row the slot a
    ///     recorded-but-unapplied <c>Retry</c> had reserved (FU3-4). One definition, three callers: this policy, the
    ///     reconciler, and — in its own SQL — the store's transactional admission.
    /// </remarks>
    internal static int Promised(IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns, IReadOnlyList<DevWorkflowDecisionSnapshot> decisions) =>
        nodeRuns.Sum(static row => row.Attempt - 1)
        + decisions.Count(decision => decision.Decision == DevWorkflowDecisionKind.Retry
                                      && nodeRuns.Any(row => row.Id == decision.NodeRunId && row.Attempt == decision.Attempt));

    /// <summary>
    ///     The target's inputs with the failure that sent the run back to it, as flat members so the objective renders
    ///     them as the lines it renders every other input as.
    /// </summary>
    /// <param name="fromNodeKey">The node whose verdict routed the run back; null for a same-node retry.</param>
    /// <param name="fromAttempt">That node run's attempt, which produced the verdict and wrote the report behind it.</param>
    /// <remarks>
    ///     The DevTask lane reads <c>priorFailureNode</c> as "a downstream node rejected this", so a retry writing its
    ///     own key there reads as a rejection; a null leaves whatever an earlier genuine route wrote untouched and
    ///     adds none. Key and attempt together are the ROUTE's identity, which is what makes it answerable exactly
    ///     once: that lane keys its one change request on the route rather than on its own attempt, which a same-node
    ///     retry moves while the same rejection is outstanding, and reads a report only from the attempt that refused.
    /// </remarks>
    private static string PriorFailure(string? inputJson, string? fromNodeKey, int? fromAttempt, string outputJson)
    {
        if (fromNodeKey is null && CarriesRoutedFailure(inputJson))
        {
            // A transient retry landing in the MIDDLE of a genuine fix loop. Overwriting priorFailure would leave the routed node's name over this node's count-less output, so the rework
            // request that follows quotes a verdict it cannot evidence. Both members stay as the route wrote them; the merge still runs, an operator's reason belonging to one attempt.
            return DevWorkflowNodeInputs.Merge(inputJson, write: null);
        }

        return DevWorkflowNodeInputs.Merge(inputJson,
            writer =>
            {
                if (fromNodeKey is not null)
                {
                    writer.WriteString("priorFailureNode", fromNodeKey);
                    if (fromAttempt is { } attempt)
                    {
                        writer.WriteNumber("priorFailureAttempt", attempt);
                    }
                }

                writer.WritePropertyName("priorFailure");
                DevWorkflowNodeInputs.WriteJsonOrString(writer, outputJson);
            },
            fromNodeKey is null ? ["priorFailure"] : ["priorFailure", "priorFailureNode", "priorFailureAttempt"]);
    }

    /// <summary>Whether these inputs already name the node whose verdict routed the run back to them.</summary>
    private static bool CarriesRoutedFailure(string? inputJson)
    {
        using var existing = DevWorkflowNodeInputs.Parse(inputJson);
        return existing is not null && existing.RootElement.TryGetProperty("priorFailureNode", out _);
    }

    private static RetryDetail DetailFor(DevWorkflowNodeRunSnapshot nodeRun, DevWorkflowFailure failure) =>
        new() { Attempt = nodeRun.Attempt, FailureClass = failure.FailureClass, Reason = failure.SanitizedReason, DelayUntil = null };

    private static string BudgetExhausted(DevWorkflowFailure failure) =>
        $"{failure.SanitizedReason} This run has spent every re-attempt it allows, so nothing here can try again without a decision.";

    /// <summary>
    ///     What a <c>node.retry.scheduled</c> event carries.
    /// </summary>
    /// <remarks>
    ///     The attempt is the one that FAILED, which is what makes the per-attempt history readable off the log the
    ///     single-row node-run schema does not keep.
    /// </remarks>
    private sealed record RetryDetail
    {
        public required int Attempt { get; init; }

        public required string FailureClass { get; init; }

        public required string Reason { get; init; }

        public required long? DelayUntil { get; init; }
    }

    /// <summary>One promised re-attempt: when it may go, and the run whose ending makes the promise moot.</summary>
    private sealed record ScheduledRetry
    {
        public required Guid RunId { get; init; }

        public required DateTimeOffset NotBefore { get; init; }
    }

    private sealed record RoutedDetail
    {
        public required string From { get; init; }

        public required string To { get; init; }

        public required string FailureClass { get; init; }

        public required string Reason { get; init; }
    }
}
