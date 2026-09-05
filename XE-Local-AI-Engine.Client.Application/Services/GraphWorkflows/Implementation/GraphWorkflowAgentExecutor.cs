namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     What one agent turn cost, as the node run's output document reports it. Every member is nullable because the
///     runner reports what its provider gave it and no provider reports all of them.
/// </summary>
internal sealed record GraphWorkflowAgentUsage(int? InputTokens,
    int? OutputTokens,
    int? TotalTokens,
    int? ReasoningTokens,
    long? DurationMs,
    string? FinishReason,
    string? Model);

/// <summary>
///     What one agent turn came to. It is a RESULT and not a row: the lane produces it off the tick, and the poll is
///     the only thing that turns it into a status.
///     <para>
///         Carries a failure class rather than an exception because the task body catches everything — a turn that
///         faulted would leave the poll rethrowing on every tick forever, about work that is long over.
///     </para>
/// </summary>
internal sealed record GraphWorkflowAgentTurn(bool Succeeded,
    GraphWorkflowFailureClass FailureClass,
    string? SanitizedReason,
    string Text,
    JsonElement? Json,
    GraphWorkflowAgentUsage? Usage);

/// <summary>
///     The <c>Agent</c> lane: a headless saved-agent invocation, driven off the tick through
///     <see cref="GraphWorkflowInFlightLane{TResult}" /> and never inside it.
///     <para>
///         Shape from the development-workflow TOOL lane rather than its agent lane, and the reason is the restart
///         verdict: a work session is durable and pollable across one, an <see cref="IInvocationRunner" /> turn is an
///         in-process task with no durable handle. That is why an interrupted <c>Running</c> Agent row is failed rather
///         than resumed, and why <see cref="GraphWorkflowNodeRun.InvocationId" /> is written at all — it is the
///         correlation id in the node logs for a turn nothing else survives.
///     </para>
///     <para>
///         Contents from <c>RunSavedAgentHandler</c>, which is the node's other unattended caller of this stack: the
///         locality gate before capacity, the capacity reservation disposed on every terminal path, the approval-required
///         tools stripped from the offer, <c>IsUnattended</c>, and the terminal state read off a
///         <see cref="StrongBox{T}" /> the state-changed handler fills.
///     </para>
///     <para>
///         <b>Singleton.</b> The lane and its slot count are properties of the node and outlive both a tick and a DI
///         scope, so every scoped collaborator is resolved inside the task body from its own scope — the scope the tick
///         handed the store is gone long before the turn lands.
///     </para>
/// </summary>
internal sealed class GraphWorkflowAgentExecutor : IGraphWorkflowNodeExecutor, IAsyncDisposable
{
    /// <summary>What a <c>Queued</c> Agent row is waiting for. It is waiting, not failing, so it carries no event.</summary>
    private const string AwaitingAgentSlot = "awaiting-agent-slot";

    /// <summary>What a row says when its turn was cancelled before it produced a reason of its own.</summary>
    private const string CancelledInFlight = "The run was cancelled while this node run's agent turn was in flight.";

    /// <summary>
    ///     The finish reasons a row's terminal reason may repeat. Everything else reads <c>unknown</c>: the token is
    ///     provider-supplied, and a row's reason column is not the place to find out what a provider can put in one.
    /// </summary>
    private static readonly string[] KnownFinishReasons = ["stop", "length", "tool_calls", "content_filter"];

    /// <summary>camelCase, matching every other document this product puts on a wire.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IInvocationRunner _invocationRunner;
    private readonly GraphWorkflowInFlightLane<GraphWorkflowAgentTurn> _lane;
    private readonly ILogger<GraphWorkflowAgentExecutor> _logger;
    private readonly GraphWorkflowOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;

    public GraphWorkflowAgentExecutor(IServiceScopeFactory scopeFactory,
        IInvocationRunner invocationRunner,
        IOptions<GraphWorkflowOptions> options,
        ILogger<GraphWorkflowAgentExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

        // The runner is a SINGLETON and is injected as one: the stop and discard paths are documented as non-blocking,
        // and opening a DI scope per cancel to reach a singleton is work a hot drain loop pays for nothing.
        _invocationRunner = invocationRunner ?? throw new ArgumentNullException(nameof(invocationRunner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options.Value;

        // Sized on the run cap rather than on an option of its own: what this bounds is how many node runs may be
        // parked on the node-wide invocation lease at once, and a node that may run four workflows concurrently has no
        // reason to let a fifth turn queue inside this process as well.
        _lane = new GraphWorkflowInFlightLane<GraphWorkflowAgentTurn>(_options.MaxConcurrentRuns,
            // A dropped turn's token is not enough on its own: the runner is what knows how to unwind one parked in a
            // provider stream, and a superseded entry never comes back through StopAsync to say so.
            flight => CancelInvocation(flight.InvocationId));
    }

    public bool Owns(GraphWorkflowNodeKind kind) =>
        kind == GraphWorkflowNodeKind.Agent;

    public bool IsInFlight(Guid nodeRunId) =>
        _lane.IsInFlight(nodeRunId);

    /// <summary>
    ///     Admits an eligible Agent node run, and answers how many transitions it wrote.
    ///     <para>
    ///         The row goes to <c>Queued</c> first ALWAYS, even when a slot is free a line later, and stays there until
    ///         the turn holds the node-wide invocation lease. Three parallel Agent nodes on a node with one invocation
    ///         slot therefore read <c>Running, Queued, Queued</c> rather than leaving a reader to infer it from timing.
    ///     </para>
    /// </summary>
    public async Task<int> DispatchAsync(IGraphWorkflowStore store,
        GraphWorkflowRunSnapshot run,
        GraphWorkflowGraph graph,
        GraphWorkflowGraphNode node,
        GraphWorkflowNodeRunSnapshot nodeRun,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(nodeRun);

        if (_lane.TryGet(nodeRun.Id, out var existing) && existing.Attempt == nodeRun.Attempt)
        {
            // THIS attempt's turn is already being driven. The only thing that can have left the row behind it is the
            // Running write failing after the slot was taken, so the row is caught up rather than re-run — a second
            // turn would spend a whole model call to arrive at the answer already coming.
            //
            // The attempt is compared rather than assumed: a retry re-attempts a row WITHOUT coming through this lane,
            // and admitting such a row against the turn belonging to the attempt before would settle one off the other.
            return nodeRun.Status == GraphWorkflowNodeRunStatus.Queued && existing.LeaseAcquired.Value
                ? await RunningAsync(store, run, nodeRun, existing.InvocationId, inputJson: null, cancellationToken).ConfigureAwait(false)
                : 0;
        }

        if (node.Config is not GraphWorkflowAgentConfig config)
        {
            // Unreachable through the parser, which types a node's config by its kind. Refused rather than assumed,
            // because the alternative is a NullReferenceException inside a detached task nobody is watching.
            return await FailAsync(store,
                    graph,
                    run,
                    node,
                    nodeRun,
                    GraphWorkflowFailureClass.ValidationFailed,
                    $"Node '{node.NodeKey}' is an Agent node without agent settings.",
                    eventType: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        string inputJson;
        var written = 0;
        if (nodeRun.Status == GraphWorkflowNodeRunStatus.Pending)
        {
            // Composed here rather than in the task body: it is the same set the admission that got us here judged, and
            // reading the rows again a model call later could see a different world.
            inputJson = await InputDocumentAsync(store, graph, node, run, cancellationToken).ConfigureAwait(false);
            GraphWorkflowStateMachine.EnsureLegal(nodeRun.Status, GraphWorkflowNodeRunStatus.Queued, nodeRun.NodeKey);
            _ = await store.TransitionNodeRunAsync(new TransitionGraphWorkflowNodeRunCommand(run.Id,
                                   nodeRun.Id,
                                   GraphWorkflowVersions.Any,
                                   GraphWorkflowNodeRunStatus.Queued,
                                   QueueReason: AwaitingAgentSlot,
                                   InputJson: inputJson),
                               cancellationToken)
                           .ConfigureAwait(false);
            written++;
        }
        else
        {
            // A re-offer of a row this lane already queued. Only that first write persists a document, so composing a
            // second one here would hand the turn something the row does not carry — and something a later reader of
            // the row could not reconcile with the answer it produced.
            inputJson = nodeRun.InputJson ?? await InputDocumentAsync(store, graph, node, run, cancellationToken).ConfigureAwait(false);
        }

        // Minted HERE, before the task starts: it is the first argument of the runtime package request AND what the
        // stop path hands the runner, so a turn that minted it privately would leave the cancel path with nothing to
        // call for the whole of its first tick.
        var invocationId = Guid.NewGuid();
        var flight = await _lane.TryStartAsync(nodeRun.Id,
                                   nodeRun.Attempt,
                                   invocationId,
                                   (leaseAcquired, token) => RunTurnAsync(run.Id, nodeRun.Id, node, config, invocationId, inputJson, leaseAcquired, token),
                                   cancellationToken)
                               .ConfigureAwait(false);
        if (flight is null)
        {
            // Queueing, not failure: every slot is held. No event and no failure class — the row's reason says what it
            // is waiting for, and the next tick asks again.
            return written;
        }

        // Still Queued, deliberately. The turn has started but holds no node-wide slot yet, and a row that read Running
        // here would be claiming a model is working on it while it waits behind an interactive chat turn. The catch-up
        // to Running happens on the tick that first sees the lease land — through this method or through the poll,
        // whichever reaches the row first.
        return written;
    }

    /// <summary>
    ///     Settles the row if its turn has landed, and answers how many transitions that wrote.
    /// </summary>
    public async Task<int> PollAsync(IGraphWorkflowStore store,
        GraphWorkflowRunSnapshot run,
        GraphWorkflowGraph graph,
        GraphWorkflowGraphNode node,
        GraphWorkflowNodeRunSnapshot nodeRun,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(nodeRun);

        if (!_lane.TryGet(nodeRun.Id, out var flight))
        {
            // Nothing on this node is driving this row and nothing ever will: the lane holds no memory across a
            // restart, which is exactly what the startup reconciler collapses such rows for. Reaching here means it did
            // not, so the row is judged for what it is rather than swept forever. Never resumed — the model's partial
            // output died with the process, and whether it is tried again is the retry stage's answer, not this one's.
            return await FailAsync(store,
                    graph,
                    run,
                    node,
                    nodeRun,
                    GraphWorkflowFailures.Classify(GraphWorkflowFailureClass.Interrupted, nodeRun.Attempt, node.MaxAttempts),
                    "The host stopped while this node run's agent turn was in flight.",
                    GraphWorkflowEventTypes.NodeInterrupted,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!flight.Work.IsCompleted)
        {
            return nodeRun.Status == GraphWorkflowNodeRunStatus.Queued && flight.LeaseAcquired.Value
                ? await RunningAsync(store, run, nodeRun, flight.InvocationId, inputJson: null, cancellationToken).ConfigureAwait(false)
                : 0;
        }

        int written;
        if (flight.Work.IsCanceled)
        {
            // Checked BEFORE the await, and that is the load-bearing half of this branch: a stop can cancel a turn
            // still parked on the invocation lease, which ends it Canceled with no state to map. Awaiting it would
            // rethrow, the dispatcher would swallow it, and the row would rethrow again on every tick forever.
            written = await SettleCancelledAsync(store, run, nodeRun, CancelledInFlight, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // A Queued row whose turn landed between ticks: the state machine has no Queued → Succeeded edge, and
            // deliberately so — an attempt that produced an answer ran, whatever the row managed to say while it did.
            written = nodeRun.Status == GraphWorkflowNodeRunStatus.Queued
                ? await RunningAsync(store, run, nodeRun, flight.InvocationId, inputJson: null, cancellationToken).ConfigureAwait(false)
                : 0;
            nodeRun = nodeRun with
            {
                Status = GraphWorkflowNodeRunStatus.Running
            };

            var turn = await flight.Work.ConfigureAwait(false);

            // A turn that ended Cancelled WITHOUT this node asking — the shutdown drain, a model eject, a CancelAll —
            // returns normally with a cancelled terminal rather than a cancelled task, so it reaches here rather than
            // the branch above. It is still a cancellation: settling it as a failure would classify it, fail the row
            // and make the run recompute Failed for work nobody judged.
            written += turn.FailureClass == GraphWorkflowFailureClass.Cancelled
                ? await SettleCancelledAsync(store, run, nodeRun, turn.SanitizedReason ?? CancelledInFlight, cancellationToken).ConfigureAwait(false)
                : await SettleLandedAsync(store, graph, run, node, nodeRun, turn, cancellationToken).ConfigureAwait(false);
        }

        // Consumed only once the settle has COMMITTED. Doing it first would spend the answer on a write that may throw
        // — an over-cap document, a lost version race — and the next poll would then find no entry, take the branch
        // above and record "the host stopped" about a turn that finished perfectly.
        _lane.Consume(nodeRun.Id);
        return written;
    }

    /// <summary>
    ///     Asks a turn to stop, answering whether there was anything to ask. The runner is told as well as the token:
    ///     only it knows how to unwind a turn parked in a provider stream.
    /// </summary>
    public async Task<bool> StopAsync(Guid nodeRunId)
    {
        // The no-on-a-repeat answer is the lane's, and it is the whole reason a cancelling drain does not spin. See
        // GraphWorkflowInFlightLane.StopAsync for the ceiling that buys.
        if (!_lane.TryGet(nodeRunId, out var flight) || !await _lane.StopAsync(nodeRunId).ConfigureAwait(false))
        {
            return false;
        }

        CancelInvocation(flight.InvocationId);
        return true;
    }

    /// <summary>Drops the entry outright — the lane's discard hook cancels the invocation behind it.</summary>
    public Task DiscardAsync(Guid nodeRunId) =>
        _lane.DiscardAsync(nodeRunId);

    public Task ForgetSupersededAsync(IReadOnlyList<GraphWorkflowNodeRunSnapshot> nodeRuns) =>
        _lane.ForgetSupersededAsync(nodeRuns);

    public ValueTask DisposeAsync() =>
        _lane.DisposeAsync();

    /// <summary>
    ///     One agent turn, start to finish, off the tick. It NEVER faults: every step answers with a
    ///     <see cref="GraphWorkflowAgentTurn" /> and one outer catch maps the unforeseen, so the poll never has to
    ///     rethrow. Cancellation is the one exception that leaves here, and it leaves as a cancelled TASK.
    /// </summary>
    private async Task<GraphWorkflowAgentTurn> RunTurnAsync(Guid runId,
        Guid nodeRunId,
        GraphWorkflowGraphNode node,
        GraphWorkflowAgentConfig config,
        Guid invocationId,
        string inputJson,
        StrongBox<bool> leaseAcquired,
        CancellationToken cancellationToken)
    {
        IDisposable? reservation = null;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var services = scope.ServiceProvider;

            // 1. The node's agent. A node naming one that has since been deleted is a configuration error, not a run
            //    that should be re-attempted; a node naming none takes the resolver's default persona.
            string? pinnedModel = null;
            if (config.AgentDefinitionId is { } agentDefinitionId)
            {
                var definition = await services.GetRequiredService<IAgentDefinitionStore>().GetByIdAsync(agentDefinitionId, cancellationToken).ConfigureAwait(false);
                if (definition is null)
                {
                    return Invalid("The agent this node runs could not be found. It may have been deleted.");
                }

                pinnedModel = string.IsNullOrWhiteSpace(definition.ModelProfile) ? null : definition.ModelProfile;
            }

            // 2. The EFFECTIVE model: what the node pins, else what the agent pins, else the node's local default.
            //    The same settings carry MaxMessageRequestTimeoutSeconds, which is deliberately NOT read — step 6 says
            //    why: the graph author declared this node's budget, and the runner must end before the deadline stage.
            var nodeSettings = await services.GetRequiredService<INodeSettingsStore>().LoadAsync(cancellationToken).ConfigureAwait(false);
            var localDefault = await services.GetRequiredService<ILocalDefaultChatModelResolver>()
                                             .ResolveAsync(nodeSettings.DefaultModelName, cancellationToken)
                                             .ConfigureAwait(false);
            var effectiveModel = config.Model ?? pinnedModel ?? localDefault;
            if (string.IsNullOrWhiteSpace(effectiveModel))
            {
                return Invalid("No local chat model is available to run this agent node. Install a local model or pin one to the agent.");
            }

            // 3. LOCALITY GATE. Classified on the EFFECTIVE model and refused before capacity and before any
            //    invocation: a graph workflow run is unattended by construction, so node-local prompt and upstream
            //    content is never handed to a cloud model.
            var capabilities = await services.GetRequiredService<IModelCapabilityResolver>().ResolveAsync(effectiveModel, cancellationToken).ConfigureAwait(false);
            if (capabilities.IsCloud)
            {
                _logger.LogInformation("Graph workflow run {RunId} refused node '{NodeKey}': its effective model is cloud-hosted and unattended runs are node-local only.",
                    runId,
                    node.NodeKey);
                return Invalid("Graph workflow agent nodes are restricted to node-local models. This node's effective model is a cloud model, so it will not run unattended.");
            }

            // 4. CAPACITY. A local Allow carries a footprint reservation that MUST be released on every terminal path:
            //    a leaked one wrongly rejects later spawns node-wide.
            var decision = await services.GetRequiredService<ICapacityService>().DecideAsync(effectiveModel, ModelRole.Chat, cancellationToken).ConfigureAwait(false);
            if (decision.Verdict == CapacityVerdict.RejectInsufficient)
            {
                return Failure(GraphWorkflowFailureClass.NodeFailed, decision.Reason);
            }

            reservation = decision.Reservation;

            // The seed prompt is also the retrieval query below, so it is built before the resolve rather than beside
            // the package: a playbook gated on a blank query injects its full static prepend instead of the relevant
            // slice, and that difference is a different resolved prompt.
            var seedPrompt = SeedPrompt(config, inputJson);

            // 5. The agent's COMPLETE runtime. honorModelProfile is FALSE exactly when this node names its own model:
            //    with a bare true, a node overriding a cloud-pinned agent to a local one would pass step 3 on its own
            //    choice while the resolver gated the offer against — and returned — the cloud pin.
            var resolved = await services.GetRequiredService<IAgentDefinitionResolver>()
                                         .ResolveAsync(config.AgentDefinitionId,
                                             effectiveModel,
                                             seedPrompt,
                                             capabilities.SupportsTools,
                                             config.Model is null,
                                             activeModelIsCloud: false,
                                             cancellationToken)
                                         .ConfigureAwait(false);
            if (resolved is null)
            {
                // The definition existed at step 1 and was deleted before the resolve finished (rare race).
                return Invalid("The agent this node runs could not be found. It may have been deleted.");
            }

            // 6. The headless package.
            var package = BuildPackage(services.GetRequiredService<ILocalChatRuntimePackageBuilder>(),
                resolved,
                node,
                config,
                effectiveModel,
                capabilities,
                invocationId,
                seedPrompt);

            // 8–10. The lease, the terminal capture, and the run.
            var terminal = await RunInvocationAsync(services.GetRequiredService<IWorkerEventDispatcher>(),
                    _invocationRunner,
                    package,
                    leaseAcquired,
                    cancellationToken)
                .ConfigureAwait(false);

            // 11. What the turn came to.
            return Map(terminal, config);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The detail belongs in the node logs and nowhere else: a row's reason is read by an operator, and a raw
            // provider error is neither safe nor useful there.
            _logger.LogWarning(exception,
                "Graph workflow run {RunId} node run {NodeRunId} ('{NodeKey}', invocation {InvocationId}) could not complete its agent turn.",
                runId,
                nodeRunId,
                node.NodeKey,
                invocationId);
            return Failure(GraphWorkflowFailureClass.NodeFailed, "This node run's agent turn did not complete. See the node logs for details.");
        }
        finally
        {
            // The outermost finally, on success, failure, refusal and cancellation alike.
            reservation?.Dispose();
        }
    }

    /// <summary>
    ///     Takes the node-wide invocation slot, runs the package, and answers the terminal state the runner reported.
    ///     <para>
    ///         The slot is taken BEFORE the run and flips <paramref name="leaseAcquired" />, which is the moment the
    ///         row may honestly say <c>Running</c>. The runner has no return value, so the result comes back through
    ///         <see cref="IWorkerEventDispatcher.InvocationStateChanged" /> — held in a
    ///         <see cref="StrongBox{T}" /> rather than a local, which flow analysis would otherwise prove always-null
    ///         because it cannot see the handler fire synchronously from the completion report.
    ///     </para>
    /// </summary>
    private static async Task<InvocationState?> RunInvocationAsync(IWorkerEventDispatcher eventDispatcher,
        IInvocationRunner invocationRunner,
        RuntimePackage package,
        StrongBox<bool> leaseAcquired,
        CancellationToken cancellationToken)
    {
        var terminalState = new StrongBox<InvocationState?>(null);

        void OnInvocationStateChanged(object? sender, InvocationStateChangedEventArgs args)
        {
            if (args.State.InvocationId == package.InvocationId
                && args.State.Status is InvocationStatus.Completed or InvocationStatus.Failed or InvocationStatus.Cancelled)
            {
                terminalState.Value = args.State;
            }
        }

        var lease = await eventDispatcher.ReportInvocationAssignedAsync(package, cancellationToken).ConfigureAwait(false);
        leaseAcquired.Value = true;
        eventDispatcher.InvocationStateChanged += OnInvocationStateChanged;
        try
        {
            using var executionContext = InvocationExecutionContext.CreatePlain(package, Guid.Empty);
            await invocationRunner.RunAsync(executionContext, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            eventDispatcher.InvocationStateChanged -= OnInvocationStateChanged;
            await lease.DisposeAsync().ConfigureAwait(false);
        }

        // The runner reports Cancelled and returns normally rather than rethrowing, so a stop would otherwise land here
        // as an ordinary terminal and settle the row as a failure instead of a cancellation.
        cancellationToken.ThrowIfCancellationRequested();
        return terminalState.Value;
    }

    /// <summary>
    ///     The headless loopback package for one node's turn.
    ///     <para>
    ///         Approval-required tools are stripped: an unattended run has no approval round-trip, so an approval-gated
    ///         tool would surface a request nobody can answer. <c>IsUnattended</c> is what covers the rest of it —
    ///         skill tools arrive through MAF's context providers and never through the offer, so stripping alone
    ///         cannot reach them.
    ///     </para>
    ///     <para>
    ///         The whole-turn deadline is the NODE's own <c>timeoutSeconds</c>, deliberately not the node-level
    ///         "maximum message request timeout" the scheduler uses: the runner must end first, or the dispatcher's
    ///         30-second-grace expiry stops being a backstop and becomes a race with the answer.
    ///     </para>
    /// </summary>
    private RuntimePackage BuildPackage(ILocalChatRuntimePackageBuilder packageBuilder,
        ResolvedAgentRuntime resolved,
        GraphWorkflowGraphNode node,
        GraphWorkflowAgentConfig config,
        string effectiveModel,
        ModelCapabilitySnapshot capabilities,
        Guid invocationId,
        string seedPrompt)
    {
        var offeredTools = resolved.AllowedTools.Where(static tool => !tool.RequiresApproval).ToArray();
        var strippedTools = resolved.AllowedTools.Where(static tool => tool.RequiresApproval).ToArray();
        if (strippedTools.Length > 0)
        {
            _logger.LogWarning("Graph workflow node '{NodeKey}' stripped {StrippedCount} approval-required tool(s) ({StrippedTools}) from its unattended offer: a graph workflow run has no approval round-trip.",
                node.NodeKey,
                strippedTools.Length,
                string.Join(", ", strippedTools.Select(static tool => tool.Name)));
        }

        // An unrecognized override normalizes to null and falls back to the agent's own effort. It must never reach the
        // builder, whose own normalize would drop it to null and suppress reasoning the agent asked for.
        var reasoningEffort = ReasoningEffortNormalizer.Normalize(config.ReasoningEffort) ?? resolved.ReasoningEffort;

        var seedTurn = new ConversationMessageDto
        {
            Id = Guid.NewGuid(),
            Role = MessageRole.User,
            Content = seedPrompt,
            SortOrder = 0
        };

        return packageBuilder.Build(new LocalChatRuntimePackageRequest(invocationId,
            Guid.NewGuid(),
            resolved.ResolvedSystemPrompt,
            [seedTurn],

            // Always the effective model, never resolved.ModelProfile: this node's own choice is what step 3 gated and
            // what the offer was built against, and binding the pin instead would run the turn on an ungated model.
            effectiveModel,
            resolved.AgentDefinitionVersion,
            LocalChatLoopbackDefaults.ClientNodeId,
            offeredTools,
            RequestedCapabilities: [LocalChatLoopbackDefaults.RequestedCapability],
            Timeouts: new TimeoutSettings
            {
                InvocationTimeoutSeconds = node.TimeoutSeconds ?? _options.DefaultNodeTimeoutSeconds
            },
            ReasoningEffort: reasoningEffort,

            // Threaded rather than defaulted: the builder defaults SupportsThinking to true, so omitting these claims a
            // capability the model may not have.
            SupportsThinking: capabilities.SupportsThinking,
            Skills: resolved.Skills,
            IsUnattended: true,
            ResponseJsonSchema: config.ResponseJsonSchema,
            ReasoningBudgetEnforceable: capabilities.ReasoningBudgetEnforceable));
    }

    /// <summary>
    ///     What the runner's terminal state means for this node run.
    ///     <para>
    ///         A node declaring a response schema must come back a JSON OBJECT. A parse failure fails
    ///         <c>NodeFailed</c> — the RETRYABLE class, because a re-ask under the same grammar can land where one
    ///         attempt did not — with the finish reason named, since a truncated answer is still
    ///         <c>Completed</c> and <c>length</c> is the common cause. There is deliberately no salvage path: grammar-
    ///         constrained output carries no fences, and stripping some would quietly mask a broken grammar.
    ///     </para>
    /// </summary>
    private static GraphWorkflowAgentTurn Map(InvocationState? terminal, GraphWorkflowAgentConfig config)
    {
        switch (terminal?.Status)
        {
            case null:
                return Failure(GraphWorkflowFailureClass.NodeFailed, "The agent turn reported no result.");

            case InvocationStatus.Failed when terminal.FailureCategory == FailureCategory.Timeout:
                // The runner's own watchdog reports a timeout as a FAILED terminal, so the category is the only place
                // it survives. Still retryable, and classed Timeout rather than NodeFailed because the answer to a
                // node that ran out of time is a different one from the answer to a node whose provider said no.
                return Failure(GraphWorkflowFailureClass.Timeout, "The agent turn ran out of time before it answered. See the node logs for details.");

            case InvocationStatus.Failed:
                // Never the raw provider error: the detail is in the node logs.
                return Failure(GraphWorkflowFailureClass.NodeFailed, "The agent turn failed. See the node logs for details.");

            case InvocationStatus.Cancelled:
                return Failure(GraphWorkflowFailureClass.Cancelled, "The agent turn was interrupted before it completed.");
        }

        var text = terminal.StreamedContent;
        var usage = new GraphWorkflowAgentUsage(terminal.InputTokens,
            terminal.OutputTokens,
            terminal.TotalTokens,
            terminal.ReasoningTokens,
            terminal.GenerationDurationMs,
            terminal.FinishReason,
            terminal.ModelUsed);

        if (config.ResponseJsonSchema is null)
        {
            return new GraphWorkflowAgentTurn(Succeeded: true, GraphWorkflowFailureClass.None, SanitizedReason: null, text, Json: null, usage);
        }

        try
        {
            using var parsed = JsonDocument.Parse(text);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                return SchemaFailure(terminal.FinishReason);
            }

            return new GraphWorkflowAgentTurn(Succeeded: true, GraphWorkflowFailureClass.None, SanitizedReason: null, text, parsed.RootElement.Clone(), usage);
        }
        catch (JsonException)
        {
            return SchemaFailure(terminal.FinishReason);
        }
    }

    /// <summary>
    ///     The finish reason is ALLOW-LISTED rather than interpolated: it arrives verbatim from a provider, and a row's
    ///     reason is an operator-facing column. An unrecognized token still says the turn ended, just not in words the
    ///     provider chose.
    /// </summary>
    private static GraphWorkflowAgentTurn SchemaFailure(string? finishReason) =>
        Failure(GraphWorkflowFailureClass.NodeFailed,
            $"The agent did not answer with the JSON object its response schema requires (finish reason '{Known(finishReason)}').");

    private static string Known(string? finishReason) =>
        Array.Find(KnownFinishReasons, known => string.Equals(known, finishReason, StringComparison.Ordinal)) ?? "unknown";

    private static GraphWorkflowAgentTurn Invalid(string reason) =>
        Failure(GraphWorkflowFailureClass.ValidationFailed, reason);

    private static GraphWorkflowAgentTurn Failure(GraphWorkflowFailureClass failureClass, string reason) =>
        new(Succeeded: false, failureClass, GraphWorkflowStateMachine.Bounded(reason, GraphWorkflowStateMachine.MaxTerminalReason), Text: string.Empty, Json: null, Usage: null);

    /// <summary>
    ///     The seed user turn's content: the node's instructions, followed by the upstream documents when the node asks
    ///     for them.
    ///     <para>
    ///         Inlined rather than referenced, which is this codebase's established upstream-consumption pattern —
    ///         there is no dereference tool an agent could call. Budgeted at <c>MaxRunInputBytes</c>, the same bound the
    ///         run's own input carries, and truncated with an explicit marker rather than silently: a prompt that lost
    ///         half its evidence without saying so is how a node produces a confident wrong answer.
    ///     </para>
    /// </summary>
    private string SeedPrompt(GraphWorkflowAgentConfig config, string inputJson)
    {
        if (!config.IncludeUpstreamOutputs)
        {
            return config.Instructions;
        }

        JsonElement upstream;
        try
        {
            using var document = JsonDocument.Parse(inputJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("upstream", out var element)
                || element.ValueKind != JsonValueKind.Object
                || !element.EnumerateObject().Any())
            {
                return config.Instructions;
            }

            upstream = element.Clone();
        }
        catch (JsonException)
        {
            return config.Instructions;
        }

        var rendered = TruncateUtf8(upstream.GetRawText(), _options.MaxRunInputBytes);
        return $"{config.Instructions}\n\nThe nodes before this one produced:\n\n```json\n{rendered}\n```";
    }

    /// <summary>
    ///     At most <paramref name="maxBytes" /> UTF-8 bytes, never cutting a character in half, with what was dropped
    ///     stated on its own line.
    /// </summary>
    private static string TruncateUtf8(string text, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= maxBytes)
        {
            return text;
        }

        // Back off the cut while it lands on a UTF-8 continuation byte, which is the middle of a character.
        var cut = maxBytes;
        while (cut > 0 && (bytes[cut] & 0xC0) == 0x80)
        {
            cut--;
        }

        return $"{Encoding.UTF8.GetString(bytes, index: 0, cut)}\n… truncated, {bytes.Length - cut} bytes omitted";
    }

    /// <summary>The input document this node run reads, over the SATISFIED inbound edges the admission judged.</summary>
    private static async Task<string> InputDocumentAsync(IGraphWorkflowStore store,
        GraphWorkflowGraph graph,
        GraphWorkflowGraphNode node,
        GraphWorkflowRunSnapshot run,
        CancellationToken cancellationToken)
    {
        var byKey = (await store.ListNodeRunsAsync(run.Id, cancellationToken).ConfigureAwait(false))
            .ToDictionary(static nodeRun => nodeRun.NodeKey, StringComparer.Ordinal);
        return GraphWorkflowDocuments.ComposeInput(run.InputJson, GraphWorkflowInlineExecutor.Upstream(graph, node, byKey));
    }

    /// <summary>Moves the row to <c>Running</c> and stamps the invocation id the node logs correlate on.</summary>
    private static async Task<int> RunningAsync(IGraphWorkflowStore store,
        GraphWorkflowRunSnapshot run,
        GraphWorkflowNodeRunSnapshot nodeRun,
        Guid invocationId,
        string? inputJson,
        CancellationToken cancellationToken)
    {
        GraphWorkflowStateMachine.EnsureLegal(nodeRun.Status, GraphWorkflowNodeRunStatus.Running, nodeRun.NodeKey);
        _ = await store.TransitionNodeRunAsync(new TransitionGraphWorkflowNodeRunCommand(run.Id,
                               nodeRun.Id,
                               GraphWorkflowVersions.Any,
                               GraphWorkflowNodeRunStatus.Running,
                               InputJson: inputJson,
                               InvocationId: invocationId),
                           cancellationToken)
                       .ConfigureAwait(false);
        return 1;
    }

    /// <summary>Turns one landed turn into a document and a terminal status.</summary>
    private async Task<int> SettleLandedAsync(IGraphWorkflowStore store,
        GraphWorkflowGraph graph,
        GraphWorkflowRunSnapshot run,
        GraphWorkflowGraphNode node,
        GraphWorkflowNodeRunSnapshot nodeRun,
        GraphWorkflowAgentTurn turn,
        CancellationToken cancellationToken)
    {
        var status = turn.Succeeded ? GraphWorkflowNodeRunStatus.Succeeded : GraphWorkflowNodeRunStatus.Failed;
        string document;
        try
        {
            document = GraphWorkflowDocuments.Compose(graph,
                node,
                nodeRun.Attempt,
                turn.Succeeded ? GraphWorkflowNodeOutputStatuses.Succeeded : GraphWorkflowNodeOutputStatuses.Failed,
                Output(turn),
                _options.MaxOutputJsonBytes);
        }
        catch (GraphWorkflowOutputTooLargeException exception)
        {
            // Not retryable, and deliberately: the same turn composes the same bytes, and this one is already spent.
            return await FailAsync(store, graph, run, node, nodeRun, GraphWorkflowFailureClass.OutputTooLarge, exception.Message, eventType: null, cancellationToken)
                .ConfigureAwait(false);
        }

        GraphWorkflowStateMachine.EnsureLegal(nodeRun.Status, status, nodeRun.NodeKey);
        _ = await store.TransitionNodeRunAsync(new TransitionGraphWorkflowNodeRunCommand(run.Id,
                               nodeRun.Id,
                               GraphWorkflowVersions.Any,
                               status,
                               OutputJson: document,

                               // Classified at the moment of the failing write, like every other failure this runtime
                               // records: the state machine has no Failed → Failed edge, so nothing can re-classify one
                               // afterwards.
                               FailureClass: turn.Succeeded ? null : GraphWorkflowFailures.Classify(turn.FailureClass, nodeRun.Attempt, node.MaxAttempts),
                               TerminalReason: turn.SanitizedReason),
                           cancellationToken)
                       .ConfigureAwait(false);
        return 1;
    }

    /// <summary>A turn that ended because it was asked to. Not a failure, and it carries no document of its own.</summary>
    private static async Task<int> SettleCancelledAsync(IGraphWorkflowStore store,
        GraphWorkflowRunSnapshot run,
        GraphWorkflowNodeRunSnapshot nodeRun,
        string sanitizedReason,
        CancellationToken cancellationToken)
    {
        GraphWorkflowStateMachine.EnsureLegal(nodeRun.Status, GraphWorkflowNodeRunStatus.Cancelled, nodeRun.NodeKey);
        _ = await store.TransitionNodeRunAsync(new TransitionGraphWorkflowNodeRunCommand(run.Id,
                               nodeRun.Id,
                               GraphWorkflowVersions.Any,
                               GraphWorkflowNodeRunStatus.Cancelled,
                               FailureClass: GraphWorkflowFailureClass.Cancelled,
                               TerminalReason: GraphWorkflowStateMachine.Bounded(sanitizedReason, GraphWorkflowStateMachine.MaxTerminalReason)),
                           cancellationToken)
                       .ConfigureAwait(false);
        return 1;
    }

    /// <summary>
    ///     Fails the row with the document that failure produces, walking a <c>Pending</c> row through <c>Running</c>
    ///     first for the same reason the dispatcher does: a failure about an attempt needs an attempt to have opened.
    /// </summary>
    private async Task<int> FailAsync(IGraphWorkflowStore store,
        GraphWorkflowGraph graph,
        GraphWorkflowRunSnapshot run,
        GraphWorkflowGraphNode node,
        GraphWorkflowNodeRunSnapshot nodeRun,
        GraphWorkflowFailureClass failureClass,
        string sanitizedReason,
        string? eventType,
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

        GraphWorkflowStateMachine.EnsureLegal(nodeRun.Status, GraphWorkflowNodeRunStatus.Failed, nodeRun.NodeKey);
        _ = await store.TransitionNodeRunAsync(new TransitionGraphWorkflowNodeRunCommand(run.Id,
                               nodeRun.Id,
                               GraphWorkflowVersions.Any,
                               GraphWorkflowNodeRunStatus.Failed,
                               OutputJson: document,
                               FailureClass: failureClass,
                               TerminalReason: GraphWorkflowStateMachine.Bounded(sanitizedReason, GraphWorkflowStateMachine.MaxTerminalReason),
                               EventType: eventType),
                           cancellationToken)
                       .ConfigureAwait(false);
        return written + 1;
    }

    /// <summary>The Agent <c>output</c> shape, per the binding document contract.</summary>
    private static JsonElement Output(GraphWorkflowAgentTurn turn) =>
        JsonSerializer.SerializeToElement(new AgentOutputPayload(turn.Text, turn.Json, turn.Usage), JsonOptions);

    /// <summary>Tells the runner to unwind a turn. A cancel for an invocation it no longer knows about is a no-op.</summary>
    private void CancelInvocation(Guid invocationId) =>
        _invocationRunner.Cancel(invocationId);

    private sealed record AgentOutputPayload(string Text, JsonElement? Json, GraphWorkflowAgentUsage? Usage);
}
