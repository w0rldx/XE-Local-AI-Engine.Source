namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The workflow runtime's one loop. It advances a persisted run by transitioning persisted node runs, holding no
///     authoritative state of its own — the parsed-graph cache is a cost optimisation and nothing else, which is exactly
///     why a restart costs at most the work in flight.
///     <para>
///         <b>Every node-run status write happens inside a serialized <see cref="AdvanceOnceAsync" /> call.</b> That is
///         the invariant the whole design rests on: lane work, when it exists, only produces a pollable result and never
///         transitions a row itself, so the only other writer to a run is the human-decision path — which is what the
///         store's <c>Any</c> version sentinel exists for.
///     </para>
///     <para>
///         Advancement is a pure database decision and takes microseconds, so one loop for every run is enough and gives
///         one place where graph invariants are decided. Seam if the run count ever justifies it: partition by run id.
///         Nothing here assumes it is alone.
///     </para>
/// </summary>
internal sealed class DevWorkflowDispatcher : IDevWorkflowDispatcherSignal, IHostedService, IAsyncDisposable
{
    /// <summary>The statuses a sweep looks at. Paused and the three terminals are not advanced by a tick.</summary>
    private static readonly DevWorkflowRunStatus[] LiveRunStatuses =
    [
        DevWorkflowRunStatus.Pending,
        DevWorkflowRunStatus.Running,
        DevWorkflowRunStatus.WaitingForApproval,
        DevWorkflowRunStatus.Pausing,
        DevWorkflowRunStatus.Cancelling
    ];

    /// <summary>The statuses that mean the dispatcher has work in hand for a run, and so count against the run cap.</summary>
    private static readonly DevWorkflowRunStatus[] ActiveRunStatuses =
    [
        DevWorkflowRunStatus.Running,
        DevWorkflowRunStatus.Pausing,
        DevWorkflowRunStatus.Cancelling
    ];

    /// <summary>
    ///     How many runs of one status a sweep pages in. Generous rather than tuned: a node that somehow held more live
    ///     runs than this has a bigger problem than sweep latency, and every real deployment is far below it.
    /// </summary>
    private const int SweepPageSize = 500;

    /// <summary>
    ///     How much of an operator's decision comment reaches the node run's <c>terminal_reason</c>. The column holds
    ///     1024 and the comment is free text a person typed, so it is cut here rather than at the store's rejection —
    ///     a decision that could not be applied because someone was verbose is the wrong way for a run to stop.
    /// </summary>
    private const int MaxDecisionComment = 500;

    /// <summary>camelCase, matching every other document this product puts on a wire.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Written by the gate itself, so an upstream document carrying one of these names does not shadow it.</summary>
    private static readonly HashSet<string> ReservedGateProperties = new(StringComparer.Ordinal)
    {
        "status",
        "attempt",
        "branch",
        "failureClass"
    };

    /// <summary>Bounded and drop-on-full: a signal is a latency hint, and blocking a committing caller to deliver one would be the wrong trade.</summary>
    private readonly Channel<Guid> _signals = Channel.CreateBounded<Guid>(new BoundedChannelOptions(capacity: 256)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true
    });

    private readonly SemaphoreSlim _advanceGate = new(initialCount: 1, maxCount: 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly DevWorkflowGraphCache _graphs;
    private readonly ILogger<DevWorkflowDispatcher> _logger;
    private readonly DevWorkflowMaterializer _materializer;
    private readonly DevWorkflowOptions _options;
    private readonly DevWorkflowRetryPolicy _retries;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly DevWorkflowToolExecutor _tools;
    private int _disposed;
    private Task? _loop;

    public DevWorkflowDispatcher(IServiceScopeFactory scopeFactory,
        DevWorkflowGraphCache graphs,
        DevWorkflowToolExecutor tools,
        DevWorkflowRetryPolicy retries,
        DevWorkflowMaterializer materializer,
        IOptions<DevWorkflowOptions> options,
        TimeProvider timeProvider,
        ILogger<DevWorkflowDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _graphs = graphs ?? throw new ArgumentNullException(nameof(graphs));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _retries = retries ?? throw new ArgumentNullException(nameof(retries));
        _materializer = materializer ?? throw new ArgumentNullException(nameof(materializer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options.Value;
    }

    public void Signal(Guid runId) =>
        _ = _signals.Writer.TryWrite(runId);

    /// <summary>What the signal pump is about to read. The only way to assert that a productive tick re-signals.</summary>
    internal ChannelReader<Guid> PendingSignals => _signals.Reader;

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
            return await AdvanceCoreAsync(scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>(),
                    new DevWorkflowLanes(scope.ServiceProvider.GetRequiredService<DevWorkflowAgentExecutor>(),
                        scope.ServiceProvider.GetRequiredService<DevWorkflowDevTaskExecutor>()),
                    runId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _ = _advanceGate.Release();
        }
    }

    private async Task<int> AdvanceCoreAsync(IDevWorkflowStore store, DevWorkflowLanes lanes, Guid runId, CancellationToken cancellationToken)
    {
        var run = await store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (DevWorkflowStateMachine.IsTerminal(run.Status))
        {
            Forget(runId);
            return 0;
        }

        if (run.Status == DevWorkflowRunStatus.Paused)
        {
            // A decision recorded while the run was paused is not lost, only deferred: it is a durable row, and the
            // first tick after a resume settles it.
            return 0;
        }

        if (run.Status == DevWorkflowRunStatus.Pending)
        {
            return await StartPendingRunAsync(store, run, cancellationToken).ConfigureAwait(false);
        }

        DevWorkflowGraph graph;
        try
        {
            graph = _graphs.Resolve(run);
        }
        catch (DevWorkflowValidationException exception)
        {
            // A running run's graph parsed once already, so reaching here means the pinned blob changed underneath it.
            // Throwing would re-throw on every sweep forever; the run is unroutable and says so instead.
            return await FailUnroutableAsync(store, run, exception, cancellationToken).ConfigureAwait(false);
        }

        // Settle what the lanes have landed FIRST, before anything reads the node runs: a session that finished between
        // ticks has to be seen as finished, or the run would judge its whole graph against a row that is only still
        // Running because nothing asked.
        var written = await PollAsync(store, lanes, graph, run, cancellationToken).ConfigureAwait(false);
        var nodeRuns = await store.ListNodeRunsAsync(runId, cancellationToken).ConfigureAwait(false);

        // Settle what has landed. A recorded decision is the durable half of a human act; turning it into a transition
        // is this step's job, and doing it here rather than at admission is what lets a decision taken during a pause
        // apply on the first tick after the resume.
        var (settledCount, gateRejection) = await SettleDecisionsAsync(store, run, graph, nodeRuns, cancellationToken).ConfigureAwait(false);
        written += settledCount;

        // Only an in-flight cancel supersedes it. A PAUSING run must still take this branch: the gate is already
        // Succeeded by the time the pause settles, so nothing would ever re-detect the rejection and the run would
        // resume and complete — the exact lie the rule exists to prevent.
        if (gateRejection is { } rejection && run.Status != DevWorkflowRunStatus.Cancelling)
        {
            // A gate answered in a way no out-edge accepts ends the run — reading it as Completed (every downstream
            // skipped) or as Failed (nothing failed) would both lie. It goes through the drain like every other
            // terminal, so live siblings settle and release what they hold instead of being orphaned.
            DevWorkflowStateMachine.EnsureLegal(run.Status, DevWorkflowRunStatus.Cancelling);
            _ = await store.TransitionRunAsync(new TransitionDevWorkflowRunCommand(run.Id,
                                   await CurrentVersionAsync(store, run.Id, cancellationToken).ConfigureAwait(false),
                                   DevWorkflowRunStatus.Cancelling,
                                   FailureClass: DevWorkflowFailureClasses.GateRejected,
                                   SanitizedReason: rejection),
                               cancellationToken)
                           .ConfigureAwait(false);
            return written + 1;
        }

        if (run.Status is DevWorkflowRunStatus.Pausing or DevWorkflowRunStatus.Cancelling)
        {
            written += await DrainAsync(store, lanes, run, cancellationToken).ConfigureAwait(false);
            return written;
        }

        // A decomposition that has settled grows the graph, and the tick ENDS there (§5.3): everything below judges
        // node runs against a parsed graph, and this call has just replaced the one this tick parsed. The next tick
        // re-parses on the bumped revision and admits what the expansion created — which is what the parse-count
        // assertion pins, because the failure mode is silent rather than loud.
        // The rows this tick already read, re-read ONLY if a decision moved one: the decomposition it is looking for
        // has to be Succeeded, and a tick that settled nothing cannot have changed which rows are.
        var materialized = await _materializer.MaterializeAsync(store,
                                                  graph,
                                                  run,
                                                  settledCount > 0 ? await store.ListNodeRunsAsync(run.Id, cancellationToken).ConfigureAwait(false) : nodeRuns,
                                                  cancellationToken)
                                              .ConfigureAwait(false);
        if (materialized > 0)
        {
            return written + materialized;
        }

        written += await AdmitAsync(store, lanes, run, graph, cancellationToken).ConfigureAwait(false);
        written += await RecomputeRunStatusAsync(store, run, graph, cancellationToken).ConfigureAwait(false);
        return written;
    }

    /// <summary>
    ///     Asks every lane-owned node run what became of the work it was driving, and settles the ones that landed.
    ///     <para>
    ///         Deliberately the executor's answer rather than this loop's memory: the dispatcher holds nothing about a
    ///         run between ticks, so a restart loses nothing a poll cannot re-read. It runs in every non-terminal status
    ///         including the two drains — a session asked to stop settles here, which is how the drain learns it may
    ///         finish.
    ///     </para>
    /// </summary>
    private async Task<int> PollAsync(IDevWorkflowStore store,
        DevWorkflowLanes lanes,
        DevWorkflowGraph graph,
        DevWorkflowRunSnapshot run,
        CancellationToken cancellationToken)
    {
        var nodeRuns = await store.ListNodeRunsAsync(run.Id, cancellationToken).ConfigureAwait(false);

        // Before anything is read off the lane: a fix loop can re-attempt a row the lane is driving without going
        // through the lane, and a pass belonging to the attempt before is not an answer about the one the row is on now.
        await _tools.ForgetSupersededAsync(nodeRuns).ConfigureAwait(false);

        // A Tool row still reading Queued is polled too, when the lane is in fact already driving it: the Running write
        // can fail after the slot and the registry entry were taken, and outside a drain the next admission repairs
        // that — but a drain admits nothing, so without this the run waits on a row nothing would ever move again.
        var running = nodeRuns.Where(nodeRun => (nodeRun.Status == DevWorkflowNodeRunStatus.Running
                                                 && nodeRun.NodeType is DevWorkflowNodeType.Agent
                                                     or DevWorkflowNodeType.Tool
                                                     or DevWorkflowNodeType.DevTask)
                                                || (nodeRun.Status == DevWorkflowNodeRunStatus.Queued
                                                    && nodeRun.NodeType == DevWorkflowNodeType.Tool
                                                    && _tools.IsInFlight(nodeRun.Id)))
                              .ToList();

        var written = 0;
        foreach (var candidate in running)
        {
            // One poll can move rows that are not its own: a failure routed to an upstream node resets that node's whole
            // subtree, and the rest of this list is then a picture of a graph that has changed underneath it. So once
            // anything has been written the rows are re-read, and one this lane no longer owns is left alone — settling
            // it would write an answer about the round the run has just decided to do again.
            if (written > 0)
            {
                nodeRuns = await store.ListNodeRunsAsync(run.Id, cancellationToken).ConfigureAwait(false);
            }

            if (nodeRuns.FirstOrDefault(nodeRun => nodeRun.Id == candidate.Id) is not { Status: DevWorkflowNodeRunStatus.Running or DevWorkflowNodeRunStatus.Queued } current)
            {
                continue;
            }

            var polled = current.NodeType switch
            {
                DevWorkflowNodeType.Agent => await lanes.Agent.PollAsync(store, graph, run, current, nodeRuns, cancellationToken).ConfigureAwait(false),
                DevWorkflowNodeType.DevTask => await lanes.DevTasks.PollAsync(store, graph, run, current, nodeRuns, cancellationToken).ConfigureAwait(false),
                _ => await _tools.PollAsync(store, graph, run, current, nodeRuns, cancellationToken).ConfigureAwait(false)
            };

            // Only a row its lane had nothing to say about. A pass that landed inside its budget is settled off what it
            // actually came to — including a sandbox timeout, which arrives with the evidence gathered before the clock
            // ran out — and expiring it here as well would overwrite that answer with a coarser one.
            written += polled > 0
                ? polled
                : await ExpireAsync(store, lanes, graph, run, current, nodeRuns, cancellationToken).ConfigureAwait(false);
        }

        return written;
    }

    /// <summary>
    ///     Ends a node run that has been running longer than its node allows, and answers how many transitions it wrote.
    ///     <para>
    ///         The deadline is re-derived from the row every tick rather than armed once in memory, so it survives the
    ///         restart that would otherwise leave a node run bounded by nothing. Where the expiry LEADS — another
    ///         attempt, the node that produced what this one was judging, or a human — is the retry policy's answer, the
    ///         same as for every other retryable failure class.
    ///     </para>
    /// </summary>
    private async Task<int> ExpireAsync(IDevWorkflowStore store,
        DevWorkflowLanes lanes,
        DevWorkflowGraph graph,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        CancellationToken cancellationToken)
    {
        if (!graph.Nodes.TryGetValue(nodeRun.NodeKey, out var node) || !DevWorkflowDeadline.HasExpired(node, nodeRun, _timeProvider))
        {
            return 0;
        }

        // Dropped BEFORE the row is settled, and dropped rather than merely stopped: a re-attempt lands the row on a new
        // attempt inside this same call, and this tick's admission would then find the registry still holding the pass
        // that ran out of time — leaving the fresh attempt's pass running with nothing to poll it.
        if (nodeRun.NodeType == DevWorkflowNodeType.Tool)
        {
            await _tools.DiscardAsync(nodeRun.Id).ConfigureAwait(false);
        }
        else if (nodeRun.NodeType == DevWorkflowNodeType.DevTask)
        {
            _ = await lanes.DevTasks.StopAttemptAsync(nodeRun, cancel: true, cancellationToken).ConfigureAwait(false);
        }
        else if (nodeRun is { NodeType: DevWorkflowNodeType.Agent, WorkSessionId: { } sessionId })
        {
            await lanes.Agent.StopAsync(sessionId, cancel: true, cancellationToken).ConfigureAwait(false);
        }

        return await _retries.SettleFailureAsync(store,
                                 graph,
                                 run,
                                 nodeRun,
                                 nodeRuns,
                                 new DevWorkflowFailure(DevWorkflowFailureClasses.Timeout,
                                     $"This node run did not finish within the {node.NodeTimeoutSeconds} seconds its node allows.",
                                     JsonSerializer.Serialize(new TimedOutOutput(DevWorkflowNodeOutputStatuses.Failed, nodeRun.Attempt, DevWorkflowFailureClasses.Timeout),
                                         JsonOptions),
                                     DevWorkflowOutcomes.Timeout),
                                 cancellationToken)
                             .ConfigureAwait(false);
    }

    /// <summary>
    ///     Starts a <c>Pending</c> run, or fails it for good if its pinned graph cannot be routed.
    ///     <para>
    ///         The graph is validated again here rather than trusted from the definition's save, because an agent
    ///         definition can be deleted in between. A run left <c>Pending</c> on a graph nothing can route would be
    ///         swept forever, so the refusal is written down rather than retried.
    ///     </para>
    /// </summary>
    private async Task<int> StartPendingRunAsync(IDevWorkflowStore store, DevWorkflowRunSnapshot run, CancellationToken cancellationToken)
    {
        try
        {
            // The graph is still resolved first: a run whose pinned blob cannot be routed has to fail rather than
            // queue behind runs that can.
            _ = _graphs.Resolve(run);
            return await StartRunAsync(store, run, _options.MaxConcurrentRuns, cancellationToken).ConfigureAwait(false);
        }
        catch (DevWorkflowValidationException exception)
        {
            return await FailUnroutableAsync(store, run, exception, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     The run's version as of right now, for a run-level write that follows this tick's own node-run writes.
    ///     <para>
    ///         Every node-run transition bumps the run version, so the top-of-tick version is stale by the time a drain
    ///         or a recomputation writes — using it would make the dispatcher lose a race against itself. Re-reading
    ///         narrows the window to what the check is actually for: a human decision or a lifecycle command landing
    ///         between the read and the write, which must win rather than be overwritten by a status move.
    ///     </para>
    /// </summary>
    private static async Task<long> CurrentVersionAsync(IDevWorkflowStore store, Guid runId, CancellationToken cancellationToken) =>
        (await store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false)).Version;

    /// <summary>Writes the refusal down. A run nothing can route must not be retried forever by the sweep.</summary>
    private async Task<int> FailUnroutableAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        DevWorkflowValidationException exception,
        CancellationToken cancellationToken)
    {
        if (!DevWorkflowStateMachine.IsLegal(run.Status, DevWorkflowRunStatus.Failed))
        {
            // Already draining: the drain reaches its own terminal without the graph, so there is nothing to write.
            _logger.LogError(exception, "Development workflow run {RunId} has an unroutable graph while {Status}.", run.Id, run.Status);
            return 0;
        }

        _ = await store.TransitionRunAsync(new TransitionDevWorkflowRunCommand(run.Id,
                               await CurrentVersionAsync(store, run.Id, cancellationToken).ConfigureAwait(false),
                               DevWorkflowRunStatus.Failed,
                               FailureClass: DevWorkflowFailureClasses.Configuration,
                               SanitizedReason: exception.Message,
                               WorkItemStatus: DevWorkflowWorkItemStatus.Blocked),
                           cancellationToken)
                       .ConfigureAwait(false);
        Forget(run.Id);
        return 1;
    }

    /// <summary>
    ///     Drops everything the runtime holds in memory about a run that has ended: its parsed graph, and any re-attempt
    ///     it had promised itself but will now never ask for. Both are caches over durable rows, so a run that turns out
    ///     to be live again simply re-derives them.
    /// </summary>
    private void Forget(Guid runId)
    {
        _graphs.Forget(runId);
        _retries.Forget(runId);
    }

    /// <summary>
    ///     Moves a validated <c>Pending</c> run to <c>Running</c>, if the node has room to drive another one.
    ///     <para>
    ///         The run's node runs already exist: they are written in the same transaction as the run row, so a run can
    ///         no longer be found without them. Materializing here as well would re-derive seeds whose per-run inputs
    ///         only the starting caller ever held. (A run with no node runs is therefore unreachable from any runtime
    ///         path; the recomputation's no-rows guard stays as the belt that keeps such a row from reading Completed.)
    ///     </para>
    /// </summary>
    private static async Task<int> StartRunAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        int maxConcurrentRuns,
        CancellationToken cancellationToken)
    {
        if (await CountActiveRunsAsync(store, maxConcurrentRuns, cancellationToken).ConfigureAwait(false) >= maxConcurrentRuns)
        {
            // Not refused — waiting. The run keeps its rows and its place, and the next sweep offers it again; refusing
            // it would push a queue the node is perfectly able to work through back onto the person who started it.
            return 0;
        }

        DevWorkflowStateMachine.EnsureLegal(run.Status, DevWorkflowRunStatus.Running);
        _ = await store.TransitionRunAsync(new TransitionDevWorkflowRunCommand(run.Id,
                               await CurrentVersionAsync(store, run.Id, cancellationToken).ConfigureAwait(false),
                               DevWorkflowRunStatus.Running,
                               WorkItemStatus: DevWorkflowWorkItemStatus.Active),
                           cancellationToken)
                       .ConfigureAwait(false);
        return 1;
    }

    /// <summary>
    ///     How many runs this node is actually driving.
    ///     <para>
    ///         <c>Running</c> and the two drains, and deliberately nothing else. A <c>Paused</c> run is not being
    ///         advanced and a <c>WaitingForApproval</c> one is waiting on a person who may take days — counting either
    ///         would let one unanswered gate stop every other run on the node, which is the cap protecting nothing at
    ///         the cost of the throughput it exists to manage.
    ///     </para>
    ///     <para>
    ///         Read as summaries, not snapshots: a count must not decrypt a graph blob per live run. Each status is
    ///         asked for one row more than the cap, which is all the answer needs.
    ///     </para>
    /// </summary>
    private static async Task<int> CountActiveRunsAsync(IDevWorkflowStore store, int maxConcurrentRuns, CancellationToken cancellationToken)
    {
        var active = 0;
        foreach (var status in ActiveRunStatuses)
        {
            active += (await store.ListRunSummariesAsync(workItemId: null, status, maxConcurrentRuns + 1, cancellationToken).ConfigureAwait(false)).Count;
        }

        return active;
    }

    /// <summary>
    ///     Turns recorded decisions into transitions. A gate's answer succeeds it and lets the edges route; the
    ///     retries-exhausted interventions re-attempt, route around, or give up.
    ///     <para>
    ///         Answers with the reason the run should end, when a gate was answered in a way none of its out-edges
    ///         accepts. Deliberately not written here: it is the RUN's transition, and this method only moves node runs.
    ///     </para>
    /// </summary>
    private static async Task<(int Written, string? GateRejection)> SettleDecisionsAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        DevWorkflowGraph graph,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        CancellationToken cancellationToken)
    {
        var waiting = nodeRuns.Where(static nodeRun => nodeRun.Status is DevWorkflowNodeRunStatus.WaitingForApproval or DevWorkflowNodeRunStatus.Blocked)
                              .ToList();
        if (waiting.Count == 0)
        {
            return (0, null);
        }

        var decisions = await store.ListDecisionsAsync(run.Id, cancellationToken).ConfigureAwait(false);

        // Carried forward across the loop: two answered node runs in one tick each decide where the work item lands,
        // and the second must judge that against the first's move rather than against the tick's opening picture.
        var settledSoFar = nodeRuns.ToList();
        var written = 0;
        string? rejection = null;
        foreach (var nodeRun in waiting)
        {
            // One decision per node-run ATTEMPT, so the attempt is what makes this the decision for the current try
            // rather than one an earlier attempt already consumed.
            if (decisions.LastOrDefault(decision => decision.NodeRunId == nodeRun.Id && decision.Attempt == nodeRun.Attempt) is not { } settled)
            {
                continue;
            }

            var (target, outcome, incrementAttempt) = Resolve(settled.Decision);
            var outputJson = target == DevWorkflowNodeRunStatus.Succeeded ? Output(settled.Decision) : null;

            // A decision the node run's status forbids — an Approve recorded against a Blocked row, say — is a durable
            // row re-read on every tick. Left to throw it would wedge the whole run, siblings included, so it is
            // recorded against its own node run and the tick carries on.
            if (!DevWorkflowStateMachine.IsLegal(nodeRun.Status, target))
            {
                var reason = $"A recorded {settled.Decision} decision cannot be applied to a node run that is {nodeRun.Status}.";
                if (DevWorkflowStateMachine.IsLegal(nodeRun.Status, DevWorkflowNodeRunStatus.Blocked))
                {
                    written += await BlockAsync(store, run, nodeRun, reason, DevWorkflowFailureClasses.Configuration, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // Already Blocked: there is no status left to move it to, so only the note is new. Keyed by operation
                // id, which the store resolves query-first — so it is written once and not on every tick thereafter.
                _ = await store.AppendEventAsync(new AppendDevWorkflowEventCommand(run.Id,
                                       DevWorkflowVersions.Any,
                                       DevWorkflowEventTypes.NodeInterventionRequired,
                                       nodeRun.Id,
                                       DevWorkflowOperationId.For(run.Id, nodeRun.NodeKey, nodeRun.Attempt, "decision-not-applicable"),
                                       DetailJson: JsonSerializer.Serialize(new ReasonDetail(reason), JsonOptions)),
                                   cancellationToken)
                               .ConfigureAwait(false);
                continue;
            }

            // What the operator SAID travels with the attempt their decision starts, not only into the decision row a
            // panel lists. Merged into the node run's inputs exactly as a routed failure is, because that document is
            // where both lanes already read the next attempt's brief from — the agent objective composes it, and the
            // DevTask lane turns it into the coder's change request. Bounded like every other decision comment: the
            // text is free-form and a prompt is the wrong place to discover that.
            // EVERY Retry goes through the merge, including one typed with nothing in the box: the merge is also what
            // DROPS an earlier retry's reason, and a silent Retry that skipped it would leave the previous operator's
            // sentence on the row for a try they said nothing about.
            Action<Utf8JsonWriter>? writeRetryReason = settled.Comment?.Trim() is { Length: > 0 } retried
                ? writer =>
                {
                    writer.WriteString(DevWorkflowNodeInputs.OperatorRetryReason, DevWorkflowStateMachine.Bounded(retried, MaxDecisionComment));

                    // The attempt this reason is FOR. Without it the members would be read again by every later
                    // automatic re-attempt, quoting a person who said nothing about that try.
                    writer.WriteNumber(DevWorkflowNodeInputs.OperatorRetryAttempt, nodeRun.Attempt + 1);
                }
                : null;
            var retryInput = incrementAttempt ? DevWorkflowNodeInputs.Merge(nodeRun.InputJson, writeRetryReason) : null;

            _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(run.Id,
                                   nodeRun.Id,
                                   DevWorkflowVersions.Any,
                                   target,
                                   OutputJson: outputJson,
                                   InputJson: retryInput,
                                   FailureClass: target == DevWorkflowNodeRunStatus.Failed ? DevWorkflowFailureClasses.GateRejected : null,
                                   TerminalReason: DecidedReason(target, settled.Comment),
                                   IncrementAttempt: incrementAttempt,

                                   // EVERY Retry widens the cap by one, not only a Retry at the cap — that is the
                                   // ruling as written, and the simpler rule to explain to the person clicking it.
                                   // So a Retry at attempt 1 of 3 leaves a node that can now reach 4 on its own, and
                                   // that fourth try is an ORDINARY automatic re-attempt: the operator's reason is
                                   // scoped to the one attempt their decision started, so the attempt they bought
                                   // carries it and nothing after it does. What still bounds all of this is the
                                   // run-wide MaxTotalAttempts budget, which counts an operator's re-attempt and an
                                   // automatic one alike and which no widening touches. Nothing else sets this flag.
                                   WidenMaxAttempts: incrementAttempt,

                                   // A retry gets a NEW session: resuming the one that just failed resumes the context
                                   // that failed with it. Releasing it here is also what stops the fresh attempt being
                                   // settled straight back off the old session's answer.
                                   ClearWorkSession: incrementAttempt,
                                   Outcome: outcome,

                                   // An answered node run may be the last thing the work item was blocked on, and the run
                                   // status often does not move when it settles — so the release travels with the answer,
                                   // for the same reason blocking it does.
                                   WorkItemStatus: DevWorkflowStateMachine.WorkItemStatusAfter(run.Status, settledSoFar, nodeRun.Id, target)),
                               cancellationToken)
                           .ConfigureAwait(false);
            settledSoFar =
            [
                .. settledSoFar.Select(entry => entry.Id == nodeRun.Id
                    ? entry with
                    {
                        Status = target
                    }
                    : entry)
            ];
            written++;

            // Only a human gate can strand a run this way. Every other node's dead out-edges skip their targets, which
            // is a route rather than a dead end; a gate answer nothing accepts leaves the run with nowhere to go, and
            // saying so is more honest than completing a run whose approval was refused.
            //
            // A gate with NO out-edges counts, and that case is the seeded "Research → Plan → Approval" shape rather
            // than a corner: rejecting its terminal approval must not read as the run having succeeded. Approve is
            // exempt ONLY there — at a gate that HAS branches, an Approve none of them accepts is as stranded as any
            // other answer, and completing it through skipped downstream would be the same lie in the other direction.
            if (outputJson is not null
                && nodeRun.NodeType == DevWorkflowNodeType.HumanGate
                && (settled.Decision != DevWorkflowDecisionKind.Approve || graph.OutboundEdges(nodeRun.NodeKey).Count > 0)
                && !graph.OutboundEdges(nodeRun.NodeKey).Any(edge => DevWorkflowStateMachine.GateEdgeFires(edge, settled.Decision)))
            {
                rejection ??= $"The gate '{nodeRun.NodeKey}' was answered {settled.Decision}, which none of its branches accepts.";
            }
        }

        return (written, rejection);

        static (DevWorkflowNodeRunStatus Target, string? Outcome, bool IncrementAttempt) Resolve(DevWorkflowDecisionKind decision) =>
            (DevWorkflowStateMachine.TargetFor(decision),
                decision switch
                {
                    DevWorkflowDecisionKind.Reject => DevWorkflowOutcomes.Rejected,
                    DevWorkflowDecisionKind.RequestChanges => DevWorkflowOutcomes.ChangesRequested,
                    DevWorkflowDecisionKind.Approve => DevWorkflowOutcomes.Succeeded,
                    DevWorkflowDecisionKind.Abandon => DevWorkflowOutcomes.Failed,
                    _ => null
                },
                decision == DevWorkflowDecisionKind.Retry);

        // The gate's output shape lives with the state machine, because the API answers "does a rejection route
        // anywhere" by evaluating these same edges against this same document before the operator clicks.
        static string Output(DevWorkflowDecisionKind decision) =>
            DevWorkflowStateMachine.GateOutputJson(decision);

        // What a person's decision leaves on the row. A Skip used to leave nothing, and it is the one terminal that
        // most needs a reason: an All join now carries on past a skipped leaf, so the node downstream is handed the
        // skip as evidence and has only this string to say WHY the work it was expecting is not there. The operator's
        // own words are the whole of that why, so they travel — bounded, because the column is 1024 and a comment is
        // free text an operator typed.
        static string? DecidedReason(DevWorkflowNodeRunStatus target, string? comment) =>
            target switch
            {
                DevWorkflowNodeRunStatus.Failed => "A human abandoned this node run.",
                DevWorkflowNodeRunStatus.Skipped when comment?.Trim() is { Length: > 0 } said =>
                    $"Skipped by an operator: {DevWorkflowStateMachine.Bounded(said, MaxDecisionComment)}",
                DevWorkflowNodeRunStatus.Skipped => "Skipped by an operator.",
                _ => null
            };
    }

    /// <summary>
    ///     Completes a <c>Pausing</c> or <c>Cancelling</c> transition once nothing is live any more, and admits nothing
    ///     while it drains.
    ///     <para>
    ///         Every terminal is reached this way or through the "nothing is live" recomputation — there is no path that
    ///         writes one directly, because doing so would strand the run's live node runs under a run no tick ever
    ///         looks at again.
    ///     </para>
    /// </summary>
    private async Task<int> DrainAsync(IDevWorkflowStore store, DevWorkflowLanes lanes, DevWorkflowRunSnapshot run, CancellationToken cancellationToken)
    {
        var nodeRuns = await store.ListNodeRunsAsync(run.Id, cancellationToken).ConfigureAwait(false);
        var written = 0;

        foreach (var nodeRun in nodeRuns.Where(static nodeRun => DevWorkflowStateMachine.IsLive(nodeRun.Status)))
        {
            // ASK, do not settle. A node run that is Running belongs to an executor, and only the executor knows what
            // stopping it costs — so the drain requests the stop and the next tick's poll writes the terminal off what
            // actually happened. Rows no lane owns are settled here, because for them there is nothing to ask.
            written += await StopAsync(store, lanes, run, nodeRun, cancellationToken).ConfigureAwait(false);
        }

        // Re-read: the stops above may have settled every row already, and judging "is anything still live" off the
        // snapshot taken before them would cost a whole extra tick for a drain that is in fact finished.
        nodeRuns = await store.ListNodeRunsAsync(run.Id, cancellationToken).ConfigureAwait(false);
        if (nodeRuns.Any(static nodeRun => nodeRun.Status is DevWorkflowNodeRunStatus.Queued or DevWorkflowNodeRunStatus.Running))
        {
            // Still settling — an executor was asked to stop and has not answered yet. The command already committed
            // its intent, so the UI can say "cancelling" honestly rather than claiming one that has not landed.
            return written;
        }

        var settledStatus = run.Status == DevWorkflowRunStatus.Pausing ? DevWorkflowRunStatus.Paused : DevWorkflowRunStatus.Cancelled;
        DevWorkflowStateMachine.EnsureLegal(run.Status, settledStatus);
        _ = await store.TransitionRunAsync(new TransitionDevWorkflowRunCommand(run.Id,
                               await CurrentVersionAsync(store, run.Id, cancellationToken).ConfigureAwait(false),
                               settledStatus,
                               WorkItemStatus: DevWorkflowStateMachine.WorkItemStatusFor(settledStatus, nodeRuns)),
                           cancellationToken)
                       .ConfigureAwait(false);

        // A PAUSED run keeps its promised re-attempts: it is coming back, and a resume that skipped every cushion a
        // definition asked for would be the pause spending them.
        if (DevWorkflowStateMachine.IsTerminal(settledStatus))
        {
            _retries.Forget(run.Id);
        }

        _graphs.Forget(run.Id);
        return written + 1;
    }

    /// <summary>
    ///     Asks one live node run to stop, for whichever of the two drains is running.
    ///     <para>
    ///         Cancelling abandons the node run; pausing keeps the durable human waits and the not-yet-admitted rows
    ///         exactly where they are, because a pause is meant to be resumed. The one thing a pause does move is a
    ///         <c>Queued</c> row back to <c>Pending</c>: it is queued for a slot nothing will hand out while the run is
    ///         draining, so leaving it would pin <c>Pausing</c> for as long as the lane stayed busy. That is the same
    ///         collapse the startup reconciler performs, for the same reason.
    ///     </para>
    /// </summary>
    private async Task<int> StopAsync(IDevWorkflowStore store,
        DevWorkflowLanes lanes,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        CancellationToken cancellationToken)
    {
        if (nodeRun.NodeType == DevWorkflowNodeType.Tool && _tools.IsInFlight(nodeRun.Id))
        {
            // A pause lets a build finish. It holds no model slot, it cannot be resumed halfway, and killing it would
            // throw away minutes of work to save seconds — so the run stays Pausing until the poll settles the row,
            // which is the same "once nothing is live" rule every other drain uses.
            if (run.Status == DevWorkflowRunStatus.Pausing)
            {
                return 0;
            }

            // Asked, not settled: only the next tick's poll knows whether the commands stopped or finished inside the
            // window. Counted as work so that tick comes immediately rather than a sweep later.
            return await _tools.StopAsync(nodeRun.Id).ConfigureAwait(false) ? 1 : 0;
        }

        if (nodeRun is { Status: DevWorkflowNodeRunStatus.Running, NodeType: DevWorkflowNodeType.DevTask })
        {
            // The development chain owns what stopping ITS work costs, the same way the two other lanes do: a cancel
            // asks the attempt to stop and the next poll settles the row on what it did, and a pause leaves the attempt
            // to finish and parks the row where the resume can re-drive the task from.
            return await lanes.DevTasks.StopAsync(store, run, nodeRun, run.Status == DevWorkflowRunStatus.Cancelling, cancellationToken).ConfigureAwait(false);
        }

        var owned = nodeRun is { Status: DevWorkflowNodeRunStatus.Running, NodeType: DevWorkflowNodeType.Agent, WorkSessionId: { } };
        if (run.Status == DevWorkflowRunStatus.Pausing)
        {
            if (owned)
            {
                // The session checkpoints and parks, and the row collapses to Pending rather than to a terminal: a pause
                // is meant to be RESUMED, and a Pending row with its session still attached is exactly what the resume
                // re-admits — it finds the paused session and continues it instead of starting the work over.
                await lanes.Agent.StopAsync(nodeRun.WorkSessionId!.Value, cancel: false, cancellationToken).ConfigureAwait(false);
            }
            else if (nodeRun.Status != DevWorkflowNodeRunStatus.Queued)
            {
                return 0;
            }

            _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(run.Id,
                                   nodeRun.Id,
                                   DevWorkflowVersions.Any,
                                   DevWorkflowNodeRunStatus.Pending),
                               cancellationToken)
                           .ConfigureAwait(false);
            return 1;
        }

        if (owned)
        {
            // Asked, not settled: only the session knows whether it landed Cancelled or finished inside the window, and
            // the top of the NEXT tick polls it. Counted as work so that tick comes immediately — the drain re-signals
            // on a productive tick — rather than settling it here, which would hold the advance gate, and with it every
            // other run, for as long as the stop's grace period.
            await lanes.Agent.StopAsync(nodeRun.WorkSessionId!.Value, cancel: true, cancellationToken).ConfigureAwait(false);
            return 1;
        }

        DevWorkflowStateMachine.EnsureLegal(nodeRun.Status, DevWorkflowNodeRunStatus.Cancelled, nodeRun.NodeKey);
        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(run.Id,
                               nodeRun.Id,
                               DevWorkflowVersions.Any,
                               DevWorkflowNodeRunStatus.Cancelled,
                               FailureClass: DevWorkflowFailureClasses.Cancelled,
                               TerminalReason: "The run was cancelled."),
                           cancellationToken)
                       .ConfigureAwait(false);
        return 1;
    }

    /// <summary>
    ///     Judges every <c>Pending</c> node run against its inbound edges and runs the ones the inline lane owns.
    /// </summary>
    private async Task<int> AdmitAsync(IDevWorkflowStore store,
        DevWorkflowLanes lanes,
        DevWorkflowRunSnapshot run,
        DevWorkflowGraph graph,
        CancellationToken cancellationToken)
    {
        var nodeRuns = await store.ListNodeRunsAsync(run.Id, cancellationToken).ConfigureAwait(false);
        var byKey = nodeRuns.ToDictionary(static nodeRun => nodeRun.NodeKey, StringComparer.Ordinal);
        var written = 0;

        // Queued rows are re-offered to their lane every tick, because a slot that was held when they were queued may
        // be free now. This is what "the queue drains" means concretely: nothing hands out slots, the rows ask again.
        var admissible = nodeRuns.Where(static nodeRun => nodeRun.Status is DevWorkflowNodeRunStatus.Pending or DevWorkflowNodeRunStatus.Queued).ToList();
        foreach (var nodeRun in admissible)
        {
            if (!_retries.IsReady(nodeRun.Id))
            {
                // A re-attempt whose node asked for a pause before it tries again. The row stays Pending and says
                // nothing new — a queue reason would have to name a slot, and this is waiting on a clock.
                continue;
            }

            if (!graph.Nodes.TryGetValue(nodeRun.NodeKey, out var node))
            {
                // The run's pinned graph no longer declares this node. Nothing can route it, and nothing should guess.
                written += await BlockAsync(store,
                        run,
                        nodeRun,
                        $"The run's graph no longer declares node '{nodeRun.NodeKey}'.",
                        DevWorkflowFailureClasses.Configuration,
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (nodeRun.Status == DevWorkflowNodeRunStatus.Queued)
            {
                // Already judged eligible when it was queued; only the slot was missing. Re-judging its edges would be
                // asking a question whose answer cannot have changed — nothing un-succeeds.
                written += await DispatchAsync(store, lanes, run, graph, node, nodeRun, nodeRuns, byKey, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var admission = DevWorkflowStateMachine.Admission(node, graph, byKey);
            if (admission == DevWorkflowNodeAdmission.Wait)
            {
                continue;
            }

            if (admission == DevWorkflowNodeAdmission.Skip)
            {
                // Named, not bare. A cascade writes as many Skipped rows as it reaches, and without the cause on each
                // one an operator reading the tail cannot tell which row was the decision and which followed it.
                _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(run.Id,
                                       nodeRun.Id,
                                       DevWorkflowVersions.Any,
                                       DevWorkflowNodeRunStatus.Skipped,
                                       TerminalReason: DevWorkflowStateMachine.SkipReason(node, graph, byKey)),
                                   cancellationToken)
                               .ConfigureAwait(false);
                written++;
                continue;
            }

            written += await DispatchAsync(store, lanes, run, graph, node, nodeRun, nodeRuns, byKey, cancellationToken).ConfigureAwait(false);
        }

        return written;
    }

    /// <summary>
    ///     Queues an eligible node run and, for the four node types the inline lane owns, runs it in the same tick.
    ///     <para>
    ///         An inline node goes <c>Pending</c> → <c>Running</c> → <c>Succeeded</c>, skipping <c>Queued</c> — see the
    ///         remark at the inline write below. It still costs two event rows, and that is what makes the timing of a
    ///         fan-out visible, which is the only reason Parallel and Join exist as node types at all.
    ///     </para>
    ///     <para>
    ///         <b>Seam:</b> the dev-task lane attaches here. Until it does, a node run of that type is blocked for a
    ///         human rather than left queued forever — a queue nothing drains is the one answer that would look like
    ///         progress.
    ///     </para>
    /// </summary>
    private async Task<int> DispatchAsync(IDevWorkflowStore store,
        DevWorkflowLanes lanes,
        DevWorkflowRunSnapshot run,
        DevWorkflowGraph graph,
        DevWorkflowGraphNode node,
        DevWorkflowNodeRunSnapshot nodeRun,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        IReadOnlyDictionary<string, DevWorkflowNodeRunSnapshot> byKey,
        CancellationToken cancellationToken)
    {
        if (node.NodeType == DevWorkflowNodeType.Agent)
        {
            return await lanes.Agent.DispatchAsync(store, graph, run, node, nodeRun, nodeRuns, cancellationToken).ConfigureAwait(false);
        }

        if (node.NodeType == DevWorkflowNodeType.Tool)
        {
            if (node.ToolMode == DevWorkflowToolMode.Apply && !AValidationSucceededOnThePathTaken(graph, node, byKey))
            {
                // GRAPH-C4-3's runtime half, and it goes BEFORE the consumption record below: a blocked apply must not
                // first record that it consumed inputs it never read. Policy rather than a failure — nothing broke,
                // and the answer is a person's.
                return await BlockAsync(store,
                        run,
                        nodeRun,
                        $"Node '{node.NodeKey}' applies approved patches, and no validation node succeeded on the path this run took. "
                        + "Nothing has judged what is about to be applied (invariant GRAPH-C4-3).",
                        DevWorkflowFailureClasses.Policy,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (nodeRun.Status == DevWorkflowNodeRunStatus.Pending)
            {
                // These commands judge what the steps before them produced, so a later version of any of it makes this
                // node run's report describe something that no longer exists. Recorded here because the lane cannot:
                // it reads its inputs through a prepared workspace and through Dev Mode, neither of which the store can
                // see, so without this a Tool node consumes everything and records nothing — and the whole "stale
                // because" link is dead on every graph whose fix loop reaches past a check.
                //
                // Once per attempt, on the first tick that admits it: a re-attempt is a new use of whatever version is
                // current, and the tick that only finds the lane full must not record a second.
                _ = await DevWorkflowUpstreamArtifacts.RecordAsync(store, graph, run, nodeRun, cancellationToken).ConfigureAwait(false);
            }

            return await _tools.DispatchAsync(store, run, node, nodeRun, cancellationToken).ConfigureAwait(false);
        }

        if (node.NodeType == DevWorkflowNodeType.DevTask)
        {
            return await lanes.DevTasks.DispatchAsync(store, graph, run, nodeRun, nodeRuns, cancellationToken).ConfigureAwait(false);
        }

        // No Queued hop: an inline node waits for no slot, and the three queue-reason tokens all name something real
        // to be waiting for. A Queued row with none of them would be the row lying about why it is not running.
        DevWorkflowStateMachine.EnsureLegal(nodeRun.Status, DevWorkflowNodeRunStatus.Running, nodeRun.NodeKey);
        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(run.Id,
                               nodeRun.Id,
                               DevWorkflowVersions.Any,
                               DevWorkflowNodeRunStatus.Running),
                           cancellationToken)
                       .ConfigureAwait(false);

        if (node.NodeType == DevWorkflowNodeType.HumanGate)
        {
            // The gate consumes what its predecessors produced, and recording that is what gives the approval panel its
            // evidence list. Without it the panel renders a prompt and three buttons over nothing, and the operator
            // approves a plan they cannot see — and the record is simply true, so it costs no new field.
            _ = await DevWorkflowUpstreamArtifacts.RecordAsync(store, graph, run, nodeRun, cancellationToken).ConfigureAwait(false);

            _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(run.Id,
                                   nodeRun.Id,
                                   DevWorkflowVersions.Any,
                                   DevWorkflowNodeRunStatus.WaitingForApproval,
                                   PendingDecisionKind: DevWorkflowDecisionKind.Approve),
                               cancellationToken)
                           .ConfigureAwait(false);
            return 2;
        }

        var outputJson = node.NodeType == DevWorkflowNodeType.Gate
            ? ComposeGateOutput(node, graph, byKey, nodeRun.Attempt)
            : JsonSerializer.Serialize(new InlineOutput(DevWorkflowNodeOutputStatuses.Succeeded, nodeRun.Attempt, Branch: null), JsonOptions);

        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(run.Id,
                               nodeRun.Id,
                               DevWorkflowVersions.Any,
                               DevWorkflowNodeRunStatus.Succeeded,
                               OutputJson: outputJson),
                           cancellationToken)
                       .ConfigureAwait(false);
        return 2;
    }

    /// <summary>
    ///     A gate's output: its upstream node's document, carried through, plus the branch the gate chose.
    ///     <para>
    ///         The pass-through is what keeps a Gate from adding routing power a conditional edge does not already have.
    ///         Its out-edges are evaluated by the same generic edge rule as everything else, against this document — so
    ///         the gate cannot decide one thing and the edges another. What it buys is <c>branch</c>: one recorded answer
    ///         to "which way did the run go, and on what", which is otherwise only reconstructible by re-evaluating
    ///         conditions against a document that may since have been superseded.
    ///     </para>
    /// </summary>
    private static string ComposeGateOutput(DevWorkflowGraphNode node,
        DevWorkflowGraph graph,
        IReadOnlyDictionary<string, DevWorkflowNodeRunSnapshot> byKey,
        int attempt)
    {
        var upstream = graph.InboundEdges(node.NodeKey)
                            .Select(edge => byKey.GetValueOrDefault(edge.From))
                            .FirstOrDefault(candidate => candidate is { Status: DevWorkflowNodeRunStatus.Succeeded });

        using var document = ParseObject(upstream?.OutputJson);
        var branch = graph.OutboundEdges(node.NodeKey)
                          .FirstOrDefault(edge => DevWorkflowCondition.Evaluate(edge.Condition, document?.RootElement))
                          ?.To;

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (document is not null)
            {
                foreach (var property in document.RootElement.EnumerateObject().Where(static property => !ReservedGateProperties.Contains(property.Name)))
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteString("status", DevWorkflowNodeOutputStatuses.Succeeded);
            writer.WriteNumber("attempt", attempt);
            if (branch is null)
            {
                writer.WriteNull("branch");
            }
            else
            {
                writer.WriteString("branch", branch);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>The upstream document, or null when there is none or it is not an object this can carry through.</summary>
    private static JsonDocument? ParseObject(string? json)
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

    /// <summary>
    ///     The edge states <see cref="AValidationSucceededOnThePathTaken" /> walks THROUGH: the ones in which the run
    ///     really came this way. A set rather than an equality test because the set is what the rule is about.
    ///     <para>
    ///         <c>Dead</c> and <c>Pending</c> are the two that must never be in it — a branch that did not run, or has
    ///         not run yet, carries no provenance. Every other state the machine has today belongs here, <c>Waived</c>
    ///         included: an operator's skip is waived precisely when everything behind it was satisfied or waived in
    ///         turn, so the rows further back DID run and the walk has to be able to reach them. Leaving it out is what
    ///         would block <c>integrate</c> on the shipped template the moment someone skips <c>verify</c>.
    ///         <c>DevWorkflowMaterializationTests.TheProvenanceWalkCrossesEveryEdgeStateThatIsNotDeadOrPending</c>
    ///         ENFORCES that rather than proving it: it demands an entry for every state outside those two, so a new
    ///         one cannot be added to <see cref="DevWorkflowEdgeState" /> without this walk being told about it. A
    ///         future state that means "undecided in some new way" is a third exclusion for that test to name, not an
    ///         entry here — the assertion asks whoever hits it which of the two it is.
    ///     </para>
    /// </summary>
    private static readonly DevWorkflowEdgeState[] ProvenanceEdgeStates = [DevWorkflowEdgeState.Satisfied, DevWorkflowEdgeState.Waived];

    /// <summary>
    ///     <c>GRAPH-C4-3</c>, asked of the rows a run actually landed rather than of every structural ancestor: does the
    ///     apply node's provenance contain a <c>Tool</c>/<c>Validate</c> node whose row succeeded?
    ///     <para>
    ///         One backward walk over the inbound edges whose state is in <see cref="ProvenanceEdgeStates" />, which is
    ///         what makes the rule the smaller one. A branch that did not run drops out with no special case — a
    ///         <c>Failed</c> or <c>Cancelled</c> source, and a <c>Skipped</c> one with anything dead behind it, kills
    ///         every out-edge — so an <c>Any</c> convergence whose other branch carried its own validation does not
    ///         block the apply on work that was correctly not done.
    ///     </para>
    ///     <para>
    ///         The candidate row is tested for <c>Succeeded</c> separately, and that test is NOT redundant with the edge
    ///         state: a <c>Satisfied</c> edge does imply a succeeded source, but a <c>Waived</c> one does not — the rows
    ///         BEHIND a waived edge succeeded while the waived node itself was skipped. Crossing the edge and still
    ///         refusing to count a non-succeeded validation is the pair that stays correct either way, and it is what
    ///         makes an operator's skip of the validation node itself still block the apply.
    ///     </para>
    ///     <para>
    ///         An unmaterialized template key reads <c>Pending</c> and falls out exactly as admission's own template
    ///         filter drops it — and the zero-task decomposition's no-op verdict row puts it back in, which is what
    ///         makes that path pass this check without an exemption of its own.
    ///     </para>
    /// </summary>
    private static bool AValidationSucceededOnThePathTaken(DevWorkflowGraph graph,
        DevWorkflowGraphNode apply,
        IReadOnlyDictionary<string, DevWorkflowNodeRunSnapshot> byKey)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal)
        {
            apply.NodeKey
        };
        var pending = new Stack<string>();
        pending.Push(apply.NodeKey);
        while (pending.Count > 0)
        {
            foreach (var edge in graph.InboundEdges(pending.Pop()))
            {
                var source = byKey.GetValueOrDefault(edge.From);
                if (!ProvenanceEdgeStates.Contains(DevWorkflowStateMachine.EdgeState(edge, graph, byKey)) || !seen.Add(edge.From))
                {
                    continue;
                }

                if (graph.Nodes.GetValueOrDefault(edge.From) is { NodeType: DevWorkflowNodeType.Tool, ToolMode: DevWorkflowToolMode.Validate }
                    && source?.Status == DevWorkflowNodeRunStatus.Succeeded)
                {
                    return true;
                }

                pending.Push(edge.From);
            }
        }

        return false;
    }

    /// <summary>
    ///     Stands a node run down for a human, and blocks its work item in the same transaction.
    ///     <para>
    ///         The work-item write has to travel HERE rather than wait for the end-of-tick recomputation: a node
    ///         blocking while a sibling still works leaves the run <c>Running</c>, so the recomputation writes nothing
    ///         and the item would keep reading <c>Active</c> with a node run nobody is coming to unblock.
    ///     </para>
    /// </summary>
    private static async Task<int> BlockAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        string sanitizedReason,
        string failureClass,
        CancellationToken cancellationToken)
    {
        DevWorkflowStateMachine.EnsureLegal(nodeRun.Status, DevWorkflowNodeRunStatus.Blocked, nodeRun.NodeKey);
        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(run.Id,
                               nodeRun.Id,
                               DevWorkflowVersions.Any,
                               DevWorkflowNodeRunStatus.Blocked,
                               PendingDecisionKind: DevWorkflowDecisionKind.Abandon,
                               FailureClass: failureClass,
                               TerminalReason: sanitizedReason,
                               WorkItemStatus: DevWorkflowWorkItemStatus.Blocked),
                           cancellationToken)
                       .ConfigureAwait(false);
        return 1;
    }

    /// <summary>
    ///     Ends the tick by asking what the run now is, of the rows AND of the graph they belong to — the same parsed
    ///     graph this tick already resolved, so the rule that decides whether an end was reached costs no extra read.
    /// </summary>
    private async Task<int> RecomputeRunStatusAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        DevWorkflowGraph graph,
        CancellationToken cancellationToken)
    {
        var current = await store.GetRunAsync(run.Id, cancellationToken).ConfigureAwait(false);
        var nodeRuns = await store.ListNodeRunsAsync(run.Id, cancellationToken).ConfigureAwait(false);
        var outcome = DevWorkflowStateMachine.Recompute(current.Status, graph, nodeRuns);
        if (outcome.Status == current.Status)
        {
            return 0;
        }

        DevWorkflowStateMachine.EnsureLegal(current.Status, outcome.Status);
        _ = await store.TransitionRunAsync(new TransitionDevWorkflowRunCommand(run.Id,

                               // The version this decision was made against. Any would let a status move overwrite a
                               // lifecycle command that landed between the read and this write — a cancel silently
                               // becoming a Running again — and the run service is the second writer that makes it real.
                               current.Version,
                               outcome.Status,

                               // Both are null for Completed and Failed: a failing node run already carries the class that
                               // explains it, and a second, coarser copy on the run would only ever be a worse answer to
                               // the same question. A run that reached no end is the one case with no such node run —
                               // nothing failed — so there the outcome carries the whole account itself.
                               FailureClass: outcome.FailureClass,
                               SanitizedReason: outcome.TerminalReason,
                               WorkItemStatus: DevWorkflowStateMachine.WorkItemStatusFor(outcome.Status, nodeRuns)),
                           cancellationToken)
                       .ConfigureAwait(false);

        if (DevWorkflowStateMachine.IsTerminal(outcome.Status))
        {
            Forget(run.Id);
        }

        return 1;
    }

    /// <summary>
    ///     Two pumps, one advance. A signal is a latency hint and a sweep is the backstop, so they are independent —
    ///     and they are safe to be independent because <see cref="AdvanceOnceAsync" /> serializes: the single-advance
    ///     invariant lives in the gate, not in the shape of the wait.
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
        using var sweep = new PeriodicTimer(TimeSpan.FromSeconds(_options.SweepSeconds), _timeProvider);
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
            var store = scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>();
            foreach (var status in LiveRunStatuses)
            {
                // NOT MaxConcurrentRuns: that is an admission cap for the run service, and using it as a page size here
                // orders live runs by creation date and then silently stops sweeping everything past the cap — the
                // oldest stuck run, which is exactly the one a sweep exists to rescue.
                var runs = await store.ListRunsAsync(workItemId: null, status, SweepPageSize, cancellationToken).ConfigureAwait(false);
                runIds.UnionWith(runs.Select(static run => run.Id));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "The development workflow sweep could not list its live runs.");
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
                // this every hop would wait for the next sweep and a five-node run would take five sweep intervals.
                Signal(runId);
            }
        }
        catch (DevWorkflowConcurrencyException exception)
        {
            // Someone else moved the run between this tick's read and its write — a human decision, or a lifecycle
            // command. Their write stands and the next tick re-derives from it; there is nothing to repair.
            _logger.LogDebug(exception, "Development workflow run {RunId} was moved by another writer mid-tick.", runId);
            Signal(runId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Development workflow run {RunId} could not be advanced.", runId);
        }
    }

    /// <summary>
    ///     The two lanes a tick resolves per scope. The sandbox lane is not here: it is a singleton, because its slots
    ///     and its registry outlive a tick and these do not.
    /// </summary>
    private sealed record DevWorkflowLanes(DevWorkflowAgentExecutor Agent, DevWorkflowDevTaskExecutor DevTasks);

    private sealed record ReasonDetail(string Reason);

    /// <summary>
    ///     What a node run that ran out of time leaves as its output document. Deliberately the three members every
    ///     output carries and nothing else: the lane holds the detail, and this row's lane had nothing to hand over.
    /// </summary>
    private sealed record TimedOutOutput(string Status, int Attempt, string FailureClass);

    private sealed record InlineOutput(string Status, int Attempt, string? Branch);
}
