namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>What an inbound edge says about whether its target may proceed.</summary>
internal enum DevWorkflowEdgeState
{
    /// <summary>The source has not settled, or has settled and this edge is still to be judged against a live sibling.</summary>
    Pending,

    /// <summary>The source succeeded and this edge's condition (if any) fired.</summary>
    Satisfied,

    /// <summary>
    ///     The source was Skipped, and nothing upstream of it refused it — so a person did. It carries nothing, but it
    ///     is not a reason to throw away what its siblings carried. Excused rather than satisfied: an <c>Any</c> join
    ///     still needs a branch that actually arrived.
    /// </summary>
    Waived,

    /// <summary>The source settled in a way this edge can never fire on. Nothing downstream of it will ever come.</summary>
    Dead
}

/// <summary>What the dispatcher should do with a <c>Pending</c> node run this tick.</summary>
internal enum DevWorkflowNodeAdmission
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
///     nothing failed. Passed straight into the run transition, which is the only writer of both columns.
/// </summary>
internal readonly record struct DevWorkflowRunOutcome(DevWorkflowRunStatus Status, string? FailureClass = null, string? TerminalReason = null);

/// <summary>
///     Where one SETTLED node run's out-edges went, as the state machine itself judged them — the record behind the
///     node run's <c>route_json</c> column.
///     <para>
///         <see cref="Satisfied" /> means "this node's out-edge condition was satisfied". It does <b>not</b> mean the
///         successor ran: admission is a question about a TARGET's inbound edges, so a successor with an <c>All</c>
///         join can still be skipped by a dead sibling edge, and one with an <c>Any</c> join can become eligible on a
///         sibling's edge without this one firing.
///     </para>
///     <para>
///         There is no <c>Pending</c> bucket, and that is a proven impossibility rather than an omission:
///         <see cref="DevWorkflowStateMachine.RouteTaken" /> refuses a source that is not terminal, which is the only
///         state in which <see cref="DevWorkflowStateMachine.EdgeState" /> answers <c>Pending</c>.
///     </para>
/// </summary>
/// <param name="GateAnswer">The decision token a human gate settled on; null on every other node type.</param>
/// <param name="Truncated">Whether keys were dropped to keep the serialized document inside the column's bound.</param>
public sealed record DevWorkflowRoute(
    IReadOnlyList<string> Satisfied,
    IReadOnlyList<string> Dead,
    IReadOnlyList<string> Waived,
    string? GateAnswer,
    bool Truncated);

/// <summary>
///     The run and node-run state machines, as pure functions over persisted rows and the parsed graph.
///     <para>
///         The store deliberately does not judge transitions — it provides the rejection channel and enforces only what
///         the database can. These functions are therefore the only guard, and being free of I/O is what lets the whole
///         truth table be tested without a database.
///     </para>
/// </summary>
internal static class DevWorkflowStateMachine
{
    /// <summary>camelCase, matching every other document this product puts on a wire.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The schema's own bound on the RUN's <c>terminal_reason</c> (<c>DevWorkflowRunConfiguration</c>).</summary>
    private const int MaxTerminalReason = 512;

    /// <summary>
    ///     The NODE RUN's own bound (<c>DevWorkflowNodeRunConfiguration</c>), twice the run's. A cascaded skip's reason
    ///     quotes the reason above it, so a long chain under a verbose operator comment is the one shape that can grow
    ///     toward it — cut here rather than at the store, because a run that wedges on a column length is a run stopped
    ///     by punctuation.
    /// </summary>
    internal const int MaxNodeTerminalReason = 1024;

    /// <summary>How many terminal nodes a reason names one by one before it starts counting them instead.</summary>
    private const int MaxNamedNodes = 3;

    /// <summary>How many successor keys each bucket of a route names. A fan-out wider than this is a shape, not a list.</summary>
    private const int MaxRoutedKeys = 8;

    /// <summary>The schema's own bound on the node run's <c>route_json</c> (<c>DevWorkflowNodeRunConfiguration</c>).</summary>
    private const int MaxRouteJson = 1024;

    /// <summary>The two answers a human gate refuses with, by the output document each one stores. Nothing varies.</summary>
    private static readonly Dictionary<string, DevWorkflowDecisionKind> GateRefusals = new(StringComparer.Ordinal)
    {
        [GateOutputJson(DevWorkflowDecisionKind.Reject)] = DevWorkflowDecisionKind.Reject,
        [GateOutputJson(DevWorkflowDecisionKind.RequestChanges)] = DevWorkflowDecisionKind.RequestChanges
    };

    /// <summary>
    ///     The output document a human gate produces for one answer — the document its out-edge conditions are then
    ///     evaluated against. Written in ONE place because two callers ask questions of it: the dispatcher, when it
    ///     routes an answer that has landed, and the API, when it tells the operator in advance whether a rejection has
    ///     anywhere to go. A second spelling of this shape would make those two disagree in exactly the case that
    ///     matters.
    /// </summary>
    public static string GateOutputJson(DevWorkflowDecisionKind decision) =>
        JsonSerializer.Serialize(new GateOutput(DevWorkflowNodeOutputStatuses.Succeeded, decision.ToString()), JsonOptions);

    /// <summary>
    ///     Every answer a human gate SUCCEEDS on — the three that part company in the graph rather than on the row. What
    ///     a gate's out-edges route is exactly this set, so anything that reasons about where an answer can go reads it
    ///     from <see cref="TargetFor" /> rather than listing the three by hand.
    /// </summary>
    public static IReadOnlyList<DevWorkflowDecisionKind> GateAnswers { get; } =
    [
        .. Enum.GetValues<DevWorkflowDecisionKind>().Where(static decision => TargetFor(decision) == DevWorkflowNodeRunStatus.Succeeded)
    ];

    /// <summary>
    ///     Whether an out-edge of a human gate fires for one answer, asked of the gate's own output document rather than
    ///     of a row — which is what a check made BEFORE the run has instead of one.
    ///     <para>
    ///         Composed from this class's own <see cref="GateOutputJson" /> and read by the same pair
    ///         <see cref="EdgeState" /> reads a landed row's output with, so a definition-time rule about where an answer
    ///         goes and the routing that actually takes it there cannot answer differently. Three callers ask: the parse
    ///         rule that refuses an apply a rejection could reach, the dispatcher when a recorded answer has landed, and
    ///         the API when it tells an operator in advance whether an answer has anywhere to go.
    ///     </para>
    /// </summary>
    public static bool GateEdgeFires(DevWorkflowGraphEdge edge, DevWorkflowDecisionKind decision)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return Fires(edge, GateOutputJson(decision));
    }

    /// <summary>A node run nothing further will happen to on its own.</summary>
    public static bool IsTerminal(DevWorkflowNodeRunStatus status) =>
        status is DevWorkflowNodeRunStatus.Succeeded
            or DevWorkflowNodeRunStatus.Failed
            or DevWorkflowNodeRunStatus.Skipped
            or DevWorkflowNodeRunStatus.Cancelled;

    /// <summary>
    ///     A node run the run is still waiting on — including the two human-wait states, which is what keeps a run from
    ///     completing behind an unanswered gate.
    /// </summary>
    public static bool IsLive(DevWorkflowNodeRunStatus status) =>
        !IsTerminal(status);

    public static bool IsTerminal(DevWorkflowRunStatus status) =>
        status is DevWorkflowRunStatus.Completed or DevWorkflowRunStatus.Failed or DevWorkflowRunStatus.Cancelled;

    /// <summary>
    ///     Whether an inbound edge lets its target through, read off the run's node runs — <c>Pending</c> when the
    ///     source has no row yet, because a source that has not been materialized is a wait rather than a refusal.
    ///     <para>
    ///         <c>Failed</c> and <c>Cancelled</c> sources kill every out-edge: neither produced the output a condition
    ///         would read, and treating "no output" as a passing condition is how a run routes on evidence it never
    ///         had. So does a <c>Succeeded</c> source whose condition did not fire — the branch a gate did not take.
    ///     </para>
    ///     <para>
    ///         A <c>Skipped</c> source is the one that parts company with them, because a skip has two origins the row
    ///         itself cannot tell apart. One is a PERSON: an operator looked at a node that could never succeed — one
    ///         slice of a decomposition whose file did not exist — and decided the run should carry on without it. The
    ///         other slices did their work, and skipping the join over the one that was excused throws all of it away;
    ///         live, a single Skip on one clone's implementation cancelled a run whose every sibling had succeeded. The
    ///         other origin is a CASCADE off something dead — a failed ancestor, or a gate branch nothing routed down —
    ///         where the skip is the run being told where it may not go, and carrying on past it would be routing on
    ///         work that was never done.
    ///     </para>
    ///     <para>
    ///         The difference is readable from the GRAPH rather than from a column, which is why no new one was added:
    ///         a skip is WAIVED when every path back from it is itself Satisfied or Waived, because that is exactly the
    ///         case where nothing upstream refused it and only a person could have. One dead path behind it and it
    ///         stays <c>Dead</c>, so a not-taken branch and a failed branch's tail keep the semantics they have always
    ///         had. That recursion is why this needs the graph and the whole dictionary rather than one source row.
    ///     </para>
    /// </summary>
    public static DevWorkflowEdgeState EdgeState(DevWorkflowGraphEdge edge,
        DevWorkflowGraph graph,
        IReadOnlyDictionary<string, DevWorkflowNodeRunSnapshot> nodeRunsByKey)
    {
        ArgumentNullException.ThrowIfNull(edge);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(nodeRunsByKey);

        return EdgeState(edge, graph, nodeRunsByKey, new Dictionary<string, bool>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
    }

    /// <summary>
    ///     Which of a run's SKIPPED node runs the state machine WAIVES — the skips a person chose, as opposed to the
    ///     ones that cascaded off something dead. Exactly the question <see cref="EdgeState" /> asks before it answers
    ///     <c>Waived</c> rather than <c>Dead</c>, asked here for a whole run at once.
    ///     <para>
    ///         Exposed because the answer is NOT readable from a skipped row on its own, and a read model that guesses
    ///         from status alone gets it backwards on the shape that matters: a failed node, a skip cascaded off it,
    ///         and a join beside a succeeded sibling. There the runtime skips the join, and the failed ancestor need
    ///         not be among the join's own dependencies for a client to see. Rather than mirror this recursion in the
    ///         browser, the API sends the verdict — which is why this is the state machine's own predicate and not a
    ///         second one shaped like it.
    ///     </para>
    ///     <para>
    ///         ONE memo across every skipped row, not one per row: it is the same walk over the same graph, and
    ///         re-running it per skip is what turns a wide fan-out of skips into a quadratic one.
    ///     </para>
    /// </summary>
    public static IReadOnlySet<string> WaivedSkipNodeKeys(DevWorkflowGraph graph,
        IReadOnlyDictionary<string, DevWorkflowNodeRunSnapshot> nodeRunsByKey)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(nodeRunsByKey);

        var waived = new Dictionary<string, bool>(StringComparer.Ordinal);
        var resolving = new HashSet<string>(StringComparer.Ordinal);
        return new HashSet<string>(nodeRunsByKey.Where(entry => entry.Value.Status == DevWorkflowNodeRunStatus.Skipped
                                                                && IsWaived(entry.Key, graph, nodeRunsByKey, waived, resolving))
                                                .Select(static entry => entry.Key),
            StringComparer.Ordinal);
    }

    private static DevWorkflowEdgeState EdgeState(DevWorkflowGraphEdge edge,
        DevWorkflowGraph graph,
        IReadOnlyDictionary<string, DevWorkflowNodeRunSnapshot> nodeRunsByKey,
        Dictionary<string, bool> waived,
        HashSet<string> resolving)
    {
        if (nodeRunsByKey.GetValueOrDefault(edge.From) is not { } source || !IsTerminal(source.Status))
        {
            return DevWorkflowEdgeState.Pending;
        }

        if (source.Status == DevWorkflowNodeRunStatus.Succeeded)
        {
            return Fires(edge, source.OutputJson) ? DevWorkflowEdgeState.Satisfied : DevWorkflowEdgeState.Dead;
        }

        return source.Status == DevWorkflowNodeRunStatus.Skipped && IsWaived(edge.From, graph, nodeRunsByKey, waived, resolving)
            ? DevWorkflowEdgeState.Waived
            : DevWorkflowEdgeState.Dead;
    }

    /// <summary>
    ///     Where a settled node run's own out-edges went, judged edge by edge by <see cref="EdgeState" /> itself rather
    ///     than by a second copy of the rule — so the recorded route and the routing that actually happened cannot
    ///     answer differently.
    ///     <para>
    ///         Two rules it inherits by delegating. A source that settled anything other than <c>Succeeded</c> kills
    ///         every out-edge without its conditions being consulted, so a failed, cancelled or skipped node run
    ///         records an empty <c>satisfied</c> list — which is the truth: it routed nowhere. And an edge leaving a
    ///         materialization TEMPLATE is dropped, matching <see cref="Admission" />: the template is the one node
    ///         deliberately never instantiated, and its edges are the authored shape the clones' own edges stand in for.
    ///     </para>
    ///     <para>
    ///         <c>Waived</c> gets a bucket of its OWN rather than being folded into either half, because no fold is
    ///         true under both join policies: a waived edge does not admit an <c>Any</c> successor, which is what
    ///         <c>satisfied</c> would claim, and it does not kill an <c>All</c> one, which is what <c>dead</c> would
    ///         claim. The three buckets are the three states <see cref="EdgeState" /> can answer for a terminal
    ///         source, one for one, which is the only shape that keeps the record and the routing from answering
    ///         differently.
    ///     </para>
    ///     <para>
    ///         Whether a skip was WAIVED is not readable off the source row — it is a walk back over the graph — so
    ///         the run's other rows come in as <paramref name="nodeRunsByKey" />. <paramref name="source" /> is laid
    ///         over that dictionary at its own key, because the caller projects the command's target status and output
    ///         onto it before asking and the stored row is still the previous attempt's.
    ///     </para>
    ///     <para>
    ///         A non-terminal source is REFUSED rather than recorded, because <see cref="EdgeState" /> answers
    ///         <c>Pending</c> for one and <see cref="DevWorkflowRoute" /> has no bucket for that. The caller computes a
    ///         route only for a terminal settle; <c>Blocked</c> and <c>WaitingForApproval</c> get no route at all,
    ///         which is honest — the node has not finished, so it has routed nowhere yet.
    ///     </para>
    /// </summary>
    /// <param name="decision">The gate's answer, for a <c>HumanGate</c> source. Recorded as the route's gate answer; the edge verdicts come from the output document either way, which is what keeps it agreeing with <see cref="GateEdgeFires" />.</param>
    internal static DevWorkflowRoute RouteTaken(DevWorkflowGraph graph,
        DevWorkflowNodeRunSnapshot source,
        IReadOnlyDictionary<string, DevWorkflowNodeRunSnapshot> nodeRunsByKey,
        DevWorkflowDecisionKind? decision)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(nodeRunsByKey);
        if (!IsTerminal(source.Status))
        {
            throw new ArgumentException($"A route may only be taken from a terminal node run; '{source.NodeKey}' is {source.Status}.", nameof(source));
        }

        var rows = new Dictionary<string, DevWorkflowNodeRunSnapshot>(nodeRunsByKey, StringComparer.Ordinal)
        {
            [source.NodeKey] = source
        };

        var satisfied = new List<string>();
        var dead = new List<string>();
        var waived = new List<string>();
        var edges = graph.TemplateKeys.Contains(source.NodeKey) ? [] : graph.OutboundEdges(source.NodeKey);
        foreach (var edge in edges)
        {
            switch (EdgeState(edge, graph, rows))
            {
                case DevWorkflowEdgeState.Satisfied:
                    satisfied.Add(edge.To);
                    break;
                case DevWorkflowEdgeState.Waived:
                    waived.Add(edge.To);
                    break;

                // Dead, and only Dead: Pending needs a source that is null or non-terminal, and both are refused above.
                default:
                    dead.Add(edge.To);
                    break;
            }
        }

        var truncated = satisfied.Count > MaxRoutedKeys || dead.Count > MaxRoutedKeys || waived.Count > MaxRoutedKeys;
        return new DevWorkflowRoute([.. satisfied.Take(MaxRoutedKeys)],
            [.. dead.Take(MaxRoutedKeys)],
            [.. waived.Take(MaxRoutedKeys)],
            decision?.ToString(),
            truncated);
    }

    /// <summary>
    ///     A route as the <c>route_json</c> column stores it, dropping keys until the document fits the column's bound
    ///     rather than clipping it mid-string — a truncated document that no longer parses would take the whole recipe
    ///     down with it. Anything dropped raises <see cref="DevWorkflowRoute.Truncated" />, so a short list is never
    ///     read as a complete one.
    /// </summary>
    internal static string RouteJson(DevWorkflowRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);

        var satisfied = route.Satisfied.ToList();
        var dead = route.Dead.ToList();
        var waived = route.Waived.ToList();
        var truncated = route.Truncated;
        while (true)
        {
            var json = JsonSerializer.Serialize(new DevWorkflowRoute(satisfied, dead, waived, route.GateAnswer, truncated), JsonOptions);
            if (json.Length <= MaxRouteJson || (satisfied.Count == 0 && dead.Count == 0 && waived.Count == 0))
            {
                return json;
            }

            // Drop from the longest list, so a route with one satisfied edge and nine dead ones keeps the edge that says
            // where the run went rather than losing it to the ones that say where it did not.
            var longest = new[]
            {
                satisfied,
                dead,
                waived
            }.MaxBy(static list => list.Count)!;
            longest.RemoveAt(longest.Count - 1);
            truncated = true;
        }
    }

    /// <summary>
    ///     The answer a human gate settled on, read back off the output document <see cref="GateOutputJson" /> wrote —
    ///     the same pairing, so the writer and this reader cannot drift. Null for any other document, including a
    ///     structural node's.
    /// </summary>
    internal static DevWorkflowDecisionKind? GateDecisionFrom(string? outputJson)
    {
        if (ParseOutput(outputJson) is not { ValueKind: JsonValueKind.Object } output
            || !output.TryGetProperty("decision", out var decision)
            || decision.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return Enum.TryParse<DevWorkflowDecisionKind>(decision.GetString(), out var parsed) ? parsed : null;
    }

    /// <summary>
    ///     Whether a Skipped node's own skip was a person's choice rather than something upstream refusing it. The
    ///     question is asked under the node's OWN join policy, because that policy is what decides which of its
    ///     dependencies ever had to arrive. Under <c>All</c> every one of them did, so a skip is a person's only when
    ///     they all read <c>Satisfied</c> or <c>Waived</c>. Under <c>Any</c> one arriving was the whole contract: a
    ///     node admitted on one satisfied edge RAN, and if it then blocked and an operator skipped it, the Dead sibling
    ///     it was never waiting on is not what caused that. Judging it with an <c>All</c> predicate regardless is how
    ///     one such skip read as a cascade and took a downstream <c>All</c> join's surviving work down with it.
    ///     <para>
    ///         A node with NO inbound edges is vacuously waived under either policy, and that is the right answer
    ///         rather than a gap — an entry node has nothing upstream that could have refused it, so an operator is
    ///         the only thing that can have skipped it. An <c>Any</c> node whose edges are Dead and Waived with none
    ///         Satisfied stays <c>Dead</c>: nothing arrived, so it never had an admission of its own to lose.
    ///     </para>
    ///     <para>
    ///         A <c>Pending</c> dependency waives nothing under either policy, which is the conservative half of the
    ///         old rule kept intact: a source still to settle may yet die, and answering now is answering ahead of it.
    ///     </para>
    ///     <para>
    ///         Two collections, and both are load-bearing. <paramref name="waived" /> MEMOIZES: a diamond reaches the
    ///         same ancestor down two paths, and re-walking it is what turns a wide fan-out into an exponential one.
    ///         <paramref name="resolving" /> is the cycle guard, and it has to be separate from the memo because it is
    ///         popped again on the way out — a node still on the stack is not an answer, whereas one the memo holds is.
    ///         The graph is a validated DAG so the guard should never fire; refusing to waive is the answer that keeps
    ///         the old behaviour if a malformed one ever reaches here.
    ///     </para>
    /// </summary>
    private static bool IsWaived(string nodeKey,
        DevWorkflowGraph graph,
        IReadOnlyDictionary<string, DevWorkflowNodeRunSnapshot> nodeRunsByKey,
        Dictionary<string, bool> waived,
        HashSet<string> resolving)
    {
        if (waived.TryGetValue(nodeKey, out var known))
        {
            return known;
        }

        if (!resolving.Add(nodeKey))
        {
            return false;
        }

        var states = Dependencies(graph, nodeKey)
                     .Select(edge => EdgeState(edge, graph, nodeRunsByKey, waived, resolving))
                     .ToList();
        var nothingRefusedIt = states.All(static state => state is DevWorkflowEdgeState.Satisfied or DevWorkflowEdgeState.Waived);
        var answer = graph.Nodes.GetValueOrDefault(nodeKey)?.JoinPolicy == DevWorkflowJoinPolicy.Any
            ? nothingRefusedIt || states.Contains(DevWorkflowEdgeState.Satisfied)
            : nothingRefusedIt;
        _ = resolving.Remove(nodeKey);
        waived[nodeKey] = answer;
        return answer;
    }

    /// <summary>
    ///     The inbound edges that are DEPENDENCIES. An edge whose source is a materialization TEMPLATE is not one: the
    ///     template is the one node deliberately never instantiated, so its edge into the join can never be satisfied
    ///     and can never die either — it is the authored shape the clones' own edges stand in for, and reading it as a
    ///     dependency would leave every decomposing run waiting on a row that is never written.
    /// </summary>
    private static IEnumerable<DevWorkflowGraphEdge> Dependencies(DevWorkflowGraph graph, string nodeKey) =>
        graph.InboundEdges(nodeKey).Where(edge => !graph.TemplateKeys.Contains(edge.From));

    /// <summary>Whether one edge's condition accepts one output document. The only place either question is answered.</summary>
    private static bool Fires(DevWorkflowGraphEdge edge, string? outputJson) =>
        DevWorkflowCondition.Evaluate(edge.Condition, ParseOutput(outputJson));

    /// <summary>
    ///     Whether a <c>Pending</c> node run may be queued, must be skipped, or is still waiting.
    ///     <para>
    ///         <c>All</c> over ZERO inbound edges is vacuously satisfied, and that is load-bearing rather than pedantic:
    ///         it is how an entry node becomes eligible at all, Start being implicit, so an entry node is one with no
    ///         inbound edges. A decomposition that produced no tasks is a neighbouring case and no longer this one: it
    ///         rewrites nothing, so its join keeps its edge from the decomposition, and that one Satisfied edge is what
    ///         carries it — which is why the materializer preserves that edge on the expanding path too.
    ///     </para>
    ///     <para>
    ///         Under <c>All</c>, one DEAD edge still skips the join, and one WAIVED edge no longer does. A person who
    ///         skips one leaf of a fan-out is saying the run should go on without it, so the join goes on as long as
    ///         something actually arrived — a satisfied sibling. With every edge waived nothing arrived, so it skips,
    ///         and because that node's own inbound edges were all waived, its skip is waived in turn: the cascade runs
    ///         on exactly as far as the excusing does, and stops at the first node a satisfied edge reaches.
    ///     </para>
    ///     <para>
    ///         Which is what makes the seeded <c>feature-development-v1</c> shape worth naming: its join also has the
    ///         <c>decompose → join</c> edge, Satisfied from the moment the decomposition succeeded. So if an operator
    ///         skips EVERY clone, that one edge still carries the join and the verification node runs — over a task
    ///         package and no implementations. That is accepted rather than overlooked: verify is an agent asked to
    ///         judge what was produced, and judging that nothing was is a better answer than a run that goes terminal
    ///         without saying so. The skipped clones reach its objective by name, so it is judging with the facts.
    ///     </para>
    ///     <para>
    ///         <c>Any</c> is untouched: <c>Waived</c> is not <c>Satisfied</c>, and a merge that exists to carry ONE
    ///         live branch cannot be carried by a branch nobody ran.
    ///     </para>
    /// </summary>
    public static DevWorkflowNodeAdmission Admission(DevWorkflowGraphNode node,
        DevWorkflowGraph graph,
        IReadOnlyDictionary<string, DevWorkflowNodeRunSnapshot> nodeRunsByKey)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(nodeRunsByKey);

        var waived = new Dictionary<string, bool>(StringComparer.Ordinal);
        var resolving = new HashSet<string>(StringComparer.Ordinal);
        var states = Dependencies(graph, node.NodeKey)
                     .Select(edge => EdgeState(edge, graph, nodeRunsByKey, waived, resolving))
                     .ToList();

        // Pending outranks Dead under BOTH policies, and for the same reason: the answer is not allowed to depend on
        // which branch happened to land first. A dead inbound edge already settles what an `All` join will DO — it can
        // never fire, so it will be skipped — but settling it while a sibling branch is still running skips the node,
        // and everything after it, in front of work the run has not finished. Live, one clone's validate was skipped
        // and the integration stage went terminal with the other clone's implementation still to come.
        if (states.Contains(DevWorkflowEdgeState.Pending))
        {
            return DevWorkflowNodeAdmission.Wait;
        }

        if (node.JoinPolicy == DevWorkflowJoinPolicy.All)
        {
            if (states.Contains(DevWorkflowEdgeState.Dead))
            {
                return DevWorkflowNodeAdmission.Skip;
            }

            // Zero edges is the vacuous case above; otherwise something has to have ARRIVED. Every edge waived means
            // every branch was excused and none of them carried anything here.
            return states.Count == 0 || states.Contains(DevWorkflowEdgeState.Satisfied)
                ? DevWorkflowNodeAdmission.Eligible
                : DevWorkflowNodeAdmission.Skip;
        }

        // Any: one satisfied branch is enough, but only once no sibling could still satisfy one.
        return states.Contains(DevWorkflowEdgeState.Satisfied) ? DevWorkflowNodeAdmission.Eligible : DevWorkflowNodeAdmission.Skip;
    }

    /// <summary>
    ///     Why a node run <see cref="Admission" /> answered <c>Skip</c> for is being skipped, in the words its
    ///     <c>terminal_reason</c> keeps. A cascaded skip used to record nothing at all, which left an operator reading
    ///     fourteen Skipped rows with no way to tell which one of them the decision was and which thirteen followed it
    ///     — and left the downstream evidence with nothing to quote.
    ///     <para>
    ///         Names ONE dead dependency, because a skip needs one cause rather than a list — and prefers a branch that
    ///         broke or was skipped over one a condition merely routed past. Both are dead, but only the first is news:
    ///         a gate taking its other branch is the graph working, and naming it first reads as the cause when a real
    ///         refusal is sitting beside it.
    ///     </para>
    ///     <para>
    ///         With NO dead dependency the node is being skipped because every branch into it was excused, and then the
    ///         only account of why is the one the excused row already carries — which for a person's decision is the
    ///         operator's own sentence. It is PROPAGATED rather than restated, because the node someone actually
    ///         skipped is routinely not the one a downstream reader sees: on the seeded template an operator skips
    ///         <c>implement#task</c>, its <c>validate#task</c> clone is skipped behind it, and the clone is what the
    ///         verification node's producing-ancestor walk stops at. Restating it generically there is how the comment
    ///         explaining the whole thing failed to reach the agent asked to judge it.
    ///     </para>
    ///     <para>
    ///         Sanitized by construction as far as this class writes it — every word is fixed text or a node key. The
    ///         propagated tail is another row's <c>terminal_reason</c>, which is the operator comment the decision path
    ///         already bounded and sanitized before it was stored.
    ///     </para>
    /// </summary>
    public static string SkipReason(DevWorkflowGraphNode node,
        DevWorkflowGraph graph,
        IReadOnlyDictionary<string, DevWorkflowNodeRunSnapshot> nodeRunsByKey)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(nodeRunsByKey);

        var waived = new Dictionary<string, bool>(StringComparer.Ordinal);
        var resolving = new HashSet<string>(StringComparer.Ordinal);
        var dead = new List<string>();
        string? excused = null;
        foreach (var edge in Dependencies(graph, node.NodeKey))
        {
            switch (EdgeState(edge, graph, nodeRunsByKey, waived, resolving))
            {
                case DevWorkflowEdgeState.Dead:
                    dead.Add(edge.From);
                    break;
                case DevWorkflowEdgeState.Waived:
                    excused ??= edge.From;
                    break;
                default:
                    break;
            }
        }

        if (dead.Count > 0)
        {
            var cause = dead.Find(key => nodeRunsByKey.GetValueOrDefault(key)?.Status != DevWorkflowNodeRunStatus.Succeeded) ?? dead[0];
            return nodeRunsByKey.GetValueOrDefault(cause)?.Status switch
            {
                DevWorkflowNodeRunStatus.Skipped => $"Skipped: upstream '{cause}' was skipped.",

                // Succeeded and still dead means its condition did not accept this edge — the branch was not taken.
                DevWorkflowNodeRunStatus.Succeeded => $"Skipped: upstream '{cause}' routed elsewhere.",
                _ => $"Skipped: upstream '{cause}' did not succeed."
            };
        }

        // Every branch excused: quote the first one's own reason, so a chain of them carries the original sentence.
        if (excused is not null && nodeRunsByKey.GetValueOrDefault(excused)?.TerminalReason is { Length: > 0 } carried)
        {
            return Bounded($"Skipped: upstream '{excused}' was {char.ToLowerInvariant(carried[0])}{carried[1..]}", MaxNodeTerminalReason);
        }

        return "Skipped: every step before this one was skipped.";
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
    ///     The status a run should hold given its node runs and its CURRENT pinned graph, recomputed from scratch at the
    ///     end of every tick rather than accumulated. It is denormalized on purpose so a reader can answer "what is this
    ///     run doing" without a join.
    ///     <para>
    ///         The <c>-ing</c> statuses are not decided here: they are intents a command wrote, and only the drain that
    ///         settles them may clear them. Terminal runs are likewise left alone.
    ///     </para>
    ///     <para>
    ///         Graph-aware on purpose. <c>Completed</c> means at least one TERMINAL node — one no edge leaves,
    ///         <see cref="DevWorkflowGraph.TerminalNodeKeys" /> — succeeded, so a run whose tail an operator skipped, or
    ///         whose gate rejection routed down a branch that skipped the remainder, reads <c>Cancelled</c> with a
    ///         reason naming the ends it never reached rather than <c>Completed</c> like a run that did its job.
    ///         <c>Failed</c> outranks both, unchanged: a node that failed is the answer to why the run stopped.
    ///     </para>
    /// </summary>
    public static DevWorkflowRunOutcome Recompute(DevWorkflowRunStatus current,
        DevWorkflowGraph graph,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(nodeRuns);

        return Settled(current, nodeRuns) is { } settled ? new DevWorkflowRunOutcome(settled) : Terminalize(graph, nodeRuns);
    }

    /// <summary>
    ///     Everything a run's status can be decided from the ROWS alone — or <see langword="null" />, meaning every node
    ///     run is terminal and only the graph can say what that amounts to.
    /// </summary>
    private static DevWorkflowRunStatus? Settled(DevWorkflowRunStatus current, IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns)
    {
        if (IsTerminal(current) || current is DevWorkflowRunStatus.Pausing or DevWorkflowRunStatus.Cancelling or DevWorkflowRunStatus.Paused)
        {
            return current;
        }

        if (nodeRuns.Any(nodeRun => IsLive(nodeRun.Status)))
        {
            if (nodeRuns.Any(nodeRun => nodeRun.Status is DevWorkflowNodeRunStatus.Queued or DevWorkflowNodeRunStatus.Running))
            {
                return DevWorkflowRunStatus.Running;
            }

            // Blocked and WaitingForApproval both mean "a human has to act", and they outrank Pending deliberately:
            // every node run of a graph exists from the moment the run starts, so there are almost always Pending rows
            // waiting on a branch that has not settled. Reading those as Running would report a run blocked on an
            // unanswered gate as busy, which is the one thing the two statuses exist to tell apart.
            return nodeRuns.Any(nodeRun => nodeRun.Status is DevWorkflowNodeRunStatus.WaitingForApproval or DevWorkflowNodeRunStatus.Blocked)
                ? DevWorkflowRunStatus.WaitingForApproval
                : DevWorkflowRunStatus.Running;
        }

        // A run with no node runs at all has not been materialized yet; it is still Pending, not complete.
        return nodeRuns.Count == 0 ? current : null;
    }

    /// <summary>
    ///     What a run whose every node run is terminal amounts to, asked of the graph. Skipped and Cancelled node runs
    ///     do not block an end — they simply are not one, and this is where that distinction is made.
    ///     <para>
    ///         <c>GateRejected</c> here means only this: a human gate somewhere in this run was refused, and the run
    ///         reached no end. It is NOT a causal proof, and must not be read as one — a rejection can route into a
    ///         branch that runs perfectly well, and a false condition further down can be what actually killed the
    ///         tail. The class narrows where a reader should look; the reason names what was actually not reached, and
    ///         the event log is what says in which order. Proving the cause would mean walking the dead edges back to
    ///         their first refusal, which is a graph search this rule does not need to pick the right thing to show.
    ///     </para>
    /// </summary>
    private static DevWorkflowRunOutcome Terminalize(DevWorkflowGraph graph, IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns)
    {
        if (nodeRuns.Any(static nodeRun => nodeRun.Status == DevWorkflowNodeRunStatus.Failed))
        {
            return new DevWorkflowRunOutcome(DevWorkflowRunStatus.Failed);
        }

        var ends = nodeRuns.Where(nodeRun => graph.TerminalNodeKeys.Contains(nodeRun.NodeKey)).ToList();
        if (ends.Any(static nodeRun => nodeRun.Status == DevWorkflowNodeRunStatus.Succeeded))
        {
            return new DevWorkflowRunOutcome(DevWorkflowRunStatus.Completed);
        }

        // GateRejected only when a gate was refused at all. The other way here is an operator Skip abandoning the
        // tail, which is a human decision rather than a failure and has no honest class in the vocabulary — the reason
        // carries the whole answer there, and inventing a class for it would put a token in the durable log that means
        // "a person chose this".
        var refused = nodeRuns.FirstOrDefault(static nodeRun => Refusal(nodeRun) is not null);
        return new DevWorkflowRunOutcome(DevWorkflowRunStatus.Cancelled,
            refused is null ? null : DevWorkflowFailureClasses.GateRejected,
            Reason(ends, refused));
    }

    /// <summary>
    ///     Why a run that reached no end stopped, naming the ends it did not reach and what became of them.
    ///     <para>
    ///         Sanitized by construction rather than by a pass: every word of it is either fixed text or a node key
    ///         from the run's own definition graph, and a materialization CLONE — the one node key a model has a hand
    ///         in — is never terminal, because its leaf edge is rewired to the join when it is created. Bounded the
    ///         same way: at most <see cref="MaxNamedNodes" /> keys are named before the rest are counted, and the
    ///         whole sentence is cut to the column's own <see cref="MaxTerminalReason" />.
    ///     </para>
    /// </summary>
    private static string Reason(IReadOnlyList<DevWorkflowNodeRunSnapshot> ends, DevWorkflowNodeRunSnapshot? refused)
    {
        var listed = string.Join(", ", ends.Take(MaxNamedNodes).Select(static end => $"'{end.NodeKey}' was {end.Status}"));
        if (ends.Count > MaxNamedNodes)
        {
            listed += $", and {ends.Count - MaxNamedNodes} more";
        }

        var named = ends.Count == 0 ? "this run reached none of the graph's ends" : listed;

        // The answer itself, not a paraphrase: Reject and RequestChanges dead-end a run identically, and a reader who
        // is shown "was rejected" for a RequestChanges goes looking for a decision row that says no such thing.
        var cause = refused is null ? string.Empty : $", after the gate '{refused.NodeKey}' answered {Refusal(refused)}";
        var reason = $"No terminal node succeeded: {named}{cause}.";
        if (reason.Length <= MaxTerminalReason)
        {
            return reason;
        }

        // Back off one when the bound falls between a surrogate pair. A node key is not charset-restricted, so an
        // astral character can straddle the cut, and half of one is a broken string in the column and on the wire.
        var cut = char.IsHighSurrogate(reason[MaxTerminalReason - 1]) ? MaxTerminalReason - 1 : MaxTerminalReason;
        return reason[..cut];
    }

    /// <summary>
    ///     Which answer a human gate was REFUSED with, or <see langword="null" /> when this node run is not a refused
    ///     gate. <c>Reject</c> and <c>RequestChanges</c> both count: each is a person declining to let the run
    ///     through, and each leaves the same shape behind when nothing downstream of it reaches an end.
    ///     <para>
    ///         Matched against <see cref="GateOutputJson" /> because the dispatcher writes a gate's output FROM that
    ///         method and nothing else writes it at all — so this is the cheapest honest source, and it cannot drift
    ///         from what a gate actually stores the way a second spelling of the shape would.
    ///     </para>
    /// </summary>
    private static DevWorkflowDecisionKind? Refusal(DevWorkflowNodeRunSnapshot nodeRun) =>
        nodeRun is { NodeType: DevWorkflowNodeType.HumanGate, Status: DevWorkflowNodeRunStatus.Succeeded }
        && GateRefusals.TryGetValue(nodeRun.OutputJson ?? string.Empty, out var decision)
            ? decision
            : null;

    /// <summary>
    ///     Where a run's status and its node runs leave the work item. Written inside the same transaction as the run
    ///     transition, never derived on read and never client-writable, so the two can never disagree.
    ///     <para>
    ///         ANY blocked node run blocks the work item, even while the run itself reads <c>Running</c> because a
    ///         sibling is still working. Reading only the run status would leave a work item Active with a node run
    ///         nobody is coming to unblock — the list page's whole job is to surface exactly that.
    ///     </para>
    /// </summary>
    public static DevWorkflowWorkItemStatus WorkItemStatusFor(DevWorkflowRunStatus runStatus, IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns)
    {
        ArgumentNullException.ThrowIfNull(nodeRuns);

        return runStatus switch
        {
            DevWorkflowRunStatus.Completed => DevWorkflowWorkItemStatus.Completed,
            DevWorkflowRunStatus.Cancelled => DevWorkflowWorkItemStatus.Cancelled,

            // A failed run needs attention; it is not done. Same for a run waiting on a human.
            DevWorkflowRunStatus.Failed or DevWorkflowRunStatus.WaitingForApproval => DevWorkflowWorkItemStatus.Blocked,
            _ when nodeRuns.Any(static nodeRun => nodeRun.Status == DevWorkflowNodeRunStatus.Blocked) => DevWorkflowWorkItemStatus.Blocked,
            _ => DevWorkflowWorkItemStatus.Active
        };
    }

    /// <summary>
    ///     Where a human's answer leaves the node run it answers.
    ///     <para>
    ///         A gate answer always SUCCEEDS the gate, whichever of the three it is: the answer is the node's output,
    ///         and routing on it is the edges' job. A rejection reaches the run through an out-edge that matches
    ///         nothing, not through a node failure — which is why <c>Reject</c> and <c>Approve</c> land in the same
    ///         place here and part company in the graph.
    ///     </para>
    ///     <para>
    ///         Shared by the decision endpoint and the dispatcher so the two cannot disagree about which answers a row
    ///         in a given status can take: the endpoint refuses the rest with a conflict, and the dispatcher keeps its
    ///         own guard for a decision recorded around it.
    ///     </para>
    /// </summary>
    public static DevWorkflowNodeRunStatus TargetFor(DevWorkflowDecisionKind decision) =>
        decision switch
        {
            DevWorkflowDecisionKind.Approve or DevWorkflowDecisionKind.Reject or DevWorkflowDecisionKind.RequestChanges => DevWorkflowNodeRunStatus.Succeeded,

            // Forced: a human retry is legal AT the cap, and pays for it by RAISING the row's MaxAttempts by one
            // (TransitionDevWorkflowNodeRunCommand.WidenMaxAttempts), so the attempt never exceeds the cap it is
            // measured against. Beyond that only the run-wide attempt budget bounds it.
            DevWorkflowDecisionKind.Retry => DevWorkflowNodeRunStatus.Pending,
            DevWorkflowDecisionKind.Skip => DevWorkflowNodeRunStatus.Skipped,
            _ => DevWorkflowNodeRunStatus.Failed
        };

    /// <summary>
    ///     Whether a human may answer <paramref name="decision" /> on a node run in <paramref name="status" />.
    ///     <para>
    ///         Nearly the transition table, and deliberately not quite: that table answers "may the RUNTIME move this row
    ///         here", and one of its edges — an open gate going back to <c>Pending</c> — belongs to the fix loop's reset
    ///         and to nothing a person clicks. A <c>Retry</c> on an unanswered gate has no failed attempt to schedule
    ///         again, and X3 is explicit that a gate takes the first three answers and nothing else.
    ///     </para>
    ///     <para>
    ///         Shared by the decision endpoint and by the surface that advertises the answers, so what is offered and
    ///         what is accepted cannot drift.
    ///     </para>
    /// </summary>
    public static bool IsDecidable(DevWorkflowNodeRunStatus status, DevWorkflowDecisionKind decision) =>
        status is DevWorkflowNodeRunStatus.WaitingForApproval or DevWorkflowNodeRunStatus.Blocked
        && (status != DevWorkflowNodeRunStatus.WaitingForApproval || decision != DevWorkflowDecisionKind.Retry)
        && IsLegal(status, TargetFor(decision));

    /// <summary>
    ///     Where a node-run transition about to be written leaves the work item, so the move can carry it in its own
    ///     transaction.
    ///     <para>
    ///         Needed because the run status often does not change when a node run does — a node blocking while a
    ///         sibling still works leaves the run <c>Running</c> — and the end-of-tick recomputation writes nothing when
    ///         the run status is unchanged. Without this the work item would keep reading <c>Active</c> with a node run
    ///         nobody is coming to unblock, which is the one thing the list page exists to surface.
    ///     </para>
    /// </summary>
    public static DevWorkflowWorkItemStatus WorkItemStatusAfter(DevWorkflowRunStatus runStatus,
        IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns,
        Guid nodeRunId,
        DevWorkflowNodeRunStatus target)
    {
        ArgumentNullException.ThrowIfNull(nodeRuns);

        var projected = nodeRuns.Select(nodeRun => nodeRun.Id == nodeRunId
            ? nodeRun with
            {
                Status = target
            }
            : nodeRun).ToList();

        // Deliberately NOT graph-aware, and it does not need to be. This exists for the case the end-of-tick
        // recomputation cannot carry — a node blocking under a run whose status does not move — and every TERMINAL
        // answer it gives is provisional: the same tick's Recompute asks the graph and writes the work item again
        // from that answer, so the only thing a graph would buy here is a parameter two of the five callers have
        // no way to supply, for a value nobody reads by the time they look.
        var projectedRun = Settled(runStatus, projected)
                           ?? (projected.Any(static nodeRun => nodeRun.Status == DevWorkflowNodeRunStatus.Failed)
                               ? DevWorkflowRunStatus.Failed
                               : DevWorkflowRunStatus.Completed);
        return WorkItemStatusFor(projectedRun, projected);
    }

    /// <summary>
    ///     The run transition table. Every terminal is reached through a drain (<c>Pausing</c>/<c>Cancelling</c>) or
    ///     through the "nothing is live any more" recomputation, and the invariant behind that is about LIVE work: a
    ///     terminal written over a run with a live node run strands it under a run no tick advances again, leaking the
    ///     slots its executor holds.
    ///     <para>
    ///         <c>Running → Cancelled</c> and <c>WaitingForApproval → Cancelled</c> are that recomputation's own edges
    ///         and nothing else's. They are safe under exactly the same invariant rather than in spite of it: the only
    ///         caller that can produce that target is <c>RecomputeRunStatusAsync</c>, and <see cref="Recompute" />
    ///         reaches its terminalization branch ONLY once every node run is already terminal. There is nothing left
    ///         to strand, and nothing to drain either — routing through <c>Cancelling</c> would cost a whole extra tick
    ///         to settle something already knowable. The X10 gate-reject path keeps its drain, because there a live
    ///         sibling genuinely may still be mid-build.
    ///     </para>
    /// </summary>
    public static bool IsLegal(DevWorkflowRunStatus from, DevWorkflowRunStatus to) =>
        from switch
        {
            DevWorkflowRunStatus.Pending => to is DevWorkflowRunStatus.Running or DevWorkflowRunStatus.Failed or DevWorkflowRunStatus.Pausing or DevWorkflowRunStatus.Cancelling,
            DevWorkflowRunStatus.Running => to is DevWorkflowRunStatus.WaitingForApproval
                or DevWorkflowRunStatus.Pausing
                or DevWorkflowRunStatus.Cancelling
                or DevWorkflowRunStatus.Completed
                or DevWorkflowRunStatus.Cancelled
                or DevWorkflowRunStatus.Failed,
            DevWorkflowRunStatus.WaitingForApproval => to is DevWorkflowRunStatus.Running
                or DevWorkflowRunStatus.Pausing
                or DevWorkflowRunStatus.Cancelling
                or DevWorkflowRunStatus.Completed
                or DevWorkflowRunStatus.Cancelled
                or DevWorkflowRunStatus.Failed,
            DevWorkflowRunStatus.Pausing => to is DevWorkflowRunStatus.Paused or DevWorkflowRunStatus.Cancelling,
            DevWorkflowRunStatus.Paused => to is DevWorkflowRunStatus.Running or DevWorkflowRunStatus.Cancelling,
            DevWorkflowRunStatus.Cancelling => to is DevWorkflowRunStatus.Cancelled,
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
    ///         The four edges OUT of a terminal status back to <c>Pending</c> belong to the cross-node fix loop (X9) and
    ///         to nothing else: when a failure routes to an upstream node, every node run downstream of that node has to
    ///         re-run against the new implementation, and those rows are settled by definition — the whole point is that
    ///         they already produced an answer to a question that is being asked again. A <c>Succeeded</c> row left
    ///         alone would be a stale result masquerading as a current one, which is the outcome that rule exists to
    ///         prevent. Nothing else may write them: the decision path only ever moves a row out of
    ///         <c>WaitingForApproval</c> or <c>Blocked</c>, and every executor settles forwards.
    ///     </para>
    /// </summary>
    public static bool IsLegal(DevWorkflowNodeRunStatus from, DevWorkflowNodeRunStatus to) =>
        from switch
        {
            // Straight to Running is the inline lane: a gate, a join or a fan-out waits for no slot, so routing it
            // through Queued would write a queue reason there is no honest token for.
            DevWorkflowNodeRunStatus.Pending => to is DevWorkflowNodeRunStatus.Queued
                or DevWorkflowNodeRunStatus.Running
                or DevWorkflowNodeRunStatus.Skipped
                or DevWorkflowNodeRunStatus.Blocked
                or DevWorkflowNodeRunStatus.Cancelled,
            DevWorkflowNodeRunStatus.Queued => to is DevWorkflowNodeRunStatus.Running
                or DevWorkflowNodeRunStatus.Pending
                or DevWorkflowNodeRunStatus.Blocked
                or DevWorkflowNodeRunStatus.Failed
                or DevWorkflowNodeRunStatus.Cancelled,
            DevWorkflowNodeRunStatus.Running => to is DevWorkflowNodeRunStatus.Succeeded
                or DevWorkflowNodeRunStatus.Failed
                or DevWorkflowNodeRunStatus.WaitingForApproval
                or DevWorkflowNodeRunStatus.Blocked
                or DevWorkflowNodeRunStatus.Pending
                or DevWorkflowNodeRunStatus.Cancelled,
            // NOT Skipped (X3): a gate's three answers all SUCCEED it and route on the answer, while the three
            // interventions belong to Blocked. Skipping an open gate would be an operator walking past an approval
            // instead of giving one — the one thing a gate exists to make impossible. The only other moves are the
            // drain's cancel and the fix loop's reset, and the reset is the OPPOSITE of walking past it: an open gate
            // downstream of a node being re-attempted is being asked to approve work that is being replaced, so it is
            // re-asked from the start of a new attempt rather than answered about the old one.
            DevWorkflowNodeRunStatus.WaitingForApproval => to is DevWorkflowNodeRunStatus.Succeeded
                or DevWorkflowNodeRunStatus.Cancelled
                or DevWorkflowNodeRunStatus.Pending,

            // The intervention answers: Retry re-attempts, Skip routes around, Abandon gives up for good.
            DevWorkflowNodeRunStatus.Blocked => to is DevWorkflowNodeRunStatus.Pending
                or DevWorkflowNodeRunStatus.Skipped
                or DevWorkflowNodeRunStatus.Failed
                or DevWorkflowNodeRunStatus.Cancelled,

            // Succeeded has one move the other terminals do not, and exactly one: a decomposition's output is JUDGED
            // after the row that produced it settled, so a task package nothing can use has to stand its own author
            // down for a human. Leaving it Succeeded would let the run complete over work it never decomposed, and
            // failing it would say the agent broke when what it did was answer badly.
            DevWorkflowNodeRunStatus.Succeeded => to is DevWorkflowNodeRunStatus.Pending or DevWorkflowNodeRunStatus.Blocked,

            // The fix loop's reset, and only it. See the remarks above.
            DevWorkflowNodeRunStatus.Failed
                or DevWorkflowNodeRunStatus.Skipped
                or DevWorkflowNodeRunStatus.Cancelled => to is DevWorkflowNodeRunStatus.Pending,
            _ => false
        };

    public static void EnsureLegal(DevWorkflowRunStatus from, DevWorkflowRunStatus to)
    {
        if (!IsLegal(from, to))
        {
            throw new DevWorkflowInvalidTransitionException($"A development workflow run in {from} cannot move to {to}.");
        }
    }

    public static void EnsureLegal(DevWorkflowNodeRunStatus from, DevWorkflowNodeRunStatus to, string nodeKey)
    {
        if (!IsLegal(from, to))
        {
            throw new DevWorkflowInvalidTransitionException($"Node run '{nodeKey}' is {from} and cannot move to {to}.");
        }
    }

    /// <summary>
    ///     A node run's output document, or <see langword="null" /> when it has none or the stored text is not an object.
    ///     Unreadable output is treated as absent so conditions fail closed rather than throwing mid-tick.
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

    private sealed record GateOutput(string Status, string Decision);
}
