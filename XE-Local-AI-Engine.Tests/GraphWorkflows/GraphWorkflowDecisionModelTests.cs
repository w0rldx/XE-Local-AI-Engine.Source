namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The <c>DecisionModel</c> node end to end on the invocation lane, with only the runner faked.</summary>
/// <remarks>
///     The package builder, lane and store are real, so the grammar and output assertions are about what production
///     sends and stores. Every test names its own question, which is what the fake scripts on.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class GraphWorkflowDecisionModelTests
{
    [ClassDataSource<GraphWorkflowAgentHostFixture>(Shared = SharedType.PerClass)]
    public required GraphWorkflowAgentHostFixture Host { get; init; }

    [Test]
    public void TheInvocationLane_OwnsTheDecisionModelKind() =>
        AssertEx.ContainsSingle(Host.Factory.Services.GetServices<IGraphWorkflowNodeExecutor>(), executor => executor.Owns(GraphWorkflowNodeKind.DecisionModel));

    [Test]
    public async Task AnAnswerNamingALabel_RoutesOnTheChoice()
    {
        const string question = "decision-routes-on-coding";
        await using var harness = new GraphWorkflowHarness(Host);
        harness.Invocations.Script(question, new GraphWorkflowScriptedTurn { Text = """{"choice":"coding"}""" });
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.DecisionModelRouting(question), """{"message":"fix my build"}""");

        await harness.AdvanceUntilAsync(runId,
            async () => GraphWorkflowStateMachine.IsTerminal((await harness.ReadRunAsync(runId)).Status),
            "the decision run was expected to finish.");

        var classify = await harness.ReadNodeRunAsync(runId, "classify");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, classify.Status);
        using var document = JsonDocument.Parse(AssertEx.NotNull(classify.OutputJson));
        var output = document.RootElement.GetProperty("output");
        AssertEx.Equal("coding", output.GetProperty("choice").GetString());
        AssertEx.Equal(JsonValueKind.Null, output.GetProperty("confidence").ValueKind, "the llm provider reports no confidence rather than inventing one.");
        AssertEx.Equal(JsonValueKind.Null, output.GetProperty("probabilities").ValueKind);
        AssertEx.Equal("llm", output.GetProperty("provider").GetString());
        AssertEx.Equal(expected: 11, output.GetProperty("usage").GetProperty("inputTokens").GetInt32(), "the turn's usage rides along.");
        AssertEx.Equal("coding", document.RootElement.GetProperty("branch").GetString(), "the envelope names the branch the choice took.");

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, "coding")).Status);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Skipped, (await harness.ReadNodeRunAsync(runId, "other")).Status);
        AssertEx.Equal(GraphWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId)).Status);

        // What production sent: the enum grammar, reasoning off, the question and the bound input in the prompt.
        var package = harness.Invocations.PackageFor(question);
        AssertEx.Equal("none", package.ReasoningEffort);
        AssertEx.Equal(0f, AssertEx.NotNull(package.SamplingOptions).Temperature, "a classifier routes reproducibly.");
        var schema = package.ResponseJsonSchema ?? throw new AssertionException("a decision turn must carry its enum schema.");
        AssertEx.Equal("coding,research,general",
            string.Join(",", schema.GetProperty("properties").GetProperty("choice").GetProperty("enum").EnumerateArray().Select(static label => label.GetString())));
        AssertEx.Contains(package.ConversationContext[0].Content, "fix my build");
        AssertEx.False(package.OmitSystemPrompt, "the classifier's fixed system prompt is sent.");
        AssertEx.Empty(package.AllowedTools);
    }

    [Test]
    public async Task AnAnswerOutsideTheLabels_FailsTheNodeAsNodeFailed()
    {
        const string question = "decision-answers-an-unknown-label";
        await using var harness = new GraphWorkflowHarness(Host);
        harness.Invocations.Script(question, new GraphWorkflowScriptedTurn { Text = """{"choice":"cooking"}""" });
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.DecisionModelRouting(question), """{"message":"bake a cake"}""");

        await harness.AdvanceUntilAsync(runId,
            async () => GraphWorkflowStateMachine.IsTerminal((await harness.ReadRunAsync(runId)).Status),
            "the decision run was expected to finish.");

        var classify = await harness.ReadNodeRunAsync(runId, "classify");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, classify.Status);
        AssertEx.Equal(GraphWorkflowFailureClass.AttemptsExhausted, classify.FailureClass, "a wrong label fails the retryable class, and this node's one attempt is spent.");
        AssertEx.Contains(classify.Error, "not one of this node's labels");
        AssertEx.False((classify.Error ?? string.Empty).Contains("cooking", StringComparison.Ordinal), "the model's raw answer never reaches the operator-facing reason.");
        AssertEx.Equal(GraphWorkflowRunStatus.Failed, (await harness.ReadRunAsync(runId)).Status);
    }

    /// <summary>The retryable claim, proved: a wrong label on attempt 1 is re-asked, and the right one on attempt 2 routes.</summary>
    [Test]
    public async Task AWrongLabel_IsReAskedAndTheSecondAttemptRoutes()
    {
        const string question = "decision-retries-a-wrong-label";
        await using var harness = new GraphWorkflowHarness(Host);
        harness.Invocations.ScriptSequence(question,
            new GraphWorkflowScriptedTurn { Text = """{"choice":"cooking"}""" },
            new GraphWorkflowScriptedTurn { Text = """{"choice":"coding"}""" });
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.DecisionModelRouting(question, maxAttempts: 2), """{"message":"fix my build"}""");

        await harness.AdvanceUntilAsync(runId,
            async () => GraphWorkflowStateMachine.IsTerminal((await harness.ReadRunAsync(runId)).Status),
            "the decision run was expected to finish.");

        var classify = await harness.ReadNodeRunAsync(runId, "classify");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, classify.Status);
        AssertEx.Equal(expected: 2, classify.Attempt, "the first answer failed the attempt and the node was re-asked.");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, "coding")).Status);
        AssertEx.Equal(GraphWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId)).Status);
        AssertEx.Contains(await harness.ReadEventTrailAsync(runId), GraphWorkflowEventTypes.NodeRetried);
    }

    [Test]
    public async Task AnAnswerThatIsNotJson_FailsBeforeAnyChoiceIsRead()
    {
        const string question = "decision-answers-prose";
        await using var harness = new GraphWorkflowHarness(Host);
        harness.Invocations.Script(question, new GraphWorkflowScriptedTurn { Text = "coding, probably" });
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.DecisionModelRouting(question), """{"message":"?"}""");

        await harness.AdvanceUntilAsync(runId,
            async () => GraphWorkflowStateMachine.IsTerminal((await harness.ReadRunAsync(runId)).Status),
            "the decision run was expected to finish.");

        var classify = await harness.ReadNodeRunAsync(runId, "classify");
        AssertEx.Equal(GraphWorkflowFailureClass.AttemptsExhausted, classify.FailureClass, "a retryable failure on the node's only attempt.");
        AssertEx.Contains(classify.Error, "JSON object its response schema requires");
    }
}
