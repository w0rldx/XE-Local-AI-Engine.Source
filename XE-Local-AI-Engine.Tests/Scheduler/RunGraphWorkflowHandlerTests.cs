namespace XE_Local_AI_Engine.Tests.Scheduler;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Client.Services.Scheduler.Handlers;
using XE_Local_AI_Engine.Client.Services.Scheduler.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     <see cref="RunGraphWorkflowHandler" />: parameter validation, a Schedule-origin start per fire, the poll to a
///     terminal status on a stepping clock, and the operator-safe failure, refusal, timeout and feature-off reasons.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class RunGraphWorkflowHandlerTests
{
    private const string DefinitionIdString = "22222222-2222-2222-2222-222222222222";

    private static readonly Guid DefinitionId = Guid.Parse(DefinitionIdString);

    private static readonly Guid RunId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Test]
    public void Descriptor_ClaimsRunGraphAndKeepsAgentsOut()
    {
        var harness = new Harness();
        var descriptor = harness.Handler.Descriptor;

        AssertEx.Equal("run-graph", harness.Handler.TemplateId);
        AssertEx.Equal("run-graph", descriptor.TemplateId);
        AssertEx.Equal("Run a graph workflow", descriptor.DisplayName);
        AssertEx.True(descriptor.AllowManualTrigger, "operators may run a scheduled workflow on demand.");
        AssertEx.False(descriptor.AllowAgentCreation, "an agent must not schedule a side-effecting workflow.");
        AssertEx.Equal(HistoryDetailLevel.Detailed, descriptor.HistoryDetailLevel);
        AssertEx.Equal(SchedulerMisfirePolicy.SkipMissed, descriptor.DefaultMisfirePolicy);
        using var schema = System.Text.Json.JsonDocument.Parse(descriptor.ParameterSchema!);
        AssertEx.Equal("definitionId", schema.RootElement.GetProperty("required")[0].GetString());
    }

    [Test]
    [Arguments("")]
    [Arguments("not json")]
    [Arguments("""{ }""")]
    [Arguments("""{ "definitionId": "not-a-guid" }""")]
    [Arguments("""{ "definitionId": "00000000-0000-0000-0000-000000000000" }""")]
    [Arguments("""{ "definitionId": "22222222-2222-2222-2222-222222222222", "inputJson": "[1, 2]" }""")]
    [Arguments("""{ "definitionId": "22222222-2222-2222-2222-222222222222", "inputJson": "{ broken" }""")]
    [Arguments("""{ "definitionId": "22222222-2222-2222-2222-222222222222", "timeoutSeconds": 0 }""")]
    [Arguments("""{ "definitionId": "22222222-2222-2222-2222-222222222222", "timeoutSeconds": 86401 }""")]
    public async Task ExecuteAsync_WhenParametersInvalid_ThrowsValidationExceptionWithoutStarting(string parametersJson)
    {
        var harness = new Harness();

        AssertEx.Throws<ScheduledJobValidationException>(() => harness.Handler.ValidateParameters(parametersJson));
        await AssertEx.ThrowsAsync<ScheduledJobValidationException>(() => harness.Handler.ExecuteAsync(Context(parametersJson), CancellationToken.None));

        AssertEx.Empty(harness.RunService.ReceivedCalls());
    }

    [Test]
    public void ValidateParameters_WhenInputIsOverTheRunInputCap_Throws()
    {
        var harness = new Harness(maxRunInputBytes: 16);

        var exception = AssertEx.Throws<ScheduledJobValidationException>(() =>
            harness.Handler.ValidateParameters("""{ "definitionId": "22222222-2222-2222-2222-222222222222", "inputJson": "{ \"room\": \"living room\" }" }"""));

        AssertEx.Equal("The run input is larger than the 16 bytes one run may carry.", exception.Message);
    }

    [Test]
    public async Task ExecuteAsync_WhenRunCompletes_StartsWithScheduleOriginAndRecordsTheOutcome()
    {
        var harness = new Harness();
        var progress = new List<string>();
        harness.RunService.StartAsync(DefinitionId, Arg.Any<Guid>(), Arg.Any<string?>(), null, Arg.Any<GraphWorkflowRunOrigin>(), Arg.Any<CancellationToken>())
               .Returns(Detail(GraphWorkflowRunStatus.Pending));
        harness.RunService.GetRunAsync(RunId, Arg.Any<CancellationToken>())
               .Returns(Detail(GraphWorkflowRunStatus.Running), Detail(GraphWorkflowRunStatus.Completed, EndNode("lights-on")));
        var context = Context("""{ "definitionId": "22222222-2222-2222-2222-222222222222", "inputJson": "{ \"room\": \"kitchen\" }" }""",
            (message, _, _) =>
            {
                progress.Add(message);
                return Task.CompletedTask;
            });

        await harness.Handler.ExecuteAsync(context, CancellationToken.None);

        var start = harness.RunService.ReceivedCalls().Single(IsStart);
        var arguments = start.GetArguments();
        var requestId = (Guid)arguments[1]!;
        AssertEx.NotEqual(Guid.Empty, requestId);
        AssertEx.Equal("""{ "room": "kitchen" }""", (string?)arguments[2]);
        var origin = (GraphWorkflowRunOrigin)arguments[4]!;
        AssertEx.Equal(GraphWorkflowTriggerSource.Schedule, origin.Source);
        AssertEx.Null(origin.PrincipalId);
        AssertEx.Equal($"Graph workflow run {RunId} completed with outcome 'lights-on' (request {requestId}).", context.Summary);
        AssertEx.Equal($"Graph workflow run {RunId} is Pending.|Graph workflow run {RunId} is Running.", string.Join('|', progress));
    }

    [Test]
    public async Task ExecuteAsync_EachFire_MintsANewRequestId()
    {
        var harness = new Harness();
        harness.RunService.StartAsync(DefinitionId, Arg.Any<Guid>(), Arg.Any<string?>(), null, Arg.Any<GraphWorkflowRunOrigin>(), Arg.Any<CancellationToken>())
               .Returns(Detail(GraphWorkflowRunStatus.Completed));

        await harness.Handler.ExecuteAsync(Context(ValidParams()), CancellationToken.None);
        await harness.Handler.ExecuteAsync(Context(ValidParams()), CancellationToken.None);

        var requestIds = harness.RunService.ReceivedCalls()
                                .Where(IsStart)
                                .Select(call => (Guid)call.GetArguments()[1]!)
                                .ToList();
        AssertEx.Equal(expected: 2, requestIds.Distinct().Count());
    }

    [Test]
    public async Task ExecuteAsync_WhenTheDefinitionRefusesSchedule_FailsWithTheSettingsHint()
    {
        var harness = new Harness();
        harness.RunService.StartAsync(DefinitionId, Arg.Any<Guid>(), Arg.Any<string?>(), null, Arg.Any<GraphWorkflowRunOrigin>(), Arg.Any<CancellationToken>())
               .ThrowsAsync(new GraphWorkflowRunConflictException("Graph workflow definition 'Lights' does not accept runs started by Schedule. The operator can add the source in the workflow settings."));

        var exception = await AssertEx.ThrowsAsync<ScheduledJobExecutionException>(() => harness.Handler.ExecuteAsync(Context(ValidParams()), CancellationToken.None));

        AssertEx.Contains(exception.Message, "'Lights'");
        AssertEx.Contains(exception.Message, "Add Schedule to the workflow's trigger sources in its settings");
    }

    [Test]
    public async Task ExecuteAsync_WhenTheRunFails_ThrowsTheStructuralReasonNotTheNodeError()
    {
        var harness = new Harness();
        var failedNode = NodeRun("call-ha", GraphWorkflowNodeKind.Tool, GraphWorkflowNodeRunStatus.Failed, outputJson: null) with
        {
            FailureClass = GraphWorkflowFailureClass.AttemptsExhausted,
            Error = "secret upstream body"
        };
        harness.RunService.StartAsync(DefinitionId, Arg.Any<Guid>(), Arg.Any<string?>(), null, Arg.Any<GraphWorkflowRunOrigin>(), Arg.Any<CancellationToken>())
               .Returns(Detail(GraphWorkflowRunStatus.Failed, failedNode, GraphWorkflowFailureClass.NodeFailed));

        var exception = await AssertEx.ThrowsAsync<ScheduledJobExecutionException>(() => harness.Handler.ExecuteAsync(Context(ValidParams()), CancellationToken.None));

        AssertEx.Equal($"Graph workflow run {RunId} failed: NodeFailed at node 'call-ha' (AttemptsExhausted).", exception.Message);
    }

    [Test]
    public async Task ExecuteAsync_WhenCancelledWhileTheStartCommits_FindsTheRunByRequestAndCancelsIt()
    {
        // The start commits the run, then the job's cancellation surfaces before the detail comes back: the run id was
        // never returned, so only the request id can reach the committed run.
        var harness = new Harness();
        using var job = new CancellationTokenSource();
        Guid? requestId = null;
        harness.RunService.StartAsync(DefinitionId, Arg.Any<Guid>(), Arg.Any<string?>(), null, Arg.Any<GraphWorkflowRunOrigin>(), Arg.Any<CancellationToken>())
               .Returns<GraphWorkflowRunDetail>(call =>
               {
                   requestId = call.ArgAt<Guid>(1);
                   job.Cancel();
                   throw new OperationCanceledException(job.Token);
               });
        harness.Store.FindRunByRequestAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
               .Returns(call => call.ArgAt<Guid>(0) == requestId ? Detail(GraphWorkflowRunStatus.Running).Run : null);

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => harness.Handler.ExecuteAsync(Context(ValidParams()), job.Token));

        await harness.RunService.Received(1).CancelAsync(RunId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_WhenTheRunOutlivesTheTimeout_CancelsItOnceAndFails()
    {
        var harness = new Harness();
        harness.RunService.StartAsync(DefinitionId, Arg.Any<Guid>(), Arg.Any<string?>(), null, Arg.Any<GraphWorkflowRunOrigin>(), Arg.Any<CancellationToken>())
               .Returns(Detail(GraphWorkflowRunStatus.Running));
        harness.RunService.GetRunAsync(RunId, Arg.Any<CancellationToken>()).Returns(Detail(GraphWorkflowRunStatus.Running));

        var exception = await AssertEx.ThrowsAsync<ScheduledJobExecutionException>(() =>
            harness.Handler.ExecuteAsync(Context("""{ "definitionId": "22222222-2222-2222-2222-222222222222", "timeoutSeconds": 3 }"""), CancellationToken.None));

        AssertEx.Equal($"Graph workflow run {RunId} did not finish within 3 s and was cancelled.", exception.Message);
        await harness.RunService.Received(1).CancelAsync(RunId, Arg.Any<CancellationToken>());
        // One poll per stepped second until the deadline: the wait is driven by the clock, not by a real delay.
        await harness.RunService.Received(3).GetRunAsync(RunId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_WhenGraphWorkflowsAreOff_FailsWithoutStarting()
    {
        var harness = new Harness(graphWorkflowsEnabled: false);

        var exception = await AssertEx.ThrowsAsync<ScheduledJobExecutionException>(() => harness.Handler.ExecuteAsync(Context(ValidParams()), CancellationToken.None));

        AssertEx.Contains(exception.Message, "Graph workflows are turned off on this node");
        AssertEx.Empty(harness.RunService.ReceivedCalls());
    }

    [Test]
    public void Registry_WithTheHandler_ListsRunGraphOnce()
    {
        var harness = new Harness();
        var registry = new ScheduledJobTemplateRegistry([harness.Handler]);

        AssertEx.ContainsSingle(registry.ListTemplates(), descriptor => descriptor.TemplateId == RunGraphWorkflowHandler.TemplateIdValue);
        AssertEx.True(registry.TryGetHandler("run-graph", out var resolved), "run-graph resolves through the registry.");
        AssertEx.Equal(harness.Handler, resolved);
    }

    private static bool IsStart(NSubstitute.Core.ICall call) =>
        string.Equals(call.GetMethodInfo().Name, nameof(IGraphWorkflowRunService.StartAsync), StringComparison.Ordinal);

    private static string ValidParams() => $$"""{ "definitionId": "{{DefinitionIdString}}" }""";

    private static ScheduledJobExecutionContext Context(string? parametersJson, Func<string, int?, CancellationToken, Task>? reportProgress = null) =>
        new()
        {
            ScheduledJobId = Guid.NewGuid(),
            TemplateId = RunGraphWorkflowHandler.TemplateIdValue,
            DisplayName = "Evening lights",
            Parameters = parametersJson,
            FireInstanceId = "fire-1",
            ScheduledFireTimeUtc = null,
            ActualFireTimeUtc = DateTimeOffset.UnixEpoch,
            TriggeredBy = ScheduledRunTrigger.Schedule,
            ReportProgressAsync = reportProgress
        };

    private static GraphWorkflowNodeRunSnapshot EndNode(string outcome) =>
        NodeRun("end", GraphWorkflowNodeKind.End, GraphWorkflowNodeRunStatus.Succeeded,
            $$"""{ "status": "succeeded", "attempt": 1, "branch": null, "output": { "outcome": "{{outcome}}", "result": "model text" } }""");

    private static GraphWorkflowNodeRunSnapshot NodeRun(string nodeKey, GraphWorkflowNodeKind kind, GraphWorkflowNodeRunStatus status, string? outputJson) =>
        new()
        {
            Id = Guid.NewGuid(),
            RunId = RunId,
            NodeKey = nodeKey,
            Kind = kind,
            Status = status,
            Attempt = 1,
            PendingDecisionKind = null,
            DecisionOperationId = null,
            DecidedBySubject = null,
            FailureClass = GraphWorkflowFailureClass.None,
            Error = null,
            InputJson = null,
            OutputJson = outputJson,
            InvocationId = null,
            StartedAtUtc = null,
            CompletedAtUtc = null,
            UpdatedAtUtc = 0
        };

    private static GraphWorkflowRunDetail Detail(GraphWorkflowRunStatus status,
        GraphWorkflowNodeRunSnapshot? nodeRun = null,
        GraphWorkflowFailureClass failureClass = GraphWorkflowFailureClass.None) =>
        new()
        {
            Run = new GraphWorkflowRunSnapshot
            {
                Id = RunId,
                RequestId = Guid.NewGuid(),
                DefinitionId = DefinitionId,
                DefinitionVersion = 1,
                GraphHash = "hash",
                Status = status,
                FailureClass = failureClass,
                GraphJson = "{}",
                InputJson = null,
                OutputJson = null,
                Seq = 0,
                Version = 0,
                CancelRequestedAtUtc = null,
                StartedAtUtc = null,
                CompletedAtUtc = null,
                CreatedAtUtc = 0,
                TriggerSource = GraphWorkflowTriggerSource.Schedule
            },
            NodeRuns = nodeRun is null ? [] : [nodeRun]
        };

    /// <summary>A clock that moves forward by each timer's due time and fires it at once, so a polling wait never sleeps.</summary>
    private sealed class SteppingClock : TimeProvider
    {
        private long _nowTicks = DateTimeOffset.UnixEpoch.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _nowTicks), TimeSpan.Zero);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Add(ref _nowTicks, dueTime.Ticks);
            _ = Task.Run(() => callback(state));
            return new InertTimer();
        }

        private sealed class InertTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class Harness
    {
        public Harness(bool graphWorkflowsEnabled = true, int maxRunInputBytes = 64 * 1024)
        {
            var services = new ServiceCollection();
            services.AddSingleton(RunService);
            services.AddSingleton(Store);
            services.AddSingleton<INodeRuntimeSettings>(StubNodeRuntimeSettings.Create().WithGraphWorkflowsEnabled(graphWorkflowsEnabled).Build());
            var provider = services.BuildServiceProvider();
            var options = Options.Create(new GraphWorkflowOptions { MaxRunInputBytes = maxRunInputBytes });

            Handler = new RunGraphWorkflowHandler(provider.GetRequiredService<IServiceScopeFactory>(), options, new SteppingClock());
        }

        public IGraphWorkflowRunService RunService { get; } = Substitute.For<IGraphWorkflowRunService>();

        public IGraphWorkflowStore Store { get; } = Substitute.For<IGraphWorkflowStore>();

        public RunGraphWorkflowHandler Handler { get; }
    }
}
