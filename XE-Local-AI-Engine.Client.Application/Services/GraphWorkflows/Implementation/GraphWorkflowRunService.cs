namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Tools;

/// <summary>
///     The command surface over a graph workflow run. Everything it does is validate, commit, signal, and answer with
///     what the rows now say — the dispatcher does the rest on its own clock.
/// </summary>
internal sealed class GraphWorkflowRunService(
    IGraphWorkflowStore store,
    IGraphWorkflowDispatcherSignal signal,
    IToolInvocationService tools,
    IOptions<GraphWorkflowOptions> options) : IGraphWorkflowRunService
{
    /// <summary>
    ///     The longest comment an answer may carry, matching the development-workflow gate's own cap. It is free text
    ///     beside the act rather than part of it, and an unbounded one would ride inside the node's output envelope.
    /// </summary>
    private const int MaxDecisionComment = 500;

    private readonly GraphWorkflowOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    private readonly IGraphWorkflowDispatcherSignal _signal = signal ?? throw new ArgumentNullException(nameof(signal));
    private readonly IGraphWorkflowStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IToolInvocationService _tools = tools ?? throw new ArgumentNullException(nameof(tools));

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
            EnsureReplayIsOfTheSameDefinition(replayed, definitionId, requestId);

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
        await EnsureToolNodesAreRunnableAsync(graph, cancellationToken).ConfigureAwait(false);

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

        // The lookup above is a fast path both concurrent callers can pass, and the store answers a lost race on the
        // request id with the run that WON — which may be a run of somebody else's definition. Re-checked here, or the
        // loser of that race would receive a run it never asked for by the one route the fast path cannot cover.
        EnsureReplayIsOfTheSameDefinition(run, definitionId, requestId);

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

    public async Task<GraphWorkflowDecisionResult> DecideAsync(Guid runId,
        string nodeKey,
        Guid operationId,
        GraphWorkflowDecisionKind decision,
        string? comment,
        string? payloadJson,
        string? decidedBySubject,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(nodeKey))
        {
            throw new GraphWorkflowValidationException("A graph workflow decision names the pause it answers by node key.");
        }

        if (operationId == Guid.Empty)
        {
            throw new GraphWorkflowValidationException("A graph workflow decision needs a caller-minted operation id.");
        }

        // 1. REPLAY FIRST, and run-wide — the scope the store's filtered unique index enforces. Looked up before the
        // (run, node key) row is read at all: without this, one id reused across two pauses of a run passes every check
        // below and then violates that index inside the write, as a database error rather than the conflict this API
        // promises.
        if (await _store.FindNodeRunByDecisionOperationAsync(runId, operationId, cancellationToken).ConfigureAwait(false) is { } recorded)
        {
            return await ReplayAsync(runId, nodeKey, operationId, decision, decidedBySubject, recorded, cancellationToken).ConfigureAwait(false);
        }

        // 2. The row must be waiting — and a row that is not gets the SAME resolution a lost write does, replay
        // lookup first. Two identical requests both miss step 1, one commits, and the other reads a Succeeded row: it
        // is this caller's own answer arriving twice, so refusing it here would 409 a decision that did land.
        var nodeRun = await _store.GetNodeRunAsync(runId, nodeKey, cancellationToken).ConfigureAwait(false);
        if (nodeRun.Status != GraphWorkflowNodeRunStatus.WaitingForApproval)
        {
            return await LostTheRaceAsync(runId, nodeKey, operationId, decision, decidedBySubject, cancellationToken).ConfigureAwait(false);
        }

        // 3. The run must be live. A drain is already settling this row, and a terminal run has no tick left to route
        // the answer with — but the SAME resolution as step 2, replay lookup first: this caller's own answer can have
        // committed under its own id and the run stopped between the row read and here, and refusing then would 409 a
        // decision that did land.
        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run.Status is GraphWorkflowRunStatus.Cancelling || GraphWorkflowStateMachine.IsTerminal(run.Status))
        {
            return await LostTheRaceAsync(runId, nodeKey, operationId, decision, decidedBySubject, cancellationToken).ConfigureAwait(false);
        }

        // 4. The answer must be one the PINNED graph offers. A graph that does not offer it is wrong, not the request.
        var graph = GraphWorkflowGraph.Parse(run.GraphJson);
        if (!graph.Nodes.TryGetValue(nodeKey, out var node) || node.Config is not GraphWorkflowPauseConfig pause)
        {
            throw new GraphWorkflowRunConflictException($"The run's pinned graph no longer declares '{nodeKey}' as a Pause node.");
        }

        if (!pause.AllowedDecisions.Contains(decision))
        {
            throw new GraphWorkflowRunConflictException($"The pause '{nodeKey}' offers {string.Join(", ", pause.AllowedDecisions)}, so it cannot be answered {decision}.");
        }

        // 5. Body rules. Everything here is about the REQUEST rather than about the run, which is what makes them 400s.
        var payload = ValidateBody(nodeKey, pause, comment, payloadJson);

        // 6. Composed through the one document writer, so a pause row gets the same envelope, the same branch
        // derivation and the same size check as every other kind — and the same `output.decision` spelling the
        // definition-time pre-flight evaluated.
        string document;
        try
        {
            document = GraphWorkflowDocuments.Compose(graph,
                node,
                nodeRun.Attempt,
                GraphWorkflowNodeOutputStatuses.Succeeded,
                GraphWorkflowDocuments.PauseOutput(decision, comment, payload),
                _options.MaxOutputJsonBytes);
        }
        catch (GraphWorkflowOutputTooLargeException exception)
        {
            // Unreachable while the payload cap stays strictly under the envelope budget, and kept because that is a
            // relation between two options rather than a fact: an operator's oversized answer is their 400 to fix, not
            // a node failure they cannot see the cause of.
            throw new GraphWorkflowValidationException(exception.Message);
        }

        // 7. ONE conditional write: the status move, the decision columns, the output and the gate.decided event. It
        // can lose in TWO ways, and both are the same story to the caller. A null answer means the compare-and-set
        // matched no row. An exception means the run row's own concurrency token lost, or the store converted the
        // unique index, which is what two operators answering at once with different operation ids produce. Letting
        // the second escape would reach the client as a bare run conflict with no standing decision, in exactly the
        // case the standing decision exists to describe.
        GraphWorkflowMutationResult? written;
        try
        {
            written = await _store.DecideNodeRunAsync(new DecideGraphWorkflowNodeRunCommand(runId,
                                          nodeRun.Id,
                                          GraphWorkflowVersions.Any,
                                          operationId,
                                          decision,
                                          decidedBySubject,
                                          document),
                                      cancellationToken)
                                  .ConfigureAwait(false);
        }
        catch (GraphWorkflowInvalidTransitionException)
        {
            return await LostTheRaceAsync(runId, nodeKey, operationId, decision, decidedBySubject, cancellationToken).ConfigureAwait(false);
        }

        if (written is null)
        {
            return await LostTheRaceAsync(runId, nodeKey, operationId, decision, decidedBySubject, cancellationToken).ConfigureAwait(false);
        }

        // 8. The run follows its rows, written against the version this read saw; then the dispatcher is told, AFTER
        // the commit, so the downstream nodes are admitted on its own clock rather than inside this request.
        await RecomputeRunStatusAsync(runId, graph, cancellationToken).ConfigureAwait(false);
        _signal.Signal(runId);
        return await ComposeDecisionAsync(runId, nodeKey, decision, cancellationToken).ConfigureAwait(false);
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
    ///     The same act arriving twice. It IS the same act only if it names the same pause, the same answer and the
    ///     same person — a reused id naming any of those differently would read as success for a decision nobody took.
    ///     Comment and payload are deliberately not compared: free text around the act, not the act.
    /// </summary>
    private async Task<GraphWorkflowDecisionResult> ReplayAsync(Guid runId,
        string nodeKey,
        Guid operationId,
        GraphWorkflowDecisionKind decision,
        string? decidedBySubject,
        GraphWorkflowNodeRunSnapshot recorded,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(recorded.NodeKey, nodeKey, StringComparison.Ordinal))
        {
            throw StandingConflict(recorded, $"Operation '{operationId}' already decided the pause '{recorded.NodeKey}' of this run.");
        }

        if (GraphWorkflowStateMachine.DecisionOf(recorded.OutputJson) != decision
            || !string.Equals(recorded.DecidedBySubject, decidedBySubject, StringComparison.Ordinal))
        {
            throw StandingConflict(recorded, $"Operation '{operationId}' already recorded a different decision on the pause '{nodeKey}'.");
        }

        return await ComposeDecisionAsync(runId, nodeKey, decision, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     What a decide write that wrote nothing means, and what a row that was no longer waiting when it was read
    ///     means: something committed between this caller's checks and its write. Answered off what the rows NOW say
    ///     rather than by retrying — this same operation id committed it, which is a replay; the run stopped being
    ///     live, which is a run conflict; or another operation id answered the pause, which is the second human act.
    /// </summary>
    private async Task<GraphWorkflowDecisionResult> LostTheRaceAsync(Guid runId,
        string nodeKey,
        Guid operationId,
        GraphWorkflowDecisionKind decision,
        string? decidedBySubject,
        CancellationToken cancellationToken)
    {
        if (await _store.FindNodeRunByDecisionOperationAsync(runId, operationId, cancellationToken).ConfigureAwait(false) is { } settled)
        {
            return await ReplayAsync(runId, nodeKey, operationId, decision, decidedBySubject, settled, cancellationToken).ConfigureAwait(false);
        }

        // Before the row: the store also declines once the RUN stops being live, and answering that with a node-status
        // refusal would name the pause when the cancel is the reason — or, worse, name a standing decision on a row the
        // drain has since cancelled.
        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run.Status is GraphWorkflowRunStatus.Cancelling || GraphWorkflowStateMachine.IsTerminal(run.Status))
        {
            throw new GraphWorkflowRunConflictException($"This run is {run.Status}, so the pause '{nodeKey}' can no longer be answered.");
        }

        var current = await _store.GetNodeRunAsync(runId, nodeKey, cancellationToken).ConfigureAwait(false);
        throw StandingConflict(current, $"Node run '{nodeKey}' is {current.Status}, so there is nothing to decide on it.");
    }

    /// <summary>
    ///     The refusal a row that is not open to a decision earns: one that NAMES the answer that stands where the row
    ///     was actually answered, so the second person to click is told what was decided rather than only that their
    ///     click failed, and S1's generic run conflict where it was not.
    ///     <para>
    ///         Gated on <c>DecisionOperationId</c>, the column an answered gate writes — NOT on the output document
    ///         carrying an <c>output.decision</c>. A <c>Condition</c> or <c>Parallel</c> node downstream of an answered
    ///         pause passes that predecessor's output through verbatim, so reading the document alone would report a
    ///         standing decision for a node nobody ever decided.
    ///     </para>
    /// </summary>
    private static Exception StandingConflict(GraphWorkflowNodeRunSnapshot nodeRun, string message) =>
        nodeRun.DecisionOperationId is not null && GraphWorkflowStateMachine.DecisionOf(nodeRun.OutputJson) is { } standing
            ? new GraphWorkflowGateAlreadyDecidedException($"{message} It was answered {standing}.", standing)
            : new GraphWorkflowRunConflictException(message);

    /// <summary>
    ///     The request-shaped rules, and the parsed payload they admit. All 400s: they are about what was sent rather
    ///     than about what the run is.
    /// </summary>
    private JsonElement? ValidateBody(string nodeKey, GraphWorkflowPauseConfig pause, string? comment, string? payloadJson)
    {
        if (pause.RequireComment && string.IsNullOrWhiteSpace(comment))
        {
            throw new GraphWorkflowValidationException($"The pause '{nodeKey}' requires a comment with its answer.");
        }

        if (comment is { Length: > MaxDecisionComment })
        {
            throw new GraphWorkflowValidationException($"A decision comment is longer than the {MaxDecisionComment}-character limit.");
        }

        if (payloadJson is null)
        {
            return null;
        }

        // Strictly under the envelope budget rather than equal to it: an at-cap payload would pass here and then
        // overflow the document it is embedded in, turning an operator's 400 into a node failure.
        var maxPayloadBytes = _options.MaxOutputJsonBytes / 2;
        if (Encoding.UTF8.GetByteCount(payloadJson) > maxPayloadBytes)
        {
            throw new GraphWorkflowValidationException($"The decision payload is larger than the {maxPayloadBytes} bytes an answer may carry.");
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.Clone()
                : throw new GraphWorkflowValidationException("A decision payload is a JSON object.");
        }
        catch (JsonException)
        {
            throw new GraphWorkflowValidationException("A decision payload is a JSON object.");
        }
    }

    /// <summary>
    ///     The run status that follows its rows, written against the version it was read at.
    ///     <para>
    ///         Deliberately NON-terminal only: terminalization carries the run's own result off the End node that
    ///         succeeded, and that is the dispatcher's write in the tick this decision has just signalled. The one move
    ///         this owns is <c>WaitingForApproval → Running</c>, which is what makes the answer this method returns
    ///         honest instead of a status the caller would have to re-read to disbelieve.
    ///     </para>
    /// </summary>
    private async Task RecomputeRunStatusAsync(Guid runId, GraphWorkflowGraph graph, CancellationToken cancellationToken)
    {
        var current = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        var nodeRuns = await _store.ListNodeRunsAsync(runId, cancellationToken).ConfigureAwait(false);
        var outcome = GraphWorkflowStateMachine.Recompute(current.Status, graph, nodeRuns);
        if (outcome.Status == current.Status
            || GraphWorkflowStateMachine.IsTerminal(outcome.Status)
            || !GraphWorkflowStateMachine.IsLegal(current.Status, outcome.Status))
        {
            return;
        }

        try
        {
            _ = await _store.TransitionRunAsync(new TransitionGraphWorkflowRunCommand(runId, current.Version, outcome.Status), cancellationToken).ConfigureAwait(false);
        }
        catch (GraphWorkflowInvalidTransitionException)
        {
            // A concurrent writer moved the run between the read and this write. The decision itself is committed, and
            // the tick this call is about to signal recomputes the same answer from the same rows.
        }
    }

    private async Task<GraphWorkflowDecisionResult> ComposeDecisionAsync(Guid runId,
        string nodeKey,
        GraphWorkflowDecisionKind decision,
        CancellationToken cancellationToken)
    {
        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        var nodeRun = await _store.GetNodeRunAsync(runId, nodeKey, cancellationToken).ConfigureAwait(false);
        return new GraphWorkflowDecisionResult(decision, run.Status, nodeRun.Status);
    }

    /// <summary>
    ///     Refuses a run that a request id resolved to but the caller did not ask for. ONE spelling for both routes to
    ///     it — the serial fast path and the loser of a concurrent insert — because they are the same caller bug.
    /// </summary>
    private static void EnsureReplayIsOfTheSameDefinition(GraphWorkflowRunSnapshot run, Guid definitionId, Guid requestId)
    {
        if (run.DefinitionId != definitionId)
        {
            throw new GraphWorkflowInvalidTransitionException($"Request '{requestId}' already started a run of a different graph workflow definition.");
        }
    }

    /// <summary>
    ///     Re-checks, at run start, that every <c>Tool</c> node of the pinned graph names a tool this node will actually
    ///     run — ruling D6's gate, as ONE mechanism in one place rather than a check the dispatcher repeats per kind.
    ///     <para>
    ///         Asked again here rather than trusted from save time: a definition saved when a tool was invocable must
    ///         not start once the envelope has been tightened away from it. Failing the START rather than the node is
    ///         what that buys — the operator learns immediately instead of three nodes in — so this runs BEFORE the run
    ///         row is written and a refusal leaves nothing behind.
    ///     </para>
    /// </summary>
    private async Task EnsureToolNodesAreRunnableAsync(GraphWorkflowGraph graph, CancellationToken cancellationToken)
    {
        var errors = await GraphWorkflowToolGate.ErrorsAsync(graph, _tools, cancellationToken).ConfigureAwait(false);
        if (errors.Count > 0)
        {
            throw new GraphWorkflowValidationException(GraphWorkflowValidationResult.Invalid(errors));
        }
    }

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
