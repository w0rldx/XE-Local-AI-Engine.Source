namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Parsing the pinned graph, and the structural rules that go with it. Every rule here exists because breaking it
///     produces a run that HANGS rather than one that fails — which is the failure mode a durable runtime can least
///     afford, since nothing ever comes along to notice.
/// </summary>
public sealed class DevWorkflowGraphTests
{
    [Test]
    public void Parse_ReadsNodesEdgesAndTheirDefaults()
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowGraphs.ResearchPlanApproval);

        AssertEx.Equal(expected: 3, graph.Nodes.Count);
        AssertEx.Equal(expected: 2, graph.Edges.Count);
        AssertEx.Equal("research", string.Join(", ", graph.EntryNodeKeys));

        var research = graph.Nodes["research"];
        AssertEx.Equal(DevWorkflowNodeType.Agent, research.NodeType);
        AssertEx.Equal("Research", research.Label);
        AssertEx.Equal(expected: 3, research.MaxAttempts, "an agent node defaults to three attempts.");
        AssertEx.Equal(DevWorkflowJoinPolicy.All, research.JoinPolicy);
        AssertEx.Null(research.NodeTimeoutSeconds);

        AssertEx.Equal(expected: 1, graph.Nodes["approve"].MaxAttempts, "a human gate gets one try; retrying a question asks it twice.");
        AssertEx.Equal("approve", string.Join(", ", graph.Nodes.Keys.Where(key => graph.OutboundEdges(key).Count == 0)));
    }

    [Test]
    public void Parse_ReadsTheLabelFromTheNodeKeyWhenNoneIsGiven()
    {
        var graph = DevWorkflowGraph.Parse("""{"schemaVersion":1,"nodes":[{"nodeKey":"only","nodeType":"HumanGate"}],"edges":[]}""");

        AssertEx.Equal("only", graph.Nodes["only"].Label);
    }

    [Test]
    public void Parse_ReadsTheFullMaterializationObject()
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowGraphs.Decomposition);
        var materialization = AssertEx.NotNull(graph.Nodes["decompose"].Materialization);

        AssertEx.Equal("implement", materialization.TemplateNodeKey);
        AssertEx.Equal(DevWorkflowArtifactKind.TaskPackage, materialization.ArtifactKind);
        AssertEx.Equal("join", materialization.JoinNodeKey);
        AssertEx.Equal(expected: 20, materialization.MaxChildren);
    }

    /// <summary>
    ///     A template node is deliberately unreachable — that is how it stays uninstantiated until a decomposition
    ///     clones it — so the reachability rule has to exempt it and its subtree, or every decomposing definition would
    ///     be rejected at save.
    /// </summary>
    [Test]
    public void Parse_ExemptsTheMaterializationTemplateFromReachabilityAndFromTheEntryCount()
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowGraphs.Decomposition);

        AssertEx.Contains(graph.EntryNodeKeys, key => key == "implement", "the template really has no inbound edge…");
        AssertEx.True(graph.Nodes.ContainsKey("implement"), "…and it parsed anyway.");
    }

    /// <summary>
    ///     A template with edges of its own is refused, because the alternative hangs rather than fails: the template is
    ///     the one node deliberately never instantiated, so a successor it declared would get a node run at run start and
    ///     then wait forever on an inbound edge whose source has no row and never will.
    /// </summary>
    [Test]
    public void Parse_WithATemplateThatDeclaresItsOwnEdges_IsRejected()
    {
        const string TemplateSubtree = """
                                       {
                                         "nodes": [{ "nodeKey": "decompose", "nodeType": "Agent",
                                                     "materialization": { "templateNodeKey": "implement", "artifactKind": "TaskPackage", "joinNodeKey": "join", "maxChildren": 4 } },
                                                   { "nodeKey": "implement", "nodeType": "DevTask" },
                                                   { "nodeKey": "verify", "nodeType": "Tool" },
                                                   { "nodeKey": "join", "nodeType": "Join" }],
                                         "edges": [{ "from": "decompose", "to": "join" }, { "from": "implement", "to": "verify" }]
                                       }
                                       """;

        AssertEx.Contains(AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(TemplateSubtree)).Message, "declares edges of its own");
    }

    [Test]
    public void Descendants_FollowsOutEdgesAndExcludesTheNodeItself()
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowGraphs.FanOut);

        AssertEx.Equal("join, lint, test", string.Join(", ", graph.Descendants("implement").Order(StringComparer.Ordinal)));
        AssertEx.Equal("join", string.Join(", ", graph.Descendants("lint")));
        AssertEx.Empty(graph.Descendants("join"));
    }

    [Test]
    [Arguments("not json at all", "not valid JSON")]
    [Arguments("""[]""", "must be a JSON object")]
    [Arguments("""{"schemaVersion":2,"nodes":[],"edges":[]}""", "schema version 1")]
    [Arguments("""{"edges":[]}""", "needs a 'nodes' array")]
    [Arguments("""{"nodes":[],"edges":[]}""", "at least one node")]
    [Arguments("""{"nodes":[{"nodeType":"Agent"}],"edges":[]}""", "needs a non-empty 'nodeKey'")]
    [Arguments("""{"nodes":[{"nodeKey":"a","nodeType":"Sorcery"}],"edges":[]}""", "needs a 'nodeType'")]
    [Arguments("""{"nodes":[{"nodeKey":"a","nodeType":"Agent"},{"nodeKey":"a","nodeType":"Gate"}],"edges":[]}""", "twice")]
    [Arguments("""{"nodes":[{"nodeKey":"a","nodeType":"Agent"}],"edges":[{"from":"a","to":"ghost"}]}""", "does not declare")]
    [Arguments("""{"nodes":[{"nodeKey":"a","nodeType":"Agent","maxAttempts":0}],"edges":[]}""", "must be positive")]
    [Arguments("""{"nodes":[{"nodeKey":"a","nodeType":"Agent","retryDelaySeconds":-1}],"edges":[]}""", "cannot be negative")]
    [Arguments("""{"nodes":[{"nodeKey":"a","nodeType":"Agent","joinPolicy":"Maybe"}],"edges":[]}""", "unknown 'joinPolicy'")]
    [Arguments("""{"nodes":[{"nodeKey":"a","nodeType":"Agent","agentDefinitionId":"not-a-guid"}],"edges":[]}""", "not a GUID")]
    [Arguments("""{"nodes":[{"nodeKey":"a","nodeType":"Tool","validationCommandIds":"build"}],"edges":[]}""", "array of strings")]
    public void Parse_RejectsAGraphItCannotRoute(string json, string expectedMessage) =>
        AssertEx.Contains(AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(json)).Message, expectedMessage);

    [Test]
    public void Parse_WithNoEntryNode_IsRejected()
    {
        // Every node has an inbound edge, so nothing could ever start. The entry check names it first, which is the more
        // useful complaint of the two this graph earns.
        const string Cyclic = """{"nodes":[{"nodeKey":"a","nodeType":"Agent"},{"nodeKey":"b","nodeType":"Agent"}],"edges":[{"from":"a","to":"b"},{"from":"b","to":"a"}]}""";

        AssertEx.Contains(AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(Cyclic)).Message, "has none");
    }

    /// <summary>
    ///     A cycle that still has an entry node, so the cycle rule is the one that has to catch it. Cycles are forbidden
    ///     because a run that revisits a node has no bound on its own length; the fix loop is a retry target instead.
    /// </summary>
    [Test]
    public void Parse_WithACycleBelowTheEntryNode_IsRejected()
    {
        const string Cyclic = """
                              {
                                "nodes": [{ "nodeKey": "a", "nodeType": "Agent" }, { "nodeKey": "b", "nodeType": "Agent" }, { "nodeKey": "c", "nodeType": "Agent" }],
                                "edges": [{ "from": "a", "to": "b" }, { "from": "b", "to": "c" }, { "from": "c", "to": "b" }]
                              }
                              """;

        AssertEx.Contains(AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(Cyclic)).Message, "cycle");
    }

    [Test]
    public void Parse_WithTwoEntryNodes_IsRejected()
    {
        const string TwoEntries =
            """{"nodes":[{"nodeKey":"a","nodeType":"Agent"},{"nodeKey":"b","nodeType":"Agent"},{"nodeKey":"c","nodeType":"Join"}],"edges":[{"from":"a","to":"c"},{"from":"b","to":"c"}]}""";

        AssertEx.Contains(AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(TwoEntries)).Message, "exactly one entry node");
    }

    [Test]
    public void Parse_WithAnUnreachableNode_IsRejected()
    {
        const string Orphan = """{"nodes":[{"nodeKey":"a","nodeType":"Agent"},{"nodeKey":"b","nodeType":"Agent"},{"nodeKey":"c","nodeType":"Agent"}],"edges":[{"from":"b","to":"c"}]}""";

        // Two entry nodes is what this actually is; either refusal is correct, and both keep the orphan out.
        AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(Orphan));
    }

    [Test]
    public void Parse_WithAnyJoinOnFewerThanTwoInboundEdges_IsRejected()
    {
        const string LonelyAny = """{"nodes":[{"nodeKey":"a","nodeType":"Agent"},{"nodeKey":"b","nodeType":"Join","joinPolicy":"Any"}],"edges":[{"from":"a","to":"b"}]}""";

        AssertEx.Contains(AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(LonelyAny)).Message, "fewer than two inbound edges");
    }

    /// <summary>
    ///     The fix loop is a retry target rather than a back edge, and the ancestry rule is what keeps it from becoming
    ///     one: routing a failure to a node that does not lead back here would re-run something whose result nothing
    ///     downstream consumes, forever.
    /// </summary>
    [Test]
    public void Parse_WithARetryTargetThatIsNotAnAncestor_IsRejected()
    {
        const string SidewaysTarget = """
                                      {
                                        "nodes": [{ "nodeKey": "implement", "nodeType": "DevTask" },
                                                  { "nodeKey": "lint", "nodeType": "Tool" },
                                                  { "nodeKey": "test", "nodeType": "Tool", "retryTarget": "lint" },
                                                  { "nodeKey": "join", "nodeType": "Join" }],
                                        "edges": [{ "from": "implement", "to": "lint" }, { "from": "implement", "to": "test" },
                                                  { "from": "lint", "to": "join" }, { "from": "test", "to": "join" }]
                                      }
                                      """;

        AssertEx.Contains(AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(SidewaysTarget)).Message, "not one of its ancestors");
    }

    [Test]
    public void Parse_WithARetryTargetThatIsAnAncestor_IsAccepted()
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowGraphs.FanOut);

        AssertEx.Equal("implement", graph.Nodes["test"].RetryTarget);
    }

    [Test]
    public void Parse_WithAnUnknownRetryTarget_IsRejected()
    {
        const string GhostTarget = """{"nodes":[{"nodeKey":"a","nodeType":"Agent"},{"nodeKey":"b","nodeType":"Tool","retryTarget":"ghost"}],"edges":[{"from":"a","to":"b"}]}""";

        AssertEx.Contains(AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(GhostTarget)).Message, "does not declare");
    }

    [Test]
    public void Parse_WithAMaterializationNamingAnUndeclaredNode_IsRejected()
    {
        const string GhostTemplate = """
                                     {
                                       "nodes": [{ "nodeKey": "a", "nodeType": "Agent",
                                                   "materialization": { "templateNodeKey": "ghost", "artifactKind": "TaskPackage", "joinNodeKey": "a", "maxChildren": 2 } }],
                                       "edges": []
                                     }
                                     """;

        AssertEx.Contains(AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(GhostTemplate)).Message, "names template node");
    }
}
