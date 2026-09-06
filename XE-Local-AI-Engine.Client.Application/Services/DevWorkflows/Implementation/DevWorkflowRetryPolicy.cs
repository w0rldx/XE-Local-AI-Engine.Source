namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>What a lane came back with, in the four terms the retry decision is made on.</summary>
/// <param name="FailureClass">The closed §7.1 token that says why.</param>
/// <param name="SanitizedReason">What an operator is shown. Already sanitized by whoever produced it.</param>
/// <param name="OutputJson">The node's output document, which a routed retry hands to the node it re-runs.</param>
/// <param name="Outcome">The event outcome, for the two cases the status alone cannot express.</param>
internal sealed record DevWorkflowFailure(string FailureClass, string SanitizedReason, string OutputJson, string? Outcome = null);

/// <summary>
///     Where a failed node run's next move is decided: re-attempt it, re-run the upstream node that produced what it was
///     judging (X9), or stand it down for a human.
///     <para>
///         One class rather than a branch in each executor, because the agent lane and the sandbox lane must answer this
///         question identically — a build failing three times and an agent failing three times differ in what produced
///         the failure and in nothing else — and because the cross-node fix loop reaches rows neither lane owns.
///     </para>
///     <para>
///         Every write it makes goes through the store inside the dispatcher's serialized tick, exactly as the executors'
///         own settles do. It holds one piece of non-authoritative state: when a re-attempt may be admitted, for the
///         nodes that ask for a delay.
///     </para>
/// </summary>
internal sealed class DevWorkflowRetryPolicy
{
    /// <summary>camelCase, matching every other document this product puts on a wire.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     The §7.1 classes another attempt can answer. The three that are absent are absent on evidence: a
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
    ///     When a re-attempt may be admitted, for the node runs whose node asks for a delay. Keyed by node run, and each
    ///     entry naming its run so a run that ends mid-delay can be forgotten in one call.
    ///     <para>
    ///         ponytail: in memory, so a restart re-admits immediately, and that is the answer rather than a gap in it.
    ///         A delay is a CUSHION, never a bound — the bounds are <c>Attempt</c> on the row and the run's total, both
    ///         durable — so re-admitting a node run early can only shorten a wait, in the one situation (a restart) that
    ///         has already cost more wall-clock than any delay a definition would ask for. The durable record exists
    ///         either way: <c>node.retry.scheduled</c> carries <c>delayUntil</c>, so the log says what was promised even
    ///         though nothing re-arms it. Upgrade path, if a definition ever asks for a delay long enough to matter:
    ///         re-read that event for the node runs whose node declares a delay, at startup, rather than adding a column.
    ///     </para>
    /// </summary>
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

    /// <summary>
    ///     Drops what this run had promised itself, because it will never ask again.
    ///     <para>
    ///         Called wherever the dispatcher forgets a run's parsed graph, for the same reason and at the same moments:
    ///         a run that is cancelled or fails while one of its node runs is waiting out a delay never reaches
    ///         <see cref="IsReady" /> again, so the entry would otherwise sit here until the process restarted. A PAUSED
    ///         run is deliberately not forgotten — it is coming back, and its cushions still stand. Deleting a work item
    ///         needs nothing of its own: the store refuses a delete while any run of the item is non-terminal, so a
    ///         deletable run has already been through here.
    ///     </para>
    /// </summary>
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
    ///     Settles a node run whose work failed: another attempt at it, another attempt at the node it was judging, or a
    ///     stand-down for a human.
    ///     <para>
    ///         A run that is CANCELLING is exempt: it has been told to stop, and re-attempting anything under it would be
    ///         the runtime resurrecting work an operator asked it to abandon. Such a failure settles <c>Failed</c> and the
    ///         drain takes it from there. A run that is PAUSING is not exempt — a re-attempt lands the row at
    ///         <c>Pending</c>, which is exactly where the pause drain parks work anyway.
    ///     </para>
    /// </summary>
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
            return await FailAsync(store, run, nodeRun, nodeRuns, failure, cancellationToken).ConfigureAwait(false);
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
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!IsRetryable(node, failure.FailureClass, nodeRun.Attempt))
        {
            // No attempt is spent: nothing was tried again, and a row reading attempt 2 would say one was.
            return await BlockAsync(store, run, nodeRun, failure.FailureClass, failure.SanitizedReason, failure.OutputJson, cancellationToken)
                .ConfigureAwait(false);
        }

        return node.RetryTarget is { } retryTarget
            ? await RouteAsync(store, graph, run, node, retryTarget, nodeRun, nodeRuns, failure, cancellationToken).ConfigureAwait(false)
            : await ReAttemptSameNodeAsync(store, run, node, nodeRun, nodeRuns, failure, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <c>Internal</c> is retryable exactly once (§7.1): an executor that threw something nobody predicted may have
    ///     hit a transient, but a second identical throw is a defect, and spending a node's whole attempt budget on it
    ///     only delays the human who has to read the log.
    ///     <para>
    ///         A DECOMPOSING node's <c>Configuration</c> failure is retryable once for the same reason and by §7.1's own
    ///         named exception: the thing that wrote the unusable task package is the thing that can rewrite it, and the
    ///         re-attempt carries the complaint into its objective. Scoped to the node rather than to the failure
    ///         because the failure class is all a lane hands over — the cost of the wider reading is one spent attempt
    ///         on a decomposing node that is misconfigured in some other way, and the answer after it is the same human.
    ///     </para>
    /// </summary>
    private static bool IsRetryable(DevWorkflowGraphNode node, string failureClass, int attempt) =>
        (RetryableFailureClasses.Contains(failureClass) && (failureClass != DevWorkflowFailureClasses.Internal || attempt < 2))
        || (failureClass == DevWorkflowFailureClasses.Configuration && node.Materialization is not null && attempt < 2);

    /// <summary>The same node again: what §7.2 calls a same-node retry, bounded by the node's cap and the run's budget.</summary>
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
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (await PromisedAsync(store, run.Id, nodeRuns, cancellationToken).ConfigureAwait(false) + 1 > _options.MaxTotalAttempts)
        {
            return await BlockAsync(store, run, nodeRun, DevWorkflowFailureClasses.BudgetExhausted, BudgetExhausted(failure), failure.OutputJson, cancellationToken)
                .ConfigureAwait(false);
        }

        // The next attempt is told what the last one came to (§7.2), or the agent composes a byte-identical objective
        // and does the same thing again. Read off the failure in hand rather than the row, which the Pending write is
        // about to clear; the helper strips any earlier priorFailure, so rounds replace rather than nest.
        //
        // NO priorFailureNode: that key names the OTHER node whose verdict sent the run back here, and only RouteAsync
        // has one. Writing this node's own key into it made a same-node retry indistinguishable from a cross-node
        // rejection — measured live on 2026-09-02, a transient reviewer failure on a DevTask node then had the
        // executor ask an ALREADY-APPROVED task to be implemented again, quoting a verdict nothing had reached, until
        // the task's review rounds ran out and its approved patch was discarded. An earlier genuine route's key is left
        // in place: a transient retry in the middle of a fix loop must not lose the rework the loop asked for.
        try
        {
            return await ReAttemptAsync(store,
                    run,
                    nodeRun,
                    node.RetryDelaySeconds,
                    DetailFor(nodeRun, failure),
                    failure.Outcome ?? DevWorkflowOutcomes.Failed,
                    cancellationToken,
                    PriorFailure(nodeRun.InputJson, fromNodeKey: null, fromAttempt: null, failure.OutputJson))
                .ConfigureAwait(false);
        }
        catch (DevWorkflowRetryBudgetExceededException refused)
        {
            // The budget check the store takes under its writer lock refused this attempt: a human Retry committed
            // between the PromisedAsync pre-check above and this write, and it spent the slot this re-attempt was
            // counting on. The pre-check stays as the cheap fast path; THIS is the authority, and its answer is the
            // same block the pre-check would have written.
            _logger.LogInformation(refused,
                "Development workflow run {RunId} could not re-attempt '{NodeKey}': the run's re-attempt budget was spent under the write.",
                run.Id,
                nodeRun.NodeKey);
            return await BlockAsync(store, run, nodeRun, DevWorkflowFailureClasses.BudgetExhausted, BudgetExhausted(failure), failure.OutputJson, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     The cross-node fix loop (X9): the node that failed is not the node that is re-run. The named upstream target
    ///     re-runs with this failure in its inputs, and every node run downstream of it re-runs with it — including the
    ///     one that failed, which is a descendant by the ancestry rule the graph validates at parse.
    ///     <para>
    ///         The reset set is ALL descendants rather than the path back to the failure, because a <c>Succeeded</c>
    ///         sibling holds an answer about an implementation that no longer exists. Leaving it would be a stale result
    ///         presented as a current one, and a re-run that was not needed is the cheaper mistake.
    ///     </para>
    /// </summary>
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

        // The caller's row, not the tick's opening copy of it: a lane may have caught its row up to Running since, and
        // the reset has to judge the status it is actually moving from.
        byKey[nodeRun.NodeKey] = nodeRun;
        if (!byKey.TryGetValue(retryTarget, out var target))
        {
            // Declared and validated as an ancestor at parse, so the row exists in every run this build materializes.
            // Reaching here means the graph and the rows disagree, which nothing downstream should guess about.
            return await BlockAsync(store,
                    run,
                    nodeRun,
                    DevWorkflowFailureClasses.Configuration,
                    $"This node routes its failures to '{retryTarget}', which this run has no node run for.",
                    failure.OutputJson,
                    cancellationToken)
                .ConfigureAwait(false);
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
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // GRAPH-C4-4: this node's own fix loop, bounded by what the definition said. Absent means no cap (ruling D9) —
        // a parse-time default would tighten routing on every already-stored definition at run start, silently.
        //
        // Attempt is the right base and needs no column of its own: a node with a retryTarget never takes the
        // same-node path, and each route re-attempts the whole descendant set including this node. An operator Retry
        // raises the same counter and is bounded only by the run-wide budget, so it is subtracted. The count still
        // over-attributes when two nodes route to one target and the reset bumps both rows — which errs toward
        // blocking, the direction every budget here errs, and is why the message does not claim this node looped N
        // times.
        if (node.MaxLoopIterations is { } maxLoopIterations)
        {
            var decisions = await store.ListDecisionsAsync(run.Id, cancellationToken).ConfigureAwait(false);
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
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        // The WHOLE cascade has to fit, not just the target's own attempt. Admitting a fan-out one attempt at a time is
        // how a run spends more re-attempts than it allows by the width of its graph — the same accounting the startup
        // reconciler does for the same reason.
        var cost = reset.Count + 1;
        if (await PromisedAsync(store, run.Id, nodeRuns, cancellationToken).ConfigureAwait(false) + cost > _options.MaxTotalAttempts)
        {
            return await BlockAsync(store, run, nodeRun, DevWorkflowFailureClasses.BudgetExhausted, BudgetExhausted(failure), failure.OutputJson, cancellationToken)
                .ConfigureAwait(false);
        }

        // Composed before anything is touched, so an illegal move is refused while the run still stands where it did.
        // The target is built LAST so the event log reads decision, then the answers being discarded, then the node
        // being re-run — the order a person reconstructs the round in.
        var moves = new List<(TransitionDevWorkflowNodeRunCommand Command, Guid NodeRunId, DateTimeOffset? DelayUntil)>(reset.Count + 1);
        foreach (var row in reset)
        {
            // Only the node that failed ended failed. The rest are being re-run because the answer they gave is about
            // to describe something that no longer exists, and stamping their event "failed" would say they broke.
            var (command, delayUntil) = row.Id == nodeRun.Id
                ? ReAttempt(run, row, delaySeconds: 0, DetailFor(row, failure), failure.Outcome ?? DevWorkflowOutcomes.Failed, inputJson: null)
                : ReAttempt(run,
                    row,
                    delaySeconds: 0,
                    new RetryDetail(row.Attempt,
                        failure.FailureClass,
                        $"Node '{retryTarget}' is being re-attempted because '{nodeRun.NodeKey}' failed, so this node run's result no longer describes it.",
                        DelayUntil: null),
                    outcome: null,
                    inputJson: null);
            moves.Add((command, row.Id, delayUntil));
        }

        var (targetCommand, targetDelay) = ReAttempt(run,
            target,
            node.RetryDelaySeconds,
            new RetryDetail(target.Attempt, failure.FailureClass, $"Re-attempted because '{nodeRun.NodeKey}' failed.", DelayUntil: null),
            outcome: null,
            PriorFailure(target.InputJson, nodeRun.NodeKey, nodeRun.Attempt, failure.OutputJson));
        moves.Add((targetCommand, target.Id, targetDelay));

        // Quiesce EVERY row the route supersedes before the transaction opens. Stopping a live session is not something
        // a rollback can undo, so it cannot sit inside the write — and a lane still driving a row the reset is about to
        // take would otherwise settle it back off the answer being discarded.
        //
        // The target is almost always Succeeded by the time anything downstream of it can fail, so its pass is almost
        // always a no-op — but an Any join lets a descendant run on a sibling branch while the target is still working,
        // and that target is as live as any other row the reset moves.
        // Asked AGAIN, immediately before anything is stopped. The first check ran before the moves were composed and
        // the rows were read; a human Retry committing since then makes this route unaffordable, and the transactional
        // refusal below arrives too late to give the quiesced lanes their work back. Narrowing the window to the
        // transaction itself is the whole of the fix — the store stays the authority.
        if (await PromisedAsync(store, run.Id, nodeRuns, cancellationToken).ConfigureAwait(false) + cost > _options.MaxTotalAttempts)
        {
            return await BlockAsync(store, run, nodeRun, DevWorkflowFailureClasses.BudgetExhausted, BudgetExhausted(failure), failure.OutputJson, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var row in reset.Where(row => row.Id != nodeRun.Id))
        {
            await QuiesceAsync(row, cancellationToken).ConfigureAwait(false);
        }

        await QuiesceAsync(target, cancellationToken).ConfigureAwait(false);

        // ONE transaction for the routing event and every reset under it. Committing them a row at a time left a crash
        // window in which the failed check was Pending again while the verification and gate approval beside it still
        // read Succeeded — and nothing reconciles that, because startup recovery only judges rows left Queued or
        // Running. The run would then repeat the check and complete on evidence and an approval about an implementation
        // that no longer existed. All or nothing means a crash leaves the failure still recorded, which the next sweep
        // re-derives and re-routes.
        var route = new RouteDevWorkflowRetryCommand(new AppendDevWorkflowEventCommand(run.Id,
                DevWorkflowVersions.Any,
                DevWorkflowEventTypes.NodeRetryRouted,
                nodeRun.Id,
                DevWorkflowOperationId.For(run.Id, nodeRun.NodeKey, nodeRun.Attempt, "retry-routed"),
                failure.Outcome ?? DevWorkflowOutcomes.Failed,
                JsonSerializer.Serialize(new RoutedDetail(nodeRun.NodeKey, retryTarget, failure.FailureClass, failure.SanitizedReason), JsonOptions)),
            [.. moves.Select(static move => move.Command)],
            _options.MaxTotalAttempts);
        try
        {
            await RouteOnceMoreOnAClashAsync(store, run, nodeRun, retryTarget, route, cancellationToken).ConfigureAwait(false);
        }
        catch (DevWorkflowRetryBudgetExceededException refused)
        {
            // A budget refusal that got past BOTH pre-checks: a human Retry committed inside the transaction's own
            // window. Warning, not Information, because the lanes above are already stopped and this answer does not
            // reset them — unlike a concurrency clash, a refusal has no next route to redo them, so the rows named
            // here are the ones a human has to look at.
            _logger.LogWarning(refused,
                "Development workflow run {RunId} could not route '{NodeKey}' back to '{RetryTarget}': the run's re-attempt budget was spent inside the write, "
                + "after node run(s) {QuiescedNodeKeys} had already been asked to stop for it. They are left as their own lanes settle them.",
                run.Id,
                nodeRun.NodeKey,
                retryTarget,
                string.Join(", ", reset.Where(row => row.Id != nodeRun.Id).Select(static row => row.NodeKey).Append(target.NodeKey)));
            return await BlockAsync(store, run, nodeRun, DevWorkflowFailureClasses.BudgetExhausted, BudgetExhausted(failure), failure.OutputJson, cancellationToken)
                .ConfigureAwait(false);
        }

        // After the commit: a cushion for a re-attempt that did not commit would hold back a row nothing reset.
        foreach (var (_, nodeRunId, delayUntil) in moves)
        {
            Cushion(run.Id, nodeRunId, delayUntil);
        }

        return moves.Count + 1;
    }

    /// <summary>
    ///     Writes the route, and on a lost race asks EXACTLY once more before giving up on this tick.
    ///     <para>
    ///         The immediate re-ask exists because the lanes this route supersedes are already stopped by the time the
    ///         write is attempted, and a clash rolls that write back whole. Leaving it to the next sweep would leave
    ///         cancelled attempts with nothing reset — and a cancelled DevTask attempt is read by that lane as a
    ///         cancellation rather than as a round to redo, so the fix loop would come back as a cancelled run.
    ///     </para>
    ///     <para>
    ///         The SAME command, deliberately, rather than one re-derived from re-read rows. Every part of it already
    ///         carries <see cref="DevWorkflowVersions.Any" />, so a re-read cannot change a single field; it could only
    ///         change which rows are in the reset set, and a row that moved into that set after the snapshot was taken
    ///         is the same race the first attempt runs anyway. Re-sending it unchanged also makes the operation id do
    ///         its job: if the first attempt did commit and only its answer was lost, this is a replay, not a second
    ///         route.
    ///     </para>
    ///     <para>
    ///         Twice and no further. A third ask is a writer that is not going away, and the honest answer is the one
    ///         that was there before: the failure is still recorded, so the next sweep re-derives it and routes again.
    ///     </para>
    /// </summary>
    private async Task RouteOnceMoreOnAClashAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        string retryTarget,
        RouteDevWorkflowRetryCommand route,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await store.RouteRetryAsync(route, cancellationToken).ConfigureAwait(false);
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
            _ = await store.RouteRetryAsync(route, cancellationToken).ConfigureAwait(false);
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
    ///     <para>
    ///         Without this a fix loop orphans live work rather than replacing it. An agent row's re-attempt clears its
    ///         <c>WorkSessionId</c>, so the session it was driving keeps the node's one invocation slot with nothing left
    ///         pointing at it — and the fresh attempt then queues behind the very session it supersedes. A dev-task row
    ///         leaves an attempt holding Dev Mode's one-active-attempt rule the same way. The sandbox lane recovers on
    ///         its own at the next tick's <c>ForgetSupersededAsync</c>, so dropping its pass here is promptness rather
    ///         than repair: it gives the slot back and stops a build whose answer is already being thrown away.
    ///     </para>
    ///     <para>
    ///         ASKED to stop, never deleted: a superseded work session RAN, so it is audit evidence and the run's event
    ///         log still names it. The tool lane DISCARDS instead of stopping, which is B5's expiry rule for the same
    ///         reason it was made there — a registry entry left behind would refuse the next attempt its place and leave
    ///         that attempt's pass with nothing polling it.
    ///     </para>
    ///     <para>
    ///         Its own scope, because the two row-driving lanes are scoped and this is a singleton: it is the
    ///         <c>Cancelling</c> drain's stop, reached from the one place that supersedes a row without draining its run.
    ///     </para>
    /// </summary>
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
                await scope.ServiceProvider.GetRequiredService<DevWorkflowToolExecutor>().DiscardAsync(nodeRun.Id).ConfigureAwait(false);
                break;

            case { NodeType: DevWorkflowNodeType.DevTask }:
                _ = await scope.ServiceProvider.GetRequiredService<DevWorkflowDevTaskExecutor>()
                               .StopAttemptAsync(nodeRun, cancel: true, cancellationToken)
                               .ConfigureAwait(false);
                break;

            case { NodeType: DevWorkflowNodeType.Agent, WorkSessionId: { } sessionId }:
                await scope.ServiceProvider.GetRequiredService<DevWorkflowAgentExecutor>().StopAsync(sessionId, cancel: true, cancellationToken).ConfigureAwait(false);
                break;

            default:
                // An inline node holds nothing: it is Running only for the width of the tick that admitted it, and a
                // human gate's wait is a durable row rather than work.
                break;
        }
    }

    /// <summary>
    ///     Moves one node run back to <c>Pending</c> for another attempt.
    ///     <para>
    ///         <c>ClearWorkSession</c> travels with EVERY re-attempt, agent node or not: a retry that resumed the session
    ///         that just failed would resume the context that failed with it, and the release is also what stops the
    ///         fresh attempt being settled straight back off the old session's answer. The failure fields are cleared by
    ///         the store, so the event this writes is the only record of what is being re-attempted — which is why it
    ///         carries its own detail rather than the reason.
    ///     </para>
    /// </summary>
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
        _ = await store.TransitionNodeRunAsync(command, cancellationToken).ConfigureAwait(false);
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
        return (new TransitionDevWorkflowNodeRunCommand(run.Id,
            nodeRun.Id,
            DevWorkflowVersions.Any,
            DevWorkflowNodeRunStatus.Pending,
            InputJson: inputJson,
            DetailJson: JsonSerializer.Serialize(detail with
            {
                DelayUntil = delayUntil?.ToUnixTimeMilliseconds()
            }, JsonOptions),
            IncrementAttempt: true,
            ClearWorkSession: true,
            Outcome: outcome,

            // The run-wide budget travels WITH the write, so the store re-checks it under the writer lock instead of
            // trusting the caller's earlier read (FU3-4). Inert on a reset inside a route — those go through the
            // route's own transaction, which admits the whole cascade once against RouteDevWorkflowRetryCommand's
            // budget rather than each reset against this one.
            MaxTotalAttempts: _options.MaxTotalAttempts), delayUntil);
    }

    /// <summary>
    ///     Records or clears when a re-attempt may be admitted. Written for EVERY re-attempt, including the ones that
    ///     ask for no delay: a fix-loop reset of a row that was already waiting on a clock must not inherit the previous
    ///     attempt's, and the removal is also what keeps the map from accumulating an entry per delayed retry for the
    ///     life of the process.
    /// </summary>
    private void Cushion(Guid runId, Guid nodeRunId, DateTimeOffset? delayUntil)
    {
        if (delayUntil is { } notBefore)
        {
            _notBefore[nodeRunId] = new ScheduledRetry(runId, notBefore);
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
        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(run.Id,
                               nodeRun.Id,
                               DevWorkflowVersions.Any,
                               DevWorkflowNodeRunStatus.Blocked,
                               PendingDecisionKind: DevWorkflowDecisionKind.Abandon,
                               OutputJson: outputJson,
                               FailureClass: failureClass,
                               TerminalReason: sanitizedReason,
                               WorkItemStatus: DevWorkflowWorkItemStatus.Blocked),
                           cancellationToken)
                       .ConfigureAwait(false);
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
        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(run.Id,
                               nodeRun.Id,
                               DevWorkflowVersions.Any,
                               DevWorkflowNodeRunStatus.Failed,
                               OutputJson: failure.OutputJson,
                               FailureClass: failure.FailureClass,
                               TerminalReason: failure.SanitizedReason,
                               Outcome: failure.Outcome,
                               WorkItemStatus: DevWorkflowStateMachine.WorkItemStatusAfter(run.Status, nodeRuns, nodeRun.Id, DevWorkflowNodeRunStatus.Failed)),
                           cancellationToken)
                       .ConfigureAwait(false);
        return 1;
    }

    /// <summary>
    ///     The re-attempts this run has already made or promised — <c>Σ(Attempt − 1)</c> plus the recorded operator
    ///     retries that have not become an attempt yet.
    ///     <para>
    ///         Deliberately the same count the store admits a human <c>Retry</c> against, because it is the same budget:
    ///         an automatic re-attempt and an operator's are both re-attempts of this run, and §8.3's <c>MaxTotalAttempts</c>
    ///         is the one bound over both. Counting the reservations is what stops an automatic retry from spending an
    ///         attempt a person has already been promised in the same tick window.
    ///     </para>
    /// </summary>
    private static async Task<int> PromisedAsync(IDevWorkflowStore store,
        Guid runId,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        CancellationToken cancellationToken) =>
        Promised(nodeRuns, await store.ListDecisionsAsync(runId, cancellationToken).ConfigureAwait(false));

    /// <summary>
    ///     The formula itself, shared with <see cref="DevWorkflowStartupReconciler" /> rather than restated there.
    ///     Restating it is how restart recovery came to count only <c>Σ(Attempt − 1)</c> and hand an interrupted row
    ///     the slot a recorded-but-unapplied <c>Retry</c> had already reserved (FU3-4). One definition, three callers:
    ///     this policy, the reconciler, and — in its own SQL — the store's transactional admission.
    /// </summary>
    internal static int Promised(IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns, IReadOnlyList<DevWorkflowDecisionSnapshot> decisions) =>
        nodeRuns.Sum(static row => row.Attempt - 1)
        + decisions.Count(decision => decision.Decision == DevWorkflowDecisionKind.Retry
                                      && nodeRuns.Any(row => row.Id == decision.NodeRunId && row.Attempt == decision.Attempt));

    /// <summary>
    ///     The target's inputs with the failure that sent the run back to it, as flat members so the objective renders
    ///     them as the lines it renders every other input as.
    ///     <para>
    ///         <paramref name="fromNodeKey" /> is the node whose verdict routed the run back, and it is
    ///         <see langword="null" /> for a same-node retry, which has no such node. The DevTask lane reads
    ///         <c>priorFailureNode</c> as "a downstream node rejected this implementation", so a retry that wrote its
    ///         own key there was read as a rejection; a null leaves whatever an earlier genuine route put there
    ///         untouched, and adds none.
    ///     </para>
    ///     <para>
    ///         <paramref name="fromAttempt" /> is that node run's attempt — the one that produced this verdict and
    ///         wrote the report behind it. Together with the key it is the ROUTE's identity, and it is what makes the
    ///         route answerable exactly once: the DevTask lane keys its one change request on it rather than on its own
    ///         attempt, which a same-node retry moves while the very same rejection is still outstanding, and it
    ///         accepts a validation report only from the attempt that actually refused.
    ///     </para>
    /// </summary>
    private static string PriorFailure(string? inputJson, string? fromNodeKey, int? fromAttempt, string outputJson)
    {
        if (fromNodeKey is null && CarriesRoutedFailure(inputJson))
        {
            // A transient retry landing in the MIDDLE of a genuine fix loop. Overwriting priorFailure here would leave
            // the routed node's name with this node's own count-less output under it, so the rework request that
            // follows quotes a verdict it can no longer evidence. Both members stay as the route wrote them — and the
            // merge still runs, because an operator's retry reason belongs to the attempt they retried and nothing
            // else, this one included.
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
        new(nodeRun.Attempt, failure.FailureClass, failure.SanitizedReason, DelayUntil: null);

    private static string BudgetExhausted(DevWorkflowFailure failure) =>
        $"{failure.SanitizedReason} This run has spent every re-attempt it allows, so nothing here can try again without a decision.";

    /// <summary>
    ///     What a <c>node.retry.scheduled</c> event carries. The attempt is the one that FAILED, which is what makes the
    ///     per-attempt history readable off the log the single-row node-run schema does not keep.
    /// </summary>
    private sealed record RetryDetail(int Attempt, string FailureClass, string Reason, long? DelayUntil);

    /// <summary>One promised re-attempt: when it may go, and the run whose ending makes the promise moot.</summary>
    private sealed record ScheduledRetry(Guid RunId, DateTimeOffset NotBefore);

    private sealed record RoutedDetail(string From, string To, string FailureClass, string Reason);
}
