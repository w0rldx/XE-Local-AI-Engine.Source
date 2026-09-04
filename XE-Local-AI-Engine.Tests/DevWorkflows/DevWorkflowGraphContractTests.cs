namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The three questions the API asks about a stored graph. They are answered by the runtime's own parser, table and
///     evaluator, so these tests are about the QUESTIONS being the right ones — the answers are proven where those
///     live.
/// </summary>
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
    ///     X10 made honest at the moment of the click: a rejection with nowhere to go ends the run, and the confirm
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
        AssertEx.Equal(expected: 2, DevWorkflowGraphContract.ValidateAndCountNodes(TerminalGate));

        var refusal = AssertEx.Throws<DevWorkflowValidationException>(() =>
            DevWorkflowGraphContract.ValidateAndCountNodes("""{"schemaVersion":1,"nodes":[{"nodeKey":"a","nodeType":"Nonsense"}],"edges":[]}"""));

        AssertEx.Contains(refusal.Message, "'nodeType'");
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
