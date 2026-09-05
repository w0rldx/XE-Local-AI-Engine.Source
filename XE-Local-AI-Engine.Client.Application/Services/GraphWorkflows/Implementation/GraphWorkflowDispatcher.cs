namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;

using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The run engine's one loop. It advances a persisted run by transitioning persisted node runs and holds no
///     authoritative state of its own — the parsed-graph cache is a cost optimisation and nothing else, which is
///     exactly why a restart costs at most the work in flight.
///     <para>
///         <b>Every dispatch-side status write happens inside a serialized <see cref="AdvanceOnceAsync" /> call.</b>
///         That is the invariant the design rests on: a lane's work produces a pollable RESULT and never transitions a
///         row itself, so the only other writers to a run are the human command paths — which is what the store's
///         <c>Any</c> version sentinel exists for.
///     </para>
///     <para>
///         Advancement is a pure database decision and takes microseconds, so one loop for every run is enough and
///         gives one place where graph invariants are decided. Seam if the run count ever justifies it: partition by
///         run id. Nothing here assumes it is alone.
///     </para>
/// </summary>
internal sealed class GraphWorkflowDispatcher : IGraphWorkflowDispatcherSignal, IHostedService, IAsyncDisposable
{
    /// <summary>The run statuses a sweep looks at. The three terminals are not advanced by a tick.</summary>
    private static readonly GraphWorkflowRunStatus[] LiveRunStatuses =
    [
        GraphWorkflowRunStatus.Pending,
        GraphWorkflowRunStatus.Running,
        GraphWorkflowRunStatus.WaitingForApproval,
        GraphWorkflowRunStatus.Cancelling
    ];

    /// <summary>
    ///     How many runs of one status a sweep pages in. Generous rather than tuned, and deliberately NOT
    ///     <c>MaxConcurrentRuns</c>: that is an admission cap, and using it as a page size here would order live runs by
    ///     creation date and then silently stop sweeping everything past it — the oldest stuck run, which is exactly the
    ///     one a sweep exists to rescue.
    /// </summary>
    private const int SweepPageSize = 500;

    /// <summary>camelCase, matching every other document this product puts on a wire.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Bounded and drop-on-full: a signal is a latency hint, and blocking a committing caller to deliver one would be the wrong trade.</summary>
    private readonly Channel<Guid> _signals = Channel.CreateBounded<Guid>(new BoundedChannelOptions(capacity: 256)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true
    });

    private readonly SemaphoreSlim _advanceGate = new(initialCount: 1, maxCount: 1);
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>
    ///     The parsed graph per live run. No cache library, no eviction policy, no expiry: the entry count is bounded by
    ///     the concurrent-run cap, and a run's graph is PINNED at start so nothing can invalidate an entry but the run
    ///     ending. It exists because decrypting and re-parsing the blob on every tick is the one repeated cost the
    ///     database-as-truth design would otherwise pay for nothing.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, GraphWorkflowGraph> _graphs = new();

    private readonly IReadOnlyList<IGraphWorkflowNodeExecutor> _executors;
    private readonly GraphWorkflowInlineExecutor _inline;
    private readonly ILogger<GraphWorkflowDispatcher> _logger;
    private readonly GraphWorkflowOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private int _disposed;
    private Task? _loop;

    public GraphWorkflowDispatcher(IServiceScopeFactory scopeFactory,
        GraphWorkflowInlineExecutor inline,
        IEnumerable<IGraphWorkflowNodeExecutor> executors,
        IOptions<GraphWorkflowOptions> options,
        TimeProvider timeProvider,
        ILogger<GraphWorkflowDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(executors);
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _inline = inline ?? throw new ArgumentNullException(nameof(inline));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _executors = [.. executors];
        _options = options.Value;
    }

    /// <summary>What the signal pump is about to read. The only way to assert that a productive tick re-signals.</summary>
    internal ChannelReader<Guid> PendingSignals => _signals.Reader;

    public void Signal(Guid runId) =>
        _ = _signals.Writer.TryWrite(runId);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.Enabled)
        {
            _loop = Task.WhenAll(PumpSignalsAsync(_stopping.Token), PumpSweepAsync(_stopping.Token));
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_loop is { } loop)
        {
            try
            {
                await loop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutdown ran out of grace. The loop holds nothing a restart cannot re-derive.
            }
        }
    }

    /// <summary>
    ///     Idempotent, because this one instance is registered under three service types and the container tracks each
    ///     factory registration's result for disposal separately — so it is disposed once per role, not once per object.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, value: 1) == 1)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _stopping.Dispose();
        _advanceGate.Dispose();
    }

    /// <summary>
    ///     Advances one run by one tick, and answers how many transitions it wrote — zero meaning the run is quiescent.
    ///     <para>
    ///         The testable seam, and a design requirement rather than an afterthought: the production loop is a thin
    ///         wrapper around it, so no test ever has to wait on a timer or race a background task.
    ///     </para>
    /// </summary>
    internal async Task<int> AdvanceOnceAsync(Guid runId, CancellationToken cancellationToken)
    {
        await _advanceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            return await AdvanceCoreAsync(scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>(), runId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _advanceGate.Release();
        }
    }

    /// <summary>
    ///     One tick. The ORDER is load-bearing and the reasons are on each step: what a lane landed is settled before
    ///     anything reads the rows, a drain admits nothing, and the run's own status is recomputed last, against the
    ///     version it was read at.
    /// </summary>
    private async Task<int> AdvanceCoreAsync(IGraphWorkflowStore store, Guid runId, CancellationToken cancellationToken)
    {
        var run = await store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (GraphWorkflowStateMachine.IsTerminal(run.Status))
        {
            Forget(runId);
            return 0;
        }

        if (run.Status == GraphWorkflowRunStatus.Pending)
        {
            return await StartPendingRunAsync(store, run, cancellationToken).ConfigureAwait(false);
        }

        GraphWorkflowGraph graph;
        try
        {
            graph = Resolve(run);
        }
        catch (GraphWorkflowValidationException exception)
        {
            // A running run's graph parsed once already, so reaching here means the pinned blob changed underneath it.
            // Throwing would re-throw on every sweep forever; the run is unroutable and says so instead.
            return await FailUnroutableAsync(store, run, exception, cancellationToken).ConfigureAwait(false);
        }

        // Settle what the lanes have landed FIRST, before anything reads the node runs for a decision: work that
        // finished between ticks has to be seen as finished, or the run judges its whole graph against a row that is
        // only still Running because nothing asked.
        var written = await PollAsync(store, graph, run, cancellationToken).ConfigureAwait(false);

        if (run.Status == GraphWorkflowRunStatus.Cancelling)
        {
            // A drain admits nothing: every terminal is reached through it or through the "nothing is live any more"
            // recomputation, because writing one over live node runs would strand them under a run no tick looks at.
            return written + await DrainAsync(store, run, cancellationToken).ConfigureAwait(false);
        }

        written += await RetryFailedNodesAsync(store, graph, run, cancellationToken).ConfigureAwait(false);
        written += await AdmitAsync(store, graph, run, cancellationToken).ConfigureAwait(false);
        written += await RecomputeRunStatusAsync(store, graph, run, cancellationToken).ConfigureAwait(false);
        return written;
    }

    /// <summary>
    ///     Asks every lane-owned node run what became of the work it was driving, settles the ones that landed, and
    ///     offers to their deadline the ones nothing had anything to say about.
    ///     <para>
    ///         Deliberately the lane's answer rather than this loop's memory: the dispatcher holds nothing about a run
    ///         between ticks, so a restart loses nothing a poll cannot re-read. It runs in every non-terminal status
    ///         including the drain — work asked to stop settles here, which is how the drain learns it may finish.
    ///     </para>
    /// </summary>
    private async Task<int> PollAsync(IGraphWorkflowStore store, GraphWorkflowGraph graph, GraphWorkflowRunSnapshot run, CancellationToken cancellationToken)
    {
        var nodeRuns = await store.ListNodeRunsAsync(run.Id, cancellationToken).ConfigureAwait(false);

        // Before anything is read off a lane: a retry can re-attempt a row a lane is driving without going through it,
        // and an answer belonging to the attempt before is not an answer about the one the row is on now.
        foreach (var executor in _executors)
        {
            await executor.ForgetSupersededAsync(nodeRuns).ConfigureAwait(false);
        }

        var candidates = nodeRuns.Where(static nodeRun => nodeRun.Status is GraphWorkflowNodeRunStatus.Running or GraphWorkflowNodeRunStatus.Queued).ToList();
        var written = 0;
        foreach (var candidate in candidates)
        {
            // One settle can move rows that are not its own, so once anything has been written the rows are re-read and
            // one that has since moved on is left alone.
            if (written > 0)
            {
                nodeRuns = await store.ListNodeRunsAsync(run.Id, cancellationToken).ConfigureAwait(false);
            }

            if (nodeRuns.FirstOrDefault(nodeRun => nodeRun.Id == candidate.Id) is not
                    { Status: GraphWorkflowNodeRunStatus.Running or GraphWorkflowNodeRunStatus.Queued } current
                || !graph.Nodes.TryGetValue(current.NodeKey, out var node))
            {
                continue;
            }

            // A Queued row is polled only when its lane is in fact already driving it: the Running write can fail after
            // the slot and the registry entry were taken, and outside a drain the next admission repairs that — but a
            // drain admits nothing, so without this the run would wait on a row nothing would ever move again.
            var lane = ExecutorFor(node.Kind);
            var polled = lane is not null && (current.Status == GraphWorkflowNodeRunStatus.Running || lane.IsInFlight(current.Id))
                ? await lane.PollAsync(store, run, graph, node, current, cancellationToken).ConfigureAwait(false)
                : 0;

            // Only a row its lane had nothing to say about. Work that landed inside its budget is settled off what it
            // actually came to, and expiring it as well would overwrite that answer with a coarser one.
            written += polled > 0 ? polled : await ExpireAsync(store, graph, run, node, current, cancellationToken).ConfigureAwait(false);
        }

        return written;
    }

    /// <summary>
    ///     Ends a node run that has been running longer than its node allows, and answers how many transitions it wrote.
    ///     <para>
    ///         The deadline is re-derived from the ROW every tick rather than armed once in memory, so it survives the
    ///         restart that would otherwise leave a node run bounded by nothing. A row that has not started has no
    ///         deadline at all, which is what leaves a queued row and a restart collapse nothing to expire.
    ///     </para>
    /// </summary>
    private async Task<int> ExpireAsync(IGraphWorkflowStore store,
        GraphWorkflowGraph graph,
        GraphWorkflowRunSnapshot run,
        GraphWorkflowGraphNode node,
        GraphWorkflowNodeRunSnapshot nodeRun,
        CancellationToken cancellationToken)
    {
        if (!GraphWorkflowDeadline.HasExpired(node, nodeRun, _options, _timeProvider))
        {
            return 0;
        }

        // Dropped BEFORE the row is settled, and dropped rather than merely stopped: a re-attempt can land the row on a
        // new attempt within a tick or two, and admission would then find the registry still holding the work that ran
        // out of time — leaving the fresh attempt with nothing to poll it.
        if (ExecutorFor(node.Kind) is { } lane)
        {
            await lane.DiscardAsync(nodeRun.Id).ConfigureAwait(false);
        }

        return await FailNodeAsync(store,
                graph,
                run,
                node,
                nodeRun,
                GraphWorkflowFailures.Classify(GraphWorkflowFailureClass.Timeout, nodeRun.Attempt, node.MaxAttempts),
                $"This node run did not finish within the {node.TimeoutSeconds ?? _options.DefaultNodeTimeoutSeconds} seconds its node allows.",
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     The ONE place retry-in-place lives. A node run reading <c>Failed</c> with a retryable class, under both its
    ///     node's own attempt cap and the run's total budget, goes back to <c>Pending</c> with the attempt incremented
    ///     in one atomic write carrying a <c>node.retried</c> event.
    ///     <para>
    ///         The event's detail is the only place the failure being re-attempted survives: the move to <c>Pending</c>
    ///         clears the row's failure fields, because a re-attempt must not report the previous try's outcome while it
    ///         runs. Executors and the startup reconciler therefore write plain failures and know nothing about retry,
    ///         which is why an interrupted row is re-attempted here on the first tick with no second mechanism.
    ///     </para>
    ///     <para>
    ///         No cross-node routing, ever. Nothing else in this runtime increments an attempt, so a graph of
    ///         single-attempt nodes never spends any of the run-wide budget.
    ///     </para>
    /// </summary>
    private async Task<int> RetryFailedNodesAsync(IGraphWorkflowStore store,
        GraphWorkflowGraph graph,
        GraphWorkflowRunSnapshot run,
        CancellationToken cancellationToken)
    {
        var nodeRuns = await store.ListNodeRunsAsync(run.Id, cancellationToken).ConfigureAwait(false);

        // The same accounting the restart reconciler uses: attempts SPENT, so a run whose nodes are all on their first
        // try has spent none of it.
        var spent = nodeRuns.Sum(static nodeRun => nodeRun.Attempt - 1);
        var written = 0;
        foreach (var nodeRun in nodeRuns.Where(static nodeRun => nodeRun.Status == GraphWorkflowNodeRunStatus.Failed
                                                                 && GraphWorkflowFailures.IsRetryable(nodeRun.FailureClass)))
        {
            // The node's own cap, then the run's. A budget the RUN ran out of leaves the plain class standing, because
            // the node still had attempts left and it is not the one that is finished.
            if (!graph.Nodes.TryGetValue(nodeRun.NodeKey, out var node) || nodeRun.Attempt >= node.MaxAttempts || spent >= _options.MaxTotalAttempts)
            {
                continue;
            }

            GraphWorkflowStateMachine.EnsureLegal(nodeRun.Status, GraphWorkflowNodeRunStatus.Pending, nodeRun.NodeKey);
            _ = await store.TransitionNodeRunAsync(new TransitionGraphWorkflowNodeRunCommand(run.Id,
                                   nodeRun.Id,
                                   GraphWorkflowVersions.Any,
                                   GraphWorkflowNodeRunStatus.Pending,
                                   IncrementAttempt: true,
                                   EventType: GraphWorkflowEventTypes.NodeRetried,
                                   DetailJson: JsonSerializer.Serialize(new RetryDetail(nodeRun.FailureClass.ToString(), nodeRun.Attempt, nodeRun.Error), JsonOptions)),
                               cancellationToken)
                           .ConfigureAwait(false);
            spent++;
            written++;
        }

        return written;
    }

    /// <summary>
    ///     Judges every <c>Pending</c> node run against its inbound edges and dispatches the ones that may run.
    ///     <para>
    ///         <c>Queued</c> rows are re-offered to their lane without re-judging their edges — nothing un-succeeds, so
    ///         the question's answer cannot have changed and only the slot was ever missing. That is what "the queue
    ///         drains" means concretely: nothing hands out slots, the rows ask again.
    ///     </para>
    /// </summary>
    private async Task<int> AdmitAsync(IGraphWorkflowStore store, GraphWorkflowGraph graph, GraphWorkflowRunSnapshot run, CancellationToken cancellationToken)
    {
        var nodeRuns = await store.ListNodeRunsAsync(run.Id, cancellationToken).ConfigureAwait(false);
        var byKey = nodeRuns.ToDictionary(static nodeRun => nodeRun.NodeKey, StringComparer.Ordinal);
        var written = 0;

        foreach (var nodeRun in nodeRuns.Where(static nodeRun => nodeRun.Status is GraphWorkflowNodeRunStatus.Pending or GraphWorkflowNodeRunStatus.Queued))
        {
            if (!graph.Nodes.TryGetValue(nodeRun.NodeKey, out var node))
            {
                // The run's pinned graph no longer declares this node. Nothing can route it, and nothing should guess.
                // There is no Blocked state in v1, so it is a node failure and the recomputation turns it into a run one.
                written += await FailNodeAsync(store,
                        graph,
                        run,
                        node: null,
                        nodeRun,
                        GraphWorkflowFailureClass.ValidationFailed,
                        $"The run's graph no longer declares node '{nodeRun.NodeKey}'.",
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (nodeRun.Status == GraphWorkflowNodeRunStatus.Queued)
            {
                written += await DispatchAsync(store, graph, run, node, nodeRun, byKey, cancellationToken).ConfigureAwait(false);
                continue;
            }

            switch (GraphWorkflowStateMachine.Admission(node, graph, byKey))
            {
                case GraphWorkflowNodeAdmission.Wait:
                    continue;

                case GraphWorkflowNodeAdmission.Skip:

                    // Named, not bare: a cascade writes as many Skipped rows as it reaches, and without the cause on
                    // each one a reader cannot tell which row was the decision and which merely followed it.
                    GraphWorkflowStateMachine.EnsureLegal(nodeRun.Status, GraphWorkflowNodeRunStatus.Skipped, nodeRun.NodeKey);
                    _ = await store.TransitionNodeRunAsync(new TransitionGraphWorkflowNodeRunCommand(run.Id,
                                           nodeRun.Id,
                                           GraphWorkflowVersions.Any,
                                           GraphWorkflowNodeRunStatus.Skipped,
                                           TerminalReason: GraphWorkflowStateMachine.SkipReason(node, graph, byKey)),
                                       cancellationToken)
                                   .ConfigureAwait(false);
                    written++;
                    continue;

                default:
                    written += await DispatchAsync(store, graph, run, node, nodeRun, byKey, cancellationToken).ConfigureAwait(false);
                    continue;
            }
        }

        return written;
    }

    /// <summary>
    ///     Runs an eligible node run, through the inline executor or through the lane that owns its kind.
    ///     <para>
    ///         A kind no lane owns and the inline executor does not run has NO ARM here, and that absence is the whole
    ///         implementation: the node run fails <c>ValidationFailed</c> because this build cannot execute it, rather
    ///         than queueing behind a lane that will never arrive. Registering an executor for the kind is what removes
    ///         the case, and it removes it without touching this method.
    ///     </para>
    /// </summary>
    private async Task<int> DispatchAsync(IGraphWorkflowStore store,
        GraphWorkflowGraph graph,
        GraphWorkflowRunSnapshot run,
        GraphWorkflowGraphNode node,
        GraphWorkflowNodeRunSnapshot nodeRun,
        IReadOnlyDictionary<string, GraphWorkflowNodeRunSnapshot> byKey,
        CancellationToken cancellationToken)
    {
        if (ExecutorFor(node.Kind) is { } lane)
        {
            return await lane.DispatchAsync(store, run, graph, node, nodeRun, cancellationToken).ConfigureAwait(false);
        }

        if (GraphWorkflowInlineExecutor.Owns(node.Kind))
        {
            return await _inline.ExecuteAsync(store, run, graph, node, nodeRun, byKey, cancellationToken).ConfigureAwait(false);
        }

        return await FailNodeAsync(store,
                graph,
                run,
                node,
                nodeRun,
                GraphWorkflowFailureClass.ValidationFailed,
                $"Node '{node.NodeKey}' is a {node.Kind} node, and this build has no executor for that kind.",
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Settles a <c>Cancelling</c> run once nothing of it is live any more, and admits nothing while it drains.
    ///     <para>
    ///         <b>Ask, do not settle.</b> A row a lane is driving belongs to that lane, and only the lane knows what
    ///         stopping it costs — so the drain requests the stop and the next tick's poll writes the terminal off what
    ///         actually happened. Rows no lane owns are settled here, because for them there is nothing to ask.
    ///     </para>
    /// </summary>
    private async Task<int> DrainAsync(IGraphWorkflowStore store, GraphWorkflowRunSnapshot run, CancellationToken cancellationToken)
    {
        var nodeRuns = await store.ListNodeRunsAsync(run.Id, cancellationToken).ConfigureAwait(false);
        var written = 0;

        foreach (var nodeRun in nodeRuns.Where(static nodeRun => GraphWorkflowStateMachine.IsLive(nodeRun.Status)))
        {
            if (ExecutorFor(nodeRun.Kind) is { } lane && lane.IsInFlight(nodeRun.Id))
            {
                // Asked, not settled, and counted as work only when it actually asked — the drain re-signals on a
                // productive tick, so a lane answering yes to a stop it has already requested would spin the run for
                // the whole duration of the work.
                written += await lane.StopAsync(nodeRun.Id).ConfigureAwait(false) ? 1 : 0;
                continue;
            }

            GraphWorkflowStateMachine.EnsureLegal(nodeRun.Status, GraphWorkflowNodeRunStatus.Cancelled, nodeRun.NodeKey);
            _ = await store.TransitionNodeRunAsync(new TransitionGraphWorkflowNodeRunCommand(run.Id,
                                   nodeRun.Id,
                                   GraphWorkflowVersions.Any,
                                   GraphWorkflowNodeRunStatus.Cancelled,
                                   FailureClass: GraphWorkflowFailureClass.Cancelled,
                                   TerminalReason: "The run was cancelled."),
                               cancellationToken)
                           .ConfigureAwait(false);
            written++;
        }

        // Re-read: the stops above may have settled every row already, and judging "is anything still live" off the
        // snapshot taken before them would cost a whole extra tick for a drain that is in fact finished.
        nodeRuns = await store.ListNodeRunsAsync(run.Id, cancellationToken).ConfigureAwait(false);
        if (nodeRuns.Any(static nodeRun => nodeRun.Status is GraphWorkflowNodeRunStatus.Queued or GraphWorkflowNodeRunStatus.Running))
        {
            // Still settling — a lane was asked to stop and has not answered yet. The command already committed its
            // intent, so a reader can say "cancelling" honestly rather than claim one that has not landed.
            return written;
        }

        GraphWorkflowStateMachine.EnsureLegal(run.Status, GraphWorkflowRunStatus.Cancelled);
        _ = await store.TransitionRunAsync(new TransitionGraphWorkflowRunCommand(run.Id,
                               await CurrentVersionAsync(store, run.Id, cancellationToken).ConfigureAwait(false),
                               GraphWorkflowRunStatus.Cancelled),
                           cancellationToken)
                       .ConfigureAwait(false);
        Forget(run.Id);
        return written + 1;
    }

    /// <summary>
    ///     Ends the tick by asking what the run now IS, of the rows and of the graph they belong to.
    ///     <para>
    ///         Written against the version the run was read at, so a cancel that landed between the read and this write
    ///         WINS rather than being silently overwritten by a status move the dispatcher decided a moment earlier.
    ///     </para>
    /// </summary>
    private async Task<int> RecomputeRunStatusAsync(IGraphWorkflowStore store,
        GraphWorkflowGraph graph,
        GraphWorkflowRunSnapshot run,
        CancellationToken cancellationToken)
    {
        var current = await store.GetRunAsync(run.Id, cancellationToken).ConfigureAwait(false);
        var nodeRuns = await store.ListNodeRunsAsync(run.Id, cancellationToken).ConfigureAwait(false);
        var outcome = GraphWorkflowStateMachine.Recompute(current.Status, graph, nodeRuns);
        if (outcome.Status == current.Status)
        {
            return 0;
        }

        GraphWorkflowStateMachine.EnsureLegal(current.Status, outcome.Status);
        _ = await store.TransitionRunAsync(new TransitionGraphWorkflowRunCommand(run.Id,
                               current.Version,
                               outcome.Status,
                               FailureClass: outcome.FailureClass,
                               SanitizedReason: outcome.TerminalReason,

                               // The run's result, in the SAME transition that completes it: it is read off the first
                               // End node that succeeded, and there is no earlier moment at which "the run's answer"
                               // is a thing that exists.
                               OutputJson: outcome.Status == GraphWorkflowRunStatus.Completed ? RunResult(graph, nodeRuns) : null),
                           cancellationToken)
                       .ConfigureAwait(false);

        if (GraphWorkflowStateMachine.IsTerminal(outcome.Status))
        {
            Forget(run.Id);
        }

        return 1;
    }

    /// <summary>The result of the first terminal node that succeeded, or <see langword="null" /> when none carries one.</summary>
    private static string? RunResult(GraphWorkflowGraph graph, IReadOnlyList<GraphWorkflowNodeRunSnapshot> nodeRuns) =>
        GraphWorkflowInlineExecutor.RunResult(nodeRuns
                                              .Where(nodeRun => nodeRun.Status == GraphWorkflowNodeRunStatus.Succeeded
                                                                && graph.TerminalNodeKeys.Contains(nodeRun.NodeKey))
                                              .OrderBy(static nodeRun => nodeRun.CompletedAtUtc)
                                              .ThenBy(static nodeRun => nodeRun.NodeKey, StringComparer.Ordinal)
                                              .FirstOrDefault()
                                              ?.OutputJson);

    /// <summary>
    ///     Starts a <c>Pending</c> run, or fails it for good if its pinned graph cannot be routed.
    ///     <para>
    ///         The graph is parsed again here rather than trusted from the definition's save. A run left <c>Pending</c>
    ///         on a graph nothing can route would be swept forever, so the refusal is written down rather than retried.
    ///     </para>
    /// </summary>
    private async Task<int> StartPendingRunAsync(IGraphWorkflowStore store, GraphWorkflowRunSnapshot run, CancellationToken cancellationToken)
    {
        try
        {
            _ = Resolve(run);
        }
        catch (GraphWorkflowValidationException exception)
        {
            return await FailUnroutableAsync(store, run, exception, cancellationToken).ConfigureAwait(false);
        }

        if (await store.CountActiveRunsAsync(_options.MaxConcurrentRuns, cancellationToken).ConfigureAwait(false) >= _options.MaxConcurrentRuns)
        {
            // Not refused — WAITING. The run keeps its rows and its place, and the next sweep offers it again; refusing
            // it would push a queue the node is perfectly able to work through back onto the person who started it.
            return 0;
        }

        GraphWorkflowStateMachine.EnsureLegal(run.Status, GraphWorkflowRunStatus.Running);
        _ = await store.TransitionRunAsync(new TransitionGraphWorkflowRunCommand(run.Id,
                               await CurrentVersionAsync(store, run.Id, cancellationToken).ConfigureAwait(false),
                               GraphWorkflowRunStatus.Running),
                           cancellationToken)
                       .ConfigureAwait(false);
        return 1;
    }

    /// <summary>Writes the refusal down. A run nothing can route must not be retried forever by the sweep.</summary>
    private async Task<int> FailUnroutableAsync(IGraphWorkflowStore store,
        GraphWorkflowRunSnapshot run,
        GraphWorkflowValidationException exception,
        CancellationToken cancellationToken)
    {
        if (!GraphWorkflowStateMachine.IsLegal(run.Status, GraphWorkflowRunStatus.Failed))
        {
            // Already draining: the drain reaches its own terminal without the graph, so there is nothing to write.
            _logger.LogError(exception, "Graph workflow run {RunId} has an unroutable graph while {Status}.", run.Id, run.Status);
            return 0;
        }

        _ = await store.TransitionRunAsync(new TransitionGraphWorkflowRunCommand(run.Id,
                               await CurrentVersionAsync(store, run.Id, cancellationToken).ConfigureAwait(false),
                               GraphWorkflowRunStatus.Failed,
                               FailureClass: GraphWorkflowFailureClass.ValidationFailed,
                               SanitizedReason: exception.Message),
                           cancellationToken)
                       .ConfigureAwait(false);
        Forget(run.Id);
        return 1;
    }

    /// <summary>
    ///     Fails one node run, with the output document that failure produces.
    ///     <para>
    ///         The document is composed through the single writer like every other, and dropped if the cap refuses it:
    ///         a failure cannot fail for being too large to describe, and the row's own reason carries the account
    ///         either way. <paramref name="node" /> is null only for a key the pinned graph no longer declares — the
    ///         one case with no node to compose against.
    ///     </para>
    ///     <para>
    ///         A <c>Pending</c> row is walked through <c>Running</c> first, because the state machine has no
    ///         <c>Pending → Failed</c> edge and deliberately so: a row that never ran has nothing to report, so a
    ///         failure about it is a failure of the ATTEMPT, and <c>Running</c> is what opens one. The pair costs the
    ///         same two event rows an inline success costs, and reads the same way in the log.
    ///     </para>
    /// </summary>
    private async Task<int> FailNodeAsync(IGraphWorkflowStore store,
        GraphWorkflowGraph graph,
        GraphWorkflowRunSnapshot run,
        GraphWorkflowGraphNode? node,
        GraphWorkflowNodeRunSnapshot nodeRun,
        GraphWorkflowFailureClass failureClass,
        string sanitizedReason,
        CancellationToken cancellationToken)
    {
        var written = 0;
        if (nodeRun.Status == GraphWorkflowNodeRunStatus.Pending)
        {
            GraphWorkflowStateMachine.EnsureLegal(nodeRun.Status, GraphWorkflowNodeRunStatus.Running, nodeRun.NodeKey);
            _ = await store.TransitionNodeRunAsync(new TransitionGraphWorkflowNodeRunCommand(run.Id,
                                   nodeRun.Id,
                                   GraphWorkflowVersions.Any,
                                   GraphWorkflowNodeRunStatus.Running),
                               cancellationToken)
                           .ConfigureAwait(false);
            nodeRun = nodeRun with
            {
                Status = GraphWorkflowNodeRunStatus.Running
            };
            written++;
        }

        string? document = null;
        if (node is not null)
        {
            try
            {
                document = GraphWorkflowDocuments.Compose(graph,
                    node,
                    nodeRun.Attempt,
                    GraphWorkflowNodeOutputStatuses.Failed,
                    GraphWorkflowDocuments.EmptyObject,
                    _options.MaxOutputJsonBytes);
            }
            catch (GraphWorkflowOutputTooLargeException)
            {
                // A cap small enough to refuse an empty envelope. The row still fails, and says why.
            }
        }

        GraphWorkflowStateMachine.EnsureLegal(nodeRun.Status, GraphWorkflowNodeRunStatus.Failed, nodeRun.NodeKey);
        _ = await store.TransitionNodeRunAsync(new TransitionGraphWorkflowNodeRunCommand(run.Id,
                               nodeRun.Id,
                               GraphWorkflowVersions.Any,
                               GraphWorkflowNodeRunStatus.Failed,
                               OutputJson: document,
                               FailureClass: failureClass,
                               TerminalReason: sanitizedReason),
                           cancellationToken)
                       .ConfigureAwait(false);
        return written + 1;
    }

    /// <summary>The lane that owns a kind, or <see langword="null" /> when nothing in this build runs it.</summary>
    private IGraphWorkflowNodeExecutor? ExecutorFor(GraphWorkflowNodeKind kind) =>
        _executors.FirstOrDefault(executor => executor.Owns(kind));

    /// <summary>The run's pinned graph, parsed once. It cannot change under a run, so nothing but the run ending evicts it.</summary>
    private GraphWorkflowGraph Resolve(GraphWorkflowRunSnapshot run) =>
        _graphs.GetOrAdd(run.Id, _ => GraphWorkflowGraph.Parse(run.GraphJson));

    /// <summary>Drops the parsed graph of a run that has ended. A run that turns out to be live again re-parses.</summary>
    private void Forget(Guid runId) =>
        _graphs.TryRemove(runId, out _);

    /// <summary>
    ///     The run's version as of right now, for a run-level write that follows this tick's own node-run writes.
    ///     <para>
    ///         Every node-run transition bumps the run version, so the top-of-tick version is stale by the time a drain
    ///         or a start writes — using it would make the dispatcher lose a race against itself. Re-reading narrows the
    ///         window to what the check is actually for.
    ///     </para>
    /// </summary>
    private static async Task<long> CurrentVersionAsync(IGraphWorkflowStore store, Guid runId, CancellationToken cancellationToken) =>
        (await store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false)).Version;

    /// <summary>
    ///     Two pumps, one advance. A signal is a latency hint and a sweep is the backstop, so they are independent — and
    ///     safe to be, because <see cref="AdvanceOnceAsync" /> serializes: the single-advance invariant lives in the
    ///     gate rather than in the shape of the wait.
    /// </summary>
    private async Task PumpSignalsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var runId in _signals.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await AdvanceSafelyAsync(runId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task PumpSweepAsync(CancellationToken cancellationToken)
    {
        // The first sweep is immediate: after a restart the reconciler has just left node runs re-dispatchable, and
        // waiting a whole interval to notice would add that interval to every recovery.
        using var sweep = new PeriodicTimer(TimeSpan.FromMilliseconds(_options.DispatchIntervalMilliseconds), _timeProvider);
        try
        {
            await SweepAsync(cancellationToken).ConfigureAwait(false);
            while (await sweep.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await SweepAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    internal async Task SweepAsync(CancellationToken cancellationToken)
    {
        var runIds = new HashSet<Guid>();
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>();
            foreach (var status in LiveRunStatuses)
            {
                var runs = await store.ListRunsAsync(status, SweepPageSize, cancellationToken).ConfigureAwait(false);
                runIds.UnionWith(runs.Select(static run => run.Id));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "The graph workflow sweep could not list its live runs.");
            return;
        }

        foreach (var runId in runIds)
        {
            await AdvanceSafelyAsync(runId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     One run's failure must not stop the loop: the others are unrelated, and this one is re-derived from unchanged
    ///     rows on the next tick.
    /// </summary>
    internal async Task AdvanceSafelyAsync(Guid runId, CancellationToken cancellationToken)
    {
        try
        {
            if (await AdvanceOnceAsync(runId, cancellationToken).ConfigureAwait(false) > 0)
            {
                // A tick advances the graph by one layer, so a productive one almost always leaves more to do. Without
                // this every hop would wait for the next sweep and a five-node run would take five intervals.
                Signal(runId);
            }
        }
        catch (GraphWorkflowInvalidTransitionException exception)
        {
            // Usually somebody else moved the run between this tick's read and its write — a cancel, or a human
            // decision. Their write stands and the next tick re-derives from it; there is nothing to repair.
            //
            // Deliberately NOT re-signalled, which is where this departs from the development-workflow original. That
            // module has a separate concurrency exception; here the store's stale-version rejection and the state
            // machine's "that move is forbidden" share ONE type, on purpose — from the caller's side both mean "the
            // row is not what you thought it was". Re-signalling would therefore turn an illegal-move BUG into a hot
            // loop at full speed rather than a line in the log, and the sweep re-offers the run either way. The cost
            // is one dispatch interval of latency after a genuinely lost race, which is the same budget every dropped
            // signal already spends.
            _logger.LogWarning(exception, "Graph workflow run {RunId} could not commit a transition mid-tick.", runId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Graph workflow run {RunId} could not be advanced.", runId);
        }
    }

    /// <summary>
    ///     What a re-attempt records about the failure it is re-attempting. The row has cleared those fields, so this
    ///     event is the only place they survive.
    /// </summary>
    private sealed record RetryDetail(string FailureClass, int Attempt, string? Reason);
}
