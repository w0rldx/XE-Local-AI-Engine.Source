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
    /// <summary>
    ///     The smallest legal integration shape: work, a human gate, an apply behind it — with the gate's edge
    ///     conditioned on the approval, which is half the rule rather than decoration. Every answer a gate takes
    ///     SUCCEEDS it, so an edge that does not name the approval carries the rejection through as well.
    /// </summary>
    private const string GatedApply = """
                                      {
                                        "nodes": [{ "nodeKey": "check", "nodeType": "Tool" },
                                                  { "nodeKey": "gate", "nodeType": "HumanGate" },
                                                  { "nodeKey": "apply", "nodeType": "Tool", "toolMode": "Apply" }],
                                        "edges": [{ "from": "check", "to": "gate" },
                                                  { "from": "gate", "to": "apply", "condition": { "path": "decision", "op": "eq", "value": "Approve" } }]
                                      }
                                      """;

    /// <summary>The approval condition on its own, so a test can take it out or put something else in its place.</summary>
    private const string ApprovalCondition = """, "condition": { "path": "decision", "op": "eq", "value": "Approve" }""";

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

    /// <summary>
    ///     The two dispatch pins. The model name is taken as written — it is matched against this node's catalog when
    ///     the session is created, exactly as an agent definition's own pin is — while the effort is checked here,
    ///     because its vocabulary is closed and cannot go stale between authoring and a run.
    /// </summary>
    [Test]
    public void Parse_ReadsThePerNodeModelAndReasoningEffort()
    {
        var graph = DevWorkflowGraph.Parse(
            """{"schemaVersion":1,"nodes":[{"nodeKey":"only","nodeType":"Agent","modelProfile":" qwen3-30b ","reasoningEffort":"High"}],"edges":[]}""");

        AssertEx.Equal("qwen3-30b", graph.Nodes["only"].ModelProfile);
        AssertEx.Equal("High", graph.Nodes["only"].ReasoningEffort, "the effort travels to the provider as written; only its membership is checked.");
    }

    /// <summary>
    ///     A node may author <c>auto</c>: the vocabulary is the agent surface's, and that surface accepts it. The node
    ///     is agent-bound, so its turn always carries a pinned model — the effort ladder applies, the model swap never
    ///     does — which needs no node-specific rule, only that the parser stops refusing the token.
    /// </summary>
    [Test]
    public void ParseNode_WhenReasoningEffortIsAuto_IsAccepted()
    {
        var graph = DevWorkflowGraph.Parse(
            """{"schemaVersion":1,"nodes":[{"nodeKey":"only","nodeType":"Agent","reasoningEffort":"auto"}],"edges":[]}""");

        AssertEx.Equal("auto", graph.Nodes["only"].ReasoningEffort);
    }

    [Test]
    public void Parse_WithNeitherPin_LeavesBothToTheBoundAgent()
    {
        var research = DevWorkflowGraph.Parse(DevWorkflowGraphs.ResearchPlanApproval).Nodes["research"];

        AssertEx.Null(research.ModelProfile);
        AssertEx.Null(research.ReasoningEffort);
    }

    /// <summary>
    ///     A cleared picker sends <c>""</c> and older stored documents already hold one, so a blank reads as "not
    ///     pinned" rather than as a graph the dispatcher refuses to route.
    /// </summary>
    [Test]
    public void Parse_WithABlankPin_ReadsItAsUnpinnedRatherThanRefusingTheGraph()
    {
        var only = DevWorkflowGraph.Parse(
                                       """{"schemaVersion":1,"nodes":[{"nodeKey":"only","nodeType":"Agent","modelProfile":"","reasoningEffort":"  "}],"edges":[]}""")
                                   .Nodes["only"];

        AssertEx.Null(only.ModelProfile);
        AssertEx.Null(only.ReasoningEffort);
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
    ///     A template is a SUBTREE, so it may declare its own internal edges — that is what keeps the per-task fix loop
    ///     (<c>Implement → Validate → Implement</c>) authorable at all. What the subtree carries is the set of nodes a
    ///     run start gives no row to, and the join is deliberately not one of them: it belongs to the graph.
    /// </summary>
    [Test]
    public void Parse_ReadsATemplateAsTheWholeSubtreeShortOfItsJoin()
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowGraphs.DecompositionSubtree);

        AssertEx.Equal("implement, validate", string.Join(", ", graph.TemplateKeys.OrderBy(static key => key, StringComparer.Ordinal)));
        AssertEx.False(graph.TemplateKeys.Contains("join"), "walking through the join would make the rest of the run part of the template.");
        AssertEx.Equal("decompose", string.Join(", ", graph.EntryNodeKeys.Where(key => !graph.TemplateKeys.Contains(key))));
    }

    /// <summary>
    ///     An edge INTO the subtree from outside is refused, because the alternative hangs rather than fails: its source
    ///     would be waiting on the one node deliberately never instantiated.
    /// </summary>
    [Test]
    public void Parse_WithAnEdgeIntoTheTemplateSubtreeFromOutside_IsRejected()
    {
        const string PointingIn = """
                                  {
                                    "nodes": [{ "nodeKey": "decompose", "nodeType": "Agent",
                                                "materialization": { "templateNodeKey": "implement", "artifactKind": "TaskPackage", "joinNodeKey": "join", "maxChildren": 4 } },
                                              { "nodeKey": "implement", "nodeType": "DevTask" },
                                              { "nodeKey": "join", "nodeType": "Join" }],
                                    "edges": [{ "from": "decompose", "to": "join" }, { "from": "decompose", "to": "implement" }]
                                  }
                                  """;

        AssertEx.Contains(AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(PointingIn)).Message, "points into the materialization template");
    }

    /// <summary>
    ///     The same rule catches the shape that would otherwise be silent: a node the run really does have to execute,
    ///     which the template also reaches, is swallowed into the template — given no row at run start while the live
    ///     graph waits on it. That is the mirror of the plan's "no edge leaves the subtree except to the join", and it
    ///     is the half a subtree defined by what it reaches cannot break any other way.
    /// </summary>
    [Test]
    public void Parse_WithATemplateSubtreeThatSwallowsALiveNode_IsRejected()
    {
        const string Swallowed = """
                                 {
                                   "nodes": [{ "nodeKey": "decompose", "nodeType": "Agent",
                                               "materialization": { "templateNodeKey": "implement", "artifactKind": "TaskPackage", "joinNodeKey": "join", "maxChildren": 4 } },
                                             { "nodeKey": "implement", "nodeType": "DevTask" },
                                             { "nodeKey": "deploy", "nodeType": "Tool" },
                                             { "nodeKey": "join", "nodeType": "Join" }],
                                   "edges": [{ "from": "decompose", "to": "deploy" }, { "from": "implement", "to": "deploy" }, { "from": "deploy", "to": "join" }]
                                 }
                                 """;

        AssertEx.Contains(AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(Swallowed)).Message, "points into the materialization template");
    }

    /// <summary>
    ///     Nested materialization is named as v2 and refused here rather than discovered at run time: a clone that
    ///     decomposed again would expand a template that has already been expanded, under keys nothing can tell apart.
    /// </summary>
    [Test]
    public void Parse_WithAMaterializationInsideATemplateSubtree_IsRejected()
    {
        const string Nested = """
                              {
                                "nodes": [{ "nodeKey": "decompose", "nodeType": "Agent",
                                            "materialization": { "templateNodeKey": "implement", "artifactKind": "TaskPackage", "joinNodeKey": "join", "maxChildren": 4 } },
                                          { "nodeKey": "implement", "nodeType": "Agent",
                                            "materialization": { "templateNodeKey": "subtask", "artifactKind": "TaskPackage", "joinNodeKey": "join", "maxChildren": 4 } },
                                          { "nodeKey": "subtask", "nodeType": "DevTask" },
                                          { "nodeKey": "join", "nodeType": "Join" }],
                                "edges": [{ "from": "decompose", "to": "join" }]
                              }
                              """;

        AssertEx.Contains(AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(Nested)).Message, "Nested materialization is not supported");
    }

    /// <summary>
    ///     Two edges between the same pair of nodes are refused. An author writing them means "either of these", and
    ///     that is the one thing they cannot mean: admission judges every inbound edge on its own, so the second one's
    ///     condition failing makes that edge DEAD and SKIPS the target — the opposite of the intent. Refusing it is also
    ///     what lets the materialization key an authored edge on its endpoints, which is where this was found.
    /// </summary>
    [Test]
    public void Parse_WithTheSameEdgeDeclaredTwice_IsRejected()
    {
        const string Twice = """
                             {
                               "nodes": [{ "nodeKey": "gate", "nodeType": "Gate" }, { "nodeKey": "ship", "nodeType": "Join" }],
                               "edges": [{ "from": "gate", "to": "ship", "condition": { "path": "decision", "op": "eq", "value": "Approve" } },
                                         { "from": "gate", "to": "ship", "condition": { "path": "decision", "op": "eq", "value": "RequestChanges" } }]
                             }
                             """;

        AssertEx.Contains(AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(Twice)).Message, "declares edge 'gate' → 'ship' twice");
    }

    /// <summary>
    ///     The width bound (R5) is checked where a definition asks for it, not where a run tries to commit it: the
    ///     expansion rewrites the run's whole encrypted graph blob, so the fan-out a template allows is the size of that
    ///     write.
    /// </summary>
    [Test]
    public void Parse_WithAMaterializationOverTheChildCap_IsRejected()
    {
        const string TooWide = """
                               {
                                 "nodes": [{ "nodeKey": "decompose", "nodeType": "Agent",
                                             "materialization": { "templateNodeKey": "implement", "artifactKind": "TaskPackage", "joinNodeKey": "join", "maxChildren": 21 } },
                                           { "nodeKey": "implement", "nodeType": "DevTask" },
                                           { "nodeKey": "join", "nodeType": "Join" }],
                                 "edges": [{ "from": "decompose", "to": "join" }]
                               }
                               """;

        AssertEx.Contains(AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(TooWide)).Message, "more than the 20");
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
    [Arguments("""{"nodes":[{"nodeKey":"a","nodeType":"Agent","reasoningEffort":"exhaustive"}],"edges":[]}""", "unknown 'reasoningEffort'")]
    [Arguments("""{"nodes":[{"nodeKey":"a","nodeType":"Agent","reasoningEffort":"xhigh"}],"edges":[]}""", "unknown 'reasoningEffort'")]
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

    /// <summary>
    ///     The bug this rule exists for: a node OUTSIDE a materialization subtree naming the template node reads as a
    ///     perfectly ordinary fix loop and can never fire. Run seeding skips template keys and the materializer renames
    ///     each clone, so at runtime the route finds no node run under that key and blocks the run on Configuration —
    ///     silently turning every failure of that node into a dead end instead of a re-attempt.
    /// </summary>
    [Test]
    public void Parse_WithARetryTargetNamingATemplateNodeFromOutsideTheSubtree_IsRejected()
    {
        const string OutsideTheSubtree = """
                                         {
                                           "nodes": [{ "nodeKey": "decompose", "nodeType": "Agent",
                                                       "materialization": { "templateNodeKey": "implement", "artifactKind": "TaskPackage", "joinNodeKey": "join", "maxChildren": 4 } },
                                                     { "nodeKey": "implement", "nodeType": "DevTask" },
                                                     { "nodeKey": "validate", "nodeType": "Tool" },
                                                     { "nodeKey": "join", "nodeType": "Join" },
                                                     { "nodeKey": "fullvalidate", "nodeType": "Tool", "retryTarget": "implement" }],
                                           "edges": [{ "from": "decompose", "to": "join" }, { "from": "implement", "to": "validate" },
                                                     { "from": "validate", "to": "join" }, { "from": "join", "to": "fullvalidate" }]
                                         }
                                         """;

        AssertEx.Contains(AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(OutsideTheSubtree)).Message,
            "is a materialization template node");
    }

    /// <summary>
    ///     And the clone-internal loop stays legal, which is the whole reason the rule is about WHERE the declaring node
    ///     sits rather than about template keys as such: the materializer rewrites both keys together, so
    ///     <c>validate#alpha</c> routes to <c>implement#alpha</c> and the row is there.
    /// </summary>
    [Test]
    public void Parse_WithARetryTargetNamingATemplateNodeFromInsideTheSubtree_IsAccepted()
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowGraphs.ShippedTailFixLoop);

        AssertEx.Equal("implement", graph.Nodes["validate"].RetryTarget, "the per-slice loop lives inside the subtree and is rewritten with it.");
        AssertEx.Equal("verify", graph.Nodes["fullvalidate"].RetryTarget, "and the one outside it names a node every run seeds.");
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

    /// <summary>
    ///     The apply variant is a Tool node with a config field, not an eighth node type (Y6): the seven stay closed and
    ///     what a Tool node does with the repository is a property of the node.
    /// </summary>
    [Test]
    public void Parse_ReadsTheToolModeAndDefaultsItToValidate()
    {
        var graph = DevWorkflowGraph.Parse(GatedApply);

        AssertEx.Equal(DevWorkflowToolMode.Apply, graph.Nodes["apply"].ToolMode);
        AssertEx.Equal(DevWorkflowToolMode.Validate, graph.Nodes["check"].ToolMode, "a Tool node that says nothing runs commands, as every Tool node did before this field existed.");
        AssertEx.Equal(DevWorkflowToolMode.Validate, DevWorkflowGraph.Parse(DevWorkflowGraphs.SingleTool).Nodes["validate"].ToolMode);
    }

    [Test]
    public void Parse_WithAnUnknownToolMode_IsRejected()
    {
        const string Nonsense = """{"nodes":[{"nodeKey":"only","nodeType":"Tool","toolMode":"Deploy"}],"edges":[]}""";

        AssertEx.Contains(AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(Nonsense)).Message, "unknown 'toolMode'");
    }

    /// <summary>A field that does nothing where it is written is a definition claiming something the runtime will not do.</summary>
    [Test]
    public void Parse_WithAToolModeOnANodeThatIsNotATool_IsRejected()
    {
        const string Misplaced = """{"nodes":[{"nodeKey":"only","nodeType":"DevTask","toolMode":"Apply","nodeTimeoutSeconds":60}],"edges":[]}""";

        AssertEx.Contains(AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(Misplaced)).Message, "only a Tool node runs one");
    }

    [Test]
    public void Parse_WithValidationCommandsOnAnApplyNode_IsRejected()
    {
        var contradictory = GatedApply.Replace("""{ "nodeKey": "apply", "nodeType": "Tool", "toolMode": "Apply" }""",
            """{ "nodeKey": "apply", "nodeType": "Tool", "toolMode": "Apply", "validationCommandIds": ["dotnet_build"] }""",
            StringComparison.Ordinal);

        AssertEx.Contains(AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(contradictory)).Message, "will never run");
    }

    /// <summary>
    ///     Y3 made structural. The rule is that no AI-authored patch reaches a real repository without an operator
    ///     decision in the run's own audit trail, and a definition is the only place that can be checked before the fact:
    ///     by the time an ungated apply runs, the approval it should have waited for does not exist to be missed.
    /// </summary>
    [Test]
    public void Parse_WithAnApplyNodeReachedFromSomethingOtherThanAHumanGate_IsRejected()
    {
        var ungated = GatedApply.Replace("""{ "nodeKey": "gate", "nodeType": "HumanGate" }""",
            """{ "nodeKey": "gate", "nodeType": "Gate" }""",
            StringComparison.Ordinal);

        AssertEx.Contains(AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(ungated)).Message, "other than a human gate");
    }

    /// <summary>
    ///     One branch may not route around the gate: EVERY inbound edge has to be the decision, or the apply happens on
    ///     whichever branch arrives first.
    /// </summary>
    [Test]
    public void Parse_WithOneBranchReachingTheApplyWithoutTheGate_IsRejected()
    {
        const string SideDoor = """
                                {
                                  "nodes": [{ "nodeKey": "split", "nodeType": "Parallel" },
                                            { "nodeKey": "gate", "nodeType": "HumanGate" },
                                            { "nodeKey": "check", "nodeType": "Tool" },
                                            { "nodeKey": "apply", "nodeType": "Tool", "toolMode": "Apply" }],
                                  "edges": [{ "from": "split", "to": "gate" }, { "from": "split", "to": "check" },
                                            { "from": "gate", "to": "apply" }, { "from": "check", "to": "apply" }]
                                }
                                """;

        AssertEx.Contains(AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(SideDoor)).Message, "other than a human gate");
    }

    /// <summary>
    ///     An apply node with no inbound edge at all is the same refusal arriving from the other side: a graph whose
    ///     entry point applies patches asks nobody anything.
    /// </summary>
    [Test]
    public void Parse_WithAnApplyNodeThatNothingLeadsTo_IsRejected()
    {
        const string Lonely = """{"nodes":[{"nodeKey":"only","nodeType":"Tool","toolMode":"Apply"}],"edges":[]}""";

        AssertEx.Contains(AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(Lonely)).Message, "other than a human gate");
    }

    /// <summary>
    ///     The exploit the source-only rule left open, refused at the definition. A gate in front of an apply is not the
    ///     rule if the edge takes every answer: <c>Reject</c> SUCCEEDS the gate exactly as <c>Approve</c> does — the
    ///     rejection is meant to reach the run through an edge that matches nothing — so an unconditional edge routes it
    ///     straight into the apply and the patches the operator declined go into the repository.
    /// </summary>
    [Test]
    public void Parse_WithAnApplyEdgeThatCarriesEveryGateAnswer_IsRejected()
    {
        var unconditional = GatedApply.Replace(ApprovalCondition, string.Empty, StringComparison.Ordinal);

        AssertEx.Contains(AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(unconditional)).Message, "also carries a Reject answer");
    }

    /// <summary>
    ///     The same exploit written out: an edge that names the refusal is an apply-on-rejection in one line. It is
    ///     refused by the rule's OTHER half — an edge conditioned on the rejection is false for the approval — which is
    ///     the same refusal reached from the side that also catches an apply nothing could ever route to.
    /// </summary>
    [Test]
    public void Parse_WithAnApplyEdgeConditionedOnTheRejection_IsRejected()
    {
        var backwards = GatedApply.Replace("""{ "path": "decision", "op": "eq", "value": "Approve" }""",
            """{ "path": "decision", "op": "eq", "value": "Reject" }""",
            StringComparison.Ordinal);

        AssertEx.Contains(AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(backwards)).Message, "does not carry an approval");
    }

    /// <summary>
    ///     And the rule's other side, which is a HANG rather than an apply: an edge no approval satisfies leaves the one
    ///     answer that may reach an apply with nowhere to go, so the definition never integrates anything.
    /// </summary>
    [Test]
    public void Parse_WithAnApplyEdgeNoApprovalSatisfies_IsRejected()
    {
        var unreachable = GatedApply.Replace("""{ "path": "decision", "op": "eq", "value": "Approve" }""",
            """{ "path": "status", "op": "eq", "value": "Failed" }""",
            StringComparison.Ordinal);

        AssertEx.Contains(AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(unreachable)).Message, "does not carry an approval");
    }

    /// <summary>
    ///     The definition-time rule is the runtime's own routing asked a question, not a second reading of it: the same
    ///     call the tick makes to decide where a landed answer goes is the call that refuses the definition.
    /// </summary>
    [Test]
    public void GateEdgeFires_AnswersTheApprovalAndNeitherOfTheOtherTwo()
    {
        var edge = DevWorkflowGraph.Parse(GatedApply).InboundEdges("apply").Single();

        AssertEx.Equal("Approve, Reject, RequestChanges", string.Join(", ", DevWorkflowStateMachine.GateAnswers));
        AssertEx.True(DevWorkflowStateMachine.GateEdgeFires(edge, DevWorkflowDecisionKind.Approve));
        AssertEx.False(DevWorkflowStateMachine.GateEdgeFires(edge, DevWorkflowDecisionKind.Reject));
        AssertEx.False(DevWorkflowStateMachine.GateEdgeFires(edge, DevWorkflowDecisionKind.RequestChanges));
    }

    /// <summary>An apply inside a template would be cloned per task, and each clone would apply the whole fan-out again.</summary>
    [Test]
    public void Parse_WithAnApplyNodeInsideAMaterializationTemplate_IsRejected()
    {
        const string ClonedApply = """
                                   {
                                     "nodes": [{ "nodeKey": "decompose", "nodeType": "Agent",
                                                 "materialization": { "templateNodeKey": "implement", "artifactKind": "TaskPackage", "joinNodeKey": "join", "maxChildren": 4 } },
                                               { "nodeKey": "implement", "nodeType": "DevTask" },
                                               { "nodeKey": "apply", "nodeType": "Tool", "toolMode": "Apply" },
                                               { "nodeKey": "join", "nodeType": "Join" }],
                                     "edges": [{ "from": "decompose", "to": "join" }, { "from": "implement", "to": "apply" }, { "from": "apply", "to": "join" }]
                                   }
                                   """;

        AssertEx.Contains(AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(ClonedApply)).Message, "Integration runs once");
    }
}
