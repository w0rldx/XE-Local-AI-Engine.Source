namespace XE_Local_AI_Engine.Tests.GraphWorkflows.Import;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Import;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The mapper's answer, actually RUN. The mapper tests pin the wiring and the parser accepts it, but neither of
///     them can say what the second agent reads — and that is the whole defect the S4 live round found.
///     <para>
///         A private agent host: the graph here is the mapper's own output rather than a hand-authored one, and its
///         scripted prompt must not be shared with another class's runs.
///     </para>
/// </summary>
[Category(TestCategories.Integration)]
public sealed class CanvasWorkflowImportRunTests
{
    /// <summary>The scripted fragment: the first agent's instructions, which is what its seed turn carries.</summary>
    private const string DrafterInstructions = "Prepare the change.";

    private const string DraftedAnswer = "the release note the reviewer signed off on";

    /// <summary>
    ///     An imported <c>Agent -> Pause -> Agent</c> run, approved. The second agent's input document has to carry the
    ///     first agent's output under <c>upstream.agent-1</c>: without the context edge its only satisfied predecessor
    ///     is the pause, and it would read the approval alone.
    /// </summary>
    [Test]
    public async Task AnImportedPause_StillHandsTheNodeAfterItTheAnswerItIsApproving()
    {
        await using var harness = GraphWorkflowHarness.PrivateAgentHost();
        harness.Invocations.Script(DrafterInstructions, new GraphWorkflowScriptedTurn { Text = DraftedAnswer });

        var graphJson = CanvasWorkflowImport.MapGraph(CanvasGraphs.AgentAcrossPause).Document.ToJsonString();
        var runId = await harness.StartRunAsync(graphJson);

        await harness.AdvanceUntilAsync(runId,
                         async () => (await harness.ReadRunAsync(runId)).Status == GraphWorkflowRunStatus.WaitingForApproval,
                         "the imported run never reached its pause");

        _ = await harness.DecideAsync(runId, "pause-1", Guid.NewGuid(), GraphWorkflowDecisionKind.Approve);
        await harness.AdvanceUntilAsync(runId,
                         async () => (await harness.ReadNodeRunAsync(runId, "agent-2")).InputJson is not null,
                         "the second agent never ran after the approval");

        var input = JsonDocument.Parse(AssertEx.NotNull((await harness.ReadNodeRunAsync(runId, "agent-2")).InputJson,
            "an agent that ran wrote the document it read.")).RootElement;

        AssertEx.Contains(input.GetProperty("upstream").GetProperty("agent-1").ToString(), DraftedAnswer);
        // An upstream entry is the node's whole ENVELOPE — { status, attempt, branch, output } — so the decision sits
        // one level in, exactly where the pause's own out-edge condition reads it as 'output.decision'.
        AssertEx.Equal("Approve", input.GetProperty("upstream").GetProperty("pause-1").GetProperty("output").GetProperty("decision").GetString(),
            "the pause is still a predecessor: the context edge adds to the approval rather than replacing it.");
        AssertEx.Contains(input.GetProperty("input").ToString(),
            DraftedAnswer,
            StringComparison.Ordinal,
            "with two satisfied predecessors 'input' is the upstream map, so the content is reachable there too.");
    }
}
