namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;

using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The run engine's one loop, advancing a persisted run by transitioning persisted node runs and holding no
///     authoritative state of its own.
/// </summary>
/// <remarks>
///     The parsed-graph cache is a cost optimisation and nothing else, which is why a restart costs at most the work
///     in flight. <b>Every dispatch-side status write happens inside a serialized <see cref="AdvanceOnceAsync" />
///     call</b>: a lane's work produces a pollable RESULT and never transitions a row itself, so the only other
///     writers to a run are the human command paths, which is what the store's <c>Any</c> version sentinel exists for.
///     Advancement is a pure database decision taking microseconds, so one loop serves every run; the seam if the count ever justifies one is to partition by run id.
/// </remarks>
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

    /// <summary>How many runs of one status a sweep pages in.</summary>
    /// <remarks>
    ///     Generous rather than tuned, and deliberately NOT <c>MaxConcurrentRuns</c>: that is an admission cap, and
    ///     using it as a page size here would order live runs by creation date and then silently stop sweeping
    ///     everything past it — the oldest stuck run, which is exactly the one a sweep exists to rescue.
    /// </remarks>
    private const int SweepPageSize = 500;

    /// <summary>
    ///     What a drained row and the run above it say for themselves. ONE spelling, because the two are read side by
    ///     side and a run that explained its cancellation differently from its own node runs would read like two
    ///     different events.
    /// </summary>
    private const string DrainedReason = "The run was cancelled.";

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

    /// <summary>The parsed graph per live run.</summary>
    /// <remarks>
    ///     No cache library, no eviction policy, no expiry: the entry count is bounded by the concurrent-run cap, and
    ///     a run's graph is PINNED at start so nothing can invalidate an entry but the run ending. It exists because
    ///     decrypting and re-parsing the blob on every tick is the one repeated cost the database-as-truth design
    ///     would otherwise pay for nothing.
    /// </remarks>
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

    /// <summary>A TEST-ONLY seam, and null in every other build.</summary>
    /// <remarks>
    ///     Run just before a <c>Pending</c> run's start is written, so a test can commit a cancel in the one window
    ///     the version check exists to lose. Production never sets it, and nothing in this class reads it for a
    ///     decision.
    /// </remarks>
    internal Func<Task>? BeforeRunWrite { get; set; }

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
        await _stopping.CancelAsync();
        if (_loop is { } loop)
        {
            try
            {
                await loop.WaitAsync(cancellationToken);
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

        await StopAsync(CancellationToken.None);
        _stopping.Dispose();
        _advanceGate.Dispose();
    }

    /// <summary>
    ///     Advances one run by one tick, and answers how many transitions it wrote — zero meaning the run is
    ///     quiescent.
    /// </summary>
    /// <remarks>
    ///     The testable seam, and a design requirement rather than an afterthought: the production loop is a thin
    ///     wrapper around it, so no test ever has to wait on a timer or race a background task.
    /// </remarks>
    internal async Task<int> AdvanceOnceAsync(Guid runId, CancellationToken cancellationToken)
    {
        await _advanceGate.WaitAsync(cancellationToken);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            return await AdvanceCoreAsync(scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>(), runId, cancellationToken);
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
        var run = await store.GetRunAsync(runId, cancellationToken);
        if (GraphWorkflowStateMachine.IsTerminal(run.Status))
        {
            Forget(runId);
            return 0;
        }

        if (run.Status == GraphWorkflowRunStatus.Pending)
        {
            return await StartPendingRunAsync(store, run, cancellationToken);
        }

        GraphWorkflowGraph graph;
        try
        {
            graph = Resolve(run);
        }
        catch (GraphWorkflowValidationException exception)
        {
            // A running run's graph parsed once already, so reaching here means the pinned blob changed underneath it; throwing would re-throw every sweep, so the
            // run is unroutable and says so. Except while CANCELLING, where no failure can be written (no Cancelling → Failed edge) and a drain is all that is left.
            return run.Status == GraphWorkflowRunStatus.Cancelling
                ? await DrainAsync(store, run, cancellationToken)
                : await FailUnroutableAsync(store, run, exception, cancellationToken);
        }

        // Settle what the lanes have landed FIRST, before anything reads the node runs for a decision: work that finished between ticks has to be seen as
        // finished, or the run judges its whole graph against a row that is only still Running because nothing asked.
        var written = await PollAsync(store, graph, run, cancellationToken);

        if (run.Status == GraphWorkflowRunStatus.Cancelling)
        {
            // A drain admits nothing: every terminal is reached through it or through the "nothing is live any more"
            // recomputation, because writing one over live node runs would strand them under a run no tick looks at.
            return written + await DrainAsync(store, run, cancellationToken);
        }

        written += await RetryFailedNodesAsync(store, graph, run, cancellationToken);
        written += await AdmitAsync(store, graph, run, cancellationToken);
        written += await RecomputeRunStatusAsync(store, graph, run, cancellationToken);
        return written;
    }

    /// <summary>
    ///     Asks every lane-owned node run what became of the work it was driving, settles the ones that landed, and
    ///     offers to their deadline the ones nothing had anything to say about.
    /// </summary>
    /// <remarks>
    ///     Deliberately the lane's answer rather than this loop's memory: the dispatcher holds nothing about a run
    ///     between ticks, so a restart loses nothing a poll cannot re-read. It runs in every non-terminal status
    ///     including the drain — work asked to stop settles here, which is how the drain learns it may finish.
    /// </remarks>
    private async Task<int> PollAsync(IGraphWorkflowStore store, GraphWorkflowGraph graph, GraphWorkflowRunSnapshot run, CancellationToken cancellationToken)
    {
        var nodeRuns = await store.ListNodeRunsAsync(run.Id, cancellationToken);

        // Before anything is read off a lane: a retry can re-attempt a row a lane is driving without going through it,
        // and an answer belonging to the attempt before is not an answer about the one the row is on now.
        foreach (var executor in _executors)
        {
            await executor.ForgetSupersededAsync(nodeRuns);
        }

        var candidates = nodeRuns.Where(static nodeRun => nodeRun.Status is GraphWorkflowNodeRunStatus.Running or GraphWorkflowNodeRunStatus.Queued).ToList();
        var written = 0;
        foreach (var candidate in candidates)
        {
            // One settle can move rows that are not its own, so once anything has been written the rows are re-read and
            // one that has since moved on is left alone.
            if (written > 0)
            {
                nodeRuns = await store.ListNodeRunsAsync(run.Id, cancellationToken);
            }

            if (nodeRuns.FirstOrDefault(nodeRun => nodeRun.Id == candidate.Id) is not { Status: GraphWorkflowNodeRunStatus.Running or GraphWorkflowNodeRunStatus.Queued } current
                || !graph.Nodes.TryGetValue(current.NodeKey, out var node))
            {
                continue;
            }

            // A Queued row is polled only when its lane is in fact already driving it: the Running write can fail after the slot and the registry entry were
            // taken, and outside a drain the next admission repairs that — but a drain admits nothing, so the run would wait on a row nothing would ever move.
            var lane = ExecutorFor(node.Kind);
            var polled = lane is not null && (current.Status == GraphWorkflowNodeRunStatus.Running || lane.IsInFlight(current.Id))
                ? await lane.PollAsync(store, run, graph, node, current, cancellationToken)
                : 0;

            // Only a row its lane had nothing to say about. Work that landed inside its budget is settled off what it
            // actually came to, and expiring it as well would overwrite that answer with a coarser one.
            written += polled > 0 ? polled : await ExpireAsync(store, graph, run, node, current, cancellationToken);
        }

        return written;
    }

    /// <summary>
    ///     Ends a node run that has been running longer than its node allows, and answers how many transitions it
    ///     wrote.
    /// </summary>
    /// <remarks>
    ///     The deadline is re-derived from the ROW every tick rather than armed once in memory, so it survives the
    ///     restart that would otherwise leave a node run bounded by nothing. A row that has not started has no
    ///     deadline at all, which is what leaves a queued row and a restart collapse nothing to expire.
    /// </remarks>
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

        // Dropped BEFORE the row is settled, and dropped rather than merely stopped: a re-attempt can land the row on a new attempt within a tick or two, and
        // admission would then find the registry still holding the work that ran out of time — leaving the fresh attempt with nothing to poll it.
        if (ExecutorFor(node.Kind) is { } lane)
        {
            await lane.DiscardAsync(nodeRun.Id);
        }

        return await FailNodeAsync(store,
                graph,
                run,
                node,
                nodeRun,
                GraphWorkflowFailures.Classify(GraphWorkflowFailureClass.Timeout, nodeRun.Attempt, node.MaxAttempts),
                $"This node run did not finish within the {node.TimeoutSeconds ?? _options.DefaultNodeTimeoutSeconds} seconds its node allows.",
                cancellationToken);
    }

    /// <summary>
    ///     The ONE place retry-in-place lives: a <c>Failed</c> node run with a retryable class, under both its node's
    ///     attempt cap and the run's total budget, goes back to <c>Pending</c> with the attempt incremented.
    /// </summary>
    /// <remarks>
    ///     One atomic write carrying a <c>node.retried</c> event, whose detail is the only place the failure being
    ///     re-attempted survives: the move to <c>Pending</c> clears the row's failure fields, because a re-attempt
    ///     must not report the previous try's outcome while it runs. Executors and the startup reconciler therefore
    ///     write plain failures and know nothing about retry, which is why an interrupted row is re-attempted here on
    ///     the first tick with no second mechanism. No cross-node routing, ever, and nothing else increments an attempt.
    /// </remarks>
    private async Task<int> RetryFailedNodesAsync(IGraphWorkflowStore store,
        GraphWorkflowGraph graph,
        GraphWorkflowRunSnapshot run,
        CancellationToken cancellationToken)
    {
        var nodeRuns = await store.ListNodeRunsAsync(run.Id, cancellationToken);

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
            _ = await store.TransitionNodeRunAsync(new TransitionGraphWorkflowNodeRunCommand
            {
                RunId = run.Id,
                NodeRunId = nodeRun.Id,
                ExpectedVersion = GraphWorkflowVersions.Any,
                TargetStatus = GraphWorkflowNodeRunStatus.Pending,
                IncrementAttempt = true,
                EventType = GraphWorkflowEventTypes.NodeRetried,
                DetailJson = JsonSerializer.Serialize(new RetryDetail { FailureClass = nodeRun.FailureClass.ToString(), Attempt = nodeRun.Attempt, Reason = nodeRun.Error }, JsonOptions)
            },
                               cancellationToken);
            spent++;
            written++;
        }

        return written;
    }

    /// <summary>Judges every <c>Pending</c> node run against its inbound edges and dispatches the ones that may run.</summary>
    /// <remarks>
    ///     <c>Queued</c> rows are re-offered to their lane without re-judging their edges — nothing un-succeeds, so
    ///     the question's answer cannot have changed and only the slot was ever missing. That is what "the queue
    ///     drains" means concretely: nothing hands out slots, the rows ask again.
    /// </remarks>
    private async Task<int> AdmitAsync(IGraphWorkflowStore store, GraphWorkflowGraph graph, GraphWorkflowRunSnapshot run, CancellationToken cancellationToken)
    {
        var nodeRuns = await store.ListNodeRunsAsync(run.Id, cancellationToken);
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
                        cancellationToken);
                continue;
            }

            if (nodeRun.Status == GraphWorkflowNodeRunStatus.Queued)
            {
                written += await DispatchAsync(store, graph, run, node, nodeRun, byKey, cancellationToken);
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
                    _ = await store.TransitionNodeRunAsync(new TransitionGraphWorkflowNodeRunCommand
                    {
                        RunId = run.Id,
                        NodeRunId = nodeRun.Id,
                        ExpectedVersion = GraphWorkflowVersions.Any,
                        TargetStatus = GraphWorkflowNodeRunStatus.Skipped,
                        TerminalReason = GraphWorkflowStateMachine.SkipReason(node, graph, byKey)
                    },
                                       cancellationToken);
                    written++;
                    continue;

                default:
                    written += await DispatchAsync(store, graph, run, node, nodeRun, byKey, cancellationToken);
                    continue;
            }
        }

        return written;
    }

    /// <summary>Runs an eligible node run, through the inline executor or through the lane that owns its kind.</summary>
    /// <remarks>
    ///     A kind no lane owns and the inline executor does not run has NO ARM here, and that absence is the whole
    ///     implementation: the node run fails <c>ValidationFailed</c> because this build cannot execute it, rather
    ///     than queueing behind a lane that will never arrive. Registering an executor for the kind removes the case,
    ///     and removes it without touching this method.
    /// </remarks>
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
            return await lane.DispatchAsync(store, run, graph, node, nodeRun, cancellationToken);
        }

        if (GraphWorkflowInlineExecutor.Owns(node.Kind))
        {
            return await _inline.ExecuteAsync(store, run, graph, node, nodeRun, byKey, cancellationToken);
        }

        return await FailNodeAsync(store,
                graph,
                run,
                node,
                nodeRun,
                GraphWorkflowFailureClass.ValidationFailed,
                $"Node '{node.NodeKey}' is a {node.Kind} node, and this build has no executor for that kind.",
                cancellationToken);
    }

    /// <summary>
    ///     Settles a <c>Cancelling</c> run once nothing of it is live any more, and admits nothing while it drains.
    /// </summary>
    /// <remarks>
    ///     <b>Ask, do not settle.</b> A row a lane is driving belongs to that lane, and only the lane knows what
    ///     stopping it costs — so the drain requests the stop and the next tick's poll writes the terminal off what
    ///     actually happened. Rows no lane owns are settled here, because for them there is nothing to ask.
    /// </remarks>
    private async Task<int> DrainAsync(IGraphWorkflowStore store, GraphWorkflowRunSnapshot run, CancellationToken cancellationToken)
    {
        var nodeRuns = await store.ListNodeRunsAsync(run.Id, cancellationToken);
        var written = 0;

        foreach (var nodeRun in nodeRuns.Where(static nodeRun => GraphWorkflowStateMachine.IsLive(nodeRun.Status)))
        {
            if (ExecutorFor(nodeRun.Kind) is { } lane && lane.IsInFlight(nodeRun.Id))
            {
                // Asked, not settled, and counted as work only when it actually asked — the drain re-signals on a productive tick, so a lane answering yes to
                // a stop it has already requested would spin the run for the whole duration of the work.
                written += await lane.StopAsync(nodeRun.Id) ? 1 : 0;
                continue;
            }

            GraphWorkflowStateMachine.EnsureLegal(nodeRun.Status, GraphWorkflowNodeRunStatus.Cancelled, nodeRun.NodeKey);
            _ = await store.TransitionNodeRunAsync(new TransitionGraphWorkflowNodeRunCommand
            {
                RunId = run.Id,
                NodeRunId = nodeRun.Id,
                ExpectedVersion = GraphWorkflowVersions.Any,
                TargetStatus = GraphWorkflowNodeRunStatus.Cancelled,
                FailureClass = GraphWorkflowFailureClass.Cancelled,
                TerminalReason = DrainedReason
            },
                               cancellationToken);
            written++;
        }

        // Re-read: the stops above may have settled every row already, and judging "is anything still live" off the
        // snapshot taken before them would cost a whole extra tick for a drain that is in fact finished.
        nodeRuns = await store.ListNodeRunsAsync(run.Id, cancellationToken);
        if (nodeRuns.Any(static nodeRun => nodeRun.Status is GraphWorkflowNodeRunStatus.Queued or GraphWorkflowNodeRunStatus.Running))
        {
            // Still settling — a lane was asked to stop and has not answered yet. The command already committed its
            // intent, so a reader can say "cancelling" honestly rather than claim one that has not landed.
            return written;
        }

        // Classified, unlike the recomputed cancellation below it: THIS one was asked for, and a drained run that
        // reported class None would leave an operator reading a terminal run with no record of why it stopped.
        GraphWorkflowStateMachine.EnsureLegal(run.Status, GraphWorkflowRunStatus.Cancelled);
        _ = await store.TransitionRunAsync(new TransitionGraphWorkflowRunCommand
        {
            RunId = run.Id,
            ExpectedVersion = await CurrentVersionAsync(store, run.Id, cancellationToken),
            TargetStatus = GraphWorkflowRunStatus.Cancelled,
            FailureClass = GraphWorkflowFailureClass.Cancelled,
            SanitizedReason = DrainedReason
        },
                           cancellationToken);
        Forget(run.Id);
        return written + 1;
    }

    /// <summary>Ends the tick by asking what the run now IS, of the rows and of the graph they belong to.</summary>
    /// <remarks>
    ///     Written against the version the run was read at, so a cancel that landed between the read and this write
    ///     WINS rather than being silently overwritten by a status move the dispatcher decided a moment earlier.
    /// </remarks>
    private async Task<int> RecomputeRunStatusAsync(IGraphWorkflowStore store,
        GraphWorkflowGraph graph,
        GraphWorkflowRunSnapshot run,
        CancellationToken cancellationToken)
    {
        var current = await store.GetRunAsync(run.Id, cancellationToken);
        var nodeRuns = await store.ListNodeRunsAsync(run.Id, cancellationToken);
        var outcome = GraphWorkflowStateMachine.Recompute(current.Status, graph, nodeRuns);
        if (outcome.Status == current.Status)
        {
            return 0;
        }

        GraphWorkflowStateMachine.EnsureLegal(current.Status, outcome.Status);
        _ = await store.TransitionRunAsync(new TransitionGraphWorkflowRunCommand
        {
            RunId = run.Id,
            ExpectedVersion = current.Version,
            TargetStatus = outcome.Status,
            FailureClass = outcome.FailureClass,
            SanitizedReason = outcome.TerminalReason,
            // The run's result, in the SAME transition that completes it: read off the first End node that succeeded, and there
            // is no earlier moment at which "the run's answer" is a thing that exists.
            OutputJson = outcome.Status == GraphWorkflowRunStatus.Completed ? RunResult(graph, nodeRuns) : null
        },
                           cancellationToken);

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

    /// <summary>Starts a <c>Pending</c> run, or fails it for good if its pinned graph cannot be routed.</summary>
    /// <remarks>
    ///     The graph is parsed again here rather than trusted from the definition's save. A run left <c>Pending</c> on
    ///     a graph nothing can route would be swept forever, so the refusal is written down rather than retried.
    /// </remarks>
    private async Task<int> StartPendingRunAsync(IGraphWorkflowStore store, GraphWorkflowRunSnapshot run, CancellationToken cancellationToken)
    {
        try
        {
            _ = Resolve(run);
        }
        catch (GraphWorkflowValidationException exception)
        {
            return await FailUnroutableAsync(store, run, exception, cancellationToken);
        }

        if (await store.CountActiveRunsAsync(_options.MaxConcurrentRuns, cancellationToken) >= _options.MaxConcurrentRuns)
        {
            // Not refused — WAITING. The run keeps its rows and its place, and the next sweep offers it again; refusing
            // it would push a queue the node is perfectly able to work through back onto the person who started it.
            return 0;
        }

        if (BeforeRunWrite is { } hook)
        {
            await hook();
        }

        GraphWorkflowStateMachine.EnsureLegal(run.Status, GraphWorkflowRunStatus.Running);

        // Against the version the run was READ at, not a fresh one: this tick has written no node run, so it has nothing of its own to lose a race against, and a
        // cancel that committed in between has bumped the version. A fresh version would carry that cancel's own bump, and the run would go Running underneath it.
        _ = await store.TransitionRunAsync(new TransitionGraphWorkflowRunCommand { RunId = run.Id, ExpectedVersion = run.Version, TargetStatus = GraphWorkflowRunStatus.Running }, cancellationToken);
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

        // The version the run was READ at, for the same reason the start above uses it: both callers reach here before this tick has written any node run, so the
        // only writer it can lose to is somebody else — and losing to them is the point. A fresh version would carry a cancel committed in between past the check.
        _ = await store.TransitionRunAsync(new TransitionGraphWorkflowRunCommand
        {
            RunId = run.Id,
            ExpectedVersion = run.Version,
            TargetStatus = GraphWorkflowRunStatus.Failed,
            FailureClass = GraphWorkflowFailureClass.ValidationFailed,
            SanitizedReason = exception.Message
        },
                           cancellationToken);
        Forget(run.Id);
        return 1;
    }

    /// <summary>Fails one node run, with the output document that failure produces.</summary>
    /// <remarks>
    ///     The document is composed through the single writer like every other, and dropped if the cap refuses it: a
    ///     failure cannot fail for being too large to describe, and the row's own reason carries the account either
    ///     way. <paramref name="node" /> is null only for a key the pinned graph no longer declares. A <c>Pending</c>
    ///     row is walked through <c>Running</c> first, because the state machine deliberately has no
    ///     <c>Pending → Failed</c> edge: a failure about a row that never ran is a failure of the ATTEMPT, which <c>Running</c> is what opens.
    /// </remarks>
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
            _ = await store.TransitionNodeRunAsync(new TransitionGraphWorkflowNodeRunCommand
            {
                RunId = run.Id,
                NodeRunId = nodeRun.Id,
                ExpectedVersion = GraphWorkflowVersions.Any,
                TargetStatus = GraphWorkflowNodeRunStatus.Running
            },
                               cancellationToken);
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
        _ = await store.TransitionNodeRunAsync(new TransitionGraphWorkflowNodeRunCommand
        {
            RunId = run.Id,
            NodeRunId = nodeRun.Id,
            ExpectedVersion = GraphWorkflowVersions.Any,
            TargetStatus = GraphWorkflowNodeRunStatus.Failed,
            OutputJson = document,
            FailureClass = failureClass,
            TerminalReason = sanitizedReason
        },
                           cancellationToken);
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
    /// </summary>
    /// <remarks>
    ///     Every node-run transition bumps the run version, so the top-of-tick version is stale by the time a drain
    ///     writes — using it would make the dispatcher lose a race against itself, and re-reading narrows the window
    ///     to what the check is for. <b>Only for a write that node writes precede:</b> a run write with nothing of its
    ///     own in front of it passes the version it READ, because a fresh one would carry somebody else's cancel
    ///     across the check. The drain is the one caller left, and its move is legal from <c>Cancelling</c> alone.
    /// </remarks>
    private static async Task<long> CurrentVersionAsync(IGraphWorkflowStore store, Guid runId, CancellationToken cancellationToken) =>
        (await store.GetRunAsync(runId, cancellationToken)).Version;

    /// <summary>
    ///     Two pumps, one advance. A signal is a latency hint and a sweep is the backstop, so they are independent — and
    ///     safe to be, because <see cref="AdvanceOnceAsync" /> serializes: the single-advance invariant lives in the
    ///     gate rather than in the shape of the wait.
    /// </summary>
    private async Task PumpSignalsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var runId in _signals.Reader.ReadAllAsync(cancellationToken))
            {
                await AdvanceSafelyAsync(runId, cancellationToken);
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
            await SweepAsync(cancellationToken);
            while (await sweep.WaitForNextTickAsync(cancellationToken))
            {
                await SweepAsync(cancellationToken);
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
                var runs = await store.ListRunsAsync(status, SweepPageSize, cancellationToken);
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
            await AdvanceSafelyAsync(runId, cancellationToken);
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
            if (await AdvanceOnceAsync(runId, cancellationToken) > 0)
            {
                // A tick advances the graph by one layer, so a productive one almost always leaves more to do. Without
                // this every hop would wait for the next sweep and a five-node run would take five intervals.
                Signal(runId);
            }
        }
        catch (GraphWorkflowInvalidTransitionException exception)
        {
            // Usually somebody else moved the run between this tick's read and its write. Their write stands and the next tick re-derives. Deliberately NOT
            // re-signalled: where Dev Workflows has a separate concurrency exception, a stale version and an illegal move share ONE type here, so it would spin a BUG.
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
    private sealed record RetryDetail
    {
        public required string FailureClass { get; init; }

        public required int Attempt { get; init; }

        public required string? Reason { get; init; }
    }
}
