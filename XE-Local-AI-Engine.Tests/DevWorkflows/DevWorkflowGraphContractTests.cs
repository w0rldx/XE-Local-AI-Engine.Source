namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The three questions the API asks about a stored graph. They are answered by the runtime's own parser, table and
///     evaluator, so these tests are about the QUESTIONS being the right ones — the answers are proven where those
///     live.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class DevWorkflowGraphContractTests
{
    private const string TerminalGate = """
                                        {"schemaVersion":1,
                                         "nodes":[{"nodeKey":"research","nodeType":"Agent"},{"nodeKey":"approval","nodeType":"HumanGate"}],
                                         "edges":[{"from":"research","to":"approval"}]}
                                        """;

    private const string BranchingGate = """
                                         {"schemaVersion":1,
                                          "nodes":[{"nodeKey":"research","nodeType":"Agent"},{"nodeKey":"approval","nodeType":"HumanGate"},
                                                   {"nodeKey":"ship","nodeType":"Agent"},{"nodeKey":"rework","nodeType":"Agent"}],
                                          "edges":[{"from":"research","to":"approval"},
                                                   {"from":"approval","to":"ship","condition":{"path":"decision","op":"eq","value":"Approve"}},
                                                   {"from":"approval","to":"rework","condition":{"path":"decision","op":"eq","value":"Reject"}}]}
                                         """;

    private const string ApproveOnlyGate = """
                                           {"schemaVersion":1,
                                            "nodes":[{"nodeKey":"research","nodeType":"Agent"},{"nodeKey":"approval","nodeType":"HumanGate"},
                                                     {"nodeKey":"ship","nodeType":"Agent"}],
                                            "edges":[{"from":"research","to":"approval"},
                                                     {"from":"approval","to":"ship","condition":{"path":"decision","op":"eq","value":"Approve"}}]}
                                           """;

    /// <summary>
    ///     The panel is driven by this list, and the endpoint refuses everything outside it with a 409. A status that
    ///     is not decidable at all must therefore advertise NOTHING — a <c>Running</c> node run offering five answers
    ///     would be five buttons that each answer "conflict".
    /// </summary>
    [Test]

    // A gate takes its three answers and nothing else. Skip belongs to the interventions: offering it here would be a
    // button for walking past an approval instead of giving one.
    [Arguments(DevWorkflowNodeRunStatus.WaitingForApproval, "Approve,Reject,RequestChanges")]
    [Arguments(DevWorkflowNodeRunStatus.Blocked, "Retry,Skip,Abandon")]
    [Arguments(DevWorkflowNodeRunStatus.Pending, "")]
    [Arguments(DevWorkflowNodeRunStatus.Queued, "")]
    [Arguments(DevWorkflowNodeRunStatus.Running, "")]
    [Arguments(DevWorkflowNodeRunStatus.Succeeded, "")]
    [Arguments(DevWorkflowNodeRunStatus.Failed, "")]
    [Arguments(DevWorkflowNodeRunStatus.Skipped, "")]
    [Arguments(DevWorkflowNodeRunStatus.Cancelled, "")]
    public void AllowedDecisions_OffersOnlyWhatTheDecisionEndpointWouldAccept(DevWorkflowNodeRunStatus status, string expected) =>
        AssertEx.Equal(expected,
            string.Join(",", DevWorkflowGraphContract.AllowedDecisions(status)),
            $"a {status} node run must advertise exactly the decisions the runtime can take from it.");

    /// <summary>
    ///     Casing is normalised on the way in; anything the parser would refuse travels untouched so that the refusal
    ///     is the parser's to give. A NUMERIC token is the case that made this by-name: <c>Enum.TryParse</c> reads
    ///     <c>"1"</c> as <c>Apply</c>, and the node would be stored running in the mode nobody wrote.
    /// </summary>
    [Test]
    [Arguments("apply", "Apply")]
    [Arguments("APPLY", "Apply")]
    [Arguments("Validate", "Validate")]
    [Arguments("1", "1")]
    [Arguments("-1", "-1")]
    [Arguments("sorcery", "sorcery")]
    [Arguments(null, null)]
    public void CanonicalToolMode_NormalisesCasingAndRewritesNothingElse(string? raw, string? expected) =>
        AssertEx.Equal<string?>(expected, DevWorkflowGraphContract.CanonicalToolMode(raw));

    /// <summary>
    ///     The rejection outcome made honest at the moment of the click: a rejection with nowhere to go ends the run, and the confirm
    ///     dialog can only say so because the server evaluated the gate's real out-edges first.
    /// </summary>
    [Test]
    public void HasRejectBranch_IsTrueOnlyWhenAnOutEdgeAcceptsTheRejection()
    {
        AssertEx.False(DevWorkflowGraphContract.HasRejectBranch(TerminalGate, "approval"), "a terminal gate has nowhere for a rejection to go.");
        AssertEx.True(DevWorkflowGraphContract.HasRejectBranch(BranchingGate, "approval"));
        AssertEx.False(DevWorkflowGraphContract.HasRejectBranch(ApproveOnlyGate, "approval"),
            "a gate that HAS branches but none that take a rejection ends the run just as a terminal one does.");
        AssertEx.True(DevWorkflowGraphContract.HasRejectBranch(BranchingGate, "research"),
            "an unconditional out-edge accepts every answer, this one included.");
    }

    /// <summary>Save time and run start share ONE parser, so a graph accepted here is one that will start.</summary>
    [Test]
    public void ValidateAndCountNodes_AnswersTheCountAndRefusesWhatTheDispatcherCouldNotRoute()
    {
        AssertEx.Equal(expected: 2, DevWorkflowGraphContract.ValidateAndCountNodes(TerminalGate, maxNodes: 500));

        var refusal = AssertEx.Throws<DevWorkflowValidationException>(() =>
            DevWorkflowGraphContract.ValidateAndCountNodes("""{"schemaVersion":1,"nodes":[{"nodeKey":"a","nodeType":"Nonsense"}],"edges":[]}""", maxNodes: 500));

        AssertEx.Contains(refusal.Message, "'nodeType'");
    }

    /// <summary>
    ///     The cap is an option, so it is READ here rather than in the parser — which stays testable without a
    ///     container — and it is INCLUSIVE, so a definition of exactly the cap saves.
    /// </summary>
    [Test]
    public void ValidateAndCountNodes_OverTheNodeCap_IsRefused()
    {
        var refusal = AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraphContract.ValidateAndCountNodes(TerminalGate, maxNodes: 1));

        AssertEx.Contains(refusal.Message, "more than the 1 one definition may carry");
        AssertEx.Equal(expected: 2, DevWorkflowGraphContract.ValidateAndCountNodes(TerminalGate, maxNodes: 2), "the cap is inclusive.");
    }

    /// <summary>
    ///     A chain of thousands of minimal nodes fits the request body cap, so the body limit is not what keeps a
    ///     definition inside the node cap. This pins the refusal itself; the ORDER it fires in is pinned by
    ///     <see cref="ValidateAndCountNodes_OverTheCapWithNothingButMalformedNodes_RefusesOnTheCap" />.
    /// </summary>
    [Test]
    public void ValidateAndCountNodes_FarOverTheNodeCap_IsRefusedBeforeTheGraphIsWalked()
    {
        var refusal = AssertEx.Throws<DevWorkflowValidationException>(() =>
            DevWorkflowGraphContract.ValidateAndCountNodes(DevWorkflowGraphs.Chain(nodeCount: 9_000), maxNodes: 500));

        AssertEx.Contains(refusal.Message, "more than the 500 one definition may carry");
        AssertEx.Equal(expected: 500,
            DevWorkflowGraphContract.ValidateAndCountNodes(DevWorkflowGraphs.Chain(nodeCount: 500), maxNodes: 500),
            "a chain as deep as the cap allows is still a graph that validates — the cap refuses size, not depth.");
    }

    /// <summary>
    ///     The cap fires BEFORE a node is read, which is the whole of what it buys: everything the parse does after
    ///     counting is proportional to how many nodes there are. Only that ordering can produce the cap message here —
    ///     EVERY declared node names a type the parser refuses on its own, so a cap checked after the parse could only
    ///     ever answer with the first node's complaint.
    /// </summary>
    [Test]
    public void ValidateAndCountNodes_OverTheCapWithNothingButMalformedNodes_RefusesOnTheCap()
    {
        var nodes = string.Join(",", Enumerable.Range(0, 501).Select(static index => $$"""{"nodeKey":"n{{index}}","nodeType":"Nonsense"}"""));
        var graphJson = $$"""{"schemaVersion":1,"nodes":[{{nodes}}],"edges":[]}""";

        var refusal = AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowGraphContract.ValidateAndCountNodes(graphJson, maxNodes: 500));

        AssertEx.Contains(refusal.Message, "The workflow graph declares 501 nodes, more than the 500 one definition may carry",
            message: "a cap checked after the parse would have answered with the first node's unknown 'nodeType' instead.");
    }

    /// <summary>
    ///     Everything the product ships stays saveable under the cap a node ships with — the two seeded templates and
    ///     every prior revision the seeder still keeps, since a definition row holding one of those is re-saved by an
    ///     operator's next edit.
    /// </summary>
    [Test]
    public void EverySeededGraph_FitsUnderTheShippedNodeCap()
    {
        var cap = new DevWorkflowOptions().MaxNodesPerDefinition;
        var graphs = new List<(string Name, string Json)>
        {
            (nameof(DevWorkflowDefinitionSeeder.ResearchPlanApprovalGraph), DevWorkflowDefinitionSeeder.ResearchPlanApprovalGraph),
            (nameof(DevWorkflowDefinitionSeeder.FeatureDevelopmentGraph), DevWorkflowDefinitionSeeder.FeatureDevelopmentGraph)
        };
        graphs.AddRange(DevWorkflowDefinitionSeeder.FeatureDevelopmentPriorRevisions
                                                   .Select(static (json, index) => ($"FeatureDevelopmentPriorRevisions[{index}]", json)));

        AssertEx.Equal(expected: 500, cap, "the shipped cap is what these are held to.");
        AssertEx.True(graphs.Count >= 4, $"every kept revision has to reach this pin; it found {graphs.Count} graphs.");
        foreach (var (name, json) in graphs)
        {
            AssertEx.True(DevWorkflowGraphContract.ValidateAndCountNodes(json, cap) is > 0 and <= 500, $"the seeded graph '{name}' must fit under the shipped cap.");
        }
    }

    /// <summary>
    ///     The editor's badge row, answered by the parser. The invariants refuse a save on these effects, so an editor
    ///     computing its own set would show badges that disagree with the 400 the operator gets — which is the drift
    ///     this class exists to prevent.
    /// </summary>
    [Test]
    public void EffectsOf_AnswersWhatEachNodeCanChange()
    {
        const string Declared = """
                                {"schemaVersion":1,"allowUngatedWrites":true,
                                 "nodes":[{"nodeKey":"research","nodeType":"Agent"},
                                          {"nodeKey":"release","nodeType":"Agent","requiredCapabilities":{"WriteExecute":"runs the release script"}},
                                          {"nodeKey":"check","nodeType":"Tool","validationCommandIds":["git_status"]},
                                          {"nodeKey":"approval","nodeType":"HumanGate"}],
                                 "edges":[{"from":"research","to":"release"},{"from":"release","to":"check"},{"from":"check","to":"approval"}]}
                                """;

        var effects = DevWorkflowGraphContract.EffectsOf(Declared);

        AssertEx.Empty(effects["research"], "an agent that declares nothing carries nothing.");
        AssertEx.Equal("WriteExecute", string.Join(", ", effects["release"]));
        AssertEx.Equal("ReadLocal", string.Join(", ", effects["check"]));
        AssertEx.Empty(effects["approval"], "a gate routes; it does not act.");
    }

    /// <summary>
    ///     Empty for a graph nothing could route, exactly as <see cref="DevWorkflowGraphContract.TemplateNodeKeys" />
    ///     is and for the same reason: this is a read path, and the run whose pinned graph is unroutable is the one an
    ///     operator most needs to be able to open.
    /// </summary>
    [Test]
    public void EffectsOf_AnswersEmptyForAGraphNothingCouldRoute() =>
        AssertEx.Empty(DevWorkflowGraphContract.EffectsOf("""{"schemaVersion":1,"nodes":[{"nodeKey":"a","nodeType":"Nonsense"}],"edges":[]}"""));
}
