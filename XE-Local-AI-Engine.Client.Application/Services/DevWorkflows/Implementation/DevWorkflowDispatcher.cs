namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>camelCase, matching every other document this product puts on a wire.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Written by the gate itself, so an upstream document carrying one of these names does not shadow it.</summary>
    private static readonly HashSet<string> ReservedGateProperties = new(StringComparer.Ordinal) { "status", "attempt", "branch" };

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
    private readonly DevWorkflowOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private int _disposed;
    private Task? _loop;

    public DevWorkflowDispatcher(IServiceScopeFactory scopeFactory,
        DevWorkflowGraphCache graphs,
        IOptions<DevWorkflowOptions> options,
        TimeProvider timeProvider,
        ILogger<DevWorkflowDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _graphs = graphs ?? throw new ArgumentNullException(nameof(graphs));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options.Value;
    }

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
            return await AdvanceCoreAsync(scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>(), runId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _advanceGate.Release();
        }
    }

    private async Task<int> AdvanceCoreAsync(IDevWorkflowStore store, Guid runId, CancellationToken cancellationToken)
    {
        var run = await store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (DevWorkflowStateMachine.IsTerminal(run.Status))
        {
            _graphs.Forget(runId);
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

        var graph = _graphs.Resolve(run);
        var written = 0;
        var nodeRuns = await store.ListNodeRunsAsync(runId, cancellationToken).ConfigureAwait(false);

        // Settle what has landed. A recorded decision is the durable half of a human act; turning it into a transition
        // is this step's job, and doing it here rather than at admission is what lets a decision taken during a pause
        // apply on the first tick after the resume.
        var (settledCount, gateRejection) = await SettleDecisionsAsync(store, run, graph, nodeRuns, cancellationToken).ConfigureAwait(false);
        written += settledCount;

        if (gateRejection is { } rejection && run.Status is not (DevWorkflowRunStatus.Pausing or DevWorkflowRunStatus.Cancelling))
        {
            // A gate answered in a way no out-edge accepts ends the run — reading it as Completed (every downstream
            // skipped) or as Failed (nothing failed) would both lie. It goes through the drain like every other
            // terminal, so live siblings settle and release what they hold instead of being orphaned.
            DevWorkflowStateMachine.EnsureLegal(run.Status, DevWorkflowRunStatus.Cancelling);
            _ = await store.TransitionRunAsync(new TransitionDevWorkflowRunCommand(run.Id,
                                DevWorkflowVersions.Any,
                                DevWorkflowRunStatus.Cancelling,
                                FailureClass: DevWorkflowFailureClasses.GateRejected,
                                SanitizedReason: rejection),
                            cancellationToken)
                        .ConfigureAwait(false);
            return written + 1;
        }

        if (run.Status is DevWorkflowRunStatus.Pausing or DevWorkflowRunStatus.Cancelling)
        {
            written += await DrainAsync(store, run, cancellationToken).ConfigureAwait(false);
            return written;
        }

        written += await AdmitAsync(store, run, graph, cancellationToken).ConfigureAwait(false);
        written += await RecomputeRunStatusAsync(store, run, cancellationToken).ConfigureAwait(false);
        return written;
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
            return await StartRunAsync(store, _graphs.Resolve(run), run, _options.MaxNodeRunsPerRun, cancellationToken).ConfigureAwait(false);
        }
        catch (DevWorkflowValidationException exception)
        {
            _ = await store.TransitionRunAsync(new TransitionDevWorkflowRunCommand(run.Id,
                                DevWorkflowVersions.Any,
                                DevWorkflowRunStatus.Failed,
                                FailureClass: DevWorkflowFailureClasses.Configuration,
                                SanitizedReason: exception.Message,
                                WorkItemStatus: DevWorkflowWorkItemStatus.Blocked),
                            cancellationToken)
                        .ConfigureAwait(false);
            _graphs.Forget(run.Id);
            return 1;
        }
    }

    /// <summary>
    ///     Materializes the run's node runs from its pinned graph and moves it to <c>Running</c>.
    ///     <para>
    ///         Every node gets a row up front, not just the entry ones. Creating them as their branches settle reads
    ///         well until terminalization: a run whose remaining rows do not exist yet has "nothing live" and completes
    ///         before it has run anything. A row that does not exist is still the right answer for a decomposition's
    ///         children — which is why an absent source reads as a pending edge — but for a graph known at run start
    ///         there is nothing to wait for.
    ///     </para>
    ///     <para>
    ///         The run is created <c>Pending</c> and only becomes <c>Running</c> once this has succeeded, because all of
    ///         it can fail — an unparseable graph, a node count over the run's budget. Creating the run <c>Running</c>
    ///         and failing afterwards is the "a run reading Running with nothing driving it" bug the work-session
    ///         service documents one level down.
    ///     </para>
    /// </summary>
    private static async Task<int> StartRunAsync(IDevWorkflowStore store,
        DevWorkflowGraph graph,
        DevWorkflowRunSnapshot run,
        int maxNodeRunsPerRun,
        CancellationToken cancellationToken)
    {
        var existing = await store.ListNodeRunsAsync(run.Id, cancellationToken).ConfigureAwait(false);
        if (existing.Count == 0)
        {
            var workItem = await store.GetWorkItemAsync(run.WorkItemId, cancellationToken).ConfigureAwait(false);
            var templateKeys = graph.Nodes.Values.Where(static node => node.Materialization is not null)
                                    .Select(static node => node.Materialization!.TemplateNodeKey)
                                    .ToHashSet(StringComparer.Ordinal);
            var entryKeys = graph.EntryNodeKeys.Where(key => !templateKeys.Contains(key)).ToHashSet(StringComparer.Ordinal);

            // The operator's request has to reach the first agent, and there is no run-level input column: every ENTRY
            // node run is seeded with it, and the objective composer renders it at the top.
            var inputJson = JsonSerializer.Serialize(new EntryInput(workItem.Request), JsonOptions);
            var seeds = graph.Nodes.Values.Where(node => !templateKeys.Contains(node.NodeKey))
                             .OrderBy(static node => node.NodeKey, StringComparer.Ordinal)
                             .Select(node => new DevWorkflowNodeRunSeed(Guid.NewGuid(),
                                 node.NodeKey,
                                 node.NodeType,
                                 node.MaxAttempts,
                                 node.AgentDefinitionId,
                                 workItem.DevelopmentProjectId,
                                 entryKeys.Contains(node.NodeKey) ? inputJson : null))
                             .ToList();

            if (seeds.Count > maxNodeRunsPerRun)
            {
                throw new DevWorkflowValidationException($"This definition has {seeds.Count} nodes, more than the {maxNodeRunsPerRun} node runs a run may carry.");
            }

            _ = await store.MaterializeNodeRunsAsync(new MaterializeDevWorkflowNodesCommand(run.Id,
                                DevWorkflowVersions.Any,
                                DevWorkflowOperationId.For(run.Id, string.Empty, attempt: 0, "materialize-graph"),
                                seeds),
                            cancellationToken)
                        .ConfigureAwait(false);
        }

        DevWorkflowStateMachine.EnsureLegal(run.Status, DevWorkflowRunStatus.Running);
        _ = await store.TransitionRunAsync(new TransitionDevWorkflowRunCommand(run.Id,
                            DevWorkflowVersions.Any,
                            DevWorkflowRunStatus.Running,
                            WorkItemStatus: DevWorkflowWorkItemStatus.Active),
                        cancellationToken)
                    .ConfigureAwait(false);
        return 1;
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
            DevWorkflowStateMachine.EnsureLegal(nodeRun.Status, target, nodeRun.NodeKey);
            _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(run.Id,
                                nodeRun.Id,
                                DevWorkflowVersions.Any,
                                target,
                                OutputJson: outputJson,
                                FailureClass: target == DevWorkflowNodeRunStatus.Failed ? DevWorkflowFailureClasses.GateRejected : null,
                                TerminalReason: target == DevWorkflowNodeRunStatus.Failed ? "A human abandoned this node run." : null,
                                IncrementAttempt: incrementAttempt,
                                Outcome: outcome),
                            cancellationToken)
                        .ConfigureAwait(false);
            written++;

            // Only a human gate can strand a run this way. Every other node's dead out-edges skip their targets, which
            // is a route rather than a dead end; a gate answer nothing accepts leaves the run with nowhere to go, and
            // saying so is more honest than completing a run whose approval was refused.
            if (outputJson is not null
                && nodeRun.NodeType == DevWorkflowNodeType.HumanGate
                && graph.OutboundEdges(nodeRun.NodeKey) is { Count: > 0 } outEdges
                && !outEdges.Any(edge => Matches(edge, outputJson)))
            {
                rejection ??= $"The gate '{nodeRun.NodeKey}' was answered {settled.Decision}, which none of its branches accepts.";
            }
        }

        return (written, rejection);

        static bool Matches(DevWorkflowGraphEdge edge, string outputJson)
        {
            using var document = JsonDocument.Parse(outputJson);
            return DevWorkflowCondition.Evaluate(edge.Condition, document.RootElement);
        }

        static (DevWorkflowNodeRunStatus Target, string? Outcome, bool IncrementAttempt) Resolve(DevWorkflowDecisionKind decision) =>
            decision switch
            {
                // A gate answer always succeeds the gate: the ANSWER is the node's output, and routing on it is the
                // edges' job. Reject reaches the run through an out-edge that matches nothing, not through a node failure.
                DevWorkflowDecisionKind.Approve => (DevWorkflowNodeRunStatus.Succeeded, DevWorkflowOutcomes.Succeeded, false),
                DevWorkflowDecisionKind.Reject => (DevWorkflowNodeRunStatus.Succeeded, DevWorkflowOutcomes.Rejected, false),
                DevWorkflowDecisionKind.RequestChanges => (DevWorkflowNodeRunStatus.Succeeded, DevWorkflowOutcomes.ChangesRequested, false),

                // Forced: a human retry ignores MaxAttempts, and only the run-wide attempt budget still bounds it.
                DevWorkflowDecisionKind.Retry => (DevWorkflowNodeRunStatus.Pending, null, true),
                DevWorkflowDecisionKind.Skip => (DevWorkflowNodeRunStatus.Skipped, null, false),
                _ => (DevWorkflowNodeRunStatus.Failed, DevWorkflowOutcomes.Failed, false)
            };

        static string Output(DevWorkflowDecisionKind decision) =>
            JsonSerializer.Serialize(new GateOutput(DevWorkflowOutcomes.Succeeded, decision.ToString()), JsonOptions);
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
    private async Task<int> DrainAsync(IDevWorkflowStore store, DevWorkflowRunSnapshot run, CancellationToken cancellationToken)
    {
        var nodeRuns = await store.ListNodeRunsAsync(run.Id, cancellationToken).ConfigureAwait(false);
        var written = 0;

        // Cancelling settles every live node run; pausing settles none. The asymmetry is the point: a pause is meant to
        // be resumed, so it leaves the durable human waits and the not-yet-admitted rows exactly where they are, while
        // a cancel abandons them. Inline nodes hold nothing outside the tick, so for them the row write is the whole of
        // it — the lane executors that DO hold something across a tick pause or kill their own work here once they exist.
        if (run.Status == DevWorkflowRunStatus.Cancelling)
        {
            foreach (var nodeRun in nodeRuns.Where(static nodeRun => DevWorkflowStateMachine.IsLive(nodeRun.Status)))
            {
                DevWorkflowStateMachine.EnsureLegal(nodeRun.Status, DevWorkflowNodeRunStatus.Cancelled, nodeRun.NodeKey);
                _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(run.Id,
                                    nodeRun.Id,
                                    DevWorkflowVersions.Any,
                                    DevWorkflowNodeRunStatus.Cancelled,
                                    FailureClass: DevWorkflowFailureClasses.Cancelled,
                                    TerminalReason: "The run was cancelled."),
                                cancellationToken)
                            .ConfigureAwait(false);
                written++;
            }
        }

        if (nodeRuns.Any(static nodeRun => nodeRun.Status is DevWorkflowNodeRunStatus.Queued or DevWorkflowNodeRunStatus.Running))
        {
            // Still settling. The command already committed its intent, so the UI can say "cancelling" honestly rather
            // than claiming a cancellation that has not landed.
            return written;
        }

        var settledStatus = run.Status == DevWorkflowRunStatus.Pausing ? DevWorkflowRunStatus.Paused : DevWorkflowRunStatus.Cancelled;
        DevWorkflowStateMachine.EnsureLegal(run.Status, settledStatus);
        _ = await store.TransitionRunAsync(new TransitionDevWorkflowRunCommand(run.Id,
                            DevWorkflowVersions.Any,
                            settledStatus,
                            WorkItemStatus: DevWorkflowStateMachine.WorkItemStatusFor(settledStatus)),
                        cancellationToken)
                    .ConfigureAwait(false);
        _graphs.Forget(run.Id);
        return written + 1;
    }

    /// <summary>
    ///     Judges every <c>Pending</c> node run against its inbound edges and runs the ones the inline lane owns.
    /// </summary>
    private static async Task<int> AdmitAsync(IDevWorkflowStore store, DevWorkflowRunSnapshot run, DevWorkflowGraph graph, CancellationToken cancellationToken)
    {
        var nodeRuns = await store.ListNodeRunsAsync(run.Id, cancellationToken).ConfigureAwait(false);
        var byKey = nodeRuns.ToDictionary(static nodeRun => nodeRun.NodeKey, StringComparer.Ordinal);
        var written = 0;

        foreach (var nodeRun in nodeRuns.Where(static nodeRun => nodeRun.Status == DevWorkflowNodeRunStatus.Pending))
        {
            if (!graph.Nodes.TryGetValue(nodeRun.NodeKey, out var node))
            {
                // The run's pinned graph no longer declares this node. Nothing can route it, and nothing should guess.
                written += await BlockAsync(store,
                        run,
                        nodeRun,
                        $"The run's graph no longer declares node '{nodeRun.NodeKey}'.",
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            var admission = DevWorkflowStateMachine.Admission(node, graph, byKey);
            if (admission == DevWorkflowNodeAdmission.Wait)
            {
                continue;
            }

            if (admission == DevWorkflowNodeAdmission.Skip)
            {
                _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(run.Id,
                                    nodeRun.Id,
                                    DevWorkflowVersions.Any,
                                    DevWorkflowNodeRunStatus.Skipped),
                                cancellationToken)
                            .ConfigureAwait(false);
                written++;
                continue;
            }

            written += await DispatchAsync(store, run, graph, node, nodeRun, byKey, cancellationToken).ConfigureAwait(false);
        }

        return written;
    }

    /// <summary>
    ///     Queues an eligible node run and, for the four node types the inline lane owns, runs it in the same tick.
    ///     <para>
    ///         Inline nodes still pass through <c>Queued</c> and <c>Running</c>: it costs two event rows and it is what
    ///         makes the timing of a fan-out visible, which is the only reason Parallel and Join exist as node types at
    ///         all.
    ///     </para>
    ///     <para>
    ///         <b>Seam:</b> the agent, tool and dev-task lanes attach here. Until they do, a node run of one of those
    ///         types is blocked for a human rather than left queued forever — a queue nothing drains is the one answer
    ///         that would look like progress.
    ///     </para>
    /// </summary>
    private static async Task<int> DispatchAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        DevWorkflowGraph graph,
        DevWorkflowGraphNode node,
        DevWorkflowNodeRunSnapshot nodeRun,
        IReadOnlyDictionary<string, DevWorkflowNodeRunSnapshot> byKey,
        CancellationToken cancellationToken)
    {
        if (node.NodeType is DevWorkflowNodeType.Agent or DevWorkflowNodeType.Tool or DevWorkflowNodeType.DevTask)
        {
            return await BlockAsync(store,
                    run,
                    nodeRun,
                    $"This node runs {node.NodeType} work, which no executor on this node can run yet.",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        DevWorkflowStateMachine.EnsureLegal(nodeRun.Status, DevWorkflowNodeRunStatus.Queued, nodeRun.NodeKey);
        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(run.Id,
                            nodeRun.Id,
                            DevWorkflowVersions.Any,
                            DevWorkflowNodeRunStatus.Queued),
                        cancellationToken)
                    .ConfigureAwait(false);
        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(run.Id,
                            nodeRun.Id,
                            DevWorkflowVersions.Any,
                            DevWorkflowNodeRunStatus.Running),
                        cancellationToken)
                    .ConfigureAwait(false);

        if (node.NodeType == DevWorkflowNodeType.HumanGate)
        {
            _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(run.Id,
                                nodeRun.Id,
                                DevWorkflowVersions.Any,
                                DevWorkflowNodeRunStatus.WaitingForApproval,
                                PendingDecisionKind: DevWorkflowDecisionKind.Approve),
                            cancellationToken)
                        .ConfigureAwait(false);
            return 3;
        }

        var outputJson = node.NodeType == DevWorkflowNodeType.Gate
            ? ComposeGateOutput(node, graph, byKey, nodeRun.Attempt)
            : JsonSerializer.Serialize(new InlineOutput(DevWorkflowOutcomes.Succeeded, nodeRun.Attempt, Branch: null), JsonOptions);

        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(run.Id,
                            nodeRun.Id,
                            DevWorkflowVersions.Any,
                            DevWorkflowNodeRunStatus.Succeeded,
                            OutputJson: outputJson),
                        cancellationToken)
                    .ConfigureAwait(false);
        return 3;
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

            writer.WriteString("status", DevWorkflowOutcomes.Succeeded);
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

    private static async Task<int> BlockAsync(IDevWorkflowStore store,
        DevWorkflowRunSnapshot run,
        DevWorkflowNodeRunSnapshot nodeRun,
        string sanitizedReason,
        CancellationToken cancellationToken)
    {
        DevWorkflowStateMachine.EnsureLegal(nodeRun.Status, DevWorkflowNodeRunStatus.Blocked, nodeRun.NodeKey);
        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(run.Id,
                            nodeRun.Id,
                            DevWorkflowVersions.Any,
                            DevWorkflowNodeRunStatus.Blocked,
                            PendingDecisionKind: DevWorkflowDecisionKind.Abandon,
                            FailureClass: DevWorkflowFailureClasses.Configuration,
                            TerminalReason: sanitizedReason),
                        cancellationToken)
                    .ConfigureAwait(false);
        return 1;
    }

    private async Task<int> RecomputeRunStatusAsync(IDevWorkflowStore store, DevWorkflowRunSnapshot run, CancellationToken cancellationToken)
    {
        var current = await store.GetRunAsync(run.Id, cancellationToken).ConfigureAwait(false);
        var nodeRuns = await store.ListNodeRunsAsync(run.Id, cancellationToken).ConfigureAwait(false);
        var target = DevWorkflowStateMachine.Recompute(current.Status, nodeRuns);
        if (target == current.Status)
        {
            return 0;
        }

        DevWorkflowStateMachine.EnsureLegal(current.Status, target);
        _ = await store.TransitionRunAsync(new TransitionDevWorkflowRunCommand(run.Id,
                            DevWorkflowVersions.Any,
                            target,

                            // No run-level failure class: the failing node run already carries the one that explains it,
                            // and a second, coarser copy on the run would only ever be a worse answer to the same question.
                            WorkItemStatus: DevWorkflowStateMachine.WorkItemStatusFor(target)),
                        cancellationToken)
                    .ConfigureAwait(false);

        if (DevWorkflowStateMachine.IsTerminal(target))
        {
            _graphs.Forget(run.Id);
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

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        var runIds = new HashSet<Guid>();
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IDevWorkflowStore>();
            foreach (var status in LiveRunStatuses)
            {
                var runs = await store.ListRunsAsync(workItemId: null, status, _options.MaxConcurrentRuns, cancellationToken).ConfigureAwait(false);
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
    private async Task AdvanceSafelyAsync(Guid runId, CancellationToken cancellationToken)
    {
        try
        {
            _ = await AdvanceOnceAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Development workflow run {RunId} could not be advanced.", runId);
        }
    }

    private sealed record EntryInput(string WorkItemRequest);

    private sealed record GateOutput(string Status, string Decision);

    private sealed record InlineOutput(string Status, int Attempt, string? Branch);
}
