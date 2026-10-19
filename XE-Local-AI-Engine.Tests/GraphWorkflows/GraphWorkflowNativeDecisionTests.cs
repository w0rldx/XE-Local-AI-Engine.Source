namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     A <c>systemone</c> <c>DecisionModel</c> node end to end on the invocation lane: the real parser, executor, provider,
///     store and router, with only the <c>/v1/systemone</c> client faked.
/// </summary>
/// <remarks>Every test names its own question, which is what the fake client scripts on, so the shared host is safe.</remarks>
[Category(TestCategories.Integration)]
public sealed class GraphWorkflowNativeDecisionTests
{
    private const string Model = GraphWorkflowSystemOneHostFixture.DecisionModel;

    [ClassDataSource<GraphWorkflowSystemOneHostFixture>(Shared = SharedType.PerClass)]
    public required GraphWorkflowSystemOneHostFixture Host { get; init; }

    [Test]
    public async Task AChoice_RoutesOnTheLabel_AndCarriesTheModelsNumbers()
    {
        const string question = "native-choice-routes";
        Host.Client.Script(question, Answered(new LlamaServerSystemOneAnswer
        {
            Type = "choice",
            Choice = "billing",
            Probabilities = new Dictionary<string, double>
            {
                ["billing"] = 0.9,
                ["shipping"] = 0.1
            },
            Confidence = 0.8
        }));
        await using var harness = new GraphWorkflowHarness(Host);

        var runId = await RunAsync(harness, Graph(question, """ "labels": ["billing", "shipping"], "criteria": { "billing": "payments" } """));

        var decide = await harness.ReadNodeRunAsync(runId, "decide");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, decide.Status);
        using var document = JsonDocument.Parse(AssertEx.NotNull(decide.OutputJson));
        var output = document.RootElement.GetProperty("output");
        AssertEx.Equal("choice", output.GetProperty("type").GetString());
        AssertEx.Equal("billing", output.GetProperty("choice").GetString());
        AssertEx.Equal(0.9, output.GetProperty("probabilities").GetProperty("billing").GetDouble());
        AssertEx.Equal(0.8, output.GetProperty("confidence").GetDouble());
        AssertEx.Equal(JsonValueKind.Null, output.GetProperty("noul").ValueKind);
        AssertEx.Equal(JsonValueKind.Null, output.GetProperty("score").ValueKind);
        AssertEx.Equal(JsonValueKind.Null, output.GetProperty("legend").ValueKind);
        AssertEx.Equal("systemone", output.GetProperty("provider").GetString());
        AssertEx.Equal(expected: 42, output.GetProperty("usage").GetProperty("inputTokens").GetInt32());
        AssertEx.Equal(expected: 0, output.GetProperty("usage").GetProperty("outputTokens").GetInt32(), "a decision model generates no tokens.");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, "billing")).Status);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Skipped, (await harness.ReadNodeRunAsync(runId, "other")).Status);

        // What production sent: the bound input as the state, the question only as the instructions, the criteria verbatim.
        var request = AssertEx.NotNull(Host.Client.RequestFor(question));
        AssertEx.Equal(Model, request.ModelName);
        AssertEx.Equal(LlamaServerSystemOneQuestionType.Choice, request.QuestionType);
        AssertEx.Contains(request.State, "my card was charged twice");
        AssertEx.False(request.State.Contains(question, StringComparison.Ordinal), "the question is not duplicated into the state.");
        AssertEx.Equal("payments", request.Criteria["billing"]);
    }

    [Test]
    public async Task ANoul_RoutesThroughGteOnTheLikelihood_AndReadsYes()
    {
        const string question = "native-noul-routes";
        Host.Client.Script(question, Answered(new LlamaServerSystemOneAnswer
        {
            Type = "noul",
            Noul = 0.63
        }));
        await using var harness = new GraphWorkflowHarness(Host);

        var runId = await RunAsync(harness, Graph(question, """ "questionType": "noul" """,
            """{ "path": "output.noul", "op": "gte", "value": 0.5 }""",
            """{ "path": "output.noul", "op": "lt", "value": 0.5 }"""));

        using var document = JsonDocument.Parse(AssertEx.NotNull((await harness.ReadNodeRunAsync(runId, "decide")).OutputJson));
        var output = document.RootElement.GetProperty("output");
        AssertEx.Equal(0.63, output.GetProperty("noul").GetDouble());
        AssertEx.Equal("yes", output.GetProperty("choice").GetString(), "the convenience label for 0.5 and above.");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, "billing")).Status);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Skipped, (await harness.ReadNodeRunAsync(runId, "other")).Status);
        AssertEx.Empty(AssertEx.NotNull(Host.Client.RequestFor(question)).Labels);
    }

    [Test]
    public async Task AScore_ReportsTheNearestLevelAndTheContinuousScore()
    {
        const string question = "native-score-routes";
        Host.Client.Script(question, Answered(new LlamaServerSystemOneAnswer
        {
            Type = "score",
            Score = 1.6,
            Legend = new Dictionary<string, string>
            {
                ["0"] = "can wait",
                ["1"] = "soon",
                ["2"] = "now"
            },
            Probabilities = new Dictionary<string, double>
            {
                ["0"] = 0.1,
                ["1"] = 0.3,
                ["2"] = 0.6
            },
            Confidence = 0.6
        }));
        await using var harness = new GraphWorkflowHarness(Host);

        var runId = await RunAsync(harness, Graph(question, """ "questionType": "score", "labels": ["can wait", "soon", "now"] """,
            """{ "path": "output.score", "op": "gte", "value": 1.5 }""",
            """{ "path": "output.score", "op": "lt", "value": 1.5 }"""));

        using var document = JsonDocument.Parse(AssertEx.NotNull((await harness.ReadNodeRunAsync(runId, "decide")).OutputJson));
        var output = document.RootElement.GetProperty("output");
        AssertEx.Equal(1.6, output.GetProperty("score").GetDouble());
        AssertEx.Equal("now", output.GetProperty("choice").GetString(), "1.6 is nearest the third level.");
        AssertEx.Equal("soon", output.GetProperty("legend").GetProperty("1").GetString());
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, "billing")).Status);
        AssertEx.Equal("can wait,soon,now", string.Join(",", AssertEx.NotNull(Host.Client.RequestFor(question)).Labels));
    }

    [Test]
    public async Task AnAnswerOfTheWrongType_FailsNodeFailed()
    {
        const string question = "native-wrong-type";
        Host.Client.Script(question, Answered(new LlamaServerSystemOneAnswer
        {
            Type = "noul",
            Noul = 0.9
        }));
        await using var harness = new GraphWorkflowHarness(Host);

        var decide = await FailedDecideAsync(harness, Graph(question, """ "labels": ["billing", "shipping"] """));

        AssertEx.Equal(GraphWorkflowFailureClass.AttemptsExhausted, decide.FailureClass, "NodeFailed is retryable, and the node's one attempt is spent.");
        AssertEx.Contains(decide.Error, "different kind of question");
    }

    [Test]
    public async Task AnOffLabelChoice_FailsNodeFailedWithoutRepeatingIt()
    {
        const string question = "native-off-label";
        Host.Client.Script(question, Answered(new LlamaServerSystemOneAnswer
        {
            Type = "choice",
            Choice = "cooking"
        }));
        await using var harness = new GraphWorkflowHarness(Host);

        var decide = await FailedDecideAsync(harness, Graph(question, """ "labels": ["billing", "shipping"] """));

        AssertEx.Equal(GraphWorkflowFailureClass.AttemptsExhausted, decide.FailureClass);
        AssertEx.Contains(decide.Error, "not one of this node's labels");
        AssertEx.False((decide.Error ?? string.Empty).Contains("cooking", StringComparison.Ordinal), "model output never reaches the reason.");
    }

    [Test]
    public async Task ANotImplementedAnswer_FailsValidationNamingTheModel()
    {
        const string question = "native-not-a-decision-model";
        Host.Client.Script(question, new LlamaServerSystemOneResult
        {
            Outcome = LlamaServerSystemOneOutcome.NotDecisionModel,
            Detail = "HTTP 501"
        });
        await using var harness = new GraphWorkflowHarness(Host);

        var decide = await FailedDecideAsync(harness, Graph(question, """ "labels": ["billing", "shipping"] """));

        AssertEx.Equal(GraphWorkflowFailureClass.ValidationFailed, decide.FailureClass, "a retry would ask the same wrong model.");
        AssertEx.Contains(decide.Error, Model);
        AssertEx.Contains(decide.Error!, "not a decision model");
    }

    [Test]
    public async Task ARefusedRequest_FailsValidationAndIsNotRetried()
    {
        const string question = "native-refused-request";
        Host.Client.Script(question, new LlamaServerSystemOneResult
        {
            Outcome = LlamaServerSystemOneOutcome.InvalidRequest,
            Detail = "HTTP 500: input (10516 tokens) is too large to process"
        });
        await using var harness = new GraphWorkflowHarness(Host);

        var decide = await FailedDecideAsync(harness, Graph(question, """ "labels": ["billing", "shipping"] """));

        AssertEx.Equal(GraphWorkflowFailureClass.ValidationFailed, decide.FailureClass, "the same over-long input would be refused on every retry.");
        AssertEx.Equal(expected: 1, decide.Attempt);
        AssertEx.Contains(decide.Error, "refused the request");
    }

    [Test]
    public async Task AScoreWithoutANumber_FailsNamingTheMissingNumber()
    {
        const string question = "native-score-without-number";
        Host.Client.Script(question, Answered(new LlamaServerSystemOneAnswer
        {
            Type = "score",
            Score = double.NaN
        }));
        await using var harness = new GraphWorkflowHarness(Host);

        var decide = await FailedDecideAsync(harness, Graph(question, """ "questionType": "score", "labels": ["low", "high"] """));

        AssertEx.Equal(GraphWorkflowFailureClass.AttemptsExhausted, decide.FailureClass);
        AssertEx.Contains(decide.Error, "without a usable number");
    }

    /// <summary>The Timeout class itself is pinned in <see cref="SystemOneDecisionProviderTests" />: one attempt settles any retryable class as AttemptsExhausted.</summary>
    [Test]
    public async Task ATimeout_FailsTheNodeWithTheTimeoutReason()
    {
        const string question = "native-timeout";
        Host.Client.Script(question, new LlamaServerSystemOneResult
        {
            Outcome = LlamaServerSystemOneOutcome.Timeout,
            Detail = "TaskCanceledException"
        });
        await using var harness = new GraphWorkflowHarness(Host);

        var decide = await FailedDecideAsync(harness, Graph(question, """ "labels": ["billing", "shipping"] """));

        AssertEx.Contains(decide.Error, "ran out of time");
    }

    [Test]
    public async Task AChatModelOnASystemOneNode_FailsValidationBeforeAnyCall()
    {
        const string question = "native-chat-model";
        await using var harness = new GraphWorkflowHarness(Host);

        var decide = await FailedDecideAsync(harness, Graph(question, """ "labels": ["billing", "shipping"] """, model: GraphWorkflowSystemOneHostFixture.ChatModel));

        AssertEx.Equal(GraphWorkflowFailureClass.ValidationFailed, decide.FailureClass);
        AssertEx.Contains(decide.Error, "not classified as a decision model");
        AssertEx.Null(Host.Client.RequestFor(question), "a chat model is refused before the decision client is asked.");
    }

    private static LlamaServerSystemOneResult Answered(LlamaServerSystemOneAnswer answer) =>
        new()
        {
            Outcome = LlamaServerSystemOneOutcome.Answered,
            Answer = answer,
            InputTokens = 42,
            DurationMs = 12
        };

    private static async Task<Guid> RunAsync(GraphWorkflowHarness harness, string graph)
    {
        var runId = await harness.StartRunAsync(graph, """{"message":"my card was charged twice"}""");
        await harness.AdvanceUntilAsync(runId,
            async () => GraphWorkflowStateMachine.IsTerminal((await harness.ReadRunAsync(runId)).Status),
            "the decision run was expected to finish.");
        return runId;
    }

    private static async Task<GraphWorkflowNodeRunSnapshot> FailedDecideAsync(GraphWorkflowHarness harness, string graph)
    {
        var runId = await RunAsync(harness, graph);
        var decide = await harness.ReadNodeRunAsync(runId, "decide");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, decide.Status);
        AssertEx.Equal(GraphWorkflowRunStatus.Failed, (await harness.ReadRunAsync(runId)).Status);
        return decide;
    }

    /// <summary><c>Start → decide (systemone, one attempt) → billing | other</c>, the two out-edges conditioned as the caller says.</summary>
    private static string Graph(string question, string members, string toBilling, string toOther, string model = Model) =>
        $$"""
          {
            "schemaVersion": 1,
            "nodes": [
              { "key": "start", "kind": "Start" },
              { "key": "decide", "kind": "DecisionModel", "maxAttempts": 1,
                "config": { "question": "{{question}}", {{members}}, "provider": "systemone", "model": "{{model}}",
                            "inputBindings": { "ticket": "run.input.message" } } },
              { "key": "billing", "kind": "End", "config": { "outcome": "billing" } },
              { "key": "other", "kind": "End", "config": { "outcome": "other" } }
            ],
            "edges": [
              { "key": "e1", "from": "start", "to": "decide" },
              { "key": "e2", "from": "decide", "to": "billing", "label": "billing", "condition": {{toBilling}} },
              { "key": "e3", "from": "decide", "to": "other", "label": "other", "condition": {{toOther}} }
            ]
          }
          """;

    private static string Graph(string question, string members, string model = Model) =>
        Graph(question,
            members,
            """{ "path": "output.choice", "op": "eq", "value": "billing" }""",
            """{ "path": "output.choice", "op": "ne", "value": "billing" }""",
            model);
}
