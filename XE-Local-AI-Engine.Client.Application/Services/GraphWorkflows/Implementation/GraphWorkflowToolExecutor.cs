namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Tools;

/// <summary>
///     The <c>Tool</c> lane: ONE named engine tool per node run, invoked in process through
///     <see cref="IToolInvocationService" /> and driven off the tick through
///     <see cref="GraphWorkflowInFlightLane{TResult}" /> rather than inside it.
///     <para>
///         Shape from <see cref="GraphWorkflowAgentExecutor" />, deliberately and to the letter — dispatch to a queue,
///         settle on the poll, stop that answers no on a repeat, forget what a retry superseded. The two lanes differ
///         in what they run and in nothing else, because a second shape is how two lanes come to disagree about
///         whether a row is still being driven.
///     </para>
///     <para>
///         What it does NOT share is the Agent lane's second gate. An agent turn queues twice — once for a lane slot
///         and once for the node-wide invocation lease — while a tool call waits for the lane slot alone, so the row
///         may say <c>Running</c> the moment <see cref="GraphWorkflowInFlightLane{TResult}.TryStartAsync" /> hands
///         back an entry. The lane is what bounds the fan-out: a tool call has no global bottleneck of its own, and a
///         <c>Parallel</c> node feeding two hundred <c>search_knowledge_base</c> nodes would otherwise fire all of
///         them at once. It is sized on <c>MaxConcurrentRuns</c> rather than on a knob of its own — the same "how much
///         of this node may be busy at once" question, and a second option is worth adding only once the two need
///         different numbers.
///     </para>
///     <para>
///         The whole invocation envelope (D6: <c>ReadLocal</c> AND a composed approval of <see langword="false" />)
///         lives inside <see cref="IToolInvocationService" />, so this class enforces none of it and cannot skip any
///         of it. Every refusal arrives as an outcome and becomes a row.
///     </para>
///     <para>
///         <b>Singleton.</b> The lane and its slot count are properties of the node and outlive both a tick and a DI
///         scope. The store it writes through is the scoped one the tick hands it, and the invocation service is a
///         singleton in its own right, so this lane needs no scope of its own.
///     </para>
/// </summary>
internal sealed class GraphWorkflowToolExecutor : IGraphWorkflowNodeExecutor, IAsyncDisposable
{
    /// <summary>What a <c>Queued</c> Tool row is waiting for. It is waiting, not failing, so it carries no event.</summary>
    private const string AwaitingToolSlot = "awaiting-tool-slot";

    /// <summary>What a row says when its tool call was cancelled before it produced a reason of its own.</summary>
    private const string CancelledInFlight = "The run was cancelled while this node run's tool call was in flight.";

    /// <summary>camelCase, matching every other document this product puts on a wire.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly GraphWorkflowInFlightLane<ToolInvocationOutcome> _lane;
    private readonly GraphWorkflowOptions _options;
    private readonly IToolInvocationService _tools;

    public GraphWorkflowToolExecutor(IToolInvocationService tools, IOptions<GraphWorkflowOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Injected as the singleton it is registered as. Unlike the agent lane there is nothing scoped behind it: the
        // service opens whatever scope its own catalog read needs, per call and on purpose.
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _options = options.Value;

        // No discard hook: a tool call is an in-process await, so a cancelled token is the whole of what unwinds one.
        // The agent lane needs one because only its runner knows how to end a turn parked in a provider stream.
        _lane = new GraphWorkflowInFlightLane<ToolInvocationOutcome>(_options.MaxConcurrentRuns);
    }

    public bool Owns(GraphWorkflowNodeKind kind) =>
        kind == GraphWorkflowNodeKind.Tool;

    public bool IsInFlight(Guid nodeRunId) =>
        _lane.IsInFlight(nodeRunId);

    /// <summary>
    ///     Admits an eligible Tool node run, and answers how many transitions it wrote.
    ///     <para>
    ///         The row goes to <c>Queued</c> first ALWAYS, carrying the input document the bindings resolve against,
    ///         and moves on to <c>Running</c> in the same tick once the call actually holds a slot. A lane with none
    ///         free leaves it <c>Queued</c> saying what it waits for, and the next tick asks again.
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
            // THIS attempt's call is already being driven. The only thing that can have left the row behind it is the
            // Running write failing after the slot was taken, so the row is caught up rather than re-run.
            //
            // The attempt is compared rather than assumed: a retry re-attempts a row WITHOUT coming through this lane,
            // and admitting such a row against the call belonging to the attempt before would settle one off the other.
            return nodeRun.Status == GraphWorkflowNodeRunStatus.Queued
                ? await RunningAsync(store, run, nodeRun, cancellationToken).ConfigureAwait(false)
                : 0;
        }

        if (node.Config is not GraphWorkflowToolConfig config)
        {
            // Unreachable through the parser, which types a node's config by its kind. Refused rather than assumed,
            // because the alternative is a NullReferenceException inside a detached task nobody is watching.
            return await FailAsync(store,
                    graph,
                    run,
                    node,
                    nodeRun,
                    GraphWorkflowFailureClass.ValidationFailed,
                    $"Node '{node.NodeKey}' is a Tool node without tool settings.",
                    eventType: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        string inputJson;
        var written = 0;
        if (nodeRun.Status == GraphWorkflowNodeRunStatus.Pending)
        {
            // Composed here rather than in the task body: it is the same set the admission that got us here judged,
            // and it is what the row persists, so a later reader can reconcile the arguments with what was bound.
            inputJson = await InputDocumentAsync(store, graph, node, run, cancellationToken).ConfigureAwait(false);
            GraphWorkflowStateMachine.EnsureLegal(nodeRun.Status, GraphWorkflowNodeRunStatus.Queued, nodeRun.NodeKey);
            _ = await store.TransitionNodeRunAsync(new TransitionGraphWorkflowNodeRunCommand(run.Id,
                                   nodeRun.Id,
                                   GraphWorkflowVersions.Any,
                                   GraphWorkflowNodeRunStatus.Queued,
                                   QueueReason: AwaitingToolSlot,
                                   InputJson: inputJson),
                               cancellationToken)
                           .ConfigureAwait(false);
            nodeRun = nodeRun with
            {
                Status = GraphWorkflowNodeRunStatus.Queued
            };
            written++;
        }
        else
        {
            // A re-offer of a row this lane already queued. Only that first write persists a document, so composing a
            // second one here would resolve the bindings against something the row does not carry.
            inputJson = nodeRun.InputJson ?? await InputDocumentAsync(store, graph, node, run, cancellationToken).ConfigureAwait(false);
        }

        if (!TryResolveArguments(config, inputJson, out var argumentsJson, out var refusal))
        {
            // Never retried, and correctly so: the same document resolves the same way, so a re-attempt would spend an
            // attempt to reach the identical refusal.
            return written + await FailAsync(store, graph, run, node, nodeRun, GraphWorkflowFailureClass.ValidationFailed, refusal, eventType: null, cancellationToken)
                .ConfigureAwait(false);
        }

        // Guid.Empty rather than a minted id: the lane takes one because the agent lane's stop path has to hand its
        // runner something, and a tool call has no such handle — it is an in-process await that its token ends. An id
        // minted here would appear on the row as a correlation nothing else in the system carries.
        var flight = await _lane.TryStartAsync(nodeRun.Id,
                                    nodeRun.Attempt,
                                    Guid.Empty,
                                    (leaseAcquired, token) => InvokeAsync(run.Id, nodeRun.Id, node, config.ToolName, argumentsJson, leaseAcquired, token),
                                    cancellationToken)
                                .ConfigureAwait(false);

        // Queueing, not failure: every slot is held. No event and no failure class — the row's reason says what it is
        // waiting for, and the next tick asks again.
        return flight is null
            ? written
            : written + await RunningAsync(store, run, nodeRun, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Settles the row if its tool call has landed, and answers how many transitions that wrote.</summary>
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
            // restart, which is exactly what the startup reconciler collapses such rows for. Reaching here means it
            // did not, so the row is judged for what it is rather than swept forever. Never resumed — the call died
            // with the process, and whether it is tried again is the retry stage's answer, not this one's.
            return await FailAsync(store,
                    graph,
                    run,
                    node,
                    nodeRun,
                    GraphWorkflowFailures.Classify(GraphWorkflowFailureClass.Interrupted, nodeRun.Attempt, node.MaxAttempts),
                    "The host stopped while this node run's tool call was in flight.",
                    GraphWorkflowEventTypes.NodeInterrupted,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!flight.Work.IsCompleted)
        {
            return nodeRun.Status == GraphWorkflowNodeRunStatus.Queued
                ? await RunningAsync(store, run, nodeRun, cancellationToken).ConfigureAwait(false)
                : 0;
        }

        int written;
        if (flight.Work.IsCanceled)
        {
            // Defence rather than an expected path: the invocation service answers every cancellation with an outcome
            // instead of throwing. Checked anyway, and BEFORE the await, because awaiting a cancelled task would
            // rethrow, the dispatcher would swallow it, and the row would rethrow again on every tick forever.
            written = await SettleCancelledAsync(store, run, nodeRun, CancelledInFlight, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // A Queued row whose call landed between ticks: the state machine has no Queued → Succeeded edge, and
            // deliberately so — an attempt that produced an answer ran, whatever the row managed to say while it did.
            written = nodeRun.Status == GraphWorkflowNodeRunStatus.Queued
                ? await RunningAsync(store, run, nodeRun, cancellationToken).ConfigureAwait(false)
                : 0;
            nodeRun = nodeRun with
            {
                Status = GraphWorkflowNodeRunStatus.Running
            };

            var outcome = await flight.Work.ConfigureAwait(false);
            written += outcome.Kind == ToolInvocationOutcomeKind.Cancelled
                ? await SettleCancelledAsync(store, run, nodeRun, outcome.Reason, cancellationToken).ConfigureAwait(false)
                : await SettleLandedAsync(store, graph, run, node, nodeRun, ToolNameOf(node), outcome, cancellationToken).ConfigureAwait(false);
        }

        // Consumed only once the settle has COMMITTED. Doing it first would spend the answer on a write that may throw
        // — an over-cap document, a lost version race — and the next poll would then find no entry, take the branch
        // above and record "the host stopped" about a call that finished perfectly.
        _lane.Consume(nodeRun.Id);
        return written;
    }

    /// <summary>
    ///     Asks a call to stop, answering whether there was anything to ask. The lane's token is the whole of it: a
    ///     tool call is an in-process await, and there is no runner to tell.
    /// </summary>
    public Task<bool> StopAsync(Guid nodeRunId) =>

        // The no-on-a-repeat answer is the lane's, and it is the whole reason a cancelling drain does not spin.
        _lane.StopAsync(nodeRunId);

    public Task DiscardAsync(Guid nodeRunId) =>
        _lane.DiscardAsync(nodeRunId);

    public Task ForgetSupersededAsync(IReadOnlyList<GraphWorkflowNodeRunSnapshot> nodeRuns) =>
        _lane.ForgetSupersededAsync(nodeRuns);

    public ValueTask DisposeAsync() =>
        _lane.DisposeAsync();

    /// <summary>
    ///     One tool call, off the tick. It NEVER faults: <see cref="IToolInvocationService.InvokeAsync" /> answers
    ///     every refusal, timeout, cancellation and fault with an outcome, which is what lets the poll settle a landed
    ///     call without ever rethrowing.
    /// </summary>
    private Task<ToolInvocationOutcome> InvokeAsync(Guid runId,
        Guid nodeRunId,
        GraphWorkflowGraphNode node,
        string toolName,
        string argumentsJson,
        StrongBox<bool> leaseAcquired,
        CancellationToken cancellationToken)
    {
        // Flipped immediately, and honestly: the lane slot this body already holds is the only thing a tool call ever
        // waits for, so there is no second gate for the box to report on the way the agent lane's lease is.
        leaseAcquired.Value = true;

        // The graph author's own budget, which the service enforces as a hard deadline over the whole call — argument
        // validation included — so the dispatcher's expiry stage stays a backstop rather than a race with the answer.
        var timeout = TimeSpan.FromSeconds(node.TimeoutSeconds ?? _options.DefaultNodeTimeoutSeconds);
        return _tools.InvokeAsync(toolName, argumentsJson, new ToolInvocationContext(runId, nodeRunId, node.NodeKey, timeout), cancellationToken);
    }

    /// <summary>
    ///     The arguments this call is made with: the node's literals, then every binding on top.
    ///     <para>
    ///         <b>A binding overwrites a literal of the same name</b>, which is the only coherent reading of a node
    ///         that sets both — a literal is the default the author typed, a binding is what the run computed. Each
    ///         path resolves against the node's INPUT document through the module's own dot-path walk, so a binding's
    ///         grammar is byte-identical to an edge condition's, and the resolved element is inserted VERBATIM: a
    ///         bound number stays a number for the tool's schema to read.
    ///     </para>
    ///     <para>
    ///         A path the document does not carry refuses the call. The reason names the argument and the path and
    ///         NEVER the document, which carries whatever an upstream node wrote.
    ///     </para>
    /// </summary>
    private static bool TryResolveArguments(GraphWorkflowToolConfig config, string inputJson, out string argumentsJson, out string refusal)
    {
        refusal = string.Empty;
        var arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (config.Arguments is { ValueKind: JsonValueKind.Object } literals)
        {
            foreach (var literal in literals.EnumerateObject())
            {
                arguments[literal.Name] = literal.Value;
            }
        }

        foreach (var (name, path) in config.ArgumentBindings)
        {
            if (GraphWorkflowDocuments.Resolve(inputJson, path) is not { } bound)
            {
                argumentsJson = string.Empty;
                refusal = $"The argument '{name}' binds to '{path}', which this node run's input document does not carry.";
                return false;
            }

            arguments[name] = bound;
        }

        // Dictionary KEYS are written verbatim under the web defaults — only property names of a type are camelCased —
        // so an argument the tool's schema spells 'maxResults' arrives spelled that way.
        argumentsJson = JsonSerializer.Serialize(arguments, JsonOptions);
        return true;
    }

    /// <summary>
    ///     The tool this node names. A node run only reaches the poll through a dispatch that already refused a config
    ///     of the wrong shape, so the fallback is unreachable and is the node's own key rather than an empty string.
    /// </summary>
    private static string ToolNameOf(GraphWorkflowGraphNode node) =>
        node.Config is GraphWorkflowToolConfig config ? config.ToolName : node.NodeKey;

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

    /// <summary>Moves the row to <c>Running</c>, which for a tool call is the moment it holds its lane slot.</summary>
    private static async Task<int> RunningAsync(IGraphWorkflowStore store,
        GraphWorkflowRunSnapshot run,
        GraphWorkflowNodeRunSnapshot nodeRun,
        CancellationToken cancellationToken)
    {
        GraphWorkflowStateMachine.EnsureLegal(nodeRun.Status, GraphWorkflowNodeRunStatus.Running, nodeRun.NodeKey);
        _ = await store.TransitionNodeRunAsync(new TransitionGraphWorkflowNodeRunCommand(run.Id,
                               nodeRun.Id,
                               GraphWorkflowVersions.Any,
                               GraphWorkflowNodeRunStatus.Running),
                           cancellationToken)
                       .ConfigureAwait(false);
        return 1;
    }

    /// <summary>
    ///     Turns one landed outcome into a document and a terminal status.
    ///     <para>
    ///         <c>Executed</c> succeeds; <c>UnknownTool</c>, <c>NotInvocable</c> and <c>InvalidArguments</c> fail
    ///         <c>ValidationFailed</c> and are therefore never re-attempted; <c>Timeout</c> and <c>Faulted</c> fail on
    ///         the two retryable classes. The service's own reason is repeated verbatim, and it is structural by
    ///         contract — it never echoes an argument value.
    ///     </para>
    /// </summary>
    private async Task<int> SettleLandedAsync(IGraphWorkflowStore store,
        GraphWorkflowGraph graph,
        GraphWorkflowRunSnapshot run,
        GraphWorkflowGraphNode node,
        GraphWorkflowNodeRunSnapshot nodeRun,
        string toolName,
        ToolInvocationOutcome outcome,
        CancellationToken cancellationToken)
    {
        if (outcome.Kind != ToolInvocationOutcomeKind.Executed)
        {
            var failureClass = outcome.Kind switch
            {
                ToolInvocationOutcomeKind.UnknownTool or ToolInvocationOutcomeKind.NotInvocable or ToolInvocationOutcomeKind.InvalidArguments =>
                    GraphWorkflowFailureClass.ValidationFailed,
                ToolInvocationOutcomeKind.Timeout => GraphWorkflowFailureClass.Timeout,
                _ => GraphWorkflowFailureClass.NodeFailed
            };

            // Classified at the moment of the failing write, like every other failure this runtime records: the state
            // machine has no Failed → Failed edge, so nothing can re-classify one afterwards.
            return await FailAsync(store,
                    graph,
                    run,
                    node,
                    nodeRun,
                    GraphWorkflowFailures.Classify(failureClass, nodeRun.Attempt, node.MaxAttempts),
                    outcome.Reason,
                    eventType: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        string document;
        try
        {
            document = GraphWorkflowDocuments.Compose(graph,
                node,
                nodeRun.Attempt,
                GraphWorkflowNodeOutputStatuses.Succeeded,
                GraphWorkflowDocuments.ToolOutput(outcome.Result),
                _options.MaxOutputJsonBytes);
        }
        catch (GraphWorkflowOutputTooLargeException exception)
        {
            // A real, reachable outcome: a knowledge-base search may legitimately answer with fifty thousand
            // characters. Not retryable, and deliberately: the same call composes the same bytes. The tool is named
            // beside the node the exception already names, because "which node" alone does not say what to shrink.
            return await FailAsync(store,
                    graph,
                    run,
                    node,
                    nodeRun,
                    GraphWorkflowFailureClass.OutputTooLarge,
                    $"{exception.Message} Its tool was '{toolName}'.",
                    eventType: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        GraphWorkflowStateMachine.EnsureLegal(nodeRun.Status, GraphWorkflowNodeRunStatus.Succeeded, nodeRun.NodeKey);
        _ = await store.TransitionNodeRunAsync(new TransitionGraphWorkflowNodeRunCommand(run.Id,
                               nodeRun.Id,
                               GraphWorkflowVersions.Any,
                               GraphWorkflowNodeRunStatus.Succeeded,
                               OutputJson: document),
                           cancellationToken)
                       .ConfigureAwait(false);
        return 1;
    }

    /// <summary>A call that ended because it was asked to. Not a failure, and it carries no document of its own.</summary>
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
}
