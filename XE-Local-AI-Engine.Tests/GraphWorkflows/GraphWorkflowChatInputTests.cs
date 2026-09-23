namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The <c>ChatInput</c> node over the real command surface and store: a run parked on the chat user's next message.</summary>
/// <remarks>It rides the pause lane; pinned here is where it differs, and that replay and restart survival hold for it too.</remarks>
[Category(TestCategories.Integration)]
public sealed class GraphWorkflowChatInputTests
{
    [ClassDataSource<GraphWorkflowHostFixture>(Shared = SharedType.PerClass)]
    public required GraphWorkflowHostFixture Host { get; init; }

    [Test]
    public async Task ADispatchedChatInput_ParksTheRunAndAsksForAnAnswer()
    {
        await using var harness = new GraphWorkflowHarness(Host);

        var runId = await ParkedRunAsync(harness);

        var nodeRun = await harness.ReadNodeRunAsync(runId, "ask");
        AssertEx.Equal<GraphWorkflowDecisionKind?>(GraphWorkflowDecisionKind.Answer, nodeRun.PendingDecisionKind, "a chat input waits for an answer, not an approval.");
        AssertEx.Null(nodeRun.OutputJson, "its output is the answer, and it has none yet.");
        AssertEx.Equal(GraphWorkflowRunStatus.WaitingForApproval, (await harness.ReadRunAsync(runId)).Status);
        AssertEx.Contains(await harness.ReadEventTrailAsync(runId), GraphWorkflowEventTypes.GateRequested);
    }

    [Test]
    public async Task Answer_RecordsTheTextAndRoutesTheRunToItsEnd()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await ParkedRunAsync(harness);

        var result = await harness.DecideAsync(runId, "ask", Guid.NewGuid(), GraphWorkflowDecisionKind.Answer, payloadJson: """{"text":"postgres"}""");
        _ = await harness.AdvanceUntilQuiescentAsync(runId);

        AssertEx.Equal(GraphWorkflowDecisionKind.Answer, result.Decision);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, result.NodeRunStatus);
        using var output = JsonDocument.Parse(AssertEx.NotNull((await harness.ReadNodeRunAsync(runId, "ask")).OutputJson));
        AssertEx.Equal("Answer", output.RootElement.GetProperty("output").GetProperty("decision").GetString());
        AssertEx.Equal("postgres", output.RootElement.GetProperty("output").GetProperty("text").GetString());
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Skipped, (await harness.ReadNodeRunAsync(runId, "stopped")).Status, "a condition on output.text saw the text and did not fire.");

        var run = await harness.ReadRunAsync(runId);
        AssertEx.Equal(GraphWorkflowRunStatus.Completed, run.Status);
        using var runOutput = JsonDocument.Parse(AssertEx.NotNull(run.OutputJson));
        AssertEx.Contains(runOutput.RootElement.GetRawText(), "postgres");
    }

    [Test]
    public async Task TheSameOperationIdTwice_ReplaysAndWritesTheAnswerOnce()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await ParkedRunAsync(harness);
        var operationId = Guid.NewGuid();

        var first = await harness.DecideAsync(runId, "ask", operationId, GraphWorkflowDecisionKind.Answer, payloadJson: """{"text":"mysql"}""");
        var replay = await harness.DecideAsync(runId, "ask", operationId, GraphWorkflowDecisionKind.Answer, payloadJson: """{"text":"mysql"}""");

        AssertEx.Equal(first.NodeRunStatus, replay.NodeRunStatus);
        AssertEx.Equal(expected: 1, (await harness.ReadEventsAsync(runId)).Count(entry => entry.EventType == GraphWorkflowEventTypes.GateDecided));

        var second = await AssertEx.ThrowsAsync<GraphWorkflowGateAlreadyDecidedException>(() =>
                         harness.DecideAsync(runId, "ask", Guid.NewGuid(), GraphWorkflowDecisionKind.Answer, payloadJson: """{"text":"other"}"""));
        AssertEx.Equal(GraphWorkflowDecisionKind.Answer, second.StandingDecision, "a second answer is told which one stands.");
    }

    /// <summary>A chat input's text IS the act: the same operation id carrying different text is a second answer, not a replay.</summary>
    [Test]
    public async Task TheSameOperationIdWithDifferentText_IsRefusedWithTheStandingAnswer()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await ParkedRunAsync(harness);
        var operationId = Guid.NewGuid();
        _ = await harness.DecideAsync(runId, "ask", operationId, GraphWorkflowDecisionKind.Answer, payloadJson: """{"text":"postgres"}""");

        var refusal = await AssertEx.ThrowsAsync<GraphWorkflowGateAlreadyDecidedException>(() =>
                          harness.DecideAsync(runId, "ask", operationId, GraphWorkflowDecisionKind.Answer, payloadJson: """{"text":"mysql"}"""));

        AssertEx.Contains(refusal.Message, "different answer");
        using var output = JsonDocument.Parse(AssertEx.NotNull((await harness.ReadNodeRunAsync(runId, "ask")).OutputJson));
        AssertEx.Equal("postgres", output.RootElement.GetProperty("output").GetProperty("text").GetString(), "the first answer stands.");
    }

    [Test]
    [Arguments(GraphWorkflowDecisionKind.Approve)]
    [Arguments(GraphWorkflowDecisionKind.Reject)]
    public async Task AChatInput_RefusesAPauseAnswer(GraphWorkflowDecisionKind decision)
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await ParkedRunAsync(harness);

        var refusal = await AssertEx.ThrowsAsync<GraphWorkflowRunConflictException>(() => harness.DecideAsync(runId, "ask", Guid.NewGuid(), decision));

        AssertEx.Contains(refusal.Message, "takes an Answer only");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.WaitingForApproval, (await harness.ReadNodeRunAsync(runId, "ask")).Status);
    }

    [Test]
    public async Task APause_RefusesAnAnswer()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.PauseTwoDecisions);
        _ = await harness.AdvanceUntilQuiescentAsync(runId);

        var refusal = await AssertEx.ThrowsAsync<GraphWorkflowRunConflictException>(() =>
                          harness.DecideAsync(runId, "review", Guid.NewGuid(), GraphWorkflowDecisionKind.Answer, payloadJson: """{"text":"yes"}"""));

        AssertEx.Contains(refusal.Message, "cannot be answered Answer");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.WaitingForApproval, (await harness.ReadNodeRunAsync(runId, "review")).Status);
    }

    [Test]
    [Arguments(null, null, "needs a non-empty 'payload.text'")]
    [Arguments("""{"text":"   "}""", null, "needs a non-empty 'payload.text'")]
    [Arguments("""{"text":42}""", null, "needs a non-empty 'payload.text'")]
    [Arguments("""{"answer":"postgres"}""", null, "needs a non-empty 'payload.text'")]
    [Arguments("""{"text":"postgres"}""", "a comment", "no comment")]
    public async Task AnAnswer_WithoutUsableText_IsRefusedAsARequestError(string? payloadJson, string? comment, string expected)
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await ParkedRunAsync(harness);

        var refusal = await AssertEx.ThrowsAsync<GraphWorkflowValidationException>(() =>
                          harness.DecideAsync(runId, "ask", Guid.NewGuid(), GraphWorkflowDecisionKind.Answer, comment, payloadJson));

        AssertEx.Contains(refusal.Message, expected);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.WaitingForApproval, (await harness.ReadNodeRunAsync(runId, "ask")).Status, "a refused answer leaves the run waiting.");
    }

    /// <summary>The cap is the SMALLER of the chat message cap and half the output envelope — here the chat cap, set to 1 KB.</summary>
    [Test]
    public async Task AnAnswer_OverTheChatMessageCap_IsRefused()
    {
        // A host of its own: the cap is host configuration a sibling must not see.
        await using var harness = new GraphWorkflowHarness(("Security:MaxMessageSizeKb", "1"));
        var runId = await ParkedRunAsync(harness);

        var refusal = await AssertEx.ThrowsAsync<GraphWorkflowValidationException>(() =>
                          harness.DecideAsync(runId, "ask", Guid.NewGuid(), GraphWorkflowDecisionKind.Answer, payloadJson: JsonSerializer.Serialize(new { text = new string('x', 1025) })));

        AssertEx.Contains(refusal.Message, "larger than the 1024 bytes");
        _ = await harness.DecideAsync(runId, "ask", Guid.NewGuid(), GraphWorkflowDecisionKind.Answer, payloadJson: JsonSerializer.Serialize(new { text = new string('x', 1024) }));
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, "ask")).Status, "an answer AT the cap is accepted.");
    }

    /// <summary>On a host of its own: startup recovery sweeps every in-flight row in the database, and a shared host's are every sibling's.</summary>
    [Test]
    public async Task ARunParkedOnAChatInput_SurvivesARestartAndStillTakesItsAnswer()
    {
        await using var harness = new GraphWorkflowHarness();
        var runId = await ParkedRunAsync(harness);

        await new GraphWorkflowStartupReconciler(harness.Services.GetRequiredService<IServiceScopeFactory>(),
                  Options.Create(harness.CurrentOptions()),
                  harness.Services.GetRequiredService<ILogger<GraphWorkflowStartupReconciler>>())
              .StartAsync(CancellationToken.None);
        _ = harness.CreateReplacementDispatcher();

        var survived = await harness.ReadNodeRunAsync(runId, "ask");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.WaitingForApproval, survived.Status, "a durable wait on the user is not what a restart invalidates.");
        AssertEx.Equal<GraphWorkflowDecisionKind?>(GraphWorkflowDecisionKind.Answer, survived.PendingDecisionKind);
        AssertEx.Equal(expected: 1, survived.Attempt);

        _ = await harness.DecideAsync(runId, "ask", Guid.NewGuid(), GraphWorkflowDecisionKind.Answer, payloadJson: """{"text":"sqlite"}""");
        _ = await harness.AdvanceUntilQuiescentAsync(runId);

        AssertEx.Equal(GraphWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId)).Status);
    }

    private static async Task<Guid> ParkedRunAsync(GraphWorkflowHarness harness)
    {
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.ChatInputAnswer);
        _ = await harness.AdvanceUntilQuiescentAsync(runId);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.WaitingForApproval,
            (await harness.ReadNodeRunAsync(runId, "ask")).Status,
            "the run was expected to park on its chat input.");
        return runId;
    }
}
