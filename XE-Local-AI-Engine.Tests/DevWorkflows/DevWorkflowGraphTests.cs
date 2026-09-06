namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using System.Reflection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;
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
        var graph = DevWorkflowGraph.Parse("""{"schemaVersion":1,"nodes":[{"nodeKey":"only","nodeType":"Agent","modelProfile":" qwen3-30b ","reasoningEffort":"High"}],"edges":[]}""");

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
        var graph = DevWorkflowGraph.Parse("""{"schemaVersion":1,"nodes":[{"nodeKey":"only","nodeType":"Agent","reasoningEffort":"auto"}],"edges":[]}""");

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
        var only = DevWorkflowGraph.Parse("""{"schemaVersion":1,"nodes":[{"nodeKey":"only","nodeType":"Agent","modelProfile":"","reasoningEffort":"  "}],"edges":[]}""")
                                   .Nodes["only"];

        AssertEx.Null(only.ModelProfile);
        AssertEx.Null(only.ReasoningEffort);
    }

    /// <summary>
    ///     <c>requiredCapabilities</c> is an OBJECT — effect token to the author's reason — and it is the one effect
    ///     answer a graph can give for an Agent node, whose real reach follows from the definition it binds and is not
    ///     knowable until dispatch. The tokens are matched case-insensitively, like every other vocabulary here.
    /// </summary>
    [Test]
    public void Parse_ReadsRequiredCapabilitiesAsATypedEffectSet()
    {
        var graph = DevWorkflowGraph.Parse("""
                                           {"schemaVersion":1,"allowUngatedWrites":true,
                                            "nodes":[{"nodeKey":"release","nodeType":"Agent",
                                                      "requiredCapabilities":{"writeexecute":"runs the release script","Network":"pushes the tag"}}],
                                            "edges":[]}
                                           """);

        AssertEx.Equal("Network, WriteExecute",
            string.Join(", ", DevWorkflowGraph.Effects(graph.Nodes["release"]).Select(static effect => effect.ToString()).Order(StringComparer.Ordinal)));
    }

    /// <summary>
    ///     An Agent that declares nothing carries nothing. The alternative — reading an undeclared agent as a writer —
    ///     would classify every node of every seeded template as a write, because they are all instructed to save
    ///     artifacts, and the product's own templates would stop validating.
    /// </summary>
    [Test]
    public void Parse_WithoutRequiredCapabilities_LeavesAnAgentNodeDeclaringNothing()
    {
        var research = DevWorkflowGraph.Parse(DevWorkflowGraphs.ResearchPlanApproval).Nodes["research"];

        AssertEx.Empty(research.RequiredCapabilities);
        AssertEx.Empty(DevWorkflowGraph.Effects(research));
    }

    /// <summary>
    ///     What the other node types carry is DERIVED, because the node itself says what it does. A Tool naming no
    ///     command inherits the project profile's set, chosen when a run picks a project up, so the answer fails toward
    ///     the wider set rather than guessing the narrower one.
    /// </summary>
    [Test]
    public void Parse_DerivesTheEffectsOfEveryNodeTypeThatSaysWhatItDoes()
    {
        var fanOut = DevWorkflowGraph.Parse(DevWorkflowGraphs.FanOut);
        var gated = DevWorkflowGraph.Parse(GatedApply);

        AssertEx.Equal("WriteExecute", Effects(fanOut, "implement"), "a DevTask writes.");
        AssertEx.Equal(DevWorkflowEffectScope.Sandbox, DevWorkflowGraph.ScopeOf(fanOut.Nodes["implement"]), "…into a worktree under this node's own data root.");
        AssertEx.Equal("Network, ReadLocal", Effects(fanOut, "lint"), "a validation naming no command may be given one that restores packages.");
        AssertEx.Equal("", Effects(fanOut, "join"), "a join routes; it does not act.");
        AssertEx.Equal("", Effects(gated, "gate"));
        AssertEx.Equal("WriteExecute", Effects(gated, "apply"), "an apply writes…");
        AssertEx.Equal(DevWorkflowEffectScope.Repository, DevWorkflowGraph.ScopeOf(gated.Nodes["apply"]), "…the operator's own repository.");
    }

    /// <summary>
    ///     A validation whose commands are all local reaches nothing off the machine; naming the restore command is
    ///     what puts it on the network. The distinction is worth making because it is the only one the derived half of
    ///     the vocabulary can draw from a definition alone.
    /// </summary>
    [Test]
    public void Parse_ReadsAValidationsNetworkReachFromTheCommandsItNames()
    {
        var graph = DevWorkflowGraph.Parse("""
                                           {"schemaVersion":1,
                                            "nodes":[{"nodeKey":"local","nodeType":"Tool","validationCommandIds":["git_status","dotnet_build_release_no_restore"]},
                                                     {"nodeKey":"restoring","nodeType":"Tool","validationCommandIds":["dotnet_restore"]}],
                                            "edges":[{"from":"local","to":"restoring"}]}
                                           """);

        AssertEx.Equal("ReadLocal", Effects(graph, "local"));
        AssertEx.Equal("Network, ReadLocal", Effects(graph, "restoring"));
    }

    /// <summary>
    ///     The per-loop cap, and the reason it is optional: absent means NO cap, so every already-stored definition
    ///     routes at run start exactly as it does today.
    /// </summary>
    [Test]
    public void Parse_ReadsMaxLoopIterationsBesideARetryTarget()
    {
        var graph = DevWorkflowGraph.Parse("""
                                           {"schemaVersion":1,
                                            "nodes":[{"nodeKey":"implement","nodeType":"Agent"},
                                                     {"nodeKey":"check","nodeType":"Tool","retryTarget":"implement","maxLoopIterations":2}],
                                            "edges":[{"from":"implement","to":"check"}]}
                                           """);

        AssertEx.Equal(expected: 2, graph.Nodes["check"].MaxLoopIterations);
        AssertEx.Null(graph.Nodes["implement"].MaxLoopIterations, "a node that names no cap has none, rather than inheriting a default nobody asked for.");
    }

    /// <summary>
    ///     The graph-level waiver. Absent is <c>false</c>, which is what keeps a definition written before this field
    ///     byte-identical: the rule it waives is new, so nothing stored can be relying on the waiver.
    /// </summary>
    [Test]
    public void Parse_ReadsAllowUngatedWritesFromTheGraphRoot()
    {
        AssertEx.True(DevWorkflowGraph.Parse("""{"schemaVersion":1,"allowUngatedWrites":true,"nodes":[{"nodeKey":"only","nodeType":"Agent"}],"edges":[]}""")
                                      .AllowUngatedWrites);
        AssertEx.False(DevWorkflowGraph.Parse(DevWorkflowGraphs.ResearchPlanApproval).AllowUngatedWrites, "a graph that says nothing waives nothing.");
    }

    /// <summary>
    ///     The reason beside a capability is a one-line justification a reviewer reads next to the node, and the whole
    ///     graph document is encrypted and rewritten on every materialization — so it is bounded where it is authored
    ///     rather than where it is stored.
    /// </summary>
    [Test]
    public void Parse_WithAnOverlongCapabilityReason_IsRejected()
    {
        var json = """{"schemaVersion":1,"nodes":[{"nodeKey":"release","nodeType":"Agent","requiredCapabilities":{"WriteExecute":"REASON"}}],"edges":[]}"""
            .Replace("REASON", new string('x', 201), StringComparison.Ordinal);

        AssertEx.Contains(AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(json)).Message, "longer than 200 characters");
    }

    /// <summary>
    ///     <c>GRAPH-C4-1</c>. The bug class this closes is a real one and reads correctly:
    ///     <c>value: "Approved"</c> — the past participle — is not an answer the gate can give, so the branch behind it
    ///     is written and unreachable. The complaint names the GATE, because that is where the damage is; the run could
    ///     not complete from there whatever is behind it.
    /// </summary>
    [Test]
    public void Parse_WithAGateWhoseOnlyOutEdgeCanNeverFire_IsRejected()
    {
        const string StrandedBranch = """
                                      {"schemaVersion":1,
                                       "nodes":[{"nodeKey":"research","nodeType":"Agent"},
                                                {"nodeKey":"planapproval","nodeType":"HumanGate"},
                                                {"nodeKey":"ship","nodeType":"Agent"}],
                                       "edges":[{"from":"research","to":"planapproval"},
                                                {"from":"planapproval","to":"ship","condition":{"path":"decision","op":"eq","value":"Approved"}}]}
                                      """;

        var refusal = AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(StrandedBranch)).Message;

        AssertEx.Contains(refusal, "Node 'planapproval' is a human gate with an out-edge that can never fire");
        AssertEx.Contains(refusal, "GRAPH-C4-1", StringComparison.Ordinal, "the id is what the operator quotes and what the client mirrors.");
    }

    /// <summary>
    ///     A dead edge that stranded nothing is still a definition saying something the runtime will not do, so it is
    ///     reported on its own — after the stranding check, because that one names the greater damage.
    /// </summary>
    [Test]
    public void Parse_WithADeadGateEdgeBesideALiveOne_IsRejectedNamingTheEdge()
    {
        const string DeadBesideLive = """
                                      {"schemaVersion":1,
                                       "nodes":[{"nodeKey":"research","nodeType":"Agent"},
                                                {"nodeKey":"approve","nodeType":"HumanGate"},
                                                {"nodeKey":"ship","nodeType":"Agent"},
                                                {"nodeKey":"rework","nodeType":"Agent"}],
                                       "edges":[{"from":"research","to":"approve"},
                                                {"from":"approve","to":"ship","condition":{"path":"decision","op":"eq","value":"Approve"}},
                                                {"from":"approve","to":"rework","condition":{"path":"decision","op":"eq","value":"Approved"}}]}
                                      """;

        var refusal = AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(DeadBesideLive)).Message;

        AssertEx.Contains(refusal, "The edge 'approve' → 'rework' leaves a human gate and is false for all three answers");
        AssertEx.Contains(refusal, "GRAPH-C4-1", StringComparison.Ordinal);
    }

    /// <summary>
    ///     Chain two gates and strand the downstream one, and every gate above it is stranded too. The sentence that
    ///     says "with an out-edge that can never fire" is only true of the gate that OWNS the dead edge, so an ordinal
    ///     tie-break naming 'alpha' would send the operator to fix an edge that is fine.
    /// </summary>
    [Test]
    public void Parse_WithTwoChainedGatesAndOneDeadEdge_NamesTheGateThatOwnsIt()
    {
        const string ChainedGates = """
                                    {"schemaVersion":1,
                                     "nodes":[{"nodeKey":"alpha","nodeType":"HumanGate"},
                                              {"nodeKey":"beta","nodeType":"HumanGate"},
                                              {"nodeKey":"ship","nodeType":"Agent"}],
                                     "edges":[{"from":"alpha","to":"beta"},
                                              {"from":"beta","to":"ship","condition":{"path":"decision","op":"eq","value":"Approved"}}]}
                                    """;

        var refusal = AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(ChainedGates)).Message;

        AssertEx.Contains(refusal, "Node 'beta' is a human gate with an out-edge that can never fire");
        AssertEx.Contains(refusal, "GRAPH-C4-1", StringComparison.Ordinal);
    }

    /// <summary>
    ///     The join collects what the clones produced, so it must FOLLOW the node that decomposes the work. This is the
    ///     shape that made the augmented graph cyclic: the virtual edge 'm' → 't' closes the loop 't' → 'j' → 'm' → 't'
    ///     that <see cref="DevWorkflowGraph" />'s cycle check cannot see, because it walks the authored edges only. The
    ///     fixpoint then ran over an order that was not topological and under-approximated, refusing this graph with a
    ///     GRAPH-C4-2 complaint although 'start' is a human gate on every path into 't'.
    /// </summary>
    [Test]
    public void Parse_WithAMaterializationJoiningIntoOneOfItsOwnAncestors_IsRejected()
    {
        const string JoinUpstream = """
                                    {"schemaVersion":1,
                                     "nodes":[{"nodeKey":"start","nodeType":"HumanGate"},
                                              {"nodeKey":"j","nodeType":"Join"},
                                              {"nodeKey":"t","nodeType":"Agent","requiredCapabilities":{"WriteExecute":"writes the release notes"}},
                                              {"nodeKey":"m","nodeType":"Agent",
                                               "materialization":{"templateNodeKey":"t","artifactKind":"TaskPackage","joinNodeKey":"j","maxChildren":2}}],
                                     "edges":[{"from":"start","to":"j"},{"from":"t","to":"j"},{"from":"j","to":"m"}]}
                                    """;

        var refusal = AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(JoinUpstream)).Message;

        AssertEx.Contains(refusal, "names join node 'j', which is 'm' itself or one of its ancestors");
        AssertEx.False(refusal.Contains("GRAPH-C4-2", StringComparison.Ordinal),
            "and it is refused for the shape it is, not with a gating complaint that is false.");
    }

    /// <summary>
    ///     The same rule's other half: a node naming ITSELF as its own join. Expansion wires every clone's leaf to the
    ///     join, so this one would route the clones straight back into the node that produced them.
    /// </summary>
    [Test]
    public void Parse_WithAMaterializationJoiningIntoItself_IsRejected()
    {
        const string SelfJoin = """
                                {"schemaVersion":1,
                                 "nodes":[{"nodeKey":"t","nodeType":"Agent"},
                                          {"nodeKey":"m","nodeType":"Agent",
                                           "materialization":{"templateNodeKey":"t","artifactKind":"TaskPackage","joinNodeKey":"m","maxChildren":2}}],
                                 "edges":[{"from":"t","to":"m"}]}
                                """;

        AssertEx.Contains(AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(SelfJoin)).Message,
            "names join node 'm', which is 'm' itself or one of its ancestors");
    }

    /// <summary>
    ///     The fourth step, and the one that is a rule rather than a footnote: a template VALIDATION leaf carries no
    ///     out-edge, so it lands in <c>TerminalNodeKeys</c> — and the zero-task decomposition's no-op verdict row would
    ///     then make a run read <c>Completed</c> at a key whose real tail never ran.
    /// </summary>
    [Test]
    public void Parse_WithATemplateValidationNodeThatNoEdgeLeaves_IsRejected()
    {
        const string EdgelessCheck = """
                                     {"schemaVersion":1,
                                      "nodes":[{"nodeKey":"decompose","nodeType":"Agent",
                                                "materialization":{"templateNodeKey":"implement","artifactKind":"TaskPackage","joinNodeKey":"join","maxChildren":4}},
                                               {"nodeKey":"implement","nodeType":"DevTask"},
                                               {"nodeKey":"validate","nodeType":"Tool"},
                                               {"nodeKey":"join","nodeType":"Join"}],
                                      "edges":[{"from":"decompose","to":"join"},{"from":"implement","to":"validate"}]}
                                     """;

        var refusal = AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(EdgelessCheck)).Message;

        AssertEx.Contains(refusal, "Node 'validate' validates inside the materialization template of 'decompose' and no edge leaves it");
        AssertEx.Contains(refusal, "Give it an edge to the join node 'join'");
        AssertEx.Contains(refusal, "GRAPH-C4-1", StringComparison.Ordinal);
    }

    /// <summary>
    ///     And the rule stops exactly there. The no-op verdict row is written for <c>Tool</c>/<c>Validate</c> template
    ///     nodes and for nothing else, so an edge-less <c>DevTask</c> or <c>Agent</c> template stays rowless and can
    ///     neither satisfy nor block the completion predicate — which is what the terminal-keys doc has always said.
    ///     Asking those for an out-edge would refuse the baseline decomposition shape, which has always validated.
    /// </summary>
    [Test]
    [Arguments("DevTask")]
    [Arguments("Agent")]
    public void Parse_WithATemplateNodeThatWritesNoVerdictAndNoEdgeLeaves_IsAccepted(string nodeType)
    {
        var graph = DevWorkflowGraph.Parse("""
                                           {"schemaVersion":1,
                                            "nodes":[{"nodeKey":"decompose","nodeType":"Agent",
                                                      "materialization":{"templateNodeKey":"implement","artifactKind":"TaskPackage","joinNodeKey":"join","maxChildren":4}},
                                                     {"nodeKey":"implement","nodeType":"NODETYPE"},
                                                     {"nodeKey":"join","nodeType":"Join"}],
                                            "edges":[{"from":"decompose","to":"join"}]}
                                           """.Replace("NODETYPE", nodeType, StringComparison.Ordinal));

        AssertEx.True(graph.TemplateKeys.Contains("implement"), "the template is still the template.");
    }

    /// <summary>
    ///     <c>GRAPH-C4-1</c>'s fourth step is what makes the no-op verdict row safe, so the shape it protects has to be
    ///     accepted: a template validation node wired to the join is not an end of the run.
    /// </summary>
    [Test]
    public void Parse_WithATemplateValidationNodeWiredToTheJoin_IsAccepted()
    {
        var graph = DevWorkflowGraph.Parse("""
                                           {"schemaVersion":1,
                                            "nodes":[{"nodeKey":"decompose","nodeType":"Agent",
                                                      "materialization":{"templateNodeKey":"implement","artifactKind":"TaskPackage","joinNodeKey":"join","maxChildren":4}},
                                                     {"nodeKey":"implement","nodeType":"DevTask"},
                                                     {"nodeKey":"validate","nodeType":"Tool"},
                                                     {"nodeKey":"join","nodeType":"Join"}],
                                            "edges":[{"from":"decompose","to":"join"},{"from":"implement","to":"validate"},{"from":"validate","to":"join"}]}
                                           """);

        AssertEx.False(graph.TerminalNodeKeys.Contains("validate"), "a template check the join follows is not an end of the run.");
    }

    /// <summary>
    ///     Two materializations whose virtual template edges close a loop between them. Each join is downstream of its
    ///     own materializer, so the single-materializer ancestry rule is silent and the AUTHORED graph is acyclic — only
    ///     the augmented one is not, and a fixpoint walked in a non-topological order would answer whatever the node
    ///     dictionary's order happened to give it.
    /// </summary>
    [Test]
    public void Parse_WithACycleOnlyTheMaterializationsClose_IsRejected()
    {
        const string AugmentedCycle = """
                                      {"schemaVersion":1,
                                       "nodes":[{"nodeKey":"start","nodeType":"Parallel"},
                                                {"nodeKey":"j1","nodeType":"Join"},
                                                {"nodeKey":"j2","nodeType":"Join"},
                                                {"nodeKey":"end","nodeType":"Join"},
                                                {"nodeKey":"m1","nodeType":"Agent",
                                                 "materialization":{"templateNodeKey":"t1","artifactKind":"TaskPackage","joinNodeKey":"j1","maxChildren":2}},
                                                {"nodeKey":"m2","nodeType":"Agent",
                                                 "materialization":{"templateNodeKey":"t2","artifactKind":"TaskPackage","joinNodeKey":"j2","maxChildren":2}},
                                                {"nodeKey":"t1","nodeType":"DevTask"},
                                                {"nodeKey":"t2","nodeType":"DevTask"}],
                                       "edges":[{"from":"start","to":"j1"},{"from":"start","to":"j2"},
                                                {"from":"t1","to":"j1"},{"from":"t2","to":"j2"},
                                                {"from":"j1","to":"m2"},{"from":"j2","to":"m1"},
                                                {"from":"m1","to":"end"},{"from":"m2","to":"end"}]}
                                      """;

        var refusal = AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(AugmentedCycle)).Message;

        AssertEx.Contains(refusal, "only appears once the materializations of");
        AssertEx.Contains(refusal, "'m1'", StringComparison.Ordinal);
        AssertEx.Contains(refusal, "'m2'", StringComparison.Ordinal);
    }

    /// <summary>
    ///     Two materializers sharing one template subtree. Structurally it looks legal, and it deadlocks the first time
    ///     a decomposition finds no work: both producers seed the same template key with their own verdict row under
    ///     their own operation id, the first commits, and the second is refused by the store on every tick after.
    /// </summary>
    [Test]
    public void Parse_WithTwoMaterializationsSharingOneTemplate_IsRejected()
    {
        const string SharedTemplate = """
                                      {"schemaVersion":1,
                                       "nodes":[{"nodeKey":"start","nodeType":"Parallel"},
                                                {"nodeKey":"m1","nodeType":"Agent",
                                                 "materialization":{"templateNodeKey":"v","artifactKind":"TaskPackage","joinNodeKey":"j","maxChildren":2}},
                                                {"nodeKey":"m2","nodeType":"Agent",
                                                 "materialization":{"templateNodeKey":"v","artifactKind":"TaskPackage","joinNodeKey":"j","maxChildren":2}},
                                                {"nodeKey":"v","nodeType":"Tool"},
                                                {"nodeKey":"j","nodeType":"Join"}],
                                       "edges":[{"from":"start","to":"m1"},{"from":"start","to":"m2"},
                                                {"from":"m1","to":"j"},{"from":"m2","to":"j"},{"from":"v","to":"j"}]}
                                      """;

        var refusal = AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(SharedTemplate)).Message;

        AssertEx.Contains(refusal, "Node 'v' is inside the materialization template of 'm1' and of 'm2'");
        AssertEx.Contains(refusal, "Give each decomposition a template of its own");
    }

    /// <summary>
    ///     What the rule is deliberately NOT: answer coverage. Both seeded gates carry an <c>Approve</c> edge and
    ///     nothing else, so a rejection ends the run — X10 working as designed. Reading C4-1 as "every answer has
    ///     somewhere to go" would refuse the product's own templates.
    /// </summary>
    [Test]
    public void Parse_WithAGateWhoseRejectionEndsTheRun_IsAccepted()
    {
        var graph = DevWorkflowGraph.Parse(GatedApply);

        AssertEx.False(DevWorkflowStateMachine.GateEdgeFires(graph.OutboundEdges("gate")[0], DevWorkflowDecisionKind.Reject),
            "the gate's one edge takes the approval and only the approval…");
        AssertEx.Equal(expected: 3, graph.Nodes.Count, "…and the graph validates anyway.");
    }

    /// <summary>
    ///     A <c>Gate</c> node's output document is whatever the node produced, so no definition-time reading of its
    ///     conditions can say which of them will fire. The dead-edge rule is decidable for a HUMAN gate only, and a
    ///     conditional edge out of an inline decision must stay authorable.
    /// </summary>
    [Test]
    public void Parse_WithAConditionalEdgeOutOfAnInlineGate_IsAccepted() =>
        AssertEx.Equal(expected: 3,
            DevWorkflowGraph.Parse("""
                                   {"schemaVersion":1,
                                    "nodes":[{"nodeKey":"decide","nodeType":"Gate"},{"nodeKey":"ship","nodeType":"Agent"},{"nodeKey":"stop","nodeType":"Agent"}],
                                    "edges":[{"from":"decide","to":"ship","condition":{"path":"passed","op":"eq","value":true}},
                                             {"from":"decide","to":"stop","condition":{"path":"passed","op":"eq","value":false}}]}
                                   """)
                            .Nodes.Count);

    /// <summary>
    ///     <c>GRAPH-C4-2</c>. An author who declares a write is declaring a real one, so a run must not be able to
    ///     reach the node without an operator having been asked. Y3 does not catch this: the node is an Agent, not an
    ///     apply.
    /// </summary>
    [Test]
    public void Parse_WithAnAgentDeclaringAWriteAndNoUpstreamGate_IsRejected()
    {
        var refusal = AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(DeclaredWrite(gated: false, waived: false))).Message;

        AssertEx.Contains(refusal, "Node 'release' can write outside its sandbox and a run can reach it without an operator ever being asked");
        AssertEx.Contains(refusal, "GRAPH-C4-2", StringComparison.Ordinal);
    }

    [Test]
    public void Parse_WithAnAgentDeclaringAWriteBehindAGate_IsAccepted() =>
        AssertEx.Equal(expected: 3, DevWorkflowGraph.Parse(DeclaredWrite(gated: true, waived: false)).Nodes.Count);

    /// <summary>
    ///     The escape hatch is the TEMPLATE's, written once and in the open, rather than each node quietly opting
    ///     itself out. It waives C4-2 and nothing else — Y3 still requires a gate in front of an apply node, because
    ///     approval policy here is tighten-only.
    /// </summary>
    [Test]
    public void Parse_WithAnUngatedDeclaredWriteAndTheTemplatesWaiver_IsAccepted()
    {
        var graph = DevWorkflowGraph.Parse(DeclaredWrite(gated: false, waived: true));

        AssertEx.True(graph.AllowUngatedWrites);
        AssertEx.Equal(expected: 2, graph.Nodes.Count);
    }

    /// <summary>
    ///     Ruling D8: a <c>DevTask</c> writes a worktree created under this node's own data root, and its patch reaches
    ///     the operator's repository only through an apply node a gate already stands in front of — so it is a SANDBOX
    ///     write and needs no gate of its own. Saying otherwise would reject this fixture, whose DevTask is the ENTRY
    ///     node and therefore cannot have an upstream gate at all.
    /// </summary>
    [Test]
    public void Parse_WithASandboxScopedDevTaskWrite_NeedsNoGate()
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowGraphs.FanOut);

        AssertEx.Equal("WriteExecute", Effects(graph, "implement"));
        AssertEx.Equal(DevWorkflowEffectScope.Sandbox, DevWorkflowGraph.ScopeOf(graph.Nodes["implement"]));
    }

    /// <summary>
    ///     <c>GRAPH-C4-3</c> at an <c>Any</c> convergence, where <c>Combine</c> is AND: only one branch may have run,
    ///     so a validation on one of them assures nothing. Driven down the branch that skips the check, the apply would
    ///     integrate patches nothing judged.
    /// </summary>
    [Test]
    public void Parse_WithAnApplyReachableByABranchThatSkipsValidation_IsRejected()
    {
        const string HalfValidated = """
                                     {"schemaVersion":1,
                                      "nodes":[{"nodeKey":"decide","nodeType":"Gate"},
                                               {"nodeKey":"check","nodeType":"Tool"},
                                               {"nodeKey":"straightthrough","nodeType":"Agent"},
                                               {"nodeKey":"merge","nodeType":"Join","joinPolicy":"Any"},
                                               {"nodeKey":"approval","nodeType":"HumanGate"},
                                               {"nodeKey":"integrate","nodeType":"Tool","toolMode":"Apply"}],
                                      "edges":[{"from":"decide","to":"check","condition":{"path":"passed","op":"eq","value":true}},
                                               {"from":"decide","to":"straightthrough","condition":{"path":"passed","op":"eq","value":false}},
                                               {"from":"check","to":"merge"},
                                               {"from":"straightthrough","to":"merge"},
                                               {"from":"merge","to":"approval"},
                                               {"from":"approval","to":"integrate","condition":{"path":"decision","op":"eq","value":"Approve"}}]}
                                     """;

        var refusal = AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(HalfValidated)).Message;

        AssertEx.Contains(refusal, "Node 'integrate' applies approved patches and a run can reach it without any validation node having run");
        AssertEx.Contains(refusal, "GRAPH-C4-3", StringComparison.Ordinal);
    }

    /// <summary>
    ///     The entry node's own property COUNTS. Initialising the fixpoint to false on the entry erases it, and this
    ///     shape — the gate guarding the write IS the entry — would be refused with a 400 naming no real fault.
    /// </summary>
    [Test]
    public void Parse_WithAnEntryHumanGateAheadOfADeclaredWrite_IsAccepted()
    {
        const string EntryGate = """
                                 {"schemaVersion":1,
                                  "nodes":[{"nodeKey":"approval","nodeType":"HumanGate"},
                                           {"nodeKey":"release","nodeType":"Agent","requiredCapabilities":{"WriteExecute":"runs the release script"}}],
                                  "edges":[{"from":"approval","to":"release"}]}
                                 """;

        AssertEx.Equal(expected: 2, DevWorkflowGraph.Parse(EntryGate).Nodes.Count);
    }

    /// <summary>
    ///     The same fix for the other invariant: the validation ahead of the gate and the apply is itself the entry
    ///     node, and it is what assures the apply. Both shapes are valid and both were 400s under a false-initialised
    ///     entry.
    /// </summary>
    [Test]
    public void Parse_WithAnEntryValidationAheadOfTheGateAndTheApply_IsAccepted() =>
        AssertEx.Equal(expected: 3, DevWorkflowGraph.Parse(GatedApply).Nodes.Count, "'check' is the entry AND the validation the apply is assured by.");

    /// <summary>
    ///     <c>Combine</c> is keyed on <c>joinPolicy</c> and never on node TYPE. This fixture is the shape that catches
    ///     the difference: <c>verify</c> is an AGENT with two inbound edges and therefore an implicit <c>All</c>, so a
    ///     rule keyed on <c>NodeType == Join</c> would read it as AND and reject the product's own template.
    /// </summary>
    [Test]
    public void Parse_WithAVerificationBehindTwoInboundEdges_IsAcceptedBecauseCombineReadsTheJoinPolicy()
    {
        var graph = DevWorkflowGraph.Parse(DevWorkflowGraphs.DecompositionWithVerification);

        AssertEx.Equal(expected: 2, graph.InboundEdges("verify").Count);
        AssertEx.Equal(DevWorkflowJoinPolicy.All, graph.Nodes["verify"].JoinPolicy, "an Agent node carries a joinPolicy too, and it defaults to All.");
    }

    /// <summary>
    ///     The byte-identical pin. Both graphs the product ships AND every prior revision it still keeps, because a run
    ///     pinned on one of those is re-parsed by the graph cache long after the seeder has upgraded the definition row
    ///     — so a revision the product no longer ships must satisfy every invariant here as well.
    ///     <para>
    ///         The revisions are read off <c>FeatureDevelopmentPriorRevisions</c> rather than listed one at a time, so
    ///         the next one kept cannot quietly fall out of this pin the way the second one did.
    ///     </para>
    /// </summary>
    [Test]
    public void SeededTemplatesStillValidate()
    {
        var graphs = new List<(string Name, string Json)>
        {
            (nameof(DevWorkflowDefinitionSeeder.ResearchPlanApprovalGraph), DevWorkflowDefinitionSeeder.ResearchPlanApprovalGraph),
            (nameof(DevWorkflowDefinitionSeeder.FeatureDevelopmentGraph), DevWorkflowDefinitionSeeder.FeatureDevelopmentGraph)
        };
        graphs.AddRange(DevWorkflowDefinitionSeeder.FeatureDevelopmentPriorRevisions
                                                   .Select(static (json, index) => ($"FeatureDevelopmentPriorRevisions[{index}]", json)));

        AssertEx.True(graphs.Count >= 4, $"every kept revision has to reach this pin; it found {graphs.Count} graphs.");
        foreach (var (name, json) in graphs)
        {
            AssertEx.NotEmpty(DevWorkflowGraph.Parse(json).Nodes, $"the seeded graph '{name}' must satisfy every invariant this validator holds.");
        }
    }

    /// <summary>
    ///     And every test fixture, found by REFLECTION rather than listed, so a fixture added later cannot quietly fall
    ///     out of coverage — which is how the two added by the follow-up round are covered here without an edit.
    /// </summary>
    [Test]
    public void EveryFixtureGraphStillValidates()
    {
        var fixtures = typeof(DevWorkflowGraphs).GetFields(BindingFlags.Public | BindingFlags.Static)
                                                .Where(static field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
                                                .ToList();
        AssertEx.True(fixtures.Count >= 24, $"the reflection has to actually find the fixtures; it found {fixtures.Count}.");

        var refused = new List<string>();
        foreach (var fixture in fixtures)
        {
            try
            {
                _ = DevWorkflowGraph.Parse((string)fixture.GetRawConstantValue()!);
            }
            catch (DevWorkflowValidationException exception)
            {
                refused.Add($"{fixture.Name}: {exception.Message}");
            }
        }

        AssertEx.Empty(refused, $"every fixture graph must still validate unchanged:{Environment.NewLine}{string.Join(Environment.NewLine, refused)}");
    }

    /// <summary>
    ///     <c>research → release</c>, with the write DECLARED on the second node and the gate optionally between them.
    ///     The waiver rides the graph ROOT, which is where a template says once and in writing that it means it.
    /// </summary>
    private static string DeclaredWrite(bool gated, bool waived)
    {
        const string Ungated = """
                               {"schemaVersion":1,
                                "nodes":[{"nodeKey":"research","nodeType":"Agent"},
                                         {"nodeKey":"release","nodeType":"Agent","requiredCapabilities":{"WriteExecute":"runs the release script"}}],
                                "edges":[{"from":"research","to":"release"}]}
                               """;
        const string Gated = """
                             {"schemaVersion":1,
                              "nodes":[{"nodeKey":"research","nodeType":"Agent"},
                                       {"nodeKey":"approval","nodeType":"HumanGate"},
                                       {"nodeKey":"release","nodeType":"Agent","requiredCapabilities":{"WriteExecute":"runs the release script"}}],
                              "edges":[{"from":"research","to":"approval"},{"from":"approval","to":"release"}]}
                             """;

        var graph = gated ? Gated : Ungated;
        return waived
            ? graph.Replace("""{"schemaVersion":1,""", """{"schemaVersion":1,"allowUngatedWrites":true,""", StringComparison.Ordinal)
            : graph;
    }

    private static string Effects(DevWorkflowGraph graph, string nodeKey) =>
        string.Join(", ", DevWorkflowGraph.Effects(graph.Nodes[nodeKey]).Select(static effect => effect.ToString()).Order(StringComparer.Ordinal));

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

    // Enum.TryParse accepts a NUMERIC token, so without a by-name rule "3" would parse into a node type no member has
    // and reach the per-type config table as a missing key — a 500 for a document an author wrote.
    [Arguments("""{"nodes":[{"nodeKey":"a","nodeType":"3"}],"edges":[]}""", "needs a 'nodeType'")]
    [Arguments("""{"nodes":[{"nodeKey":"a","nodeType":"-1"}],"edges":[]}""", "needs a 'nodeType'")]
    [Arguments("""{"nodes":[{"nodeKey":"a","nodeType":"Agent","joinPolicy":"1"}],"edges":[]}""", "unknown 'joinPolicy'")]
    [Arguments("""{"nodes":[{"nodeKey":"a","nodeType":"Agent"},{"nodeKey":"a","nodeType":"Gate"}],"edges":[]}""", "twice")]
    [Arguments("""{"nodes":[{"nodeKey":"a","nodeType":"Agent"}],"edges":[{"from":"a","to":"ghost"}]}""", "does not declare")]
    [Arguments("""{"nodes":[{"nodeKey":"a","nodeType":"Agent","maxAttempts":0}],"edges":[]}""", "must be positive")]
    [Arguments("""{"nodes":[{"nodeKey":"a","nodeType":"Agent","retryDelaySeconds":-1}],"edges":[]}""", "cannot be negative")]
    [Arguments("""{"nodes":[{"nodeKey":"a","nodeType":"Agent","joinPolicy":"Maybe"}],"edges":[]}""", "unknown 'joinPolicy'")]
    [Arguments("""{"nodes":[{"nodeKey":"a","nodeType":"Agent","agentDefinitionId":"not-a-guid"}],"edges":[]}""", "not a GUID")]
    [Arguments("""{"nodes":[{"nodeKey":"a","nodeType":"Tool","validationCommandIds":"build"}],"edges":[]}""", "array of strings")]
    [Arguments("""{"nodes":[{"nodeKey":"a","nodeType":"Agent","reasoningEffort":"exhaustive"}],"edges":[]}""", "unknown 'reasoningEffort'")]
    [Arguments("""{"nodes":[{"nodeKey":"a","nodeType":"Agent","reasoningEffort":"xhigh"}],"edges":[]}""", "unknown 'reasoningEffort'")]
    [Arguments("""{"nodes":[{"nodeKey":"a","nodeType":"Agent","requiredCapabilities":["WriteExecute"]}],"edges":[]}""", "must be an object")]
    [Arguments("""{"nodes":[{"nodeKey":"a","nodeType":"Agent","requiredCapabilities":{"Sorcery":"why"}}],"edges":[]}""", "unknown capability 'Sorcery'")]
    [Arguments("""{"nodes":[{"nodeKey":"a","nodeType":"Agent","requiredCapabilities":{"WriteExecute":true}}],"edges":[]}""", "needs a reason")]
    [Arguments("""{"nodes":[{"nodeKey":"a","nodeType":"Agent","requiredCapabilities":{"WriteExecute":""}}],"edges":[]}""", "needs a reason")]
    [Arguments("""{"nodes":[{"nodeKey":"a","nodeType":"Tool","requiredCapabilities":{"WriteExecute":"rewrites the working tree"}}],"edges":[]}""",
        "is a Tool node, and only an Agent node's reach is declared")]
    [Arguments("""{"nodes":[{"nodeKey":"a","nodeType":"HumanGate","requiredCapabilities":{"Network":"asks a webhook"}}],"edges":[]}""",
        "is a HumanGate node, and only an Agent node's reach is declared")]
    [Arguments("""{"nodes":[{"nodeKey":"a","nodeType":"Agent","maxLoopIterations":3}],"edges":[]}""", "no 'retryTarget'")]
    [Arguments("""{"nodes":[{"nodeKey":"a","nodeType":"Agent"},{"nodeKey":"b","nodeType":"Tool","retryTarget":"a","maxLoopIterations":0}],"edges":[{"from":"a","to":"b"}]}""",
        "must be positive")]
    [Arguments("""{"nodes":[{"nodeKey":"a","nodeType":"Agent"},{"nodeKey":"b","nodeType":"Tool","retryTarget":"a","maxLoopIterations":1.5}],"edges":[{"from":"a","to":"b"}]}""",
        "must be a whole number")]
    [Arguments("""{"allowUngatedWrites":"true","nodes":[{"nodeKey":"a","nodeType":"Agent"}],"edges":[]}""", "must be true or false")]
    public void Parse_RejectsAGraphItCannotRoute(string json, string expectedMessage) =>
        AssertEx.Contains(AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(json)).Message, expectedMessage);

    /// <summary>
    ///     Every <c>GRAPH-C4-4</c> parse refusal names the rule, the shared number parsers' own complaints included —
    ///     the id is what the operator quotes, and a bare "must be positive" would be the one cap message that could
    ///     not be traced back to the invariant that raised it.
    /// </summary>
    [Test]
    [Arguments("""{"nodes":[{"nodeKey":"a","nodeType":"Agent","maxLoopIterations":3}],"edges":[]}""")]
    [Arguments("""{"nodes":[{"nodeKey":"a","nodeType":"Agent"},{"nodeKey":"b","nodeType":"Tool","retryTarget":"a","maxLoopIterations":0}],"edges":[{"from":"a","to":"b"}]}""")]
    [Arguments("""{"nodes":[{"nodeKey":"a","nodeType":"Agent"},{"nodeKey":"b","nodeType":"Tool","retryTarget":"a","maxLoopIterations":1.5}],"edges":[{"from":"a","to":"b"}]}""")]
    public void Parse_WithAnUnusableLoopCap_NamesTheInvariant(string json) =>
        AssertEx.Contains(AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraph.Parse(json)).Message, "GRAPH-C4-4", StringComparison.Ordinal);

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
