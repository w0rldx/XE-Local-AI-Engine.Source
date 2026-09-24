namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Tools;

/// <summary>
///     The command surface over a graph workflow run. Everything it does is validate, commit, signal, and answer with
///     what the rows now say — the dispatcher does the rest on its own clock.
/// </summary>
internal sealed class GraphWorkflowRunService : IGraphWorkflowRunService
{
    /// <summary>
    ///     The longest comment an answer may carry, matching the development-workflow gate's own cap. It is free text
    ///     beside the act rather than part of it, and an unbounded one would ride inside the node's output envelope.
    /// </summary>
    private const int MaxDecisionComment = 500;

    private readonly GraphWorkflowOptions _options;
    private readonly SecurityOptions _security;
    private readonly IGraphWorkflowDispatcherSignal _signal;
    private readonly IGraphWorkflowStore _store;
    private readonly IToolInvocationService _tools;

    public GraphWorkflowRunService(IGraphWorkflowStore store,
        IGraphWorkflowDispatcherSignal signal,
        IToolInvocationService tools,
        IOptions<GraphWorkflowOptions> options,
        IOptions<SecurityOptions> security)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _security = (security ?? throw new ArgumentNullException(nameof(security))).Value;
        ArgumentNullException.ThrowIfNull(signal);
        _signal = signal;
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        ArgumentNullException.ThrowIfNull(tools);
        _tools = tools;
    }

    public Task<GraphWorkflowRunDetail> StartAsync(Guid definitionId,
        Guid requestId,
        string? inputJson,
        int? definitionVersion,
        CancellationToken cancellationToken = default) =>
        StartAsync(definitionId, requestId, inputJson, definitionVersion, binding: null, cancellationToken);

    public async Task<GraphWorkflowRunDetail> StartAsync(Guid definitionId,
        Guid requestId,
        string? inputJson,
        int? definitionVersion,
        GraphWorkflowRunBinding? binding,
        CancellationToken cancellationToken = default)
    {
        if (requestId == Guid.Empty)
        {
            throw new GraphWorkflowValidationException("A graph workflow run needs a caller-minted request id.");
        }

        // The FAST path, not the gate. Two genuinely concurrent identical starts can both pass it, which is why the
        // insert below is the real idempotency guarantee.
        if (await _store.FindRunByRequestAsync(requestId, cancellationToken) is { } replayed)
        {
            // A replay has to be a replay of THIS request. A reused request id naming a different definition is a
            // caller bug, and answering it with another run would hand out a run they never asked for.
            EnsureReplayIsOfTheSameDefinition(replayed, definitionId, requestId);

            // Signalled, not merely composed: a replay is what a caller sends when it never saw the first answer, and
            // the run may still be waiting for its first tick.
            return await SignalAndComposeAsync(replayed.Id, cancellationToken);
        }

        var definition = await _store.GetDefinitionAsync(definitionId, cancellationToken);
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
        await EnsureToolNodesAreRunnableAsync(graph, cancellationToken);

        if (graph.Nodes.Count > _options.MaxNodeRunsPerRun)
        {
            throw new GraphWorkflowValidationException($"The graph declares {graph.Nodes.Count} nodes, more than the {_options.MaxNodeRunsPerRun} node runs "
                                                       + "one run may instantiate.");
        }

        // ONE call. The run row, one Pending node run per graph node and the run.created event commit together, and the
        // definition is re-read inside that same transaction so a delete racing this start cannot leave an orphan run.
        var run = await _store.StartRunAsync(new StartGraphWorkflowRunCommand
            {
                RunId = Guid.NewGuid(),
                RequestId = requestId,
                DefinitionId = definitionId,
                DefinitionVersion = definition.Version,
                GraphHash = definition.GraphHash,
                GraphJson = definition.GraphJson,
                InputJson = inputJson,
                NodeRuns =
                [
                    .. graph.Nodes.Values.Select(static node => new GraphWorkflowNodeRunSeed
                    {
                        NodeRunId = Guid.NewGuid(),
                        NodeKey = node.NodeKey,
                        Kind = node.Kind
                    })
                ],
                ConversationId = binding?.ConversationId,
                TriggerMessageId = binding?.TriggerMessageId
            },
            cancellationToken);

        // The lookup above is a fast path both concurrent callers can pass, and the store answers a lost race on the request id with the run that WON — which may
        // be a run of somebody else's definition. Re-checked here, or the loser would receive a run it never asked for by the one route the fast path cannot cover.
        EnsureReplayIsOfTheSameDefinition(run, definitionId, requestId);

        return await SignalAndComposeAsync(run.Id, cancellationToken);
    }

    public async Task<GraphWorkflowRunDetail> CancelAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await _store.GetRunAsync(runId, cancellationToken);
        if (GraphWorkflowStateMachine.IsTerminal(run.Status))
        {
            throw new GraphWorkflowRunConflictException($"This run is already {run.Status}, so there is nothing to cancel.");
        }

        // A repeat cancel is the SAME ask answered again, not a conflict: the intent is already committed and the drain is already running, so this mirrors the
        // start replay — accepted, idempotent and signalled, because a caller that never saw the first answer is exactly the caller that sends this one.
        if (run.Status == GraphWorkflowRunStatus.Cancelling)
        {
            return await SignalAndComposeAsync(runId, cancellationToken);
        }

        GraphWorkflowStateMachine.EnsureLegal(run.Status, GraphWorkflowRunStatus.Cancelling);

        // Against the version it was READ at, so a recomputation that landed in between loses rather than overwriting an operator's intent with a status it decided
        // a moment earlier. Node runs are deliberately NOT settled here: the dispatcher drains them, asking each live lane to stop rather than writing over live work.
        _ = await _store.TransitionRunAsync(new TransitionGraphWorkflowRunCommand
            {
                RunId = runId,
                ExpectedVersion = run.Version,
                TargetStatus = GraphWorkflowRunStatus.Cancelling
            },
            cancellationToken);
        return await SignalAndComposeAsync(runId, cancellationToken);
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
            throw new GraphWorkflowValidationException("A graph workflow decision names the node it answers by node key.");
        }

        if (operationId == Guid.Empty)
        {
            throw new GraphWorkflowValidationException("A graph workflow decision needs a caller-minted operation id.");
        }

        // 1. REPLAY FIRST, and run-wide — the scope the store's filtered unique index enforces, looked up before the (run, node key) row is read at all: without
        // this, one id reused across two pauses of a run passes every check below and violates that index inside the write, as a database error not the promised conflict.
        if (await _store.FindNodeRunByDecisionOperationAsync(runId, operationId, cancellationToken) is { } recorded)
        {
            return await ReplayAsync(runId, nodeKey, operationId, decision, decidedBySubject, payloadJson, recorded, cancellationToken);
        }

        // 2. The row must be waiting — and one that is not gets the SAME resolution a lost write does, replay lookup first. Two identical requests both miss step 1,
        // one commits, the other reads a Succeeded row: this caller's own answer arriving twice, so refusing it here would 409 a decision that did land.
        var nodeRun = await _store.GetNodeRunAsync(runId, nodeKey, cancellationToken);
        if (nodeRun.Status != GraphWorkflowNodeRunStatus.WaitingForApproval)
        {
            return await LostTheRaceAsync(runId, nodeKey, operationId, decision, decidedBySubject, payloadJson, cancellationToken);
        }

        // 3. The run must be live: a drain is already settling this row, and a terminal run has no tick left to route the answer. SAME resolution as step 2, replay
        // first — this caller's answer can have committed under its own id and the run stopped between the row read and here, and refusing would 409 a real decision.
        var run = await _store.GetRunAsync(runId, cancellationToken);
        if (run.Status is GraphWorkflowRunStatus.Cancelling || GraphWorkflowStateMachine.IsTerminal(run.Status))
        {
            return await LostTheRaceAsync(runId, nodeKey, operationId, decision, decidedBySubject, payloadJson, cancellationToken);
        }

        // 4. The answer must be one the PINNED graph offers. A graph that does not offer it is wrong, not the request. The kind rule (a pause takes
        // Approve/Reject, a chat input takes Answer) is the state machine's; a pause's own allowedDecisions narrows it further.
        var graph = GraphWorkflowGraph.Parse(run.GraphJson);
        if (!graph.Nodes.TryGetValue(nodeKey, out var node) || node.Config is not (GraphWorkflowPauseConfig or GraphWorkflowChatInputConfig))
        {
            throw new GraphWorkflowRunConflictException($"The run's pinned graph no longer declares '{nodeKey}' as a Pause or ChatInput node.");
        }

        if (node.Config is GraphWorkflowPauseConfig offered
            && (!GraphWorkflowStateMachine.IsDecidable(node.Kind, nodeRun.Status, decision) || !offered.AllowedDecisions.Contains(decision)))
        {
            throw new GraphWorkflowRunConflictException($"The pause '{nodeKey}' offers {string.Join(", ", offered.AllowedDecisions)}, so it cannot be answered {decision}.");
        }

        if (node.Config is GraphWorkflowChatInputConfig && !GraphWorkflowStateMachine.IsDecidable(node.Kind, nodeRun.Status, decision))
        {
            throw new GraphWorkflowRunConflictException($"The chat input '{nodeKey}' takes an Answer only, so it cannot be answered {decision}.");
        }

        // 5. Body rules. Everything here is about the REQUEST rather than about the run, which is what makes them 400s.
        var output = node.Config is GraphWorkflowPauseConfig pause
            ? GraphWorkflowDocuments.PauseOutput(decision, comment, ValidateBody(nodeKey, pause, comment, payloadJson))
            : GraphWorkflowDocuments.ChatInputOutput(ValidateAnswer(nodeKey, comment, payloadJson));

        // 6. Composed through the one document writer, so a pause row gets the same envelope, the same branch derivation and the same size check as every other
        // kind — and the same `output.decision` spelling the definition-time pre-flight evaluated.
        string document;
        try
        {
            document = GraphWorkflowDocuments.Compose(graph,
                node,
                nodeRun.Attempt,
                GraphWorkflowNodeOutputStatuses.Succeeded,
                output,
                _options.MaxOutputJsonBytes);
        }
        catch (GraphWorkflowOutputTooLargeException exception)
        {
            // Unreachable while the payload cap stays strictly under the envelope budget, and kept because that is a relation between two options rather than a
            // fact: an operator's oversized answer is their 400 to fix, not a node failure they cannot see the cause of.
            throw new GraphWorkflowValidationException(exception.Message, exception);
        }

        // 7. ONE conditional write — status move, decision columns, output and the gate.decided event — losing two ways that are one story: a null answer means the
        // compare-and-set matched no row; an exception means the run's token lost or two operators answered at once. Escaping, that reaches the client as a bare conflict.
        GraphWorkflowMutationResult? written;
        try
        {
            written = await _store.DecideNodeRunAsync(new DecideGraphWorkflowNodeRunCommand
                {
                    RunId = runId,
                    NodeRunId = nodeRun.Id,
                    ExpectedVersion = GraphWorkflowVersions.Any,
                    OperationId = operationId,
                    Decision = decision,
                    DecidedBySubject = decidedBySubject,
                    OutputJson = document
                },
                cancellationToken);
        }
        catch (GraphWorkflowInvalidTransitionException)
        {
            return await LostTheRaceAsync(runId, nodeKey, operationId, decision, decidedBySubject, payloadJson, cancellationToken);
        }

        if (written is null)
        {
            return await LostTheRaceAsync(runId, nodeKey, operationId, decision, decidedBySubject, payloadJson, cancellationToken);
        }

        // 8. The run follows its rows, written against the version this read saw; then the dispatcher is told, AFTER
        // the commit, so the downstream nodes are admitted on its own clock rather than inside this request.
        await RecomputeRunStatusAsync(runId, graph, cancellationToken);
        _signal.Signal(runId);
        return await ComposeDecisionAsync(runId, nodeKey, decision, cancellationToken);
    }

    public async Task<GraphWorkflowRunDetail> SteerAsync(Guid runId,
        string nodeKey,
        Guid operationId,
        string message,
        string? steeredBySubject,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(nodeKey))
        {
            throw new GraphWorkflowValidationException("A steer names the node it steers by node key.");
        }

        if (operationId == Guid.Empty)
        {
            throw new GraphWorkflowValidationException("A steer needs a caller-minted operation id.");
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new GraphWorkflowValidationException("A steer needs a message.");
        }

        // The cap a chat input answer carries: a steer is operator text that lands in the node's prompt and in the chat.
        var maxBytes = MaxAnswerBytes();
        if (Encoding.UTF8.GetByteCount(message) > maxBytes)
        {
            throw new GraphWorkflowValidationException($"The steer is larger than the {maxBytes} bytes it may carry.");
        }

        var run = await _store.GetRunAsync(runId, cancellationToken);
        if (run.ConversationId is null)
        {
            throw new GraphWorkflowValidationException("Only a run bound to a chat conversation can be steered.");
        }

        var nodeRun = await _store.GetNodeRunAsync(runId, nodeKey, cancellationToken);
        if (nodeRun.Kind is not (GraphWorkflowNodeKind.Agent or GraphWorkflowNodeKind.LlmCall))
        {
            throw new GraphWorkflowValidationException($"Node '{nodeKey}' is a {nodeRun.Kind} node; only an Agent or LLM call node can be steered.");
        }

        // Replay first, like a decide: the same id answers again even once the row has moved on.
        if (SteerRefusal(run, nodeRun, operationId, message, steeredBySubject, out var replay) is { } refusal)
        {
            throw refusal;
        }

        if (replay)
        {
            return await SignalAndComposeAsync(runId, cancellationToken);
        }

        var written = await _store.AppendNodeRunSteeringAsync(new AppendGraphWorkflowSteeringCommand
            {
                RunId = runId,
                NodeRunId = nodeRun.Id,
                OperationId = operationId,
                Message = message,
                SteeredBySubject = steeredBySubject,
                MaxEntries = _options.MaxSteersPerNode
            },
            cancellationToken);
        if (written is null)
        {
            // The append re-checks inside its transaction and declined: answer from what the rows NOW say.
            var current = await _store.GetRunAsync(runId, cancellationToken);
            var currentNodeRun = await _store.GetNodeRunAsync(runId, nodeKey, cancellationToken);
            if (SteerRefusal(current, currentNodeRun, operationId, message, steeredBySubject, out var replayed) is { } lost)
            {
                throw lost;
            }

            if (!replayed)
            {
                throw new GraphWorkflowRunConflictException($"Node '{nodeKey}' could not be steered; re-read the run.");
            }
        }

        return await SignalAndComposeAsync(runId, cancellationToken);
    }

    public async Task<GraphWorkflowRunDetail> GetRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
        await ComposeAsync(await _store.GetRunAsync(runId, cancellationToken), cancellationToken);

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
        var events = await _store.ListEventsAsync(runId, afterSeq, _options.EventReplayLimit + 1, cancellationToken);
        var page = events.Take(_options.EventReplayLimit).ToList();
        return new GraphWorkflowRunEventPage
        {
            Events = page,
            LastSeq = page.Count == 0 ? afterSeq : page[^1].Seq,
            ReplayTruncated = events.Count > _options.EventReplayLimit
        };
    }

    /// <summary>
    ///     The refusal the rows earn, or null; <paramref name="replay" /> is true when this id already steered the row.
    ///     One judgement for the first read and for a declined append, so both answer alike.
    /// </summary>
    private Exception? SteerRefusal(GraphWorkflowRunSnapshot run,
        GraphWorkflowNodeRunSnapshot nodeRun,
        Guid operationId,
        string message,
        string? steeredBySubject,
        out bool replay)
    {
        replay = false;
        if (nodeRun.Steering.FirstOrDefault(entry => entry.OperationId == operationId) is { } recorded)
        {
            replay = string.Equals(recorded.Message, message, StringComparison.Ordinal) && string.Equals(recorded.SteeredBySubject, steeredBySubject, StringComparison.Ordinal);
            return replay
                ? null
                : new GraphWorkflowRunConflictException($"Operation '{operationId}' already steered node '{nodeRun.NodeKey}' with a different message.");
        }

        if (run.Status is GraphWorkflowRunStatus.Pending or GraphWorkflowRunStatus.Cancelling || GraphWorkflowStateMachine.IsTerminal(run.Status))
        {
            return new GraphWorkflowRunConflictException($"This run is {run.Status}, so node '{nodeRun.NodeKey}' can no longer be steered.");
        }

        if (nodeRun.Status is not (GraphWorkflowNodeRunStatus.Queued or GraphWorkflowNodeRunStatus.Running))
        {
            return new GraphWorkflowRunConflictException($"Node run '{nodeRun.NodeKey}' is {nodeRun.Status}; only a queued or running node can be steered.");
        }

        return nodeRun.Steering.Count >= _options.MaxSteersPerNode
            ? new GraphWorkflowSteerLimitReachedException($"Node '{nodeRun.NodeKey}' has been steered {nodeRun.Steering.Count} times, the most one node run allows.")
            : null;
    }

    /// <summary>The same act arriving twice.</summary>
    /// <remarks>
    ///     It IS the same act only if it names the same node, the same answer and the same person — a reused id
    ///     naming any of those differently would read as success for a decision nobody took. A pause's comment and
    ///     payload are free text around the act and are not compared; a chat input's text IS the act, so it is.
    /// </remarks>
    private async Task<GraphWorkflowDecisionResult> ReplayAsync(Guid runId,
        string nodeKey,
        Guid operationId,
        GraphWorkflowDecisionKind decision,
        string? decidedBySubject,
        string? payloadJson,
        GraphWorkflowNodeRunSnapshot recorded,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(recorded.NodeKey, nodeKey, StringComparison.Ordinal))
        {
            throw StandingConflict(recorded, $"Operation '{operationId}' already decided node '{recorded.NodeKey}' of this run.");
        }

        if (GraphWorkflowStateMachine.DecisionOf(recorded.OutputJson) != decision
            || !string.Equals(recorded.DecidedBySubject, decidedBySubject, StringComparison.Ordinal)
            || recorded.Kind == GraphWorkflowNodeKind.ChatInput && !string.Equals(TextAt(recorded.OutputJson, "output.text"), TextAt(payloadJson, "text"), StringComparison.Ordinal))
        {
            throw StandingConflict(recorded, $"Operation '{operationId}' already recorded a different answer on node '{nodeKey}'.");
        }

        return await ComposeDecisionAsync(runId, nodeKey, decision, cancellationToken);
    }

    /// <summary>
    ///     What a decide write that wrote nothing means, and what a row no longer waiting when it was read means:
    ///     something committed between this caller's checks and its write.
    /// </summary>
    /// <remarks>
    ///     Answered off what the rows NOW say rather than by retrying — this same operation id committed it, which is
    ///     a replay; the run stopped being live, which is a run conflict; or another operation id answered the pause,
    ///     which is the second human act.
    /// </remarks>
    private async Task<GraphWorkflowDecisionResult> LostTheRaceAsync(Guid runId,
        string nodeKey,
        Guid operationId,
        GraphWorkflowDecisionKind decision,
        string? decidedBySubject,
        string? payloadJson,
        CancellationToken cancellationToken)
    {
        if (await _store.FindNodeRunByDecisionOperationAsync(runId, operationId, cancellationToken) is { } settled)
        {
            return await ReplayAsync(runId, nodeKey, operationId, decision, decidedBySubject, payloadJson, settled, cancellationToken);
        }

        // Before the row: the store also declines once the RUN stops being live, and answering that with a node-status refusal would name the pause when the cancel
        // is the reason — or, worse, name a standing decision on a row the drain has since cancelled.
        var run = await _store.GetRunAsync(runId, cancellationToken);
        if (run.Status is GraphWorkflowRunStatus.Cancelling || GraphWorkflowStateMachine.IsTerminal(run.Status))
        {
            throw new GraphWorkflowRunConflictException($"This run is {run.Status}, so node '{nodeKey}' can no longer be answered.");
        }

        var current = await _store.GetNodeRunAsync(runId, nodeKey, cancellationToken);
        throw StandingConflict(current, $"Node run '{nodeKey}' is {current.Status}, so there is nothing to decide on it.");
    }

    /// <summary>
    ///     The refusal a row that is not open to a decision earns: one NAMING the answer that stands where the row was
    ///     actually answered, and a generic run conflict where it was not.
    /// </summary>
    /// <remarks>
    ///     Naming it is what tells the second person to click what was decided rather than only that their click
    ///     failed. Gated on <c>DecisionOperationId</c>, the column an answered gate writes — NOT on the output
    ///     document carrying an <c>output.decision</c>: a <c>Condition</c> or <c>Parallel</c> node downstream of an
    ///     answered pause passes that predecessor's output through verbatim, so reading the document alone would
    ///     report a standing decision for a node nobody ever decided.
    /// </remarks>
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

    /// <summary>A string at <paramref name="path" /> in a stored or submitted document, or null when there is none.</summary>
    private static string? TextAt(string? json, string path) =>
        GraphWorkflowDocuments.Resolve(json, path) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    /// <summary>
    ///     A chat input's answer: <c>payload.text</c>, non-blank, and no comment beside it — the text IS the answer.
    /// </summary>
    /// <remarks>
    ///     Capped at the smaller of the chat message cap and half the output envelope: the answer arrives as a chat
    ///     message, and it must still fit the document it is embedded in, as a pause payload must.
    /// </remarks>
    private string ValidateAnswer(string nodeKey, string? comment, string? payloadJson)
    {
        if (!string.IsNullOrEmpty(comment))
        {
            throw new GraphWorkflowValidationException($"The chat input '{nodeKey}' takes its answer as 'payload.text' and no comment.");
        }

        string? text = null;
        if (payloadJson is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(payloadJson);
                if (document.RootElement is { ValueKind: JsonValueKind.Object } root
                    && root.TryGetProperty("text", out var value)
                    && value.ValueKind == JsonValueKind.String)
                {
                    text = value.GetString();
                }
            }
            catch (JsonException)
            {
                // Answered below with the same refusal as a missing text.
            }
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new GraphWorkflowValidationException($"The chat input '{nodeKey}' needs a non-empty 'payload.text' answer.");
        }

        var maxBytes = MaxAnswerBytes();
        return Encoding.UTF8.GetByteCount(text) <= maxBytes
            ? text
            : throw new GraphWorkflowValidationException($"The answer is larger than the {maxBytes} bytes a chat input answer may carry.");
    }

    /// <summary>The byte cap of operator text a run takes in: a chat input answer, and a steer.</summary>
    private int MaxAnswerBytes() =>
        Math.Min(_security.MaxMessageSizeKb * 1024, _options.MaxOutputJsonBytes / 2);

    /// <summary>The run status that follows its rows, written against the version it was read at.</summary>
    /// <remarks>
    ///     Deliberately NON-terminal only: terminalization carries the run's own result off the End node that
    ///     succeeded, and that is the dispatcher's write in the tick this decision has just signalled. The one move
    ///     this owns is <c>WaitingForApproval → Running</c>, which is what makes the answer this method returns honest
    ///     instead of a status the caller would have to re-read to disbelieve.
    /// </remarks>
    private async Task RecomputeRunStatusAsync(Guid runId, GraphWorkflowGraph graph, CancellationToken cancellationToken)
    {
        var current = await _store.GetRunAsync(runId, cancellationToken);
        var nodeRuns = await _store.ListNodeRunsAsync(runId, cancellationToken);
        var outcome = GraphWorkflowStateMachine.Recompute(current.Status, graph, nodeRuns);
        if (outcome.Status == current.Status
            || GraphWorkflowStateMachine.IsTerminal(outcome.Status)
            || !GraphWorkflowStateMachine.IsLegal(current.Status, outcome.Status))
        {
            return;
        }

        try
        {
            _ = await _store.TransitionRunAsync(new TransitionGraphWorkflowRunCommand
            {
                RunId = runId,
                ExpectedVersion = current.Version,
                TargetStatus = outcome.Status
            }, cancellationToken);
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
        var run = await _store.GetRunAsync(runId, cancellationToken);
        var nodeRun = await _store.GetNodeRunAsync(runId, nodeKey, cancellationToken);
        return new GraphWorkflowDecisionResult
        {
            Decision = decision,
            RunStatus = run.Status,
            NodeRunStatus = nodeRun.Status
        };
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
    ///     Re-checks, at run start, that every <c>Tool</c> node of the pinned graph names a tool this node will
    ///     actually run.
    /// </summary>
    /// <remarks>
    ///     The tool gate, as ONE mechanism in one place rather than a check the dispatcher repeats per kind. Asked
    ///     again here rather than trusted from save time: a definition saved when a tool was invocable must not start
    ///     once the envelope has been tightened away from it. Failing the START rather than the node is what that buys
    ///     — the operator learns immediately instead of three nodes in — so this runs BEFORE the run row is written
    ///     and a refusal leaves nothing behind.
    /// </remarks>
    private async Task EnsureToolNodesAreRunnableAsync(GraphWorkflowGraph graph, CancellationToken cancellationToken)
    {
        var errors = await GraphWorkflowToolGate.ErrorsAsync(graph, _tools, cancellationToken);
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
        return await ComposeAsync(await _store.GetRunAsync(runId, cancellationToken), cancellationToken);
    }

    private async Task<GraphWorkflowRunDetail> ComposeAsync(GraphWorkflowRunSnapshot run, CancellationToken cancellationToken) =>
        new()
        {
            Run = run,
            NodeRuns = await _store.ListNodeRunsAsync(run.Id, cancellationToken)
        };
}
