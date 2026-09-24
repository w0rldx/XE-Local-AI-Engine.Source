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

    /// <summary>The source was Skipped and nothing upstream refused it, so a person did; it carries nothing.</summary>
    /// <remarks>
    ///     Not a reason to throw away what its siblings carried. Excused rather than satisfied: an <c>Any</c> join
    ///     still needs a branch that actually arrived.
    /// </remarks>
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

/// <summary>What recomputing a run's status concluded, and — when the answer is <c>Cancelled</c> — why.</summary>
/// <remarks>
///     A status alone cannot carry that: a run whose tail was abandoned has no failing node run to read the reason
///     off, because nothing failed. Passed straight into the run transition, the only writer of both columns.
/// </remarks>
internal readonly record struct DevWorkflowRunOutcome(DevWorkflowRunStatus Status, string? FailureClass = null, string? TerminalReason = null);

/// <summary>Where one SETTLED node run's out-edges went — the record behind its <c>route_json</c> column.</summary>
/// <remarks>
///     Judged by the state machine itself. <see cref="Satisfied" /> means this node's out-edge condition was
///     satisfied, NOT that the successor ran: admission is a question about a TARGET's inbound edges. There is no
///     <c>Pending</c> bucket, which is proven rather than omitted —
///     <see cref="DevWorkflowStateMachine.RouteTaken" /> refuses a non-terminal source, the only state
///     <see cref="DevWorkflowStateMachine.EdgeState" /> answers <c>Pending</c> for.
/// </remarks>
/// <param name="GateAnswer">The decision token a human gate settled on; null on every other node type.</param>
/// <param name="Truncated">Whether keys were dropped to keep the serialized document inside the column's bound.</param>
public sealed record DevWorkflowRoute(
    IReadOnlyList<string> Satisfied,
    IReadOnlyList<string> Dead,
    IReadOnlyList<string> Waived,
    string? GateAnswer,
    bool Truncated);

/// <summary>The run and node-run state machines, as pure functions over persisted rows and the parsed graph.</summary>
/// <remarks>
///     The store deliberately does not judge transitions — it provides the rejection channel and enforces only what
///     the database can. These functions are therefore the only guard, and being free of I/O is what lets the whole
///     truth table be tested without a database.
/// </remarks>
internal static class DevWorkflowStateMachine
{
    /// <summary>camelCase, matching every other document this product puts on a wire.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The schema's own bound on the RUN's <c>terminal_reason</c> (<c>DevWorkflowRunConfiguration</c>).</summary>
    private const int MaxTerminalReason = 512;

    /// <summary>The NODE RUN's own bound (<c>DevWorkflowNodeRunConfiguration</c>), twice the run's.</summary>
    /// <remarks>
    ///     A cascaded skip's reason quotes the reason above it, so a long chain under a verbose operator comment is
    ///     the one shape that can grow toward it. Cut here rather than at the store: a run that wedges on a column
    ///     length is a run stopped by punctuation.
    /// </remarks>
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

    /// <summary>The output document a human gate produces for one answer, which its out-edge conditions read.</summary>
    /// <remarks>
    ///     Written in ONE place because two callers ask questions of it: the dispatcher, when it routes an answer that
    ///     has landed, and the API, when it tells the operator in advance whether a rejection has anywhere to go. A
    ///     second spelling of this shape would make those two disagree in exactly the case that matters.
    /// </remarks>
    public static string GateOutputJson(DevWorkflowDecisionKind decision) =>
        JsonSerializer.Serialize(new GateOutput
        {
            Status = DevWorkflowNodeOutputStatuses.Succeeded,
            Decision = decision.ToString()
        }, JsonOptions);

    /// <summary>Every answer a human gate SUCCEEDS on — the three that part company in the graph, not on the row.</summary>
    /// <remarks>
    ///     What a gate's out-edges route is exactly this set, so anything that reasons about where an answer can go
    ///     reads it from <see cref="TargetFor" /> rather than listing the three by hand.
    /// </remarks>
    public static IReadOnlyList<DevWorkflowDecisionKind> GateAnswers { get; } =
    [
        .. Enum.GetValues<DevWorkflowDecisionKind>().Where(static decision => TargetFor(decision) == DevWorkflowNodeRunStatus.Succeeded)
    ];

    /// <summary>Whether an out-edge of a human gate fires for one answer, asked of the gate's own output document.</summary>
    /// <remarks>
    ///     A document rather than a row, which is what a check made BEFORE the run has instead. Composed from this
    ///     class's own <see cref="GateOutputJson" /> and read by the pair <see cref="EdgeState" /> reads a landed row
    ///     with, so a definition-time rule about where an answer goes and the routing that takes it there cannot
    ///     differ. The parse rule refusing an apply a rejection could reach, the dispatcher and the API all ask.
    /// </remarks>
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

    /// <summary>Whether an inbound edge lets its target through, read off the run's node runs and the graph.</summary>
    /// <remarks>
    ///     <c>Pending</c> when the source has no row yet. A <c>Failed</c> or <c>Cancelled</c> source, and a
    ///     <c>Succeeded</c> one whose condition did not fire, kill the edge. A <c>Skipped</c> source is <c>Waived</c>
    ///     when every path back from it is itself Satisfied or Waived, and <c>Dead</c> otherwise — that recursion is
    ///     why this needs the graph and the whole dictionary rather than one source row.
    ///     See docs/wiki/25-dev-workflows.md ("Edges, joins and the pinned graph").
    /// </remarks>
    public static DevWorkflowEdgeState EdgeState(DevWorkflowGraphEdge edge,
        DevWorkflowGraph graph,
        IReadOnlyDictionary<string, DevWorkflowNodeRunSnapshot> nodeRunsByKey)
    {
        ArgumentNullException.ThrowIfNull(edge);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(nodeRunsByKey);

        return EdgeState(edge, graph, nodeRunsByKey, new Dictionary<string, bool>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
    }

    /// <summary>Which of a run's SKIPPED node runs the state machine WAIVES, asked for a whole run at once.</summary>
    /// <remarks>
    ///     Exactly the question <see cref="EdgeState" /> asks before answering <c>Waived</c> rather than <c>Dead</c>.
    ///     Exposed because the answer is NOT readable from a skipped row alone, and a read model guessing from status
    ///     gets it backwards on a failed node, a skip cascaded off it and a join beside a succeeded sibling — so the
    ///     API sends the verdict. ONE memo across every skipped row: per-skip walks make a wide fan-out quadratic.
    /// </remarks>
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

    /// <summary>Where a settled node run's out-edges went, judged edge by edge by <see cref="EdgeState" /> itself.</summary>
    /// <remarks>
    ///     Not by a second copy of the rule, so the recorded route and the routing that happened cannot differ, and the
    ///     gate verdicts agree with <see cref="GateEdgeFires" />. A source that settled anything but <c>Succeeded</c>
    ///     records an empty <c>satisfied</c> list; an edge leaving a materialization TEMPLATE is dropped, matching
    ///     <see cref="Admission" />. <paramref name="source" /> is laid over <paramref name="nodeRunsByKey" /> at its
    ///     own key, the stored row there still being the previous attempt's.
    /// </remarks>
    /// <param name="decision">The gate's answer, for a <c>HumanGate</c> source; recorded as the route's gate answer. The edge verdicts come from the output document either way.</param>
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

    /// <summary>A route as the <c>route_json</c> column stores it, dropping keys until the document fits.</summary>
    /// <remarks>
    ///     Dropping keys rather than clipping mid-string, because a truncated document that no longer parses would
    ///     take the whole record down with it. Anything dropped raises <see cref="DevWorkflowRoute.Truncated" />, so a
    ///     short list is never read as a complete one.
    /// </remarks>
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

    /// <summary>The answer a human gate settled on, read back off the document <see cref="GateOutputJson" /> wrote.</summary>
    /// <remarks>
    ///     The same pairing, so the writer and this reader cannot drift. Null for any other document, including a
    ///     structural node's.
    /// </remarks>
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

    /// <summary>Whether a Skipped node's own skip was a person's choice rather than something upstream refusing it.</summary>
    /// <remarks>
    ///     Asked under the node's OWN join policy: under <c>All</c> every dependency must read Satisfied or Waived,
    ///     under <c>Any</c> one Satisfied edge is enough, and a <c>Pending</c> dependency waives nothing either way.
    ///     <paramref name="waived" /> memoizes — a diamond re-walked down two paths turns a wide fan-out exponential —
    ///     and <paramref name="resolving" /> is the cycle guard, separate because it is popped again on the way out.
    ///     See docs/wiki/25-dev-workflows.md ("Edges, joins and the pinned graph").
    /// </remarks>
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

    /// <summary>The inbound edges that are DEPENDENCIES; one whose source is a materialization TEMPLATE is not.</summary>
    /// <remarks>
    ///     The template is the one node deliberately never instantiated, so its edge into the join can never be
    ///     satisfied and can never die either — it is the authored shape the clones' own edges stand in for. Reading
    ///     it as a dependency would leave every decomposing run waiting on a row that is never written.
    /// </remarks>
    private static IEnumerable<DevWorkflowGraphEdge> Dependencies(DevWorkflowGraph graph, string nodeKey) =>
        graph.InboundEdges(nodeKey).Where(edge => !graph.TemplateKeys.Contains(edge.From));

    /// <summary>Whether one edge's condition accepts one output document. The only place either question is answered.</summary>
    private static bool Fires(DevWorkflowGraphEdge edge, string? outputJson) =>
        DevWorkflowCondition.Evaluate(edge.Condition, ParseOutput(outputJson));

    /// <summary>Whether a <c>Pending</c> node run may be queued, must be skipped, or is still waiting.</summary>
    /// <remarks>
    ///     <c>All</c> over ZERO inbound edges is vacuously satisfied, which is how an entry node becomes eligible at
    ///     all. Otherwise one DEAD edge skips the join, one WAIVED edge does not, and something must have ARRIVED.
    ///     <c>Any</c> needs a Satisfied edge: <c>Waived</c> is not <c>Satisfied</c>.
    ///     See docs/wiki/25-dev-workflows.md ("Edges, joins and the pinned graph").
    /// </remarks>
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

        // Pending outranks Dead under BOTH policies: the answer must not depend on which branch landed first. Settling
        // a dead edge while a sibling still runs skips this node, and all after it, in front of unfinished work.
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

    /// <summary>Why a node run <see cref="Admission" /> answered <c>Skip</c> for is being skipped, in its own words.</summary>
    /// <remarks>
    ///     Names ONE dead dependency, preferring a branch that broke or was skipped over one a condition merely routed
    ///     past: a gate taking its other branch is the graph working, so naming it reads as the cause when a real
    ///     refusal sits beside it. With no dead dependency every branch was excused, and the excused row's own reason
    ///     is PROPAGATED rather than restated — the node someone skipped is not the one a downstream reader sees.
    ///     Sanitized by construction: fixed text, a node key, or another row's already-bounded <c>terminal_reason</c>.
    /// </remarks>
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

    /// <summary>The status a run should hold given its node runs and its CURRENT pinned graph.</summary>
    /// <remarks>
    ///     Recomputed from scratch each tick rather than accumulated, and denormalized so a reader can answer "what is
    ///     this run doing" without a join. The <c>-ing</c> statuses are intents a command wrote and only their drain
    ///     may clear them; terminal runs are left alone. <c>Completed</c> means at least one TERMINAL node succeeded,
    ///     so a run whose tail was skipped reads <c>Cancelled</c>, naming the ends it never reached. <c>Failed</c>
    ///     outranks both: a node that failed is the answer to why the run stopped.
    /// </remarks>
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

            // Blocked and WaitingForApproval mean "a human has to act" and outrank Pending: every node run exists from
            // the run's start, so Pending rows almost always remain, and reading them as Running calls a blocked run busy.
            return nodeRuns.Any(nodeRun => nodeRun.Status is DevWorkflowNodeRunStatus.WaitingForApproval or DevWorkflowNodeRunStatus.Blocked)
                ? DevWorkflowRunStatus.WaitingForApproval
                : DevWorkflowRunStatus.Running;
        }

        // A run with no node runs at all has not been materialized yet; it is still Pending, not complete.
        return nodeRuns.Count == 0 ? current : null;
    }

    /// <summary>What a run whose every node run is terminal amounts to, asked of the graph.</summary>
    /// <remarks>
    ///     Skipped and Cancelled node runs do not block an end — they simply are not one. <c>GateRejected</c> means
    ///     only that a human gate somewhere was refused and the run reached no end; it is NOT a causal proof, since a
    ///     rejection can route into a branch that runs perfectly well and a false condition further down can be what
    ///     killed the tail. The class narrows where to look, the reason names what was not reached, the log the order.
    /// </remarks>
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

        // GateRejected only when a gate was refused at all. The other way here is an operator Skip abandoning the tail,
        // which has no honest class: inventing one would put a token in the durable log meaning "a person chose this".
        var refused = nodeRuns.FirstOrDefault(static nodeRun => Refusal(nodeRun) is not null);
        return new DevWorkflowRunOutcome(DevWorkflowRunStatus.Cancelled,
            refused is null ? null : DevWorkflowFailureClasses.GateRejected,
            Reason(ends, refused));
    }

    /// <summary>Why a run that reached no end stopped, naming the ends it did not reach and what became of them.</summary>
    /// <remarks>
    ///     Sanitized by construction: every word is fixed text or a node key from the run's own definition graph, and
    ///     a materialization CLONE — the one key a model has a hand in — is never terminal, its leaf edge being
    ///     rewired to the join at creation. Bounded the same way: at most <see cref="MaxNamedNodes" /> keys are named
    ///     before the rest are counted, and the whole sentence is cut to <see cref="MaxTerminalReason" />.
    /// </remarks>
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

    /// <summary>Which answer a human gate was REFUSED with, or null when this node run is not a refused gate.</summary>
    /// <remarks>
    ///     <c>Reject</c> and <c>RequestChanges</c> both count: each is a person declining to let the run through, and
    ///     each leaves the same shape behind when nothing downstream of it reaches an end. Matched against
    ///     <see cref="GateOutputJson" />, which the dispatcher writes a gate's output from and nothing else writes at
    ///     all, so it cannot drift from what a gate stores the way a second spelling of the shape would.
    /// </remarks>
    private static DevWorkflowDecisionKind? Refusal(DevWorkflowNodeRunSnapshot nodeRun) =>
        nodeRun is { NodeType: DevWorkflowNodeType.HumanGate, Status: DevWorkflowNodeRunStatus.Succeeded }
        && GateRefusals.TryGetValue(nodeRun.OutputJson ?? string.Empty, out var decision)
            ? decision
            : null;

    /// <summary>Where a run's status and its node runs leave the work item.</summary>
    /// <remarks>
    ///     Written inside the same transaction as the run transition, never derived on read and never client-writable,
    ///     so the two can never disagree. ANY blocked node run blocks the work item, even while the run itself reads
    ///     <c>Running</c> because a sibling is still working: reading only the run status would leave a work item
    ///     Active with a node run nobody is coming to unblock, which the list page exists to surface.
    /// </remarks>
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

    /// <summary>Where a human's answer leaves the node run it answers.</summary>
    /// <remarks>
    ///     A gate answer always SUCCEEDS the gate, whichever of the three it is: the answer is the node's output and
    ///     routing on it is the edges' job, so a rejection reaches the run through an out-edge matching nothing rather
    ///     than through a node failure. Shared by the decision endpoint and the dispatcher so the two cannot disagree
    ///     about which answers a row in a given status can take; each keeps its own refusal for the rest.
    /// </remarks>
    public static DevWorkflowNodeRunStatus TargetFor(DevWorkflowDecisionKind decision) =>
        decision switch
        {
            DevWorkflowDecisionKind.Approve or DevWorkflowDecisionKind.Reject or DevWorkflowDecisionKind.RequestChanges => DevWorkflowNodeRunStatus.Succeeded,

            // Forced: a human retry is legal AT the cap and pays by RAISING the row's MaxAttempts by one
            // (TransitionDevWorkflowNodeRunCommand.WidenMaxAttempts). Beyond that only the run-wide budget bounds it.
            DevWorkflowDecisionKind.Retry => DevWorkflowNodeRunStatus.Pending,
            DevWorkflowDecisionKind.Skip => DevWorkflowNodeRunStatus.Skipped,
            _ => DevWorkflowNodeRunStatus.Failed
        };

    /// <summary>Whether a human may answer <paramref name="decision" /> on a node run in <paramref name="status" />.</summary>
    /// <remarks>
    ///     Nearly the transition table and deliberately not quite: that one answers whether the RUNTIME may move a
    ///     row, and one of its edges — an open gate going back to <c>Pending</c> — belongs to the fix loop's reset and
    ///     to nothing a person clicks. A <c>Retry</c> on an unanswered gate has no failed attempt to schedule again.
    ///     Shared with the surface that advertises the answers, so offered and accepted cannot drift.
    /// </remarks>
    public static bool IsDecidable(DevWorkflowNodeRunStatus status, DevWorkflowDecisionKind decision) =>
        status is DevWorkflowNodeRunStatus.WaitingForApproval or DevWorkflowNodeRunStatus.Blocked
        && (status != DevWorkflowNodeRunStatus.WaitingForApproval || decision != DevWorkflowDecisionKind.Retry)
        && IsLegal(status, TargetFor(decision));

    /// <summary>Where a node-run transition about to be written leaves the work item, for the move's own transaction.</summary>
    /// <remarks>
    ///     Needed because the run status often does not change when a node run does — a node blocking while a sibling
    ///     still works leaves the run <c>Running</c> — and the end-of-tick recomputation writes nothing when the run
    ///     status is unchanged. Without this the work item would keep reading <c>Active</c> with a node run nobody is
    ///     coming to unblock, which is the one thing the list page exists to surface.
    /// </remarks>
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

        // Deliberately NOT graph-aware: every TERMINAL answer here is provisional, since the same tick's Recompute asks
        // the graph and rewrites the work item, so a graph would buy only a parameter most callers cannot supply.
        var projectedRun = Settled(runStatus, projected)
                           ?? (projected.Any(static nodeRun => nodeRun.Status == DevWorkflowNodeRunStatus.Failed)
                               ? DevWorkflowRunStatus.Failed
                               : DevWorkflowRunStatus.Completed);
        return WorkItemStatusFor(projectedRun, projected);
    }

    /// <summary>The run transition table.</summary>
    /// <remarks>
    ///     Every terminal is reached through a drain (<c>Pausing</c>/<c>Cancelling</c>) or through the "nothing is live
    ///     any more" recomputation: a terminal written over a run with a live node run strands it under a run no tick
    ///     advances again, leaking the slots its executor holds. <c>Running → Cancelled</c> and
    ///     <c>WaitingForApproval → Cancelled</c> are that recomputation's own edges, safe because <see cref="Recompute" />
    ///     terminalizes only once every node run is; the gate-reject path keeps its drain, a sibling may be mid-build.
    /// </remarks>
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

    /// <summary>The node-run transition table.</summary>
    /// <remarks>
    ///     <c>Running → Pending</c> and <c>Queued → Pending</c> carry two meanings needing no distinct edge — a retry
    ///     after a retryable failure, and a collapse after the host restarted — and both re-derive the same way, so
    ///     the row is cleaned rather than annotated. The four edges OUT of a terminal status back to <c>Pending</c>
    ///     belong to the cross-node fix loop and to nothing else: downstream of a re-attempted node, a <c>Succeeded</c>
    ///     row left alone would be a stale result masquerading as a current one. Every executor settles forwards.
    /// </remarks>
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
            // NOT Skipped: a gate's three answers all SUCCEED it, and skipping an open gate would be walking past an
            // approval. The other moves are the drain's cancel and the fix loop's reset, which re-asks from the start.
            DevWorkflowNodeRunStatus.WaitingForApproval => to is DevWorkflowNodeRunStatus.Succeeded
                or DevWorkflowNodeRunStatus.Cancelled
                or DevWorkflowNodeRunStatus.Pending,

            // The intervention answers: Retry re-attempts, Skip routes around, Abandon gives up for good.
            DevWorkflowNodeRunStatus.Blocked => to is DevWorkflowNodeRunStatus.Pending
                or DevWorkflowNodeRunStatus.Skipped
                or DevWorkflowNodeRunStatus.Failed
                or DevWorkflowNodeRunStatus.Cancelled,

            // Succeeded has one move the other terminals do not: a decomposition's output is JUDGED after its own row
            // settled, so an unusable task package stands its author down for a human instead of completing or failing.
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

    private sealed record GateOutput
    {
        public required string Status { get; init; }

        public required string Decision { get; init; }
    }
}
