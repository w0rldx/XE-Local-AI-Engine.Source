namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     What an inbound edge says about whether its target may proceed.
///     <para>
///         There is deliberately no <c>Waived</c> member. Dev Workflows has one to tell an operator's <c>Skip</c>
///         decision from a cascade off something dead; v1's decision kinds are <c>Approve</c> and <c>Reject</c> only,
///         so nothing can produce a waiver and the machinery would be unreachable. Re-add the concept with the first
///         decision kind that produces one.
///     </para>
/// </summary>
internal enum GraphWorkflowEdgeState
{
    /// <summary>The source has not settled.</summary>
    Pending,

    /// <summary>The source succeeded and this edge's condition (if any) fired.</summary>
    Satisfied,

    /// <summary>The source settled in a way this edge can never fire on. Nothing downstream of it will ever come.</summary>
    Dead
}

/// <summary>What the dispatcher should do with a <c>Pending</c> node run this tick.</summary>
internal enum GraphWorkflowNodeAdmission
{
    /// <summary>An inbound edge is still undecided. Leave it alone.</summary>
    Wait,

    /// <summary>Its dependencies are satisfied; queue it.</summary>
    Eligible,

    /// <summary>Every path into it is dead. It will never run, and its own out-edges die with it.</summary>
    Skip
}

/// <summary>
///     What recomputing a run's status concluded, and — when the answer is <c>Cancelled</c> — why. A status alone
///     cannot carry that: a run whose tail was abandoned has no failing node run to read the reason off, because
///     nothing failed.
/// </summary>
internal readonly record struct GraphWorkflowRunOutcome(GraphWorkflowRunStatus Status,
    GraphWorkflowFailureClass FailureClass = GraphWorkflowFailureClass.None,
    string? TerminalReason = null);

/// <summary>
///     The run and node-run state machines, as pure functions over persisted rows and the parsed graph.
///     <para>
///         The store deliberately does not judge transitions — it provides the rejection channel and enforces only what
///         the database can. These functions are therefore the only guard, and being free of I/O is what lets the whole
///         truth table be tested without a database.
///     </para>
/// </summary>
internal static class GraphWorkflowStateMachine
{
    /// <summary>camelCase, matching every other document this product puts on a wire.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The bound on a run's terminal reason.</summary>
    private const int MaxTerminalReason = 512;

    /// <summary>How many terminal nodes a reason names one by one before it starts counting them instead.</summary>
    private const int MaxNamedNodes = 3;

    /// <summary>The answers a pause REFUSES with, by the output document each one stores. Nothing varies.</summary>
    private static readonly Dictionary<string, GraphWorkflowDecisionKind> PauseRefusals = new(StringComparer.Ordinal)
    {
        [PauseOutputJson(GraphWorkflowDecisionKind.Reject)] = GraphWorkflowDecisionKind.Reject
    };

    /// <summary>
    ///     The output document a pause produces for one answer — the document its out-edge conditions are then
    ///     evaluated against. Written in ONE place because three callers ask questions of it: the definition-time
    ///     pre-flight rule, the dispatcher when an answer has landed, and the API when it tells an operator in advance
    ///     whether a rejection has anywhere to go. A second spelling of this shape would make them disagree in exactly
    ///     the case that matters.
    /// </summary>
    public static string PauseOutputJson(GraphWorkflowDecisionKind decision) =>
        JsonSerializer.Serialize(new PauseOutput(GraphWorkflowNodeOutputStatuses.Succeeded, new PauseDecision(decision.ToString())), JsonOptions);

    /// <summary>
    ///     Every answer a pause SUCCEEDS on — the ones that part company in the graph rather than on the row.
    ///     <para>
    ///         Derived from <see cref="TargetFor" /> rather than listed by hand. With two decision kinds that is
    ///         trivially the whole enum, and it is kept anyway for one reason: the parser's own pre-flight rule
    ///         iterates it, as does the decide endpoint when it advertises the answers, so a third kind cannot be added
    ///         in one place and forgotten in the other.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<GraphWorkflowDecisionKind> DecisionAnswers { get; } =
    [
        .. Enum.GetValues<GraphWorkflowDecisionKind>().Where(static decision => TargetFor(decision) == GraphWorkflowNodeRunStatus.Succeeded)
    ];

    /// <summary>
    ///     Whether an out-edge of a pause fires for one answer, asked of the pause's own output document rather than of
    ///     a row — which is what a check made BEFORE the run has instead of one.
    /// </summary>
    public static bool DecisionEdgeFires(GraphWorkflowGraphEdge edge, GraphWorkflowDecisionKind decision)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return Fires(edge, PauseOutputJson(decision));
    }

    /// <summary>A node run nothing further will happen to on its own.</summary>
    public static bool IsTerminal(GraphWorkflowNodeRunStatus status) =>
        status is GraphWorkflowNodeRunStatus.Succeeded
            or GraphWorkflowNodeRunStatus.Failed
            or GraphWorkflowNodeRunStatus.Skipped
            or GraphWorkflowNodeRunStatus.Cancelled;

    /// <summary>
    ///     A node run the run is still waiting on — including the human wait, which is what keeps a run from completing
    ///     behind an unanswered pause.
    /// </summary>
    public static bool IsLive(GraphWorkflowNodeRunStatus status) =>
        !IsTerminal(status);

    public static bool IsTerminal(GraphWorkflowRunStatus status) =>
        status is GraphWorkflowRunStatus.Completed or GraphWorkflowRunStatus.Failed or GraphWorkflowRunStatus.Cancelled;

    /// <summary>
    ///     Whether an inbound edge lets its target through, read off the run's node runs — <c>Pending</c> when the
    ///     source has no row yet, because a source that has not been materialized is a wait rather than a refusal.
    ///     <para>
    ///         Every terminal status but <c>Succeeded</c> kills the out-edge: none of them produced the output a
    ///         condition would read, and treating "no output" as a passing condition is how a run routes on evidence it
    ///         never had. So does a <c>Succeeded</c> source whose condition did not fire — the branch not taken.
    ///     </para>
    ///     <para>
    ///         The graph is not a parameter, unlike the Dev Workflow original: the waiver rule is what needed to walk
    ///         back through it, and v1 has no waiver.
    ///     </para>
    /// </summary>
    public static GraphWorkflowEdgeState EdgeState(GraphWorkflowGraphEdge edge, IReadOnlyDictionary<string, GraphWorkflowNodeRunSnapshot> nodeRunsByKey)
    {
        ArgumentNullException.ThrowIfNull(edge);
        ArgumentNullException.ThrowIfNull(nodeRunsByKey);

        if (nodeRunsByKey.GetValueOrDefault(edge.From) is not { } source || !IsTerminal(source.Status))
        {
            return GraphWorkflowEdgeState.Pending;
        }

        return source.Status == GraphWorkflowNodeRunStatus.Succeeded && Fires(edge, source.OutputJson)
            ? GraphWorkflowEdgeState.Satisfied
            : GraphWorkflowEdgeState.Dead;
    }

    /// <summary>Whether one edge's condition accepts one output document. The only place either question is answered.</summary>
    private static bool Fires(GraphWorkflowGraphEdge edge, string? outputJson) =>
        GraphWorkflowCondition.Evaluate(edge.Condition, ParseOutput(outputJson));

    /// <summary>
    ///     Whether a <c>Pending</c> node run may be queued, must be skipped, or is still waiting.
    ///     <para>
    ///         <c>All</c> over ZERO inbound edges is vacuously satisfied, and that is load-bearing rather than pedantic:
    ///         it is how the <c>Start</c> node becomes eligible at all.
    ///     </para>
    ///     <para>
    ///         The join policy is read off the NODE, whichever kind it is. An ordinary node with two inbound edges
    ///         joins them exactly as a <c>Join</c> node does, and reading the policy off <c>Join</c> alone is the
    ///         documented trap.
    ///     </para>
    /// </summary>
    public static GraphWorkflowNodeAdmission Admission(GraphWorkflowGraphNode node,
        GraphWorkflowGraph graph,
        IReadOnlyDictionary<string, GraphWorkflowNodeRunSnapshot> nodeRunsByKey)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(nodeRunsByKey);

        var states = graph.InboundEdges(node.NodeKey).Select(edge => EdgeState(edge, nodeRunsByKey)).ToList();

        // Pending outranks Dead under BOTH policies, and for the same reason: the answer is not allowed to depend on
        // which branch happened to land first. A dead inbound edge already settles what an `All` join will DO — it can
        // never fire, so it will be skipped — but settling it while a sibling branch is still running skips the node,
        // and everything after it, in front of work the run has not finished.
        if (states.Contains(GraphWorkflowEdgeState.Pending))
        {
            return GraphWorkflowNodeAdmission.Wait;
        }

        if (node.JoinPolicy == GraphWorkflowJoinPolicy.All)
        {
            // With no waiver to weigh, everything that is not Dead here is Satisfied, so one dead edge is the whole
            // question: an All join cannot proceed on a branch that will never arrive.
            return states.Contains(GraphWorkflowEdgeState.Dead) ? GraphWorkflowNodeAdmission.Skip : GraphWorkflowNodeAdmission.Eligible;
        }

        // Any: one satisfied branch is enough, but only once no sibling could still satisfy one.
        return states.Contains(GraphWorkflowEdgeState.Satisfied) ? GraphWorkflowNodeAdmission.Eligible : GraphWorkflowNodeAdmission.Skip;
    }

    /// <summary>
    ///     Why a node run <see cref="Admission" /> answered <c>Skip</c> for is being skipped, in the words its own row
    ///     keeps. A cascaded skip that recorded nothing leaves an operator reading a column of identical Skipped rows
    ///     with no way to tell which one of them was the decision.
    ///     <para>
    ///         Names ONE dead dependency, because a skip needs one cause rather than a list — and prefers a branch that
    ///         broke or was skipped over one a condition merely routed past. Both are dead, but only the first is news:
    ///         a Condition node taking its other branch is the graph working.
    ///     </para>
    ///     <para>
    ///         A node this is asked about has a dead dependency by construction — an <c>All</c> node is skipped only on
    ///         one, and the parser refuses an <c>Any</c> node with fewer than two inbound edges — so the no-cause
    ///         sentence is a guard rather than a case.
    ///     </para>
    /// </summary>
    public static string SkipReason(GraphWorkflowGraphNode node,
        GraphWorkflowGraph graph,
        IReadOnlyDictionary<string, GraphWorkflowNodeRunSnapshot> nodeRunsByKey)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(nodeRunsByKey);

        var dead = graph.InboundEdges(node.NodeKey)
                        .Where(edge => EdgeState(edge, nodeRunsByKey) == GraphWorkflowEdgeState.Dead)
                        .Select(static edge => edge.From)
                        .ToList();
        var cause = dead.Find(key => nodeRunsByKey.GetValueOrDefault(key)?.Status != GraphWorkflowNodeRunStatus.Succeeded) ?? dead.FirstOrDefault();

        return nodeRunsByKey.GetValueOrDefault(cause ?? string.Empty)?.Status switch
        {
            GraphWorkflowNodeRunStatus.Skipped => $"Skipped: upstream '{cause}' was skipped.",

            // Succeeded and still dead means its condition did not accept this edge — the branch was not taken.
            GraphWorkflowNodeRunStatus.Succeeded => $"Skipped: upstream '{cause}' routed elsewhere.",
            _ => cause is null
                ? "Skipped: no branch into this node can still arrive."
                : $"Skipped: upstream '{cause}' did not succeed."
        };
    }

    /// <summary>
    ///     At most <paramref name="max" /> UTF-16 units of <paramref name="text" />, never ending on the high half of a
    ///     surrogate pair: a plain slice can cut an emoji in two and persist a lone surrogate, which is not valid text.
    /// </summary>
    public static string Bounded(string text, int max)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= max)
        {
            return text;
        }

        return text[..(char.IsHighSurrogate(text[max - 1]) ? max - 1 : max)];
    }

    /// <summary>
    ///     The status a run should hold given its node runs and its pinned graph, recomputed from scratch at the end of
    ///     every tick rather than accumulated. It is denormalized on purpose so a reader can answer "what is this run
    ///     doing" without a join.
    ///     <para>
    ///         Graph-aware on purpose. <c>Completed</c> means at least one TERMINAL node succeeded, so a run whose tail
    ///         was skipped, or whose rejection routed down a branch that skipped the remainder, reads <c>Cancelled</c>
    ///         with a reason naming the ends it never reached rather than <c>Completed</c> like a run that did its job.
    ///         <c>Failed</c> outranks both: a node that failed is the answer to why the run stopped.
    ///     </para>
    /// </summary>
    public static GraphWorkflowRunOutcome Recompute(GraphWorkflowRunStatus current,
        GraphWorkflowGraph graph,
        IReadOnlyList<GraphWorkflowNodeRunSnapshot> nodeRuns)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(nodeRuns);

        return Settled(current, nodeRuns) is { } settled ? new GraphWorkflowRunOutcome(settled) : Terminalize(graph, nodeRuns);
    }

    /// <summary>
    ///     Everything a run's status can be decided from the ROWS alone — or <see langword="null" />, meaning every node
    ///     run is terminal and only the graph can say what that amounts to.
    /// </summary>
    private static GraphWorkflowRunStatus? Settled(GraphWorkflowRunStatus current, IReadOnlyList<GraphWorkflowNodeRunSnapshot> nodeRuns)
    {
        if (IsTerminal(current) || current == GraphWorkflowRunStatus.Cancelling)
        {
            return current;
        }

        if (nodeRuns.Any(nodeRun => IsLive(nodeRun.Status)))
        {
            if (nodeRuns.Any(static nodeRun => nodeRun.Status is GraphWorkflowNodeRunStatus.Queued or GraphWorkflowNodeRunStatus.Running))
            {
                return GraphWorkflowRunStatus.Running;
            }

            // WaitingForApproval outranks Pending deliberately: every node run of a graph exists from the moment the
            // run starts, so there are almost always Pending rows waiting on a branch that has not settled. Reading
            // those as Running would report a run blocked on an unanswered pause as busy, which is the one thing the
            // two statuses exist to tell apart.
            return nodeRuns.Any(static nodeRun => nodeRun.Status == GraphWorkflowNodeRunStatus.WaitingForApproval)
                ? GraphWorkflowRunStatus.WaitingForApproval
                : GraphWorkflowRunStatus.Running;
        }

        // A run with no node runs at all has not been materialized yet; it is still Pending, not complete.
        return nodeRuns.Count == 0 ? current : null;
    }

    /// <summary>
    ///     What a run whose every node run is terminal amounts to, asked of the graph. Skipped and Cancelled node runs
    ///     do not block an end — they simply are not one, and this is where that distinction is made.
    ///     <para>
    ///         <c>GateRejected</c> here means only this: a pause somewhere in this run was refused, and the run reached
    ///         no end. It is NOT a causal proof — a rejection can route into a branch that runs perfectly well, and a
    ///         false condition further down can be what actually killed the tail. The class narrows where a reader
    ///         should look; the reason names what was actually not reached.
    ///     </para>
    /// </summary>
    private static GraphWorkflowRunOutcome Terminalize(GraphWorkflowGraph graph, IReadOnlyList<GraphWorkflowNodeRunSnapshot> nodeRuns)
    {
        if (nodeRuns.Any(static nodeRun => nodeRun.Status == GraphWorkflowNodeRunStatus.Failed))
        {
            return new GraphWorkflowRunOutcome(GraphWorkflowRunStatus.Failed);
        }

        var ends = nodeRuns.Where(nodeRun => graph.TerminalNodeKeys.Contains(nodeRun.NodeKey)).ToList();
        if (ends.Any(static nodeRun => nodeRun.Status == GraphWorkflowNodeRunStatus.Succeeded))
        {
            return new GraphWorkflowRunOutcome(GraphWorkflowRunStatus.Completed);
        }

        var refused = nodeRuns.FirstOrDefault(static nodeRun => Refusal(nodeRun) is not null);
        return new GraphWorkflowRunOutcome(GraphWorkflowRunStatus.Cancelled,
            refused is null ? GraphWorkflowFailureClass.None : GraphWorkflowFailureClass.GateRejected,
            Reason(ends, refused));
    }

    /// <summary>
    ///     Why a run that reached no end stopped, naming the ends it did not reach and what became of them. Sanitized
    ///     by construction: every word of it is either fixed text or a node key, and node keys are charset-bounded.
    /// </summary>
    private static string Reason(IReadOnlyList<GraphWorkflowNodeRunSnapshot> ends, GraphWorkflowNodeRunSnapshot? refused)
    {
        var listed = string.Join(", ", ends.Take(MaxNamedNodes).Select(static end => $"'{end.NodeKey}' was {end.Status}"));
        if (ends.Count > MaxNamedNodes)
        {
            listed += $", and {ends.Count - MaxNamedNodes} more";
        }

        var named = ends.Count == 0 ? "this run reached none of the graph's ends" : listed;
        var cause = refused is null ? string.Empty : $", after the pause '{refused.NodeKey}' answered {Refusal(refused)}";
        return Bounded($"No terminal node succeeded: {named}{cause}.", MaxTerminalReason);
    }

    /// <summary>
    ///     Which answer a pause was REFUSED with, or <see langword="null" /> when this node run is not a refused pause.
    ///     Matched against <see cref="PauseOutputJson" /> because the dispatcher writes a pause's output FROM that
    ///     method and nothing else writes it at all, so this cannot drift from what a pause actually stores.
    /// </summary>
    private static GraphWorkflowDecisionKind? Refusal(GraphWorkflowNodeRunSnapshot nodeRun) =>
        nodeRun is { Kind: GraphWorkflowNodeKind.Pause, Status: GraphWorkflowNodeRunStatus.Succeeded }
        && PauseRefusals.TryGetValue(nodeRun.OutputJson ?? string.Empty, out var decision)
            ? decision
            : null;

    /// <summary>
    ///     Where a human's answer leaves the node run it answers. Both answers SUCCEED the pause: the answer is the
    ///     node's output, and routing on it is the edges' job. A rejection reaches the run through an out-edge, not
    ///     through a node failure — which is why the two land in the same place here and part company in the graph.
    /// </summary>
    public static GraphWorkflowNodeRunStatus TargetFor(GraphWorkflowDecisionKind decision) =>
        decision switch
        {
            GraphWorkflowDecisionKind.Approve or GraphWorkflowDecisionKind.Reject => GraphWorkflowNodeRunStatus.Succeeded,
            _ => GraphWorkflowNodeRunStatus.Failed
        };

    /// <summary>
    ///     Whether a human may answer <paramref name="decision" /> on a node run in <paramref name="status" />. Shared
    ///     by the decide endpoint and by the surface that advertises the answers, so what is offered and what is
    ///     accepted cannot drift.
    /// </summary>
    public static bool IsDecidable(GraphWorkflowNodeRunStatus status, GraphWorkflowDecisionKind decision) =>
        status == GraphWorkflowNodeRunStatus.WaitingForApproval && IsLegal(status, TargetFor(decision));

    /// <summary>
    ///     The run transition table. Every terminal is reached through the cancel drain or through the "nothing is live
    ///     any more" recomputation, and the invariant behind that is about LIVE work: a terminal written over a run
    ///     with a live node run strands it under a run no tick advances again.
    ///     <para>
    ///         <c>Running → Cancelled</c> and <c>WaitingForApproval → Cancelled</c> are that recomputation's own edges
    ///         and nothing else's. They are safe under exactly the same invariant rather than in spite of it:
    ///         <see cref="Recompute" /> reaches its terminalization branch ONLY once every node run is already
    ///         terminal, so there is nothing left to strand and nothing to drain either.
    ///     </para>
    /// </summary>
    public static bool IsLegal(GraphWorkflowRunStatus from, GraphWorkflowRunStatus to) =>
        from switch
        {
            GraphWorkflowRunStatus.Pending => to is GraphWorkflowRunStatus.Running or GraphWorkflowRunStatus.Failed or GraphWorkflowRunStatus.Cancelling,
            GraphWorkflowRunStatus.Running => to is GraphWorkflowRunStatus.WaitingForApproval
                or GraphWorkflowRunStatus.Cancelling
                or GraphWorkflowRunStatus.Completed
                or GraphWorkflowRunStatus.Cancelled
                or GraphWorkflowRunStatus.Failed,
            GraphWorkflowRunStatus.WaitingForApproval => to is GraphWorkflowRunStatus.Running
                or GraphWorkflowRunStatus.Cancelling
                or GraphWorkflowRunStatus.Completed
                or GraphWorkflowRunStatus.Cancelled
                or GraphWorkflowRunStatus.Failed,
            GraphWorkflowRunStatus.Cancelling => to is GraphWorkflowRunStatus.Cancelled,
            _ => false
        };

    /// <summary>
    ///     The node-run transition table.
    ///     <para>
    ///         <c>Running → Pending</c> and <c>Queued → Pending</c> carry two different meanings that need no distinct
    ///         edge: a retry scheduled after a retryable failure, and a collapse after the host restarted under the
    ///         node run. Both re-derive the same way, which is why the row is cleaned rather than annotated.
    ///     </para>
    ///     <para>
    ///         <c>Failed → Pending</c> is the ONE edge out of a terminal status, and it is retry IN PLACE: a failed row
    ///         under both the node's own attempt cap and the run's total budget goes back to <c>Pending</c> with the
    ///         attempt incremented, in one atomic write. There is no cross-node fix loop in v1, so the Dev Workflow
    ///         module's other three terminal exits go with it — a <c>Succeeded</c>, <c>Skipped</c> or <c>Cancelled</c>
    ///         row here is an answer nothing will ask again.
    ///     </para>
    /// </summary>
    public static bool IsLegal(GraphWorkflowNodeRunStatus from, GraphWorkflowNodeRunStatus to) =>
        from switch
        {
            // Straight to Running is the inline lane: a pause, a join or a fan-out waits for no slot.
            GraphWorkflowNodeRunStatus.Pending => to is GraphWorkflowNodeRunStatus.Queued
                or GraphWorkflowNodeRunStatus.Running
                or GraphWorkflowNodeRunStatus.Skipped
                or GraphWorkflowNodeRunStatus.Cancelled,
            GraphWorkflowNodeRunStatus.Queued => to is GraphWorkflowNodeRunStatus.Running
                or GraphWorkflowNodeRunStatus.Pending
                or GraphWorkflowNodeRunStatus.Failed
                or GraphWorkflowNodeRunStatus.Cancelled,
            GraphWorkflowNodeRunStatus.Running => to is GraphWorkflowNodeRunStatus.Succeeded
                or GraphWorkflowNodeRunStatus.Failed
                or GraphWorkflowNodeRunStatus.WaitingForApproval
                or GraphWorkflowNodeRunStatus.Pending
                or GraphWorkflowNodeRunStatus.Cancelled,

            // NOT Skipped: both answers SUCCEED a pause and route on the answer. Skipping an open pause would be an
            // operator walking past a decision instead of giving one — the one thing a pause exists to make impossible.
            GraphWorkflowNodeRunStatus.WaitingForApproval => to is GraphWorkflowNodeRunStatus.Succeeded
                or GraphWorkflowNodeRunStatus.Cancelled
                or GraphWorkflowNodeRunStatus.Pending,

            // Retry in place, and the only way out of a terminal status.
            GraphWorkflowNodeRunStatus.Failed => to is GraphWorkflowNodeRunStatus.Pending,
            _ => false
        };

    public static void EnsureLegal(GraphWorkflowRunStatus from, GraphWorkflowRunStatus to)
    {
        if (!IsLegal(from, to))
        {
            throw new GraphWorkflowInvalidTransitionException($"A graph workflow run in {from} cannot move to {to}.");
        }
    }

    public static void EnsureLegal(GraphWorkflowNodeRunStatus from, GraphWorkflowNodeRunStatus to, string nodeKey)
    {
        if (!IsLegal(from, to))
        {
            throw new GraphWorkflowInvalidTransitionException($"Node run '{nodeKey}' is {from} and cannot move to {to}.");
        }
    }

    /// <summary>
    ///     A node run's output document, or <see langword="null" /> when it has none or the stored text is not an
    ///     object. Unreadable output is treated as absent so conditions fail closed rather than throwing mid-tick.
    /// </summary>
    private static JsonElement? ParseOutput(string? outputJson)
    {
        if (string.IsNullOrWhiteSpace(outputJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(outputJson);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    ///     The pause half of the node-run output document, and only the members an out-edge routes on. The decision sits
    ///     under <c>output</c> because that is where every kind's own payload sits, so an edge selects on
    ///     <c>output.decision</c>.
    /// </summary>
    private sealed record PauseOutput(string Status, PauseDecision Output);

    private sealed record PauseDecision(string Decision);
}
