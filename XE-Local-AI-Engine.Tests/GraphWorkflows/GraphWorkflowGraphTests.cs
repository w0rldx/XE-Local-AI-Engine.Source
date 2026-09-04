namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Parsing a definition graph, and the rules that go with it. Most of them exist because breaking one produces a
///     run that HANGS rather than one that fails — the failure mode a durable runtime can least afford, since nothing
///     ever comes along to notice.
/// </summary>
public sealed class GraphWorkflowGraphTests
{
    private const string StartToEnd = """
                                      { "schemaVersion": 1,
                                        "nodes": [{ "key": "start", "kind": "Start" },
                                                  { "key": "done", "kind": "End", "config": { "outcome": "completed" } }],
                                        "edges": [{ "key": "e1", "from": "start", "to": "done" }] }
                                      """;

    [Test]
    public void Parse_ReadsNodesEdgesAndTheirDefaults()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.StartAgentEnd);

        AssertEx.Equal(expected: 3, graph.Nodes.Count);
        AssertEx.Equal(expected: 2, graph.Edges.Count);
        AssertEx.Equal("start", string.Join(", ", graph.EntryNodeKeys));

        var analyze = graph.Nodes["analyze"];
        AssertEx.Equal(GraphWorkflowNodeKind.Agent, analyze.Kind);
        AssertEx.Equal("Analyze", analyze.Label);
        AssertEx.Equal(expected: 3, analyze.MaxAttempts, "an agent node defaults to three attempts.");
        AssertEx.Equal(GraphWorkflowJoinPolicy.All, analyze.JoinPolicy);
        AssertEx.Null(analyze.TimeoutSeconds);

        AssertEx.Equal(expected: 1, graph.Nodes["done"].MaxAttempts, "everything that is not work gets one try.");
        AssertEx.Equal("done", string.Join(", ", graph.TerminalNodeKeys));
    }

    /// <summary>
    ///     The two dispatch pins, now inside the Agent node's config. The model name is taken as written — it is
    ///     matched against this node's catalog when the run starts, exactly as an agent definition's own pin is — while
    ///     the effort is checked here, because its vocabulary is closed and cannot go stale between authoring and a run.
    /// </summary>
    [Test]
    public void Parse_ReadsThePerNodeModelAndReasoningEffort()
    {
        var graph = GraphWorkflowGraph.Parse(Agent("""{ "instructions": "Go.", "model": " qwen3-30b ", "reasoningEffort": "High" }"""));
        var config = AssertEx.NotNull(graph.Nodes["agent"].Config as GraphWorkflowAgentConfig);

        AssertEx.Equal("qwen3-30b", config.Model);
        AssertEx.Equal("High", config.ReasoningEffort, "the effort travels to the provider as written; only its membership is checked.");
    }

    [Test]
    public void Parse_WithNeitherPin_LeavesBothToTheBoundAgent()
    {
        var config = AssertEx.NotNull(GraphWorkflowGraph.Parse(GraphWorkflowGraphs.StartAgentEnd).Nodes["analyze"].Config as GraphWorkflowAgentConfig);

        AssertEx.Null(config.Model);
        AssertEx.Null(config.ReasoningEffort);
    }

    /// <summary>
    ///     A cleared picker sends <c>""</c> and older stored documents already hold one, so a blank reads as "not
    ///     pinned" rather than as a graph the parser refuses.
    /// </summary>
    [Test]
    public void Parse_WithABlankPin_ReadsItAsUnpinnedRatherThanRefusingTheGraph()
    {
        var graph = GraphWorkflowGraph.Parse(Agent("""{ "instructions": "Go.", "model": "", "reasoningEffort": "  " }"""));
        var config = AssertEx.NotNull(graph.Nodes["agent"].Config as GraphWorkflowAgentConfig);

        AssertEx.Null(config.Model);
        AssertEx.Null(config.ReasoningEffort);
    }

    [Test]
    public void Parse_ReadsTheLabelFromTheNodeKeyWhenNoneIsGiven() =>
        AssertEx.Equal("done", GraphWorkflowGraph.Parse(GraphWorkflowGraphs.StartAgentEnd).Nodes["done"].Label);

    [Test]
    public void Descendants_FollowsOutEdgesAndExcludesTheNodeItself()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.ParallelJoinAll);

        AssertEx.Equal("done, left, merge, right, summary", string.Join(", ", graph.Descendants("fanout").Order(StringComparer.Ordinal)));
        AssertEx.Equal("done", string.Join(", ", graph.Descendants("merge")));
        AssertEx.Empty(graph.Descendants("done"));
    }

    [Test]
    [Arguments("not json at all", "not valid JSON")]
    [Arguments("""[]""", "must be a JSON object")]
    [Arguments("""{"schemaVersion":2,"nodes":[],"edges":[]}""", "schema version 1")]
    [Arguments("""{"edges":[]}""", "needs a 'nodes' array")]
    [Arguments("""{"nodes":[],"edges":[]}""", "at least one node")]
    [Arguments("""{"nodes":[{"kind":"Start"}],"edges":[]}""", "needs a non-empty 'key'")]

    // A closed vocabulary comes free: an unknown kind is refused with a message naming the eight members.
    [Arguments("""{"nodes":[{"key":"a","kind":"Sorcery"}],"edges":[]}""", "needs a 'kind'")]
    [Arguments("""{"nodes":[{"key":"a","kind":"Start"},{"key":"a","kind":"End"}],"edges":[]}""", "twice")]
    [Arguments("""{"nodes":[{"key":"a","kind":"Start"}],"edges":[{"key":"e1","from":"a","to":"ghost"}]}""", "does not declare")]
    [Arguments("""
               {"schemaVersion":1,"nodes":[{"key":"start","kind":"Start","maxAttempts":0},{"key":"done","kind":"End","config":{"outcome":"x"}}],
                "edges":[{"key":"e1","from":"start","to":"done"}]}
               """, "must be positive")]
    [Arguments("""
               {"schemaVersion":1,"nodes":[{"key":"start","kind":"Start","joinPolicy":"Maybe"},{"key":"done","kind":"End","config":{"outcome":"x"}}],
                "edges":[{"key":"e1","from":"start","to":"done"}]}
               """, "unknown 'joinPolicy'")]
    [Arguments("""
               {"schemaVersion":1,"nodes":[{"key":"start","kind":"Start","position":{"x":"12","y":0}},{"key":"done","kind":"End","config":{"outcome":"x"}}],
                "edges":[{"key":"e1","from":"start","to":"done"}]}
               """, "numeric 'x' and 'y'")]
    [Arguments("""
               {"schemaVersion":1,"nodes":[{"key":"start","kind":"Start"},
                                           {"key":"agent","kind":"Agent","config":{"instructions":"Go.","agentDefinitionId":"not-a-guid"}},
                                           {"key":"done","kind":"End","config":{"outcome":"x"}}],
                "edges":[{"key":"e1","from":"start","to":"agent"},{"key":"e2","from":"agent","to":"done"}]}
               """, "not a GUID")]
    [Arguments("""
               {"schemaVersion":1,"nodes":[{"key":"start","kind":"Start"},
                                           {"key":"agent","kind":"Agent","config":{"instructions":"Go.","reasoningEffort":"exhaustive"}},
                                           {"key":"done","kind":"End","config":{"outcome":"x"}}],
                "edges":[{"key":"e1","from":"start","to":"agent"},{"key":"e2","from":"agent","to":"done"}]}
               """, "unknown 'reasoningEffort'")]
    public void Parse_RejectsAGraphItCannotRoute(string json, string expectedMessage) =>
        AssertEx.Contains(AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse(json)).Message, expectedMessage);

    /// <summary>
    ///     A cycle below the Start node, so the cycle rule is the one that has to catch it. Cycles are forbidden
    ///     because a run that revisits a node has no bound on its own length.
    /// </summary>
    [Test]
    public void Parse_WithACycleBelowTheEntryNode_IsRejected()
    {
        const string Cyclic = """
                              { "schemaVersion": 1,
                                "nodes": [{ "key": "start", "kind": "Start" },
                                          { "key": "a", "kind": "Agent", "config": { "instructions": "a" } },
                                          { "key": "b", "kind": "Agent", "config": { "instructions": "b" } },
                                          { "key": "done", "kind": "End", "config": { "outcome": "x" } }],
                                "edges": [{ "key": "e1", "from": "start", "to": "a" }, { "key": "e2", "from": "a", "to": "b" },
                                          { "key": "e3", "from": "b", "to": "a" }, { "key": "e4", "from": "b", "to": "done" }] }
                              """;

        AssertEx.Contains(AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse(Cyclic)).Message, "cycle");
    }

    [Test]
    public void Parse_WithAnUnreachableNode_IsRejected()
    {
        const string Orphan = """
                              { "schemaVersion": 1,
                                "nodes": [{ "key": "start", "kind": "Start" },
                                          { "key": "done", "kind": "End", "config": { "outcome": "x" } },
                                          { "key": "orphan", "kind": "Agent", "config": { "instructions": "nobody calls me" } }],
                                "edges": [{ "key": "e1", "from": "start", "to": "done" }] }
                              """;

        AssertEx.Contains(AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse(Orphan)).Message, "unreachable from the Start node");
    }

    [Test]
    public void Parse_WithAnyJoinOnFewerThanTwoInboundEdges_IsRejected()
    {
        const string LonelyAny = """
                                 { "schemaVersion": 1,
                                   "nodes": [{ "key": "start", "kind": "Start" },
                                             { "key": "lonely", "kind": "Join", "joinPolicy": "Any", "config": {} },
                                             { "key": "done", "kind": "End", "config": { "outcome": "x" } }],
                                   "edges": [{ "key": "e1", "from": "start", "to": "lonely" }, { "key": "e2", "from": "lonely", "to": "done" }] }
                                 """;

        AssertEx.Contains(AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse(LonelyAny)).Message, "fewer than two inbound edges");
    }

    /// <summary>The same edge written out twice, key and all — refused where every duplicate key is.</summary>
    [Test]
    public void Parse_WithTheSameEdgeDeclaredTwice_IsRejected()
    {
        const string Twice = """
                             { "schemaVersion": 1,
                               "nodes": [{ "key": "start", "kind": "Start" }, { "key": "done", "kind": "End", "config": { "outcome": "x" } }],
                               "edges": [{ "key": "e1", "from": "start", "to": "done" }, { "key": "e1", "from": "start", "to": "done" }] }
                             """;

        AssertEx.Contains(AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse(Twice)).Message, "declares key 'e1' twice");
    }

    [Test]
    public void Parse_WithADuplicateEdgeKey_IsRejected()
    {
        const string Duplicate = """
                                 { "schemaVersion": 1,
                                   "nodes": [{ "key": "start", "kind": "Start" },
                                             { "key": "agent", "kind": "Agent", "config": { "instructions": "Go." } },
                                             { "key": "done", "kind": "End", "config": { "outcome": "x" } }],
                                   "edges": [{ "key": "e1", "from": "start", "to": "agent" }, { "key": "e1", "from": "agent", "to": "done" }] }
                                 """;

        AssertEx.Contains(AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse(Duplicate)).Message, "declares key 'e1' twice");
    }

    /// <summary>
    ///     One namespace for both, because an edge key colliding with a node key makes an element lookup ambiguous in
    ///     the editor for no gain at all.
    /// </summary>
    [Test]
    public void Parse_WithAnEdgeKeyMatchingANodeKey_IsRejected()
    {
        const string Colliding = """
                                 { "schemaVersion": 1,
                                   "nodes": [{ "key": "start", "kind": "Start" }, { "key": "done", "kind": "End", "config": { "outcome": "x" } }],
                                   "edges": [{ "key": "start", "from": "start", "to": "done" }] }
                                 """;

        AssertEx.Contains(AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse(Colliding)).Message, "declares key 'start' twice");
    }

    /// <summary>
    ///     A key reaches a plaintext database column, a canvas element id, a URL search param and a terminal-reason
    ///     sentence. One charset is what keeps all four honest at once.
    /// </summary>
    [Test]
    [Arguments("a b")]
    [Arguments("a.b")]
    [Arguments("a/b")]
    [Arguments("aaaaaaaaaabbbbbbbbbbccccccccccddddddddddeeeeeeeeeeffffffffffggggg")]
    [Arguments("")]
    public void Parse_RejectsAKeyOutsideTheCharset(string key)
    {
        var quoted = JsonSerializer.Serialize(key);
        var json = $$"""
                     { "schemaVersion": 1,
                       "nodes": [{ "key": {{quoted}}, "kind": "Start" }, { "key": "done", "kind": "End", "config": { "outcome": "x" } }],
                       "edges": [{ "key": "e1", "from": {{quoted}}, "to": "done" }] }
                     """;

        AssertEx.Contains(AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse(json)).Message, "key");
    }

    [Test]
    public void Parse_WithNoStartNode_IsRejected()
    {
        const string NoStart = """
                               { "schemaVersion": 1,
                                 "nodes": [{ "key": "agent", "kind": "Agent", "config": { "instructions": "Go." } },
                                           { "key": "done", "kind": "End", "config": { "outcome": "x" } }],
                                 "edges": [{ "key": "e1", "from": "agent", "to": "done" }] }
                               """;

        AssertEx.Contains(AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse(NoStart)).Message, "exactly one Start node, and this one has none");
    }

    [Test]
    public void Parse_WithTwoStartNodes_IsRejected()
    {
        const string TwoStarts = """
                                 { "schemaVersion": 1,
                                   "nodes": [{ "key": "one", "kind": "Start" }, { "key": "two", "kind": "Start" },
                                             { "key": "done", "kind": "End", "joinPolicy": "Any", "config": { "outcome": "x" } }],
                                   "edges": [{ "key": "e1", "from": "one", "to": "done" }, { "key": "e2", "from": "two", "to": "done" }] }
                                 """;

        AssertEx.Contains(AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse(TwoStarts)).Message, "this one has 2");
    }

    [Test]
    public void Parse_WithNoEndNode_IsRejected()
    {
        const string NoEnd = """
                             { "schemaVersion": 1,
                               "nodes": [{ "key": "start", "kind": "Start" }, { "key": "agent", "kind": "Agent", "config": { "instructions": "Go." } }],
                               "edges": [{ "key": "e1", "from": "start", "to": "agent" }] }
                             """;

        AssertEx.Contains(AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse(NoEnd)).Message, "at least one End node");
    }

    [Test]
    public void Parse_WithAnInboundEdgeIntoStart_IsRejected()
    {
        const string BeforeTheStart = """
                                      { "schemaVersion": 1,
                                        "nodes": [{ "key": "before", "kind": "Agent", "config": { "instructions": "Go." } },
                                                  { "key": "start", "kind": "Start" },
                                                  { "key": "done", "kind": "End", "config": { "outcome": "x" } }],
                                        "edges": [{ "key": "e1", "from": "before", "to": "start" }, { "key": "e2", "from": "start", "to": "done" }] }
                                      """;

        AssertEx.Contains(AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse(BeforeTheStart)).Message, "nothing can come before it");
    }

    [Test]
    public void Parse_WithAnOutboundEdgeFromEnd_IsRejected()
    {
        const string PastTheEnd = """
                                  { "schemaVersion": 1,
                                    "nodes": [{ "key": "start", "kind": "Start" },
                                              { "key": "first", "kind": "End", "config": { "outcome": "x" } },
                                              { "key": "second", "kind": "End", "config": { "outcome": "y" } }],
                                    "edges": [{ "key": "e1", "from": "start", "to": "first" }, { "key": "e2", "from": "first", "to": "second" }] }
                                  """;

        AssertEx.Contains(AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse(PastTheEnd)).Message, "A run stops there");
    }

    [Test]
    public void Parse_WithANonEndNodeThatNothingLeaves_IsRejected()
    {
        const string DeadEnd = """
                               { "schemaVersion": 1,
                                 "nodes": [{ "key": "start", "kind": "Start" },
                                           { "key": "stuck", "kind": "Agent", "config": { "instructions": "Go." } },
                                           { "key": "done", "kind": "End", "config": { "outcome": "x" } }],
                                 "edges": [{ "key": "e1", "from": "start", "to": "stuck" }, { "key": "e2", "from": "start", "to": "done" }] }
                               """;

        AssertEx.Contains(AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse(DeadEnd)).Message, "would stop without reaching an End");
    }

    [Test]
    public void Parse_WithAConditionNodeOnOneOutboundEdge_IsRejected()
    {
        const string OneWayOut = """
                                 { "schemaVersion": 1,
                                   "nodes": [{ "key": "start", "kind": "Start" },
                                             { "key": "check", "kind": "Condition", "config": { "path": "output.json.ok" } },
                                             { "key": "done", "kind": "End", "config": { "outcome": "x" } }],
                                   "edges": [{ "key": "e1", "from": "start", "to": "check" }, { "key": "e2", "from": "check", "to": "done" }] }
                                 """;

        AssertEx.Contains(AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse(OneWayOut)).Message, "A choice needs at least two");
    }

    [Test]
    public void Parse_WithAConditionNodeCarryingTwoUnconditionalEdges_IsRejected()
    {
        const string TwoDefaults = """
                                   { "schemaVersion": 1,
                                     "nodes": [{ "key": "start", "kind": "Start" },
                                               { "key": "check", "kind": "Condition", "config": { "path": "output.json.ok" } },
                                               { "key": "a", "kind": "End", "config": { "outcome": "x" } },
                                               { "key": "b", "kind": "End", "config": { "outcome": "y" } }],
                                     "edges": [{ "key": "e1", "from": "start", "to": "check" },
                                               { "key": "e2", "from": "check", "to": "a" }, { "key": "e3", "from": "check", "to": "b" }] }
                                   """;

        AssertEx.Contains(AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse(TwoDefaults)).Message, "At most one of them may be the default");
    }

    [Test]
    public void Parse_WithAConditionNodeAndOneUnconditionalDefault_IsAccepted()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.ConditionWithDefault);

        AssertEx.Equal(expected: 3, graph.OutboundEdges("check").Count);
        AssertEx.Equal(expected: 1, graph.OutboundEdges("check").Count(static edge => edge.Condition is null));
    }

    [Test]
    public void Parse_WithAPauseNodeNamingNoDecisions_IsRejected()
    {
        const string Unanswerable = """
                                    { "schemaVersion": 1,
                                      "nodes": [{ "key": "start", "kind": "Start" },
                                                { "key": "review", "kind": "Pause", "config": { "prompt": "Well?", "allowedDecisions": [] } },
                                                { "key": "done", "kind": "End", "config": { "outcome": "x" } }],
                                      "edges": [{ "key": "e1", "from": "start", "to": "review" }, { "key": "e2", "from": "review", "to": "done" }] }
                                    """;

        AssertEx.Contains(AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse(Unanswerable)).Message, "names no decisions");
    }

    /// <summary>
    ///     The pre-flight rule: an answer with nowhere to go strands the run, and a definition is the only place that
    ///     can be checked before the fact.
    /// </summary>
    [Test]
    public void Parse_WithAPauseDecisionNoOutEdgeAcceptsIsRejected()
    {
        const string ApproveOnly = """
                                   { "schemaVersion": 1,
                                     "nodes": [{ "key": "start", "kind": "Start" },
                                               { "key": "review", "kind": "Pause", "config": { "prompt": "Well?", "allowedDecisions": ["Approve", "Reject"] } },
                                               { "key": "shipped", "kind": "End", "config": { "outcome": "x" } }],
                                     "edges": [{ "key": "e1", "from": "start", "to": "review" },
                                               { "key": "e2", "from": "review", "to": "shipped",
                                                 "condition": { "path": "output.decision", "op": "eq", "value": "Approve" } }] }
                                   """;

        AssertEx.Contains(AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse(ApproveOnly)).Message,
            "offers the decision Reject and no edge out of it fires");
    }

    [Test]
    public void Parse_WithAnUnconditionalPauseOutEdge_AcceptsEveryDecision()
    {
        const string Unconditional = """
                                     { "schemaVersion": 1,
                                       "nodes": [{ "key": "start", "kind": "Start" },
                                                 { "key": "review", "kind": "Pause", "config": { "prompt": "Well?", "allowedDecisions": ["Approve", "Reject"] } },
                                                 { "key": "done", "kind": "End", "config": { "outcome": "x" } }],
                                       "edges": [{ "key": "e1", "from": "start", "to": "review" }, { "key": "e2", "from": "review", "to": "done" }] }
                                     """;

        var pause = AssertEx.NotNull(GraphWorkflowGraph.Parse(Unconditional).Nodes["review"].Config as GraphWorkflowPauseConfig);

        AssertEx.Equal("Approve, Reject", string.Join(", ", pause.AllowedDecisions));
        AssertEx.False(pause.RequireComment);
    }

    [Test]
    public void Parse_ReadsPositionAndTheRuntimeIgnoresIt()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.ToolNode);
        var position = AssertEx.NotNull(graph.Nodes["lookup"].Position);

        AssertEx.Equal(expected: 12d, position.X);
        AssertEx.Equal(expected: -4d, position.Y);
        AssertEx.Null(graph.Nodes["peek"].Position, "a node without one is laid out client-side when the definition is opened.");
    }

    [Test]
    public void Parse_WithAMalformedPosition_IsRejected()
    {
        const string Malformed = """
                                 { "schemaVersion": 1,
                                   "nodes": [{ "key": "start", "kind": "Start", "position": { "x": 0 } },
                                             { "key": "done", "kind": "End", "config": { "outcome": "x" } }],
                                   "edges": [{ "key": "e1", "from": "start", "to": "done" }] }
                                 """;

        AssertEx.Contains(AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse(Malformed)).Message, "numeric 'x' and 'y'");
    }

    [Test]
    public void Parse_WithNoPosition_IsAccepted() =>
        AssertEx.Empty(GraphWorkflowGraph.Parse(GraphWorkflowGraphs.StartAgentEnd).Nodes.Values.Where(static node => node.Position is not null));

    [Test]
    public void Parse_ReadsTheEdgeLabelAndSourceHandle()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.BranchOnJson);

        AssertEx.Equal("yes", graph.Edges.Single(static edge => edge.Key == "e3").Label);
        AssertEx.Equal("no", graph.Edges.Single(static edge => edge.Key == "e4").Label);
        AssertEx.Null(graph.Edges.Single(static edge => edge.Key == "e1").Label, "an edge that names no outcome reports none.");
        AssertEx.Equal(expected: 6, graph.Edges.Count, "a sourceHandle is authoring metadata the runtime reads past rather than refuses.");
    }

    /// <summary>
    ///     Parallel edges are how an author widens a branch, and the flat "one edge per pair" refusal is what they cost:
    ///     they are legal when their keys differ and at most one of them is unconditional.
    /// </summary>
    [Test]
    public void Parse_WithTwoConditionalEdgesOverTheSamePair_IsAccepted()
    {
        const string Widened = """
                               { "schemaVersion": 1,
                                 "nodes": [{ "key": "start", "kind": "Start" },
                                           { "key": "check", "kind": "Condition", "config": { "path": "output.json.ok" } },
                                           { "key": "done", "kind": "End", "joinPolicy": "Any", "config": { "outcome": "x" } }],
                                 "edges": [{ "key": "e1", "from": "start", "to": "check" },
                                           { "key": "e2", "from": "check", "to": "done", "condition": { "op": "eq", "value": true } },
                                           { "key": "e3", "from": "check", "to": "done", "condition": { "op": "eq", "value": false } }] }
                               """;

        AssertEx.Equal(expected: 2, GraphWorkflowGraph.Parse(Widened).OutboundEdges("check").Count);
    }

    [Test]
    public void Parse_WithTwoUnconditionalEdgesOverTheSamePair_IsRejected()
    {
        const string Repeated = """
                                { "schemaVersion": 1,
                                  "nodes": [{ "key": "start", "kind": "Start" },
                                            { "key": "agent", "kind": "Agent", "config": { "instructions": "Go." } },
                                            { "key": "done", "kind": "End", "joinPolicy": "Any", "config": { "outcome": "x" } }],
                                  "edges": [{ "key": "e1", "from": "start", "to": "agent" },
                                            { "key": "e2", "from": "agent", "to": "done" }, { "key": "e3", "from": "agent", "to": "done" }] }
                                """;

        AssertEx.Contains(AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse(Repeated)).Message, "second unconditional edge");
    }

    /// <summary>
    ///     What lets an editor prefill one path on the Condition node and write only <c>{op, value}</c> per branch.
    /// </summary>
    [Test]
    public void Parse_TakesAConditionEdgesPathFromTheNodeWhenTheEdgeOmitsIt()
    {
        var edge = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.BranchOnJson).Edges.Single(static candidate => candidate.Key == "e3");

        AssertEx.Equal("output.json.requiresReview", AssertEx.NotNull(edge.Condition).Path);
    }

    [Test]
    public void Parse_WithAConditionalEdgeAndNoPathOnEitherEdgeOrNode_IsRejected()
    {
        const string Pathless = """
                                { "schemaVersion": 1,
                                  "nodes": [{ "key": "start", "kind": "Start" },
                                            { "key": "check", "kind": "Condition", "config": {} },
                                            { "key": "a", "kind": "End", "config": { "outcome": "x" } },
                                            { "key": "b", "kind": "End", "config": { "outcome": "y" } }],
                                  "edges": [{ "key": "e1", "from": "start", "to": "check" },
                                            { "key": "e2", "from": "check", "to": "a", "condition": { "op": "eq", "value": true } },
                                            { "key": "e3", "from": "check", "to": "b", "condition": { "op": "ne", "value": true } }] }
                                """;

        AssertEx.Contains(AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse(Pathless)).Message, "non-empty 'path'");
    }

    /// <summary>
    ///     The hybrid: per-node and per-edge failures accumulate keyed by the element they belong to, so an author
    ///     fixing a canvas gets every complaint at once — while a structural failure throws alone, because there is
    ///     nothing useful to say about the rest of a graph nobody can walk.
    /// </summary>
    [Test]
    public void Validate_AccumulatesEveryNodeAndEdgeFailureAndThrowsFirstOnAStructuralOne()
    {
        const string ThreeThingsWrong = """
                                        { "schemaVersion": 1,
                                          "nodes": [{ "key": "start", "kind": "Start" },
                                                    { "key": "agent", "kind": "Agent", "config": { "instructions": "Go.", "reasoningEffort": "exhaustive" } },
                                                    { "key": "check", "kind": "Condition", "joinPolicy": "Maybe", "config": { "path": "output.json.ok" } },
                                                    { "key": "done", "kind": "End", "config": { "outcome": "x" } }],
                                          "edges": [{ "key": "e1", "from": "start", "to": "agent" }, { "key": "e2", "from": "agent", "to": "check" },
                                                    { "key": "e3", "from": "check", "to": "done", "condition": { "op": "gt", "value": true } }] }
                                        """;

        var accumulated = AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse(ThreeThingsWrong)).Result;

        AssertEx.Equal(expected: 4, accumulated.Errors.Count);
        AssertEx.Equal("agent, check, check, e3", string.Join(", ", accumulated.Errors.Select(static error => error.Key).Order(StringComparer.Ordinal)));

        const string Cyclic = """
                              { "schemaVersion": 1,
                                "nodes": [{ "key": "start", "kind": "Start" },
                                          { "key": "a", "kind": "Agent", "config": { "instructions": "a", "reasoningEffort": "exhaustive" } },
                                          { "key": "b", "kind": "Agent", "config": { "instructions": "b", "reasoningEffort": "wild" } },
                                          { "key": "done", "kind": "End", "config": { "outcome": "x" } }],
                                "edges": [{ "key": "e1", "from": "start", "to": "a" }, { "key": "e2", "from": "a", "to": "b" },
                                          { "key": "e3", "from": "b", "to": "a" }, { "key": "e4", "from": "b", "to": "done" }] }
                              """;

        var structural = AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse(Cyclic)).Result;

        AssertEx.Equal(expected: 1, structural.Errors.Count, "a graph nobody can walk earns one complaint, not a list.");
        AssertEx.Null(structural.Errors[0].Key);
        AssertEx.Contains(structural.Errors[0].Message, "cycle");
    }

    /// <summary>
    ///     An edge naming an undeclared endpoint belongs in the throw-first set: the inbound and outbound indexes are
    ///     built on the endpoints, so accumulating past one walks an adjacency that is already wrong.
    /// </summary>
    [Test]
    public void Validate_WithAnUndeclaredEdgeEndpointAndTwoConfigErrors_ThrowsTheEndpointFailureAlone()
    {
        const string Ghost = """
                             { "schemaVersion": 1,
                               "nodes": [{ "key": "start", "kind": "Start" },
                                         { "key": "a", "kind": "Agent", "config": { "instructions": "a", "reasoningEffort": "exhaustive" } },
                                         { "key": "b", "kind": "Agent", "config": { "instructions": "b", "reasoningEffort": "wild" } },
                                         { "key": "done", "kind": "End", "config": { "outcome": "x" } }],
                               "edges": [{ "key": "e1", "from": "start", "to": "a" }, { "key": "e2", "from": "a", "to": "b" },
                                         { "key": "e3", "from": "b", "to": "ghost" }] }
                             """;

        var refusal = AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse(Ghost)).Result;

        AssertEx.Equal(expected: 1, refusal.Errors.Count, "the structural failure wins and no config error is collected beside it.");
        AssertEx.Null(refusal.Errors[0].Key);
        AssertEx.Contains(refusal.Errors[0].Message, "does not declare");
    }

    [Test]
    public void Parse_ReadsEachKindsConfigAndRefusesAConfigMemberOnTheWrongKind()
    {
        var tools = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.ToolNode);
        var lookup = AssertEx.NotNull(tools.Nodes["lookup"].Config as GraphWorkflowToolConfig);
        AssertEx.Equal("read_file", lookup.ToolName);
        AssertEx.Equal("output.json.path", lookup.ArgumentBindings["path"]);
        AssertEx.True(lookup.Arguments is not null, "a Tool node's literal arguments survive the parse.");

        var branching = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.BranchOnJson);
        AssertEx.Equal("output.json.requiresReview", AssertEx.NotNull(branching.Nodes["check"].Config as GraphWorkflowConditionConfig).Path);
        AssertEx.True(AssertEx.NotNull(branching.Nodes["analyze"].Config as GraphWorkflowAgentConfig).IncludeUpstreamOutputs,
            "an agent reads what came before it unless the definition says otherwise.");
        AssertEx.Equal("completed", AssertEx.NotNull(branching.Nodes["done"].Config as GraphWorkflowEndConfig).Outcome);
        AssertEx.True(branching.Nodes["start"].Config is GraphWorkflowStartConfig);

        var misplaced = AssertEx.Throws<GraphWorkflowValidationException>(() =>
            GraphWorkflowGraph.Parse(Agent("""{ "instructions": "Go.", "toolName": "read_file" }""")));

        AssertEx.Contains(misplaced.Message, "which no Agent node reads");
    }

    /// <summary>
    ///     The output document carries an agent's parsed answer as <c>output.json</c> and a condition reads a property
    ///     off it, so a schema that is not an object schema is a branch nothing could ever route on.
    /// </summary>
    [Test]
    public void Parse_WithAResponseJsonSchemaThatIsNotAnObjectSchema_IsRejected()
    {
        var refusal = AssertEx.Throws<GraphWorkflowValidationException>(() =>
            GraphWorkflowGraph.Parse(Agent("""{ "instructions": "Go.", "responseJsonSchema": { "type": "string" } }""")));

        AssertEx.Contains(refusal.Message, "must be an object schema");
    }

    [Test]
    public void Parse_ReadsTheReasoningEffortFromTheAgentConfigAndRefusesAnUnknownOne()
    {
        var config = AssertEx.NotNull(GraphWorkflowGraph.Parse(Agent("""{ "instructions": "Go.", "reasoningEffort": "medium" }"""))
                                                        .Nodes["agent"]
                                                        .Config as GraphWorkflowAgentConfig);
        AssertEx.Equal("medium", config.ReasoningEffort);

        var refusal = AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse(Agent("""{ "instructions": "Go.", "reasoningEffort": "xhigh" }""")));

        AssertEx.Contains(refusal.Message, "unknown 'reasoningEffort'");
    }

    [Test]
    public void ToolNodeNames_ListsEveryToolNodesToolName()
    {
        AssertEx.Equal("read_file, list_files", string.Join(", ", GraphWorkflowGraph.Parse(GraphWorkflowGraphs.ToolNode).ToolNodeNames));
        AssertEx.Empty(GraphWorkflowGraph.Parse(GraphWorkflowGraphs.StartAgentEnd).ToolNodeNames);
    }

    /// <summary>
    ///     The invariant the state machine leans on: it reads completion off <c>TerminalNodeKeys</c>, and the End rules
    ///     are what make that the same set as the End nodes.
    /// </summary>
    [Test]
    public void TerminalNodeKeys_AreExactlyTheEndNodes()
    {
        foreach (var json in new[] { GraphWorkflowGraphs.TwoEnds, GraphWorkflowGraphs.StartAgentEnd, GraphWorkflowGraphs.ParallelJoinAll, StartToEnd })
        {
            var graph = GraphWorkflowGraph.Parse(json);
            var ends = graph.Nodes.Values.Where(static node => node.Kind == GraphWorkflowNodeKind.End).Select(static node => node.NodeKey);

            AssertEx.Equal(string.Join(", ", ends.Order(StringComparer.Ordinal)),
                string.Join(", ", graph.TerminalNodeKeys.Order(StringComparer.Ordinal)));
        }
    }

    /// <summary>A Start, one Agent carrying <paramref name="config" />, and an End — the smallest graph a config fits in.</summary>
    private static string Agent(string config) =>
        $$"""
          { "schemaVersion": 1,
            "nodes": [{ "key": "start", "kind": "Start" },
                      { "key": "agent", "kind": "Agent", "config": {{config}} },
                      { "key": "done", "kind": "End", "config": { "outcome": "completed" } }],
            "edges": [{ "key": "e1", "from": "start", "to": "agent" }, { "key": "e2", "from": "agent", "to": "done" }] }
          """;
}
