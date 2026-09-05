namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;

using System.Text;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The command surface over a graph workflow run. Everything it does is validate, commit, signal, and answer with
///     what the rows now say — the dispatcher does the rest on its own clock.
/// </summary>
internal sealed class GraphWorkflowRunService(IGraphWorkflowStore store, IGraphWorkflowDispatcherSignal signal, IOptions<GraphWorkflowOptions> options)
    : IGraphWorkflowRunService
{
    private readonly GraphWorkflowOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    private readonly IGraphWorkflowDispatcherSignal _signal = signal ?? throw new ArgumentNullException(nameof(signal));
    private readonly IGraphWorkflowStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<GraphWorkflowRunDetail> StartAsync(Guid definitionId,
        Guid requestId,
        string? inputJson,
        int? definitionVersion,
        CancellationToken cancellationToken = default)
    {
        if (requestId == Guid.Empty)
        {
            throw new GraphWorkflowValidationException("A graph workflow run needs a caller-minted request id.");
        }

        // The FAST path, not the gate. Two genuinely concurrent identical starts can both pass it, which is why the
        // insert below is the real idempotency guarantee.
        if (await _store.FindRunByRequestAsync(requestId, cancellationToken).ConfigureAwait(false) is { } replayed)
        {
            // A replay has to be a replay of THIS request. A reused request id naming a different definition is a
            // caller bug, and answering it with another run would hand out a run they never asked for.
            if (replayed.DefinitionId != definitionId)
            {
                throw new GraphWorkflowInvalidTransitionException($"Request '{requestId}' already started a run of a different graph workflow definition.");
            }

            // Signalled, not merely composed: a replay is what a caller sends when it never saw the first answer, and
            // the run may still be waiting for its first tick.
            return await SignalAndComposeAsync(replayed.Id, cancellationToken).ConfigureAwait(false);
        }

        var definition = await _store.GetDefinitionAsync(definitionId, cancellationToken).ConfigureAwait(false);
        if (definitionVersion is { } expected && expected != definition.Version)
        {
            throw new GraphWorkflowRunConflictException($"Graph workflow definition '{definition.Name}' is at version {definition.Version}, "
                                                        + $"not the version {expected} this run was started against.");
        }

        if (inputJson is not null && Encoding.UTF8.GetByteCount(inputJson) > _options.MaxRunInputBytes)
        {
            throw new GraphWorkflowValidationException($"The run input is larger than the {_options.MaxRunInputBytes} bytes one run may carry.");
        }

        // Validated again HERE, not trusted from save time: an agent definition can be deleted between the two, and the
        // parse is the same one the dispatcher routes with.
        var graph = GraphWorkflowGraph.Parse(definition.GraphJson);
        EnsureToolNodesAreRunnable(graph);

        if (graph.Nodes.Count > _options.MaxNodeRunsPerRun)
        {
            throw new GraphWorkflowValidationException($"The graph declares {graph.Nodes.Count} nodes, more than the {_options.MaxNodeRunsPerRun} node runs "
                                                       + "one run may instantiate.");
        }

        // ONE call. The run row, one Pending node run per graph node and the run.created event commit together, and the
        // definition is re-read inside that same transaction so a delete racing this start cannot leave an orphan run.
        var run = await _store.StartRunAsync(new StartGraphWorkflowRunCommand(Guid.NewGuid(),
                                  requestId,
                                  definitionId,
                                  definition.Version,
                                  definition.GraphHash,
                                  definition.GraphJson,
                                  inputJson,
                                  [.. graph.Nodes.Values.Select(static node => new GraphWorkflowNodeRunSeed(Guid.NewGuid(), node.NodeKey, node.Kind))]),
                              cancellationToken)
                              .ConfigureAwait(false);

        return await SignalAndComposeAsync(run.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GraphWorkflowRunDetail> CancelAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (GraphWorkflowStateMachine.IsTerminal(run.Status))
        {
            throw new GraphWorkflowRunConflictException($"This run is already {run.Status}, so there is nothing to cancel.");
        }

        // A repeat cancel is the SAME ask answered again, not a conflict: the intent is already committed and the drain
        // is already running, so this mirrors the start replay — accepted, idempotent, and signalled, because a caller
        // that never saw the first answer is exactly the caller that sends this one.
        if (run.Status == GraphWorkflowRunStatus.Cancelling)
        {
            return await SignalAndComposeAsync(runId, cancellationToken).ConfigureAwait(false);
        }

        GraphWorkflowStateMachine.EnsureLegal(run.Status, GraphWorkflowRunStatus.Cancelling);

        // Against the version it was READ at, so a recomputation that landed in between loses rather than silently
        // overwriting an operator's intent with a status it decided a moment earlier.
        //
        // Node runs are deliberately NOT settled here: the dispatcher drains them, asking each live lane to stop rather
        // than writing a terminal status over work that is still in flight.
        _ = await _store.TransitionRunAsync(new TransitionGraphWorkflowRunCommand(runId, run.Version, GraphWorkflowRunStatus.Cancelling),
                            cancellationToken)
                        .ConfigureAwait(false);
        return await SignalAndComposeAsync(runId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GraphWorkflowRunDetail> GetRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
        await ComposeAsync(await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    public Task<IReadOnlyList<GraphWorkflowRunSnapshot>> ListRunsAsync(GraphWorkflowRunStatus? status,
        int limit,
        CancellationToken cancellationToken = default) =>
        _store.ListRunsAsync(status, limit, cancellationToken);

    public Task<GraphWorkflowNodeRunSnapshot> GetNodeRunAsync(Guid runId, string nodeKey, CancellationToken cancellationToken = default) =>
        _store.GetNodeRunAsync(runId, nodeKey, cancellationToken);

    public async Task<GraphWorkflowRunEventPage> ListEventsAsync(Guid runId, long afterSeq, CancellationToken cancellationToken = default)
    {
        if (afterSeq < 0)
        {
            throw new GraphWorkflowValidationException("An event watermark cannot be negative.");
        }

        // One over the cap, so truncation is observed rather than inferred from a full page.
        var events = await _store.ListEventsAsync(runId, afterSeq, _options.EventReplayLimit + 1, cancellationToken).ConfigureAwait(false);
        var page = events.Take(_options.EventReplayLimit).ToList();
        return new GraphWorkflowRunEventPage(page, page.Count == 0 ? afterSeq : page[^1].Seq, events.Count > _options.EventReplayLimit);
    }

    /// <summary>
    ///     Re-checks, at run start, that every <c>Tool</c> node of the pinned graph names a tool this node will actually
    ///     run — ruling D6's gate, as ONE mechanism in one place rather than a check the dispatcher repeats per kind.
    ///     <para>
    ///         The body is empty because there is no tool catalog to ask yet: the tool executor and its catalog land in
    ///         the pause-and-tool slice, which fills this in. Until then a bad tool name does not block run creation —
    ///         the run starts and its <c>Tool</c> node fails at dispatch, because the dispatch switch has no arm for
    ///         that kind. That is an absent case, and it is stated here so the consequence is not a surprise.
    ///     </para>
    /// </summary>
    private static void EnsureToolNodesAreRunnable(GraphWorkflowGraph graph) =>

        // No body beyond the guard: there is no tool catalog on this node yet, so there is nothing to check the
        // graph's tool names against. The graph is taken NOW rather than added later, so the pause-and-tool slice
        // fills this in without touching the call site.
        ArgumentNullException.ThrowIfNull(graph);

    /// <summary>
    ///     Signals AFTER the commit, which is the whole of this service's obligation to the dispatcher: without it a
    ///     fresh run would sit visibly <c>Pending</c> until the next sweep, for no reason a reader could see.
    /// </summary>
    private async Task<GraphWorkflowRunDetail> SignalAndComposeAsync(Guid runId, CancellationToken cancellationToken)
    {
        _signal.Signal(runId);
        return await ComposeAsync(await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    private async Task<GraphWorkflowRunDetail> ComposeAsync(GraphWorkflowRunSnapshot run, CancellationToken cancellationToken) =>
        new(run, await _store.ListNodeRunsAsync(run.Id, cancellationToken).ConfigureAwait(false));
}
