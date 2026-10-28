namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using System.Text.Json;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.Core;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     <see cref="GraphWorkflowIntentService" />: Start-node alias parsing, the normalization aliases match in, the
///     utterance-age refusals, alias routing over Intent-enabled definitions only, and the router graph's run on a
///     stepping clock (choice, <c>unknown</c>, timeout).
/// </summary>
[Category(TestCategories.Unit)]
public sealed class GraphWorkflowIntentServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(1);

    private static readonly Guid LightsId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly Guid WordId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly Guid RouterId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static readonly Guid RequestId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static readonly Guid RouterRunId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private static readonly Guid TargetRunId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    [Test]
    [Arguments("  Lights   ON!  ", "Lights ON")]
    [Arguments("Open word?!.", "Open word")]
    [Arguments("Open\tmy\nWord", "Open my Word")]
    [Arguments("?!", "")]
    public void Normalize_CollapsesWhitespaceAndStripsTrailingPunctuation(string text, string expected) =>
        AssertEx.Equal(expected, GraphWorkflowIntentService.Normalize(text));

    [Test]
    public void RouterRequestId_IsDeterministicAndNeverTheCommandsOwnId()
    {
        var derived = GraphWorkflowIntentService.RouterRequestId(RequestId);

        AssertEx.Equal(derived, GraphWorkflowIntentService.RouterRequestId(RequestId), "a retry must replay the same router run.");
        AssertEx.NotEqual(RequestId, derived);
        AssertEx.NotEqual(derived, GraphWorkflowIntentService.RouterRequestId(Guid.NewGuid()));
        AssertEx.Equal(expected: 5, derived.Version);
    }

    [Test]
    public void Aliases_AreParsedTrimmedOntoTheStartConfig()
    {
        var start = GraphWorkflowGraph.Parse(Graph("\" Lights on \"", "\"Turn the lights on\"")).Nodes["start"].Config;

        AssertEx.Equal("Lights on|Turn the lights on", string.Join('|', ((GraphWorkflowStartConfig)start).Aliases));
    }

    [Test]
    [Arguments("\"aliases\": \"lights\"", "must be an array")]
    [Arguments("\"aliases\": []", "between 1 and 32")]
    [Arguments("\"aliases\": [\"  \"]", "1 to 120 characters")]
    [Arguments("\"aliases\": [\"?!\"]", "1 to 120 characters")]
    [Arguments("\"aliases\": [42]", "1 to 120 characters")]
    [Arguments("\"aliases\": [\"Lights on\", \"lights   ON!\"]", "twice")]
    public void Aliases_RefuseABadListKeyedByTheStartNode(string member, string expected)
    {
        var json = $$"""
                     { "schemaVersion": 1,
                       "nodes": [{ "key": "start", "kind": "Start", "config": { {{member}} } }, { "key": "done", "kind": "End", "config": { "outcome": "x" } }],
                       "edges": [{ "key": "e1", "from": "start", "to": "done" }] }
                     """;

        var result = AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse(json)).Result;

        AssertEx.Equal(expected: 1, result.Errors.Count);
        var error = result.Errors[0];
        AssertEx.Equal("start", error.Key);
        AssertEx.Contains(error.Message, expected);
    }

    [Test]
    public void Aliases_RefuseMoreThanThirtyTwoAndOneOverTheLengthCap()
    {
        var tooMany = string.Join(", ", Enumerable.Range(0, 33).Select(static index => $"\"alias {index}\""));

        AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse(Graph(tooMany)));
        AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse(Graph($"\"{new string('a', 121)}\"")));
        _ = GraphWorkflowGraph.Parse(Graph($"\"{new string('a', 120)}\""));
    }

    [Test]
    public async Task AnEmptyUtteranceOrRequestId_IsRefusedKeyedAndStartsNothing()
    {
        var harness = new Harness();

        var blank = await AssertEx.ThrowsAsync<GraphWorkflowValidationException>(() => harness.Service.ResolveAndStartAsync(Request(" !? ")));
        var noId = await AssertEx.ThrowsAsync<GraphWorkflowValidationException>(() => harness.Service.ResolveAndStartAsync(Request("lights on", requestId: Guid.Empty)));

        AssertEx.Equal("utterance", blank.Result.Errors.Single().Key);
        AssertEx.Equal("requestId", noId.Result.Errors.Single().Key);
        AssertEx.Empty(harness.Runs.ReceivedCalls());
    }

    [Test]
    public async Task AStaleUtterance_IsAnExpiredConflict_AndOneInsideTheWindowIsNot()
    {
        var harness = new Harness(maxAge: TimeSpan.FromSeconds(30));
        harness.AddDefinition(LightsId, "Lights on", GraphWorkflowTriggerSource.Intent);
        harness.StartAnswers(LightsId, RequestId, TargetRunId);

        var expired = await AssertEx.ThrowsAsync<GraphWorkflowIntentExpiredException>(() =>
            harness.Service.ResolveAndStartAsync(Request("lights on", Now.AddSeconds(-31))));
        var accepted = await harness.Service.ResolveAndStartAsync(Request("lights on", Now.AddSeconds(-30)));

        AssertEx.Contains(expired.Message, "30 s");
        AssertEx.Equal(GraphWorkflowIntentStatus.Started, accepted.Status);
    }

    [Test]
    public async Task AFutureUtterance_BeyondTheSkew_IsRefusedKeyedByUtteredAt()
    {
        var harness = new Harness();
        harness.AddDefinition(LightsId, "Lights on", GraphWorkflowTriggerSource.Intent);
        harness.StartAnswers(LightsId, RequestId, TargetRunId);

        var future = await AssertEx.ThrowsAsync<GraphWorkflowValidationException>(() =>
            harness.Service.ResolveAndStartAsync(Request("lights on", Now + GraphWorkflowIntentService.ClockSkew + TimeSpan.FromSeconds(1))));
        var skewed = await harness.Service.ResolveAndStartAsync(Request("lights on", Now + GraphWorkflowIntentService.ClockSkew));

        AssertEx.Equal("utteredAt", future.Result.Errors.Single().Key);
        AssertEx.Equal(GraphWorkflowIntentStatus.Started, skewed.Status);
    }

    [Test]
    public async Task AnAliasMatch_StartsThatDefinitionWithTheRequestIdIntentOriginAndNoInput()
    {
        var principal = Guid.NewGuid();
        var harness = new Harness();
        harness.AddDefinition(LightsId, "Living room", GraphWorkflowTriggerSource.Intent, "\"Lights on\"");
        harness.AddDefinition(WordId, "Open Word", GraphWorkflowTriggerSource.Intent);
        harness.StartAnswers(LightsId, RequestId, TargetRunId);

        var result = await harness.Service.ResolveAndStartAsync(Request("  LIGHTS on! ", principalId: principal));

        AssertEx.Equal(GraphWorkflowIntentStatus.Started, result.Status);
        AssertEx.Equal(GraphWorkflowIntentMatch.Alias, result.MatchedBy);
        AssertEx.Equal(LightsId, result.DefinitionId);
        AssertEx.Equal("Living room", result.DefinitionName);
        AssertEx.Equal(TargetRunId, AssertEx.NotNull(result.Run).Run.Id);
        var start = harness.Runs.ReceivedCalls().Single(IsStart).GetArguments();
        AssertEx.Null((string?)start[2], "the definition's defaultInput applies.");
        var origin = (GraphWorkflowRunOrigin)start[4]!;
        AssertEx.Equal(GraphWorkflowTriggerSource.Intent, origin.Source);
        AssertEx.Equal(principal, origin.PrincipalId);
    }

    [Test]
    public async Task TheDefinitionName_MatchesLikeAnAlias()
    {
        var harness = new Harness();
        harness.AddDefinition(WordId, "Open Word", GraphWorkflowTriggerSource.Intent);
        harness.StartAnswers(WordId, RequestId, TargetRunId);

        var result = await harness.Service.ResolveAndStartAsync(Request("open word."));

        AssertEx.Equal(WordId, result.DefinitionId);
        AssertEx.Equal(GraphWorkflowIntentMatch.Alias, result.MatchedBy);
    }

    [Test]
    public async Task AnAliasOnADefinitionNotOpenToIntents_RoutesNowhere()
    {
        var harness = new Harness();
        harness.AddDefinition(LightsId, "Living room", GraphWorkflowTriggerSource.Manual | GraphWorkflowTriggerSource.Chat, "\"Lights on\"");

        var result = await harness.Service.ResolveAndStartAsync(Request("lights on"));

        AssertEx.Equal(GraphWorkflowIntentStatus.NotMatched, result.Status);
        AssertEx.Equal(GraphWorkflowIntentMatch.None, result.MatchedBy);
        AssertEx.Contains(AssertEx.NotNull(result.Reason), "no intent router is set");
        AssertEx.Empty(harness.Runs.ReceivedCalls());
        await harness.Definitions.DidNotReceive().GetAsync(LightsId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TwoDefinitionsClaimingOnePhrase_AreNotMatchedAndStartNothing()
    {
        var harness = new Harness();
        harness.AddDefinition(LightsId, "Living room", GraphWorkflowTriggerSource.Intent, "\"Lights on\"");
        harness.AddDefinition(WordId, "Kitchen", GraphWorkflowTriggerSource.Intent, "\"lights on\"");

        var result = await harness.Service.ResolveAndStartAsync(Request("lights on"));

        AssertEx.Equal(GraphWorkflowIntentStatus.NotMatched, result.Status);
        AssertEx.Contains(AssertEx.NotNull(result.Reason), "matches 2 workflows ('Living room', 'Kitchen')");
        AssertEx.Empty(harness.Runs.ReceivedCalls());
    }

    [Test]
    public async Task WithNoAliasMatch_TheRouterRunsOnTheUtteranceAndChoices_AndItsChoiceStarts()
    {
        var harness = new Harness(routerId: RouterId);
        harness.AddDefinition(LightsId, "Lights on", GraphWorkflowTriggerSource.Intent);
        harness.AddDefinition(WordId, "Open Word", GraphWorkflowTriggerSource.Intent);
        harness.AddDefinition(RouterId, "Router", GraphWorkflowTriggerSource.Intent, "\"route\"");
        harness.StartAnswers(LightsId, RequestId, TargetRunId);
        harness.Runs.StartAsync(RouterId, Arg.Any<Guid>(), Arg.Any<string?>(), null, Arg.Any<GraphWorkflowRunOrigin>(), Arg.Any<CancellationToken>())
               .Returns(Detail(RouterRunId, RouterId, GraphWorkflowRunStatus.Pending));
        harness.Runs.GetRunAsync(RouterRunId, Arg.Any<CancellationToken>())
               .Returns(Detail(RouterRunId, RouterId, GraphWorkflowRunStatus.Running),
                   Detail(RouterRunId, RouterId, GraphWorkflowRunStatus.Completed, """{"choice":"Lights on","confidence":null}"""));

        var result = await harness.Service.ResolveAndStartAsync(Request("could you make it bright in here"));

        AssertEx.Equal(GraphWorkflowIntentStatus.Started, result.Status);
        AssertEx.Equal(GraphWorkflowIntentMatch.Router, result.MatchedBy);
        AssertEx.Equal(LightsId, result.DefinitionId);
        var routerStart = harness.Runs.ReceivedCalls().Where(IsStart).Select(static call => call.GetArguments()).Single(arguments => (Guid)arguments[0]! == RouterId);
        AssertEx.Equal(GraphWorkflowIntentService.RouterRequestId(RequestId), (Guid)routerStart[1]!);
        using var input = JsonDocument.Parse(AssertEx.NotNull((string?)routerStart[2]));
        AssertEx.Equal("could you make it bright in here", input.RootElement.GetProperty("utterance").GetString());
        AssertEx.Equal("Lights on|Open Word",
            string.Join('|', input.RootElement.GetProperty("choices").EnumerateArray().Select(static choice => choice.GetString())),
            "the router itself is never a choice.");
        AssertEx.Equal(GraphWorkflowTriggerSource.Intent, ((GraphWorkflowRunOrigin)routerStart[4]!).Source);
    }

    [Test]
    public async Task ABareStringResult_IsReadAsTheChoiceToo()
    {
        var harness = new Harness(routerId: RouterId);
        harness.AddDefinition(WordId, "Open Word", GraphWorkflowTriggerSource.Intent, "\"word\"");
        harness.AddDefinition(RouterId, "Router", GraphWorkflowTriggerSource.Intent);
        harness.StartAnswers(WordId, RequestId, TargetRunId);
        harness.RouterAnswers(Detail(RouterRunId, RouterId, GraphWorkflowRunStatus.Completed, "\"WORD\""));

        var result = await harness.Service.ResolveAndStartAsync(Request("start the word processor"));

        AssertEx.Equal(WordId, result.DefinitionId, "a router choice may name an alias, matched like the utterance.");
    }

    [Test]
    [Arguments("""{"choice":"unknown"}""", "answered unknown")]
    [Arguments("""{"choice":"Make coffee"}""", "no workflow open to intents")]
    [Arguments("""{"outcome":"x"}""", "carries no choice")]
    [Arguments(null, "carries no choice")]
    public async Task ARouterAnswerNamingNoIntentWorkflow_IsNotMatched(string? outputJson, string expected)
    {
        var harness = new Harness(routerId: RouterId);
        harness.AddDefinition(LightsId, "Lights on", GraphWorkflowTriggerSource.Intent);
        harness.AddDefinition(WordId, "Make coffee", GraphWorkflowTriggerSource.Manual);
        harness.AddDefinition(RouterId, "Router", GraphWorkflowTriggerSource.Intent);
        harness.RouterAnswers(Detail(RouterRunId, RouterId, GraphWorkflowRunStatus.Completed, outputJson));

        var result = await harness.Service.ResolveAndStartAsync(Request("what is the weather"));

        AssertEx.Equal(GraphWorkflowIntentStatus.NotMatched, result.Status);
        AssertEx.Equal(GraphWorkflowIntentMatch.Router, result.MatchedBy);
        AssertEx.Contains(AssertEx.NotNull(result.Reason), expected);
        AssertEx.ContainsSingle(harness.Runs.ReceivedCalls(), IsStart);
    }

    [Test]
    public async Task AFailedRouterRun_IsNotMatchedWithItsStructuralReason()
    {
        var harness = new Harness(routerId: RouterId);
        harness.AddDefinition(LightsId, "Lights on", GraphWorkflowTriggerSource.Intent);
        harness.AddDefinition(RouterId, "Router", GraphWorkflowTriggerSource.Intent);
        harness.RouterAnswers(Detail(RouterRunId, RouterId, GraphWorkflowRunStatus.Failed, failureClass: GraphWorkflowFailureClass.NodeFailed));

        var result = await harness.Service.ResolveAndStartAsync(Request("what is the weather"));

        AssertEx.Equal("The intent router run ended Failed (NodeFailed).", result.Reason);
    }

    [Test]
    public async Task ARouterThatOutlivesTheTimeout_IsCancelledOnceAndNotMatched()
    {
        var harness = new Harness(routerId: RouterId, routerTimeoutSeconds: 2);
        harness.AddDefinition(LightsId, "Lights on", GraphWorkflowTriggerSource.Intent);
        harness.AddDefinition(RouterId, "Router", GraphWorkflowTriggerSource.Intent);
        harness.Runs.StartAsync(RouterId, Arg.Any<Guid>(), Arg.Any<string?>(), null, Arg.Any<GraphWorkflowRunOrigin>(), Arg.Any<CancellationToken>())
               .Returns(Detail(RouterRunId, RouterId, GraphWorkflowRunStatus.Running));
        harness.Runs.GetRunAsync(RouterRunId, Arg.Any<CancellationToken>()).Returns(Detail(RouterRunId, RouterId, GraphWorkflowRunStatus.Running));

        var result = await harness.Service.ResolveAndStartAsync(Request("what is the weather"));

        AssertEx.Equal(GraphWorkflowIntentStatus.NotMatched, result.Status);
        AssertEx.Equal("The intent router did not answer within 2 s and was cancelled.", result.Reason);
        await harness.Runs.Received(1).CancelAsync(RouterRunId, Arg.Any<CancellationToken>());
        // One poll per stepped 250 ms until the deadline: the wait is driven by the clock, not by a real delay.
        await harness.Runs.Received(8).GetRunAsync(RouterRunId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ARouterNotOpenToIntents_IsNeverStarted()
    {
        var harness = new Harness(routerId: RouterId);
        harness.AddDefinition(LightsId, "Lights on", GraphWorkflowTriggerSource.Intent);
        harness.AddDefinition(RouterId, "Router", GraphWorkflowTriggerSource.Manual);

        var result = await harness.Service.ResolveAndStartAsync(Request("what is the weather"));

        AssertEx.Contains(AssertEx.NotNull(result.Reason), "does not accept Intent starts");
        AssertEx.Empty(harness.Runs.ReceivedCalls());
    }

    private static bool IsStart(ICall call) =>
        call.GetMethodInfo().Name == nameof(IGraphWorkflowRunService.StartAsync);

    private static GraphWorkflowIntentRequest Request(string utterance, DateTimeOffset? utteredAt = null, Guid? requestId = null, Guid? principalId = null) =>
        new()
        {
            Utterance = utterance,
            RequestId = requestId ?? RequestId,
            UtteredAt = utteredAt ?? Now,
            PrincipalId = principalId
        };

    private static string Graph(params string[] aliases) =>
        $$"""
          { "schemaVersion": 1,
            "nodes": [{ "key": "start", "kind": "Start"{{(aliases.Length == 0 ? "" : $", \"config\": {{ \"aliases\": [{string.Join(", ", aliases)}] }}")}} },
                      { "key": "done", "kind": "End", "config": { "outcome": "done" } }],
            "edges": [{ "key": "e1", "from": "start", "to": "done" }] }
          """;

    private static GraphWorkflowRunDetail Detail(Guid runId,
        Guid definitionId,
        GraphWorkflowRunStatus status,
        string? outputJson = null,
        GraphWorkflowFailureClass failureClass = GraphWorkflowFailureClass.None) =>
        new()
        {
            Run = new GraphWorkflowRunSnapshot
            {
                Id = runId,
                RequestId = Guid.NewGuid(),
                DefinitionId = definitionId,
                DefinitionVersion = 1,
                GraphHash = "hash",
                Status = status,
                FailureClass = failureClass,
                GraphJson = "{}",
                InputJson = null,
                OutputJson = outputJson,
                Seq = 0,
                Version = 0,
                CancelRequestedAtUtc = null,
                StartedAtUtc = null,
                CompletedAtUtc = null,
                CreatedAtUtc = 0,
                TriggerSource = GraphWorkflowTriggerSource.Intent
            },
            NodeRuns = []
        };

    /// <summary>A clock that moves forward by each timer's due time and fires it at once, so a polling wait never sleeps.</summary>
    private sealed class SteppingClock : TimeProvider
    {
        private long _nowTicks = Now.UtcTicks;

        public override DateTimeOffset GetUtcNow() =>
            new(Interlocked.Read(ref _nowTicks), TimeSpan.Zero);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Add(ref _nowTicks, dueTime.Ticks);
            _ = Task.Run(() => callback(state));
            return new InertTimer();
        }

        private sealed class InertTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) =>
                true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() =>
                ValueTask.CompletedTask;
        }
    }

    private sealed class Harness
    {
        private readonly List<GraphWorkflowDefinitionSummary> _summaries = [];

        public Harness(Guid? routerId = null, TimeSpan? maxAge = null, int routerTimeoutSeconds = 120)
        {
            Definitions.ListAsync(Arg.Any<CancellationToken>()).Returns(_ => _summaries);
            var settings = StubNodeRuntimeSettings.Create()
                                                  .WithIntentRouter(routerId)
                                                  .WithIntentMaxUtteranceAge(maxAge ?? TimeSpan.FromSeconds(30))
                                                  .Build();
            Service = new GraphWorkflowIntentService(Definitions,
                Runs,
                settings,
                Options.Create(new GraphWorkflowOptions
                {
                    IntentRouterTimeoutSeconds = routerTimeoutSeconds
                }),
                new SteppingClock());
        }

        public IGraphWorkflowDefinitionService Definitions { get; } = Substitute.For<IGraphWorkflowDefinitionService>();

        public IGraphWorkflowRunService Runs { get; } = Substitute.For<IGraphWorkflowRunService>();

        public GraphWorkflowIntentService Service { get; }

        public void AddDefinition(Guid id, string name, GraphWorkflowTriggerSource sources, params string[] aliases)
        {
            _summaries.Add(new GraphWorkflowDefinitionSummary
            {
                Id = id,
                Name = name,
                Description = null,
                GraphHash = "hash",
                NodeCount = 2,
                SchemaVersion = 1,
                Kind = GraphWorkflowDefinitionKind.Standard,
                TriggerSources = sources,
                Version = 1,
                CreatedAtUtc = 0,
                UpdatedAtUtc = 0
            });
            Definitions.GetAsync(id, Arg.Any<CancellationToken>()).Returns(new GraphWorkflowDefinitionSnapshot
            {
                Id = id,
                Name = name,
                Description = null,
                GraphJson = Graph(aliases),
                GraphHash = "hash",
                NodeCount = 2,
                SchemaVersion = 1,
                Kind = GraphWorkflowDefinitionKind.Standard,
                TriggerSources = sources,
                Version = 1,
                CreatedAtUtc = 0,
                UpdatedAtUtc = 0
            });
        }

        public void StartAnswers(Guid definitionId, Guid requestId, Guid runId) =>
            Runs.StartAsync(definitionId, requestId, null, null, Arg.Any<GraphWorkflowRunOrigin>(), Arg.Any<CancellationToken>())
                .Returns(Detail(runId, definitionId, GraphWorkflowRunStatus.Pending));

        public void RouterAnswers(GraphWorkflowRunDetail terminal) =>
            Runs.StartAsync(RouterId, Arg.Any<Guid>(), Arg.Any<string?>(), null, Arg.Any<GraphWorkflowRunOrigin>(), Arg.Any<CancellationToken>())
                .Returns(terminal);
    }
}
