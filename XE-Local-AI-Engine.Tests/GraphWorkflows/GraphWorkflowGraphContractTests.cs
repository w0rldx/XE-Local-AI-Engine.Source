namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The questions the API asks about a stored graph. They are answered by the runtime's own parser, table and
///     evaluator, so these tests are about the QUESTIONS being the right ones — the answers are proven where those
///     live.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class GraphWorkflowGraphContractTests
{
    /// <summary>
    ///     The panel is driven by this list, and the endpoint refuses everything outside it with a 409. A status that
    ///     is not decidable at all must therefore advertise NOTHING.
    /// </summary>
    [Test]
    [Arguments(GraphWorkflowNodeRunStatus.WaitingForApproval, "Approve,Reject")]
    [Arguments(GraphWorkflowNodeRunStatus.Pending, "")]
    [Arguments(GraphWorkflowNodeRunStatus.Queued, "")]
    [Arguments(GraphWorkflowNodeRunStatus.Running, "")]
    [Arguments(GraphWorkflowNodeRunStatus.Succeeded, "")]
    [Arguments(GraphWorkflowNodeRunStatus.Failed, "")]
    [Arguments(GraphWorkflowNodeRunStatus.Skipped, "")]
    [Arguments(GraphWorkflowNodeRunStatus.Cancelled, "")]
    public void AllowedDecisions_OffersOnlyWhatTheDecideEndpointWouldAccept(GraphWorkflowNodeRunStatus status, string expected) =>
        AssertEx.Equal(expected,
            string.Join(",", GraphWorkflowGraphContract.AllowedDecisions(status)),
            $"a {status} node run must advertise exactly the decisions the runtime can take from it.");

    /// <summary>
    ///     Made honest at the moment of the click: a rejection with nowhere to go ends the run, and the confirm dialog
    ///     can only say so because the server evaluated the node's real out-edges first.
    /// </summary>
    [Test]
    public void HasRejectBranch_IsTrueOnlyWhenAnOutEdgeAcceptsTheRejection()
    {
        AssertEx.True(GraphWorkflowGraphContract.HasRejectBranch(GraphWorkflowGraphs.PauseTwoDecisions, "review"));
        AssertEx.False(GraphWorkflowGraphContract.HasRejectBranch(GraphWorkflowGraphs.PauseTwoDecisions, "shipped"),
            "an End node has nowhere for anything to go.");
        AssertEx.False(GraphWorkflowGraphContract.HasRejectBranch(GraphWorkflowGraphs.TwoEnds, "check"),
            "a node whose branches read a different path than a decision routes on ends the run just as a terminal one does.");
        AssertEx.True(GraphWorkflowGraphContract.HasRejectBranch(GraphWorkflowGraphs.StartAgentEnd, "analyze"),
            "an unconditional out-edge accepts every answer, this one included.");
    }

    /// <summary>Save time and run start share ONE parser, so a graph accepted here is one that will start.</summary>
    [Test]
    public void ValidateAndCountNodes_AnswersTheCountAndRefusesWhatTheDispatcherCouldNotRoute()
    {
        AssertEx.Equal(expected: 3, GraphWorkflowGraphContract.ValidateAndCountNodes(GraphWorkflowGraphs.StartAgentEnd, maxNodes: 200));

        var refusal = AssertEx.Throws<GraphWorkflowValidationException>(() =>
            GraphWorkflowGraphContract.ValidateAndCountNodes("""{"schemaVersion":1,"nodes":[{"key":"a","kind":"Nonsense"}],"edges":[]}""", maxNodes: 200));

        AssertEx.Contains(refusal.Message, "'kind'");
    }

    /// <summary>
    ///     The cap is an option, so it is READ here rather than in the parser — which stays testable without a
    ///     container.
    /// </summary>
    [Test]
    public void ValidateAndCountNodes_OverTheNodeCap_IsRefused()
    {
        var refusal = AssertEx.Throws<GraphWorkflowValidationException>(() =>
            GraphWorkflowGraphContract.ValidateAndCountNodes(GraphWorkflowGraphs.StartAgentEnd, maxNodes: 2));

        AssertEx.Contains(refusal.Message, "more than the 2 one definition may carry");
        AssertEx.Equal(expected: 3, GraphWorkflowGraphContract.ValidateAndCountNodes(GraphWorkflowGraphs.StartAgentEnd, maxNodes: 3), "the cap is inclusive.");
    }

    /// <summary>
    ///     A chain of thousands of minimal nodes fits the 1 MiB request body, so the body limit is not what keeps a
    ///     definition inside the cap. This pins the refusal itself; the ORDER it fires in is pinned by
    ///     <see cref="ValidateAndCountNodes_OverTheCapWithAMalformedNode_RefusesOnTheCapRatherThanTheNode" />.
    /// </summary>
    [Test]
    public void ValidateAndCountNodes_FarOverTheNodeCap_IsRefusedBeforeTheGraphIsWalked()
    {
        var refusal = AssertEx.Throws<GraphWorkflowValidationException>(() =>
            GraphWorkflowGraphContract.ValidateAndCountNodes(GraphWorkflowGraphs.Chain(nodeCount: 9_000), maxNodes: 200));

        AssertEx.Contains(refusal.Message, "more than the 200 one definition may carry");
        AssertEx.Equal(expected: 200,
            GraphWorkflowGraphContract.ValidateAndCountNodes(GraphWorkflowGraphs.Chain(nodeCount: 200), maxNodes: 200),
            "a chain as deep as the cap allows is still a graph that validates — the cap refuses size, not depth.");
    }

    /// <summary>
    ///     The cap fires BEFORE a node is read, which is the whole of what it buys: everything the parse does after
    ///     counting is proportional to how many nodes there are. Only that ordering can produce the cap message here —
    ///     the last node's kind is one <c>ParseNodes</c> refuses on its own, so a cap checked after the parse would
    ///     answer with the node's refusal instead.
    /// </summary>
    [Test]
    public void ValidateAndCountNodes_OverTheCapWithAMalformedNode_RefusesOnTheCapRatherThanTheNode()
    {
        var refusal = AssertEx.Throws<GraphWorkflowValidationException>(() =>
            GraphWorkflowGraphContract.ValidateAndCountNodes(GraphWorkflowGraphs.Chain(nodeCount: 201).Replace("\"kind\": \"End\"", "\"kind\": \"Nonsense\"", StringComparison.Ordinal),
                maxNodes: 200));

        AssertEx.Contains(refusal.Message, "more than the 200 one definition may carry",
            message: "a cap checked after the parse would have answered with the last node's unknown 'kind' instead.");
    }

    [Test]
    public void ToolNodeNames_ReadsTheToolGateItsInput() =>
        AssertEx.Equal("read_file, list_files", string.Join(", ", GraphWorkflowGraphContract.ToolNodeNames(GraphWorkflowGraphs.ToolNode)));
}
