namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using System.Collections.Concurrent;
using System.Text;
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

    private readonly DevWorkflowOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;

    public DevWorkflowRetryPolicy(IServiceScopeFactory scopeFactory, IOptions<DevWorkflowOptions> options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
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

        if (!IsRetryable(failure.FailureClass, nodeRun.Attempt))
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
    /// </summary>
    private static bool IsRetryable(string failureClass, int attempt) =>
        RetryableFailureClasses.Contains(failureClass)
        && (failureClass != DevWorkflowFailureClasses.Internal || attempt < 2);

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
        return await ReAttemptAsync(store,
                run,
                nodeRun,
                node.RetryDelaySeconds,
                DetailFor(nodeRun, failure),
                failure.Outcome ?? DevWorkflowOutcomes.Failed,
                cancellationToken,
                PriorFailure(nodeRun.InputJson, nodeRun.NodeKey, failure.OutputJson))
            .ConfigureAwait(false);
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

        // The WHOLE cascade has to fit, not just the target's own attempt. Admitting a fan-out one attempt at a time is
        // how a run spends more re-attempts than it allows by the width of its graph — the same accounting the startup
        // reconciler does for the same reason.
        var cost = reset.Count + 1;
        if (await PromisedAsync(store, run.Id, nodeRuns, cancellationToken).ConfigureAwait(false) + cost > _options.MaxTotalAttempts)
        {
            return await BlockAsync(store, run, nodeRun, DevWorkflowFailureClasses.BudgetExhausted, BudgetExhausted(failure), failure.OutputJson, cancellationToken)
                .ConfigureAwait(false);
        }

        _ = await store.AppendEventAsync(new AppendDevWorkflowEventCommand(run.Id,
                               DevWorkflowVersions.Any,
                               DevWorkflowEventTypes.NodeRetryRouted,
                               nodeRun.Id,
                               DevWorkflowOperationId.For(run.Id, nodeRun.NodeKey, nodeRun.Attempt, "retry-routed"),
                               failure.Outcome ?? DevWorkflowOutcomes.Failed,
                               JsonSerializer.Serialize(new RoutedDetail(nodeRun.NodeKey, retryTarget, failure.FailureClass, failure.SanitizedReason), JsonOptions)),
                           cancellationToken)
                       .ConfigureAwait(false);

        // The target is reset LAST, deliberately. A crash part-way through leaves descendants at the new attempt and the
        // target still terminal, which the next round re-derives in one pass; target-first would leave the target
        // running again while descendants still carry the answers it is about to replace.
        var written = 1;
        foreach (var row in reset)
        {
            // Only the node that failed ended failed. The rest are being re-run because the answer they gave is about
            // to describe something that no longer exists, and stamping their event "failed" would say they broke.
            if (row.Id == nodeRun.Id)
            {
                written += await ReAttemptAsync(store,
                        run,
                        row,
                        delaySeconds: 0,
                        DetailFor(row, failure),
                        failure.Outcome ?? DevWorkflowOutcomes.Failed,
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            await QuiesceAsync(row, cancellationToken).ConfigureAwait(false);
            written += await ReAttemptAsync(store,
                    run,
                    row,
                    delaySeconds: 0,
                    new RetryDetail(row.Attempt,
                        failure.FailureClass,
                        $"Node '{retryTarget}' is being re-attempted because '{nodeRun.NodeKey}' failed, so this node run's result no longer describes it.",
                        DelayUntil: null),
                    outcome: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // The target is almost always Succeeded by the time anything downstream of it can fail, so this is almost
        // always a no-op — but an Any join lets a descendant run on a sibling branch while the target is still working,
        // and that target is as live as any other row the reset moves.
        await QuiesceAsync(target, cancellationToken).ConfigureAwait(false);
        written += await ReAttemptAsync(store,
                run,
                target,
                node.RetryDelaySeconds,
                new RetryDetail(target.Attempt, failure.FailureClass, $"Re-attempted because '{nodeRun.NodeKey}' failed.", DelayUntil: null),
                outcome: null,
                cancellationToken,
                PriorFailure(target.InputJson, nodeRun.NodeKey, failure.OutputJson))
            .ConfigureAwait(false);
        return written;
    }

    /// <summary>
    ///     Stops the lane work a node run is about to lose, immediately before the reset takes away the only row that
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
        var delayUntil = delaySeconds > 0 ? _timeProvider.GetUtcNow().AddSeconds(delaySeconds) : (DateTimeOffset?)null;
        DevWorkflowStateMachine.EnsureLegal(nodeRun.Status, DevWorkflowNodeRunStatus.Pending, nodeRun.NodeKey);
        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(run.Id,
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
                               Outcome: outcome),
                           cancellationToken)
                       .ConfigureAwait(false);

        // Written on EVERY re-attempt, including the ones that ask for no delay: a fix-loop reset of a row that was
        // already waiting on a clock must not inherit the previous attempt's, and the removal is also what keeps the
        // map from accumulating an entry per delayed retry for the life of the process.
        if (delayUntil is { } notBefore)
        {
            _notBefore[nodeRun.Id] = new ScheduledRetry(run.Id, notBefore);
        }
        else
        {
            _ = _notBefore.TryRemove(nodeRun.Id, out _);
        }

        return 1;
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
        CancellationToken cancellationToken)
    {
        var decisions = await store.ListDecisionsAsync(runId, cancellationToken).ConfigureAwait(false);
        return nodeRuns.Sum(static row => row.Attempt - 1)
               + decisions.Count(decision => decision.Decision == DevWorkflowDecisionKind.Retry
                                             && nodeRuns.Any(row => row.Id == decision.NodeRunId && row.Attempt == decision.Attempt));
    }

    /// <summary>
    ///     The target's inputs with the failure that sent the run back to it, as two flat members so the objective
    ///     renders them as the lines it renders every other input as.
    /// </summary>
    private static string PriorFailure(string? inputJson, string fromNodeKey, string outputJson)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            using (var existing = Parse(inputJson))
            {
                if (existing is not null)
                {
                    foreach (var property in existing.RootElement.EnumerateObject()
                                                     .Where(static property => property.Name is not ("priorFailure" or "priorFailureNode")))
                    {
                        property.WriteTo(writer);
                    }
                }
            }

            writer.WriteString("priorFailureNode", fromNodeKey);
            writer.WritePropertyName("priorFailure");
            using (var output = Parse(outputJson))
            {
                if (output is null)
                {
                    writer.WriteStringValue(outputJson);
                }
                else
                {
                    output.RootElement.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>A JSON object, or null when there is none or the text is not one this can carry through.</summary>
    private static JsonDocument? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            document.Dispose();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
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
