namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The workflow runtime's one loop: it advances a persisted run by transitioning persisted node runs and holds no
///     authoritative state, which is why a restart costs at most the work in flight.
/// </summary>
/// <remarks>
///     The parsed-graph cache is a cost optimisation and nothing else. Every node-run status write happens inside a
///     serialized <see cref="AdvanceOnceAsync" /> call — the invariant the design rests on: lane work only produces a
///     pollable result and never transitions a row itself, so the only other writer to a run is the human-decision
///     path, which the store's <c>Any</c> version sentinel exists for. Advancement is a pure database decision taking
///     microseconds, so one loop serves every run. See docs/wiki/25-dev-workflows.md ("The order of a tick").
/// </remarks>
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
    ///     How much of an operator's decision comment reaches the node run's <c>terminal_reason</c>.
    /// </summary>
    /// <remarks>
    ///     The column holds 1024 and the comment is free text a person typed, so it is cut here rather than at the
    ///     store's rejection: a decision that could not be applied because someone was verbose is the wrong way for a
    ///     run to stop.
    /// </remarks>
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
    ///     Advances one run by one tick, and answers how many transitions it wrote — zero meaning the run is quiescent.
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
            return await AdvanceCoreAsync(scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>(),
                    new DevWorkflowLanes
                    {
                        Agent = scope.ServiceProvider.GetRequiredService<DevWorkflowAgentExecutor>(),
                        DevTasks = scope.ServiceProvider.GetRequiredService<DevWorkflowDevTaskExecutor>()
                    },
                    runId,
                    cancellationToken);
        }
        finally
        {
            _ = _advanceGate.Release();
        }
    }

    private async Task<int> AdvanceCoreAsync(IDevWorkflowStore store, DevWorkflowLanes lanes, Guid runId, CancellationToken cancellationToken)
    {
        var run = await store.GetRunAsync(runId, cancellationToken);
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
            return await StartPendingRunAsync(store, run, cancellationToken);
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
            return await FailUnroutableAsync(store, run, exception, cancellationToken);
        }

        // Settle what the lanes have landed FIRST, before anything reads the node runs: a session that finished between ticks has to be seen as finished, or the run would judge its
        // whole graph against a row that is only still Running because nothing asked.
        var written = await PollAsync(store, lanes, graph, run, cancellationToken);
        var nodeRuns = await store.ListNodeRunsAsync(runId, cancellationToken);

        // A recorded decision is the durable half of a human act; turning it into a transition is this step's job, and doing it here rather than at admission is what lets a decision
        // taken during a pause apply on the first tick after the resume.
        var (settledCount, gateRejection) = await SettleDecisionsAsync(store, run, graph, nodeRuns, cancellationToken);
        written += settledCount;

        // Only an in-flight cancel supersedes it. A PAUSING run must still take this branch: the gate is already Succeeded by the time the pause settles, so nothing would re-detect the
        // rejection and the run would resume and complete — the exact lie the rule exists to prevent.
        if (gateRejection is { } rejection && run.Status != DevWorkflowRunStatus.Cancelling)
        {
            // A gate answered in a way no out-edge accepts ends the run: reading it as Completed (every downstream skipped) or as Failed (nothing failed) would both lie. It goes through
            // the drain like every other terminal, so live siblings settle and release what they hold instead of being orphaned.
            DevWorkflowStateMachine.EnsureLegal(run.Status, DevWorkflowRunStatus.Cancelling);
            _ = await store.TransitionRunAsync(new TransitionDevWorkflowRunCommand
            {
                RunId = run.Id,
                ExpectedVersion = await CurrentVersionAsync(store, run.Id, cancellationToken),
                TargetStatus = DevWorkflowRunStatus.Cancelling,
                FailureClass = DevWorkflowFailureClasses.GateRejected,
                SanitizedReason = rejection
            },
                               cancellationToken);
            return written + 1;
        }

        if (run.Status is DevWorkflowRunStatus.Pausing or DevWorkflowRunStatus.Cancelling)
        {
            written += await DrainAsync(store, lanes, run, cancellationToken);
            return written;
        }

        // A settled decomposition grows the graph and the tick ENDS there: what follows judges node runs against the graph this call just replaced, so the next tick re-parses and admits
        // the expansion. A parse-count assertion pins that, the failure being silent. Rows are re-read only if a decision moved one — the decomposition must be Succeeded to count.
        var materialized = await _materializer.MaterializeAsync(store,
                                                  graph,
                                                  run,
                                                  settledCount > 0 ? await store.ListNodeRunsAsync(run.Id, cancellationToken) : nodeRuns,
                                                  cancellationToken);
        if (materialized > 0)
        {
            return written + materialized;
        }

        written += await AdmitAsync(store, lanes, run, graph, cancellationToken);
        written += await RecomputeRunStatusAsync(store, run, graph, cancellationToken);
        return written;
    }

    /// <summary>
    ///     Asks every lane-owned node run what became of the work it was driving, and settles the ones that landed.
    /// </summary>
    /// <remarks>
    ///     Deliberately the executor's answer rather than this loop's memory: the dispatcher holds nothing about a run
    ///     between ticks, so a restart loses nothing a poll cannot re-read. It runs in every non-terminal status
    ///     including the two drains — a session asked to stop settles here, which is how the drain learns it may
    ///     finish.
    /// </remarks>
    private async Task<int> PollAsync(IDevWorkflowStore store,
        DevWorkflowLanes lanes,
        DevWorkflowGraph graph,
        DevWorkflowRunSnapshot run,
        CancellationToken cancellationToken)
    {
        var nodeRuns = await store.ListNodeRunsAsync(run.Id, cancellationToken);

        // Before anything is read off the lane: a fix loop can re-attempt a row the lane is driving without going
        // through the lane, and a pass belonging to the attempt before is not an answer about the one the row is on now.
        await _tools.ForgetSupersededAsync(nodeRuns);

        // A Tool row still reading Queued is polled too when the lane is already driving it: the Running write can fail after the slot and the registry entry were taken, and outside a
        // drain the next admission repairs that — but a drain admits nothing, so without this the run waits on a row nothing would ever move again.
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
            // One poll can move rows that are not its own: a failure routed to an upstream node resets that node's whole subtree, leaving the rest of this list a picture of a graph that
            // changed underneath it. So once anything is written the rows are re-read, and one this lane no longer owns is left alone — settling it would answer about a round being redone.
            if (written > 0)
            {
                nodeRuns = await store.ListNodeRunsAsync(run.Id, cancellationToken);
            }

            if (nodeRuns.FirstOrDefault(nodeRun => nodeRun.Id == candidate.Id) is not { Status: DevWorkflowNodeRunStatus.Running or DevWorkflowNodeRunStatus.Queued } current)
            {
                continue;
            }

            var polled = current.NodeType switch
            {
                DevWorkflowNodeType.Agent => await lanes.Agent.PollAsync(store, graph, run, current, nodeRuns, cancellationToken),
                DevWorkflowNodeType.DevTask => await lanes.DevTasks.PollAsync(store, graph, run, current, nodeRuns, cancellationToken),
                _ => await _tools.PollAsync(store, graph, run, current, nodeRuns, cancellationToken)
            };

            // Only a row its lane had nothing to say about. A pass that landed inside its budget is settled off what it actually came to — including a sandbox timeout, which arrives with
            // the evidence gathered before the clock ran out — and expiring it here as well would overwrite that answer with a coarser one.
            written += polled > 0
                ? polled
                : await ExpireAsync(store, lanes, graph, run, current, nodeRuns, cancellationToken);
        }

        return written;
    }

    /// <summary>
    ///     Ends a node run that has been running longer than its node allows, and answers how many transitions it wrote.
    /// </summary>
    /// <remarks>
    ///     The deadline is re-derived from the row every tick rather than armed once in memory, so it survives the
    ///     restart that would otherwise leave a node run bounded by nothing. Where the expiry LEADS — another attempt,
    ///     the node that produced what this one was judging, or a human — is the retry policy's answer, the same as
    ///     for every other retryable failure class.
    /// </remarks>
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

        // Dropped BEFORE the row is settled, and dropped rather than merely stopped: a re-attempt lands the row on a new attempt inside this same call, and this tick's admission would
        // then find the registry still holding the pass that ran out of time, leaving the fresh attempt's pass running with nothing to poll it.
        if (nodeRun.NodeType == DevWorkflowNodeType.Tool)
        {
            await _tools.DiscardAsync(nodeRun.Id);
        }
        else if (nodeRun.NodeType == DevWorkflowNodeType.DevTask)
        {
            _ = await lanes.DevTasks.StopAttemptAsync(nodeRun, cancel: true, cancellationToken);
        }
        else if (nodeRun is { NodeType: DevWorkflowNodeType.Agent, WorkSessionId: { } sessionId })
        {
            await lanes.Agent.StopAsync(sessionId, cancel: true, cancellationToken);
        }

        return await _retries.SettleFailureAsync(store,
                                 graph,
                                 run,
                                 nodeRun,
                                 nodeRuns,
                                 new DevWorkflowFailure
                                 {
                                     FailureClass = DevWorkflowFailureClasses.Timeout,
                                     SanitizedReason = $"This node run did not finish within the {node.NodeTimeoutSeconds} seconds its node allows.",
                                     OutputJson = JsonSerializer.Serialize(new TimedOutOutput { Status = DevWorkflowNodeOutputStatuses.Failed, Attempt = nodeRun.Attempt, FailureClass = DevWorkflowFailureClasses.Timeout },
                                         JsonOptions),
                                     Outcome = DevWorkflowOutcomes.Timeout
                                 },
                                 cancellationToken);
    }

    /// <summary>
    ///     Starts a <c>Pending</c> run, or fails it for good if its pinned graph cannot be routed.
    /// </summary>
    /// <remarks>
    ///     The graph is validated again here rather than trusted from the definition's save, because an agent
    ///     definition can be deleted in between. A run left <c>Pending</c> on a graph nothing can route would be swept
    ///     forever, so the refusal is written down rather than retried.
    /// </remarks>
    private async Task<int> StartPendingRunAsync(IDevWorkflowStore store, DevWorkflowRunSnapshot run, CancellationToken cancellationToken)
    {
        try
        {
            // The graph is still resolved first: a run whose pinned blob cannot be routed has to fail rather than
            // queue behind runs that can.
            _ = _graphs.Resolve(run);
            return await StartRunAsync(store, run, _options.MaxConcurrentRuns, cancellationToken);
        }
        catch (DevWorkflowValidationException exception)
        {
            return await FailUnroutableAsync(store, run, exception, cancellationToken);
        }
    }

    /// <summary>
    ///     The run's version as of right now, for a run-level write that follows this tick's own node-run writes.
    /// </summary>
    /// <remarks>
    ///     Every node-run transition bumps the run version, so the top-of-tick version is stale by the time a drain or
    ///     a recomputation writes — using it would make the dispatcher lose a race against itself. Re-reading narrows
    ///     the window to what the check is for: a human decision or a lifecycle command landing between the read and
    ///     the write, which must win rather than be overwritten by a status move.
    /// </remarks>
    private static async Task<long> CurrentVersionAsync(IDevWorkflowStore store, Guid runId, CancellationToken cancellationToken) =>
        (await store.GetRunAsync(runId, cancellationToken)).Version;

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

        _ = await store.TransitionRunAsync(new TransitionDevWorkflowRunCommand
        {
            RunId = run.Id,
            ExpectedVersion = await CurrentVersionAsync(store, run.Id, cancellationToken),
            TargetStatus = DevWorkflowRunStatus.Failed,
            FailureClass = DevWorkflowFailureClasses.Configuration,
            SanitizedReason = exception.Message,
            WorkItemStatus = DevWorkflowWorkItemStatus.Blocked
        },
                           cancellationToken);
        Forget(run.Id);
        return 1;
    }

    /// <summary>
    ///     Drops everything the runtime holds in memory about a run that has ended: its parsed graph, and any
    ///     re-attempt it had promised itself but will now never ask for.
    /// </summary>
    /// <remarks>
    ///     Both are caches over durable rows, so a run that turns out to be live again simply re-derives them.
    /// </remarks>
    private void Forget(Guid runId)
    {
        _graphs.Forget(runId);
        _retries.Forget(runId);
    }

    /// <summary>
    ///     Moves a validated <c>Pending</c> run to <c>Running</c>, if the node has room to drive another one.
    /// </summary>
    /// <remarks>
    ///     The run's node runs already exist — written in the same transaction as the run row, so a run cannot be
    ///     found without them — and materializing here as well would re-derive seeds whose per-run inputs only the
    ///     starting caller held. A run with no node runs is therefore unreachable from any runtime path; the
    ///     recomputation's no-rows guard stays as the belt that keeps such a row from reading Completed.
    /// </remarks>
    private static async Task<int> StartRunAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        int maxConcurrentRuns,
        CancellationToken cancellationToken)
    {
        if (await CountActiveRunsAsync(store, maxConcurrentRuns, cancellationToken) >= maxConcurrentRuns)
        {
            // Not refused — waiting. The run keeps its rows and its place and the next sweep offers it again; refusing it would push a queue the node can work through back onto the
            // person who started it.
            return 0;
        }

        DevWorkflowStateMachine.EnsureLegal(run.Status, DevWorkflowRunStatus.Running);
        _ = await store.TransitionRunAsync(new TransitionDevWorkflowRunCommand
        {
            RunId = run.Id,
            ExpectedVersion = await CurrentVersionAsync(store, run.Id, cancellationToken),
            TargetStatus = DevWorkflowRunStatus.Running,
            WorkItemStatus = DevWorkflowWorkItemStatus.Active
        },
                           cancellationToken);
        return 1;
    }

    /// <summary>How many runs this node is actually driving.</summary>
    /// <remarks>
    ///     <c>Running</c> and the two drains, deliberately nothing else: a <c>Paused</c> run is not being advanced and
    ///     a <c>WaitingForApproval</c> one waits on a person who may take days, so counting either would let one
    ///     unanswered gate stop every other run on the node — the cap protecting nothing at the cost of the throughput
    ///     it manages. Read as summaries, not snapshots, because a count must not decrypt a graph blob per live run;
    ///     each status is asked for one row more than the cap, which is all the answer needs.
    /// </remarks>
    private static async Task<int> CountActiveRunsAsync(IDevWorkflowStore store, int maxConcurrentRuns, CancellationToken cancellationToken)
    {
        var active = 0;
        foreach (var status in ActiveRunStatuses)
        {
            active += (await store.ListRunSummariesAsync(workItemId: null, status, maxConcurrentRuns + 1, cancellationToken)).Count;
        }

        return active;
    }

    /// <summary>
    ///     Turns recorded decisions into transitions: a gate's answer succeeds it and lets the edges route, while the
    ///     retries-exhausted interventions re-attempt, route around, or give up.
    /// </summary>
    /// <remarks>
    ///     Answers with the reason the run should end when a gate was answered in a way none of its out-edges accepts.
    ///     Deliberately not written here: that is the RUN's transition, and this method only moves node runs.
    /// </remarks>
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

        var decisions = await store.ListDecisionsAsync(run.Id, cancellationToken);

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

            // A decision the node run's status forbids — an Approve recorded against a Blocked row, say — is a durable row re-read on every tick. Left to throw it would wedge the whole
            // run, siblings included, so it is recorded against its own node run and the tick carries on.
            if (!DevWorkflowStateMachine.IsLegal(nodeRun.Status, target))
            {
                var reason = $"A recorded {settled.Decision} decision cannot be applied to a node run that is {nodeRun.Status}.";
                if (DevWorkflowStateMachine.IsLegal(nodeRun.Status, DevWorkflowNodeRunStatus.Blocked))
                {
                    written += await BlockAsync(store, run, nodeRun, reason, DevWorkflowFailureClasses.Configuration, cancellationToken);
                    continue;
                }

                // Already Blocked: there is no status left to move it to, so only the note is new. Keyed by operation
                // id, which the store resolves query-first — so it is written once and not on every tick thereafter.
                _ = await store.AppendEventAsync(new AppendDevWorkflowEventCommand
                {
                    RunId = run.Id,
                    ExpectedVersion = DevWorkflowVersions.Any,
                    EventType = DevWorkflowEventTypes.NodeInterventionRequired,
                    NodeRunId = nodeRun.Id,
                    OperationId = DevWorkflowOperationId.For(run.Id, nodeRun.NodeKey, nodeRun.Attempt, "decision-not-applicable"),
                    DetailJson = JsonSerializer.Serialize(new ReasonDetail(reason), JsonOptions)
                },
                                   cancellationToken);
                continue;
            }

            // What the operator SAID travels with the attempt their decision starts, merged into the node run's inputs exactly as a routed failure is, because both lanes read the brief
            // there. Bounded like every decision comment. EVERY Retry merges, silent ones included: the merge also DROPS an earlier reason that would else outlive the try it was for.
            Action<Utf8JsonWriter>? writeRetryMembers = incrementAttempt
                ? writer =>
                {
                    if (settled.Comment?.Trim() is { Length: > 0 } retried)
                    {
                        writer.WriteString(DevWorkflowNodeInputs.OperatorRetryReason, DevWorkflowStateMachine.Bounded(retried, MaxDecisionComment));
                    }

                    // The attempt this decision bought, written even when nothing was typed: without it every later automatic re-attempt would read these members and quote a person
                    // who said nothing about that try, and a lane acting on the RETRY rather than the sentence could not tell a person's re-attempt from the policy's.
                    writer.WriteNumber(DevWorkflowNodeInputs.OperatorRetryAttempt, nodeRun.Attempt + 1);
                }
                : null;
            var retryInput = incrementAttempt ? DevWorkflowNodeInputs.Merge(nodeRun.InputJson, writeRetryMembers) : null;

            _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand
            {
                RunId = run.Id,
                NodeRunId = nodeRun.Id,
                ExpectedVersion = DevWorkflowVersions.Any,
                TargetStatus = target,
                OutputJson = outputJson,
                InputJson = retryInput,
                FailureClass = target == DevWorkflowNodeRunStatus.Failed ? DevWorkflowFailureClasses.GateRejected : null,
                TerminalReason = DecidedReason(target, settled.Comment),
                IncrementAttempt = incrementAttempt,
                // EVERY Retry widens the cap by one, not only one at the cap — the ruling as written. The attempt it buys carries the operator's reason; the automatic ones it enables
                // do not. The run-wide MaxTotalAttempts budget still bounds everything, counting both alike, and no widening touches it. Nothing else sets this flag.
                WidenMaxAttempts = incrementAttempt,
                // A retry gets a NEW session: resuming the one that just failed resumes the context that failed with it. Releasing it here also stops the fresh attempt being
                // settled straight back off the old session's answer.
                ClearWorkSession = incrementAttempt,
                Outcome = outcome,
                // An answered node run may be the last thing the work item was blocked on, and the run status often does not move when it settles, so the release travels with the
                // answer for the same reason blocking it does.
                WorkItemStatus = DevWorkflowStateMachine.WorkItemStatusAfter(run.Status, settledSoFar, nodeRun.Id, target)
            },
                               cancellationToken);
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

            // Only a human gate strands a run this way: every other node's dead out-edges skip their targets, a route rather than a dead end. A gate with NO out-edges counts — the seeded
            // "Research → Plan → Approval" shape, where rejecting the terminal approval must not read as success — and Approve is exempt only there, not at a gate that HAS branches.
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

        // What a person's decision leaves on the row. A Skip needs a reason most of any terminal: an All join carries on past a skipped leaf, so the node downstream is handed the skip
        // as evidence with only this string to say WHY the work it expected is absent. The operator's words are the whole of that why, bounded because the column is 1024 of free text.
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
    /// </summary>
    /// <remarks>
    ///     Every terminal is reached this way or through the "nothing is live" recomputation. No path writes one
    ///     directly, because doing so would strand the run's live node runs under a run no tick looks at again.
    /// </remarks>
    private async Task<int> DrainAsync(IDevWorkflowStore store, DevWorkflowLanes lanes, DevWorkflowRunSnapshot run, CancellationToken cancellationToken)
    {
        var nodeRuns = await store.ListNodeRunsAsync(run.Id, cancellationToken);
        var written = 0;

        foreach (var nodeRun in nodeRuns.Where(static nodeRun => DevWorkflowStateMachine.IsLive(nodeRun.Status)))
        {
            // ASK, do not settle. A Running node run belongs to an executor and only the executor knows what stopping it costs, so the drain requests the stop and the next tick's poll
            // writes the terminal off what actually happened. Rows no lane owns are settled here, because for them there is nothing to ask.
            written += await StopAsync(store, lanes, run, nodeRun, cancellationToken);
        }

        // Re-read: the stops above may have settled every row already, and judging "is anything still live" off the snapshot taken before them would cost a whole extra tick for a
        // drain that is in fact finished.
        nodeRuns = await store.ListNodeRunsAsync(run.Id, cancellationToken);
        if (nodeRuns.Any(static nodeRun => nodeRun.Status is DevWorkflowNodeRunStatus.Queued or DevWorkflowNodeRunStatus.Running))
        {
            // Still settling — an executor was asked to stop and has not answered yet. The command already committed its intent, so the UI can say "cancelling" honestly rather than
            // claiming one that has not landed.
            return written;
        }

        var settledStatus = run.Status == DevWorkflowRunStatus.Pausing ? DevWorkflowRunStatus.Paused : DevWorkflowRunStatus.Cancelled;
        DevWorkflowStateMachine.EnsureLegal(run.Status, settledStatus);
        _ = await store.TransitionRunAsync(new TransitionDevWorkflowRunCommand
        {
            RunId = run.Id,
            ExpectedVersion = await CurrentVersionAsync(store, run.Id, cancellationToken),
            TargetStatus = settledStatus,
            WorkItemStatus = DevWorkflowStateMachine.WorkItemStatusFor(settledStatus, nodeRuns)
        },
                           cancellationToken);

        // A PAUSED run keeps its promised re-attempts: it is coming back, and a resume that skipped every cushion a definition asked for would be the pause spending them.
        if (DevWorkflowStateMachine.IsTerminal(settledStatus))
        {
            _retries.Forget(run.Id);
        }

        _graphs.Forget(run.Id);
        return written + 1;
    }

    /// <summary>Asks one live node run to stop, for whichever of the two drains is running.</summary>
    /// <remarks>
    ///     Cancelling abandons the node run; pausing keeps the durable human waits and the not-yet-admitted rows
    ///     exactly where they are, because a pause is meant to be resumed. The one thing a pause does move is a
    ///     <c>Queued</c> row back to <c>Pending</c>: it is queued for a slot nothing hands out while the run drains,
    ///     so leaving it would pin <c>Pausing</c> for as long as the lane stayed busy. That is the same collapse the
    ///     startup reconciler performs, for the same reason.
    /// </remarks>
    private async Task<int> StopAsync(IDevWorkflowStore store,
        DevWorkflowLanes lanes,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        CancellationToken cancellationToken)
    {
        if (nodeRun.NodeType == DevWorkflowNodeType.Tool && _tools.IsInFlight(nodeRun.Id))
        {
            // A pause lets a build finish: it holds no model slot, cannot be resumed halfway, and killing it would throw away minutes of work to save seconds. So the run stays Pausing
            // until the poll settles the row, the same "once nothing is live" rule every other drain uses.
            if (run.Status == DevWorkflowRunStatus.Pausing)
            {
                return 0;
            }

            // Asked, not settled: only the next tick's poll knows whether the commands stopped or finished inside the window. Counted as work so that tick comes immediately.
            return await _tools.StopAsync(nodeRun.Id) ? 1 : 0;
        }

        if (nodeRun is { Status: DevWorkflowNodeRunStatus.Running, NodeType: DevWorkflowNodeType.DevTask })
        {
            // The development chain owns what stopping ITS work costs, as the two other lanes do: a cancel asks the attempt to stop and the next poll settles the row on what it did,
            // while a pause leaves the attempt to finish and parks the row where the resume can re-drive the task from.
            return await lanes.DevTasks.StopAsync(store, run, nodeRun, run.Status == DevWorkflowRunStatus.Cancelling, cancellationToken);
        }

        var owned = nodeRun is { Status: DevWorkflowNodeRunStatus.Running, NodeType: DevWorkflowNodeType.Agent, WorkSessionId: { } };
        if (run.Status == DevWorkflowRunStatus.Pausing)
        {
            if (owned)
            {
                // The session checkpoints and parks, and the row collapses to Pending rather than to a terminal: a pause is meant to be RESUMED, and a Pending row with its session
                // still attached is exactly what the resume re-admits — it finds the paused session and continues it instead of starting the work over.
                await lanes.Agent.StopAsync(nodeRun.WorkSessionId!.Value, cancel: false, cancellationToken);
            }
            else if (nodeRun.Status != DevWorkflowNodeRunStatus.Queued)
            {
                return 0;
            }

            _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand
            {
                RunId = run.Id,
                NodeRunId = nodeRun.Id,
                ExpectedVersion = DevWorkflowVersions.Any,
                TargetStatus = DevWorkflowNodeRunStatus.Pending
            },
                               cancellationToken);
            return 1;
        }

        if (owned)
        {
            // Asked, not settled: only the session knows whether it landed Cancelled or finished inside the window, and the top of the NEXT tick polls it. Counted as work so that tick
            // comes immediately rather than settling here, which would hold the advance gate — and with it every other run — for as long as the stop's grace period.
            await lanes.Agent.StopAsync(nodeRun.WorkSessionId!.Value, cancel: true, cancellationToken);
            return 1;
        }

        DevWorkflowStateMachine.EnsureLegal(nodeRun.Status, DevWorkflowNodeRunStatus.Cancelled, nodeRun.NodeKey);
        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand
        {
            RunId = run.Id,
            NodeRunId = nodeRun.Id,
            ExpectedVersion = DevWorkflowVersions.Any,
            TargetStatus = DevWorkflowNodeRunStatus.Cancelled,
            FailureClass = DevWorkflowFailureClasses.Cancelled,
            TerminalReason = "The run was cancelled."
        },
                           cancellationToken);
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
        var nodeRuns = await store.ListNodeRunsAsync(run.Id, cancellationToken);
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
                        cancellationToken);
                continue;
            }

            if (nodeRun.Status == DevWorkflowNodeRunStatus.Queued)
            {
                // Already judged eligible when it was queued; only the slot was missing. Re-judging its edges would be
                // asking a question whose answer cannot have changed — nothing un-succeeds.
                written += await DispatchAsync(store, lanes, run, graph, node, nodeRun, nodeRuns, byKey, cancellationToken);
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
                _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand
                {
                    RunId = run.Id,
                    NodeRunId = nodeRun.Id,
                    ExpectedVersion = DevWorkflowVersions.Any,
                    TargetStatus = DevWorkflowNodeRunStatus.Skipped,
                    TerminalReason = DevWorkflowStateMachine.SkipReason(node, graph, byKey)
                },
                                   cancellationToken);
                written++;
                continue;
            }

            written += await DispatchAsync(store, lanes, run, graph, node, nodeRun, nodeRuns, byKey, cancellationToken);
        }

        return written;
    }

    /// <summary>
    ///     Queues an eligible node run and, for the four node types the inline lane owns, runs it in the same tick.
    /// </summary>
    /// <remarks>
    ///     An inline node goes <c>Pending</c> → <c>Running</c> → <c>Succeeded</c>, skipping <c>Queued</c> — see the
    ///     remark at the inline write below. It still costs two event rows, which is what makes the timing of a
    ///     fan-out visible, the only reason Parallel and Join exist as node types at all. The dev-task lane attaches
    ///     here; a node run of a type no lane claims is blocked for a human rather than left queued forever, because
    ///     a queue nothing drains is the one answer that would look like progress.
    /// </remarks>
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
            return await lanes.Agent.DispatchAsync(store, graph, run, node, nodeRun, nodeRuns, cancellationToken);
        }

        if (node.NodeType == DevWorkflowNodeType.Tool)
        {
            if (node.ToolMode == DevWorkflowToolMode.Apply && !AValidationSucceededOnThePathTaken(graph, node, byKey))
            {
                // GRAPH-C4-3's runtime half, BEFORE the consumption record below: a blocked apply must not first record that it consumed inputs it never read. Policy rather than
                // a failure — nothing broke, and the answer is a person's.
                return await BlockAsync(store,
                        run,
                        nodeRun,
                        $"Node '{node.NodeKey}' applies approved patches, and no validation node succeeded on the path this run took. "
                        + "Nothing has judged what is about to be applied (invariant GRAPH-C4-3).",
                        DevWorkflowFailureClasses.Policy,
                        cancellationToken);
            }

            if (nodeRun.Status == DevWorkflowNodeRunStatus.Pending)
            {
                // These commands judge what the steps before them produced, so a later version of any of it makes this report describe something gone. Recorded here because the lane
                // cannot see its own inputs through the store. Once per attempt, on the first tick that admits it: a tick that only finds the lane full must not record a second.
                _ = await DevWorkflowUpstreamArtifacts.RecordAsync(store, graph, run, nodeRun, cancellationToken);
            }

            return await _tools.DispatchAsync(store, run, node, nodeRun, cancellationToken);
        }

        if (node.NodeType == DevWorkflowNodeType.DevTask)
        {
            return await lanes.DevTasks.DispatchAsync(store, graph, run, nodeRun, nodeRuns, cancellationToken);
        }

        // No Queued hop: an inline node waits for no slot, and the three queue-reason tokens all name something real to wait for. A Queued row with none of them would be lying.
        DevWorkflowStateMachine.EnsureLegal(nodeRun.Status, DevWorkflowNodeRunStatus.Running, nodeRun.NodeKey);
        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand
        {
            RunId = run.Id,
            NodeRunId = nodeRun.Id,
            ExpectedVersion = DevWorkflowVersions.Any,
            TargetStatus = DevWorkflowNodeRunStatus.Running
        },
                           cancellationToken);

        if (node.NodeType == DevWorkflowNodeType.HumanGate)
        {
            // The gate consumes what its predecessors produced, and recording that gives the approval panel its evidence list. Without it the panel renders a prompt and three buttons
            // over nothing, and the operator approves a plan they cannot see. The record is simply true, so it costs no new field.
            _ = await DevWorkflowUpstreamArtifacts.RecordAsync(store, graph, run, nodeRun, cancellationToken);

            _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand
            {
                RunId = run.Id,
                NodeRunId = nodeRun.Id,
                ExpectedVersion = DevWorkflowVersions.Any,
                TargetStatus = DevWorkflowNodeRunStatus.WaitingForApproval,
                PendingDecisionKind = DevWorkflowDecisionKind.Approve
            },
                               cancellationToken);
            return 2;
        }

        var outputJson = node.NodeType == DevWorkflowNodeType.Gate
            ? ComposeGateOutput(node, graph, byKey, nodeRun.Attempt)
            : JsonSerializer.Serialize(new InlineOutput { Status = DevWorkflowNodeOutputStatuses.Succeeded, Attempt = nodeRun.Attempt, Branch = null }, JsonOptions);

        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand
        {
            RunId = run.Id,
            NodeRunId = nodeRun.Id,
            ExpectedVersion = DevWorkflowVersions.Any,
            TargetStatus = DevWorkflowNodeRunStatus.Succeeded,
            OutputJson = outputJson
        },
                           cancellationToken);
        return 2;
    }

    /// <summary>
    ///     A gate's output: its upstream node's document, carried through, plus the branch the gate chose.
    /// </summary>
    /// <remarks>
    ///     The pass-through keeps a Gate from adding routing power a conditional edge does not already have: its
    ///     out-edges are evaluated by the same generic edge rule as everything else, against this document, so the
    ///     gate cannot decide one thing and the edges another. What it buys is <c>branch</c> — one recorded answer to
    ///     "which way did the run go, and on what", otherwise reconstructible only by re-evaluating conditions
    ///     against a document that may since have been superseded.
    /// </remarks>
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
#pragma warning disable MA0045 // Utf8JsonWriter over an in-memory buffer: no I/O to await; synchronous canonical-bytes function.
        using (var writer = new Utf8JsonWriter(buffer))
#pragma warning restore MA0045
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
    ///     really came this way. A set rather than an equality test, because the set is what the rule is about.
    /// </summary>
    /// <remarks>
    ///     <c>Dead</c> and <c>Pending</c> must never be in it — a branch that did not run, or has not run yet, carries
    ///     no provenance — and every other state belongs, <c>Waived</c> included: a skip is waived precisely when
    ///     everything behind it was satisfied or waived in turn, so those rows DID run and the walk must reach them.
    ///     A materialization test demands an entry per state outside those two, so a new state cannot be added
    ///     silently. See docs/wiki/25-dev-workflows.md ("The provenance walk in front of an apply").
    /// </remarks>
    private static readonly DevWorkflowEdgeState[] ProvenanceEdgeStates = [DevWorkflowEdgeState.Satisfied, DevWorkflowEdgeState.Waived];

    /// <summary>
    ///     <c>GRAPH-C4-3</c>, asked of the rows a run actually landed on rather than of every structural ancestor:
    ///     does the apply node's provenance contain a <c>Tool</c>/<c>Validate</c> node whose row succeeded?
    /// </summary>
    /// <remarks>
    ///     One backward walk over the inbound edges whose state is in <see cref="ProvenanceEdgeStates" />. A branch
    ///     that did not run drops out with no special case, so an <c>Any</c> convergence whose other branch carried
    ///     its own validation is not blocked on work correctly not done. The row is tested for <c>Succeeded</c>
    ///     separately, which a <c>Waived</c> edge does not imply — that is what makes a skip of the validation node
    ///     block the apply. An unmaterialized template key reads <c>Pending</c>; the no-op verdict row puts it back.
    /// </remarks>
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

    /// <summary>Stands a node run down for a human, and blocks its work item in the same transaction.</summary>
    /// <remarks>
    ///     The work-item write travels HERE rather than waiting for the end-of-tick recomputation: a node blocking
    ///     while a sibling still works leaves the run <c>Running</c>, so the recomputation writes nothing and the item
    ///     would keep reading <c>Active</c> with a node run nobody is coming to unblock.
    /// </remarks>
    private static async Task<int> BlockAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        string sanitizedReason,
        string failureClass,
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
            FailureClass = failureClass,
            TerminalReason = sanitizedReason,
            WorkItemStatus = DevWorkflowWorkItemStatus.Blocked
        },
                           cancellationToken);
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
        var current = await store.GetRunAsync(run.Id, cancellationToken);
        var nodeRuns = await store.ListNodeRunsAsync(run.Id, cancellationToken);
        var outcome = DevWorkflowStateMachine.Recompute(current.Status, graph, nodeRuns);
        if (outcome.Status == current.Status)
        {
            return 0;
        }

        DevWorkflowStateMachine.EnsureLegal(current.Status, outcome.Status);
        _ = await store.TransitionRunAsync(new TransitionDevWorkflowRunCommand
        {
            RunId = run.Id,
            // The version this decision was made against. Any would let a status move overwrite a lifecycle command that landed between the read and this write — a cancel silently
            // becoming a Running again — and the run service is the second writer that makes it real.
            ExpectedVersion = current.Version,
            TargetStatus = outcome.Status,
            // Both are null for Completed and Failed: a failing node run already carries the class that explains it, and a second, coarser copy on the run would be a worse answer to
            // the same question. A run that reached no end is the one case with no such node run — nothing failed — so there the outcome carries the whole account itself.
            FailureClass = outcome.FailureClass,
            SanitizedReason = outcome.TerminalReason,
            WorkItemStatus = DevWorkflowStateMachine.WorkItemStatusFor(outcome.Status, nodeRuns)
        },
                           cancellationToken);

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
        using var sweep = new PeriodicTimer(TimeSpan.FromSeconds(_options.SweepSeconds), _timeProvider);
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
            var store = scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>();
            foreach (var status in LiveRunStatuses)
            {
                // NOT MaxConcurrentRuns: that is an admission cap for the run service, and using it as a page size here orders live runs by creation date and then silently stops
                // sweeping everything past the cap — the oldest stuck run, which is exactly the one a sweep exists to rescue.
                var runs = await store.ListRunsAsync(workItemId: null, status, SweepPageSize, cancellationToken);
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
    private sealed record DevWorkflowLanes
    {
        public required DevWorkflowAgentExecutor Agent { get; init; }

        public required DevWorkflowDevTaskExecutor DevTasks { get; init; }
    }

    private sealed record ReasonDetail(string Reason);

    /// <summary>
    ///     What a node run that ran out of time leaves as its output document. Deliberately the three members every
    ///     output carries and nothing else: the lane holds the detail, and this row's lane had nothing to hand over.
    /// </summary>
    private sealed record TimedOutOutput
    {
        public required string Status { get; init; }

        public required int Attempt { get; init; }

        public required string FailureClass { get; init; }
    }

    private sealed record InlineOutput
    {
        public required string Status { get; init; }

        public required int Attempt { get; init; }

        public required string? Branch { get; init; }
    }
}
