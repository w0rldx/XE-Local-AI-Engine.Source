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
[Category(TestCategories.Unit)]
public sealed class GraphWorkflowGraphTests
{
    /// <summary>The charset refusal in full, so a row that fails some OTHER way cannot pass by containing "key".</summary>
    private const string Charset = "is not a legal key. Keys are 1 to 64 characters of letters, digits, '_' and '-'.";

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

    // Enum.TryParse accepts a NUMERIC token, so without a by-name rule "3" would parse into a kind no member has and
    // reach the per-kind config table as a missing key — a 500 for a document an author wrote.
    [Arguments("""{"nodes":[{"key":"a","kind":"3"}],"edges":[]}""", "needs a 'kind'")]
    [Arguments("""{"nodes":[{"key":"a","kind":"-1"}],"edges":[]}""", "needs a 'kind'")]
    [Arguments("""
               {"schemaVersion":1,"nodes":[{"key":"start","kind":"Start","joinPolicy":"1"},{"key":"done","kind":"End","config":{"outcome":"x"}}],
                "edges":[{"key":"e1","from":"start","to":"done"}]}
               """, "unknown 'joinPolicy'")]
    [Arguments("""
               {"schemaVersion":1,"nodes":[{"key":"start","kind":"Start"},
                                           {"key":"review","kind":"Pause","config":{"prompt":"Well?","allowedDecisions":["1"]}},
                                           {"key":"done","kind":"End","config":{"outcome":"x"}}],
                "edges":[{"key":"e1","from":"start","to":"review"},{"key":"e2","from":"review","to":"done"}]}
               """, "does not offer")]

    // Dot paths only: no wildcards, no indexes, no functions. Saved, an index or a call is a property name literally
    // spelled that way, which no output document carries — a dead edge rather than a refusal anyone can read.
    [Arguments("""
               {"schemaVersion":1,"nodes":[{"key":"start","kind":"Start"},{"key":"done","kind":"End","config":{"outcome":"x","resultPath":"items[0].name"}}],
                "edges":[{"key":"e1","from":"start","to":"done"}]}
               """, "not a dot path")]
    [Arguments("""
               {"schemaVersion":1,"nodes":[{"key":"start","kind":"Start"},{"key":"done","kind":"End","config":{"outcome":"x","resultPath":"*.value"}}],
                "edges":[{"key":"e1","from":"start","to":"done"}]}
               """, "not a dot path")]
    [Arguments("""
               {"schemaVersion":1,"nodes":[{"key":"start","kind":"Start"},{"key":"done","kind":"End","config":{"outcome":"x","resultPath":"foo()"}}],
                "edges":[{"key":"e1","from":"start","to":"done"}]}
               """, "not a dot path")]
    [Arguments("""
               {"schemaVersion":1,"nodes":[{"key":"start","kind":"Start"},{"key":"done","kind":"End","config":{"outcome":"x","resultPath":"a..b"}}],
                "edges":[{"key":"e1","from":"start","to":"done"}]}
               """, "not a dot path")]
    [Arguments("""
               {"schemaVersion":1,"nodes":[{"key":"start","kind":"Start"},{"key":"done","kind":"End","config":{"outcome":"x","resultPath":"a. b"}}],
                "edges":[{"key":"e1","from":"start","to":"done"}]}
               """, "not a dot path")]
    [Arguments("""
               {"schemaVersion":1,"nodes":[{"key":"start","kind":"Start"},
                                           {"key":"lookup","kind":"Tool","config":{"toolName":"read_file","argumentBindings":{"path":"items[0].name"}}},
                                           {"key":"done","kind":"End","config":{"outcome":"x"}}],
                "edges":[{"key":"e1","from":"start","to":"lookup"},{"key":"e2","from":"lookup","to":"done"}]}
               """, "not a dot path")]
    [Arguments("""
               {"schemaVersion":1,"nodes":[{"key":"start","kind":"Start"},
                                           {"key":"check","kind":"Condition","config":{"path":"output.json.*"}},
                                           {"key":"done","kind":"End","config":{"outcome":"x"}}],
                "edges":[{"key":"e1","from":"start","to":"check"},{"key":"e2","from":"check","to":"done"}]}
               """, "not a dot path")]
    public void Parse_RejectsAGraphItCannotRoute(string json, string expectedMessage) =>
        AssertEx.Contains(AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse(json)).Message, expectedMessage);

    /// <summary>
    ///     The refusal's own sentence names the rule; only the reader's exception says where in the document parsing
    ///     stopped, so the wrap keeps it as the inner exception rather than collapsing the chain to one sentence.
    /// </summary>
    [Test]
    public void Parse_WhenTheDocumentIsNotJson_KeepsTheReaderFailureAsTheInnerException()
    {
        var refusal = AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse("not json at all"));

        var inner = AssertEx.NotNull(refusal.InnerException, "The JSON reader's own failure must survive the wrap.");
        AssertEx.True(inner is JsonException, $"Expected the inner exception to be a JsonException, got {inner.GetType().Name}.");
    }

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

    /// <summary>
    ///     The refusal names every node on the cycle, in the order the walk took them, starting and ending at the node
    ///     it came back to. Naming only that one node leaves an author with a canvas of edges and a single key, and the
    ///     back edge is the one thing they have to find.
    /// </summary>
    [Test]
    public void Parse_WithACycle_NamesEveryNodeOnIt()
    {
        const string ThreeNodeLoop = """
                                     { "schemaVersion": 1,
                                       "nodes": [{ "key": "start", "kind": "Start" },
                                                 { "key": "a", "kind": "Agent", "config": { "instructions": "a" } },
                                                 { "key": "b", "kind": "Agent", "config": { "instructions": "b" } },
                                                 { "key": "c", "kind": "Agent", "config": { "instructions": "c" } },
                                                 { "key": "done", "kind": "End", "config": { "outcome": "x" } }],
                                       "edges": [{ "key": "e1", "from": "start", "to": "a" }, { "key": "e2", "from": "a", "to": "b" },
                                                 { "key": "e3", "from": "b", "to": "c" }, { "key": "e4", "from": "c", "to": "a" },
                                                 { "key": "e5", "from": "c", "to": "done" }] }
                                     """;

        AssertEx.Contains(AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse(ThreeNodeLoop)).Message,
            "cycle through node 'a': a -> b -> c -> a");
    }

    /// <summary>
    ///     A stored graph is re-parsed by the run engine and the dispatcher without a cap — it was capped when it was
    ///     saved, and a cap lowered since would make a live run unroutable rather than merely unsaveable. So the walk
    ///     itself must not be bounded by a stack: this chain is far deeper than a frame-per-node walk could carry, and
    ///     reaching the assertion is the evidence.
    /// </summary>
    [Test]
    public void Parse_WithAChainDeeperThanTheStackWouldCarry_IsWalkedWithoutRecursion() =>
        AssertEx.Equal(expected: 10_000, GraphWorkflowGraph.Parse(GraphWorkflowGraphs.Chain(nodeCount: 10_000)).Nodes.Count);

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
    /// <remarks>
    ///     Each row names the sentence it expects. The empty key never reaches the charset check at all — it is refused
    ///     one step earlier, as a missing required string — and asserting the same words for both would hide the day a
    ///     charset failure started reading like an absence.
    /// </remarks>
    [Test]
    [Arguments("a b", Charset)]
    [Arguments("a.b", Charset)]
    [Arguments("a/b", Charset)]
    [Arguments("aaaaaaaaaabbbbbbbbbbccccccccccddddddddddeeeeeeeeeeffffffffffggggg", Charset)]
    [Arguments("", "needs a non-empty 'key'")]
    public void Parse_RejectsAKeyOutsideTheCharset(string key, string expected)
    {
        var quoted = JsonSerializer.Serialize(key);
        var json = $$"""
                     { "schemaVersion": 1,
                       "nodes": [{ "key": {{quoted}}, "kind": "Start" }, { "key": "done", "kind": "End", "config": { "outcome": "x" } }],
                       "edges": [{ "key": "e1", "from": {{quoted}}, "to": "done" }] }
                     """;

        AssertEx.Contains(AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse(json)).Message, expected);
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
        foreach (var json in new[]
                 {
                     GraphWorkflowGraphs.TwoEnds,
                     GraphWorkflowGraphs.StartAgentEnd,
                     GraphWorkflowGraphs.ParallelJoinAll,
                     StartToEnd
                 })
        {
            var graph = GraphWorkflowGraph.Parse(json);
            var ends = graph.Nodes.Values.Where(static node => node.Kind == GraphWorkflowNodeKind.End).Select(static node => node.NodeKey);

            AssertEx.Equal(string.Join(", ", ends.Order(StringComparer.Ordinal)),
                string.Join(", ", graph.TerminalNodeKeys.Order(StringComparer.Ordinal)));
        }
    }

    /// <summary>
    ///     The pause-context warning: a node reached ONLY through a Pause is handed the decision document rather than
    ///     the content, and the parser says so — without refusing, because the graph routes perfectly well and the
    ///     author may have meant it.
    /// </summary>
    [Test]
    public void Warnings_OnANodeReachedOnlyThroughAPause_NameTheNodeAndItsNearestNonPauseAncestor()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.PauseBetweenTwoAgents);

        AssertEx.Equal(expected: 1, graph.Warnings.Count, "one warning per node that loses the content, however many pauses reach it.");
        var warning = graph.Warnings[0];
        AssertEx.Equal("summarize", warning.Key, "the warning belongs to the node that loses the content, so the editor draws it there.");
        AssertEx.Contains(warning.Message, "'review'", message: "the warning names the pause it is about.");
        AssertEx.Contains(warning.Message,
            "Add an edge from 'analyze' to 'summarize'",
            message: $"the nearest non-Pause ancestor is unique here, so the advice names it: {warning.Message}");
    }

    /// <summary>The cure, asserted as the absence it is: the context edge gives the node a non-Pause predecessor.</summary>
    [Test]
    public void Warnings_WithTheContextEdgeAroundThePause_AreEmpty()
    {
        AssertEx.Empty(GraphWorkflowGraph.Parse(GraphWorkflowGraphs.PauseBetweenTwoAgentsWithContextEdge).Warnings);
    }

    /// <summary>
    ///     An <c>End</c> after a pause loses the result the same way a work node does — its input document IS the
    ///     decision — so both branches of a two-answer pause are warned about.
    /// </summary>
    [Test]
    public void Warnings_CoverAnEndReachedOnlyThroughAPause()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.PauseTwoDecisions);

        AssertEx.Equal("rejected, shipped", string.Join(", ", graph.Warnings.Select(static warning => warning.Key).Order(StringComparer.Ordinal)));
        AssertEx.Contains(AssertEx.NotNull(graph.Warnings.FirstOrDefault(static warning => warning.Key == "shipped")).Message,
            "Add an edge from 'start' to 'shipped'",
            message: "Start is a fine ancestor: its output is the run input, which is what the node behind the pause would have read.");
    }

    /// <summary>
    ///     A <c>Condition</c> ancestor is never named, unique or not: the edge the advice would ask for is that node's
    ///     second unconditional out-edge, which the validator refuses — so following the advice would turn a warning
    ///     into an error.
    /// </summary>
    [Test]
    public void Warnings_WithAConditionAsTheOnlyAncestor_DoNotNameIt()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.PauseBehindACondition);

        AssertEx.Equal(expected: 1, graph.Warnings.Count);
        AssertEx.Equal("shipped", graph.Warnings[0].Key);
        AssertEx.Contains(graph.Warnings[0].Message, "Add an edge from a node before the pause", message: "the generic sentence, because the ancestor is a Condition.");
        AssertEx.False(graph.Warnings[0].Message.Contains("'check'", StringComparison.Ordinal), "naming the Condition would advise an edge the validator refuses.");
    }

    /// <summary>
    ///     An <c>Any</c> successor over pauses is exempt whatever its KIND, and not as a matter of taste: the advised
    ///     edge is unconditional, so it would admit the node on its own and run the branch every rejection was meant to
    ///     stop. A join policy belongs to every node, so testing the kind here would have missed the Agent and the End.
    /// </summary>
    [Test]
    [Arguments("Join")]
    [Arguments("Agent")]
    [Arguments("End")]
    public void Warnings_ForAnAnySuccessorFedOnlyByPauses_AreEmpty(string kind)
    {
        AssertEx.Empty(GraphWorkflowGraph.Parse(GraphWorkflowGraphs.PauseFanInTo(kind, "Any")).Warnings,
            $"advising a content edge into an Any {kind} would admit it with no approval at all.");
    }

    /// <summary>The same graphs under <c>All</c> wait for both approvals, so the advice is safe and stands.</summary>
    [Test]
    [Arguments("Join")]
    [Arguments("Agent")]
    [Arguments("End")]
    public void Warnings_ForAnAllSuccessorFedOnlyByPauses_NameTheAncestor(string kind)
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.PauseFanInTo(kind, "All"));

        AssertEx.Equal(expected: 1, graph.Warnings.Count);
        AssertEx.Equal("merge", graph.Warnings[0].Key);
        AssertEx.Contains(graph.Warnings[0].Message,
            "Add an edge from 'analyze' to 'merge'",
            message: $"an All {kind} still waits for the approvals, so the content edge is safe to advise.");
    }

    /// <summary>
    ///     Two nearest non-Pause ancestors means the pause is fed by mutually exclusive branches. One of them is always
    ///     dead, so naming either would advise an edge that leaves the successor waiting on a branch never taken.
    /// </summary>
    [Test]
    public void Warnings_WithTwoExclusiveAncestors_GiveOnlyTheGenericAdvice()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.PauseBehindTwoExclusiveBranches);

        AssertEx.Equal(expected: 1, graph.Warnings.Count);
        AssertEx.Equal("done", graph.Warnings[0].Key);
        AssertEx.Contains(graph.Warnings[0].Message, "Add an edge from a node before the pause");
        AssertEx.False(graph.Warnings[0].Message.Contains("'fast'", StringComparison.Ordinal) || graph.Warnings[0].Message.Contains("'slow'", StringComparison.Ordinal),
            "either branch may be the dead one, so neither can be advised.");
    }

    /// <summary>A graph with nothing to say about it says nothing — the warning list is not a place things accumulate.</summary>
    [Test]
    public void Warnings_OnAGraphWithNoPauseAndNoResponseSchema_AreEmpty()
    {
        AssertEx.Empty(GraphWorkflowGraph.Parse(GraphWorkflowGraphs.StartAgentEnd).Warnings);
    }

    /// <summary>
    ///     The response-schema warning, whole. The schema does not reach the grammar as written: the adapter behind
    ///     <c>ChatResponseFormat.ForJsonSchema</c> relocates the value keywords into a description, marks every declared
    ///     property required and closes the object — so an author who writes <c>maxLength</c> or leaves a property
    ///     optional is believing something no run will hold them to. Asserted as the WHOLE sentence, because the three
    ///     clauses have to compose into one readable line and a substring check would not notice if they stopped.
    /// </summary>
    [Test]
    public void Warnings_OnAResponseSchemaWithADroppedKeywordAndAnOptionalProperty_NameBoth()
    {
        var graph = GraphWorkflowGraph.Parse(Agent("""
                                                   { "instructions": "Judge it.",
                                                     "responseJsonSchema": { "type": "object",
                                                                             "properties": { "summary": { "type": "string", "maxLength": 3 },
                                                                                             "notes": { "type": "string" } } } }
                                                   """));

        AssertEx.Equal(expected: 1, graph.Warnings.Count, "one warning per node, however many things the schema got wrong.");
        AssertEx.Equal("agent", graph.Warnings[0].Key, "keyed on the node, so the editor draws it on the card the author has to open.");
        AssertEx.Equal("Node 'agent' declares a response schema the runtime rewrites before it becomes a grammar: "
                       + "it drops 'maxLength' rather than enforcing it, requires every declared property ('summary', 'notes' are optional here) "
                       + "and forbids additional properties.",
            graph.Warnings[0].Message);
    }

    /// <summary>
    ///     SILENCE about <c>additionalProperties</c> is the third clause, not an explicit <c>true</c>. The transform
    ///     injects <c>additionalProperties: false</c> ONLY where the key is missing, so the JSON Schema default — an
    ///     open object — is the one the runtime quietly closes.
    /// </summary>
    [Test]
    public void Warnings_OnAResponseSchemaThatLeavesTheObjectOpenByDefault_SayTheRuntimeClosesIt()
    {
        var graph = GraphWorkflowGraph.Parse(Agent("""
                                                   { "instructions": "Judge it.",
                                                     "responseJsonSchema": { "type": "object",
                                                                             "properties": { "summary": { "type": "string" } },
                                                                             "required": ["summary"] } }
                                                   """));

        AssertEx.Equal(expected: 1, graph.Warnings.Count);
        AssertEx.Equal("Node 'agent' declares a response schema the runtime rewrites before it becomes a grammar: it forbids additional properties.",
            graph.Warnings[0].Message);
    }

    /// <summary>
    ///     The mirror, and the reason the clause cannot be written the other way round: a schema that SAYS
    ///     <c>additionalProperties: true</c> keeps it — the injection guard skips any object that already declares the
    ///     key — so the object really is open at the grammar and there is nothing to warn about.
    /// </summary>
    [Test]
    public void Warnings_OnAResponseSchemaThatOpensTheObjectExplicitly_AreEmpty()
    {
        AssertEx.Empty(GraphWorkflowGraph.Parse(Agent("""
                                                      { "instructions": "Judge it.",
                                                        "responseJsonSchema": { "type": "object",
                                                                                "properties": { "summary": { "type": "string" } },
                                                                                "required": ["summary"],
                                                                                "additionalProperties": true } }
                                                      """)).Warnings,
            "an explicit 'true' survives the transform, so the author's belief about it holds.");
    }

    /// <summary>
    ///     The transform recurses through <c>properties</c>, <c>items</c>, <c>additionalProperties</c>, <c>not</c> and
    ///     the composition keywords — and through NOTHING else. A constraint parked in a <c>$defs</c> pool is therefore
    ///     left exactly as written, and warning about it would be teaching the author a rule that is not true.
    /// </summary>
    [Test]
    public void Warnings_OnAConstraintInADefinitionPool_AreEmpty()
    {
        AssertEx.Empty(GraphWorkflowGraph.Parse(Agent("""
                                                      { "instructions": "Judge it.",
                                                        "responseJsonSchema": { "type": "object",
                                                                                "$defs": { "tag": { "type": "object",
                                                                                                    "properties": { "name": { "type": "string", "minLength": 1 } } } },
                                                                                "properties": { "verdict": { "type": "string" } },
                                                                                "required": ["verdict"],
                                                                                "additionalProperties": false } }
                                                      """)).Warnings,
            "the transform never descends into $defs, so nothing in it is relocated or made required.");
    }

    /// <summary>
    ///     Structure survives the transform, so a schema built out of nothing but structure is warned about not at all —
    ///     which is also the cure the warning is steering an author towards.
    /// </summary>
    [Test]
    public void Warnings_OnAResponseSchemaTheRuntimeEnforcesAsWritten_AreEmpty()
    {
        AssertEx.Empty(GraphWorkflowGraph.Parse(Agent("""
                                                      { "instructions": "Judge it.",
                                                        "responseJsonSchema": { "type": "object",
                                                                                "properties": { "verdict": { "type": "string", "enum": ["pass", "fail"] },
                                                                                                "score": { "type": "integer" } },
                                                                                "required": ["verdict", "score"],
                                                                                "additionalProperties": false } }
                                                      """)).Warnings,
            "enum, type, required and the object shape all reach the grammar, so there is nothing to disbelieve.");
    }

    /// <summary>
    ///     The walk goes all the way down. A constraint on an array and another inside its <c>items</c> are both
    ///     dropped, so a warning that only looked at the top level would have left the author believing the half of the
    ///     schema that is furthest from the eye.
    /// </summary>
    [Test]
    public void Warnings_OnAConstraintNestedUnderItems_FindIt()
    {
        var graph = GraphWorkflowGraph.Parse(Agent("""
                                                   { "instructions": "List them.",
                                                     "responseJsonSchema": { "type": "object",
                                                                             "properties": { "tags": { "type": "array", "minItems": 1,
                                                                                                       "items": { "type": "object",
                                                                                                                  "properties": { "name": { "type": "string", "minLength": 1 } },
                                                                                                                  "required": ["name"],
                                                                                                                  "additionalProperties": false } } },
                                                                             "required": ["tags"],
                                                                             "additionalProperties": false } }
                                                   """));

        AssertEx.Equal(expected: 1, graph.Warnings.Count);
        AssertEx.Equal("Node 'agent' declares a response schema the runtime rewrites before it becomes a grammar: "
                       + "it drops 'minItems', 'minLength' rather than enforcing them.",
            graph.Warnings[0].Message);
    }

    /// <summary>
    ///     Only an Agent node's answer is held to a schema. A <c>Start</c> node's <c>inputSchema</c> describes what the
    ///     RUN is given rather than what a model must produce, never reaches a grammar, and so is nothing to warn about.
    /// </summary>
    [Test]
    public void Warnings_OnASchemaThatIsNotAnAgentResponse_AreEmpty()
    {
        AssertEx.Empty(GraphWorkflowGraph.Parse("""
                                                { "schemaVersion": 1,
                                                  "nodes": [{ "key": "start", "kind": "Start",
                                                              "config": { "inputSchema": { "type": "object", "properties": { "topic": { "type": "string", "maxLength": 3 } } } } },
                                                            { "key": "agent", "kind": "Agent", "config": { "instructions": "Go." } },
                                                            { "key": "done", "kind": "End", "config": { "outcome": "completed" } }],
                                                  "edges": [{ "key": "e1", "from": "start", "to": "agent" }, { "key": "e2", "from": "agent", "to": "done" }] }
                                                """).Warnings);
    }

    /// <summary>
    ///     The two warning kinds are independent and both ride out at once, on their own node keys — the strip renders
    ///     one chip per key, so a graph that has both problems must not report only the first one found.
    /// </summary>
    [Test]
    public void Warnings_OfBothKindsOnOneGraph_AreBothReportedOnTheirOwnNodes()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.PauseAfterALooseResponseSchema);

        AssertEx.Equal("analyze, summarize", string.Join(", ", graph.Warnings.Select(static warning => warning.Key).Order(StringComparer.Ordinal)));
        AssertEx.Contains(AssertEx.NotNull(graph.Warnings.FirstOrDefault(static warning => warning.Key == "analyze")).Message, "'pattern'");
        AssertEx.Contains(AssertEx.NotNull(graph.Warnings.FirstOrDefault(static warning => warning.Key == "summarize")).Message, "Add an edge from 'analyze' to 'summarize'");
    }

    /// <summary>
    ///     A response schema declaring nothing but optional properties is the shape the existing branch fixture carries,
    ///     and it is exactly the false confidence this warning is for: <c>requiresReview</c> reads as optional and comes
    ///     back mandatory.
    /// </summary>
    [Test]
    public void Warnings_OnTheBranchFixturesOptionalProperty_NameIt()
    {
        var graph = GraphWorkflowGraph.Parse(GraphWorkflowGraphs.BranchOnJson);

        AssertEx.Equal(expected: 1, graph.Warnings.Count);
        AssertEx.Equal("analyze", graph.Warnings[0].Key);
        AssertEx.Contains(graph.Warnings[0].Message, "('requiresReview' is optional here)");
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
