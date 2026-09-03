namespace XE_Local_AI_Engine.Tests.Benchmarks;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Benchmarks.PythonTests;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class BenchmarkJudgeExecutorTests
{
    private static readonly Guid AttemptId = new("33333333-3333-3333-3333-333333333333");
    private static readonly Guid RevisionId = new("44444444-4444-4444-4444-444444444444");
    private const string PolicyHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>A one-criterion rubric, so a judge reply in the test stays readable.</summary>
    private const string JudgeReply =
        "{\"schemaVersion\":2,\"criteria\":[{\"id\":\"correctness\",\"score\":5,\"rationale\":\"excellent\"}],\"summary\":\"good enough\"}";

    [Test]
    public async Task Execute_ForATruncatedPrimary_TellsTheJudgeTheAnswerWasCutOff()
    {
        // The judging still runs — a truncated answer is a real answer that deserves a bad score, not an absent one —
        // but nothing else in the request says the answer stops mid-sentence, and a judge that cannot see that scores
        // the fragment as if it were the whole thing.
        var installed = Installed();
        var snapshot = Snapshot(installed);
        var run = Run(snapshot, BenchmarkRunJudgeStates.Running, version: 4, primaryStopReason: "length");
        var store = Substitute.For<IBenchmarkStore>();
        StubJudgeAttempt(store, installed);
        store.MarkJudgeSucceededAsync(Arg.Any<BenchmarkJudgeSuccessCommand>(), Arg.Any<CancellationToken>())
             .Returns(call => run with
             {
                 Judge = JudgeView(BenchmarkRunJudgeStates.Succeeded),
                 Version = 5
             });
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        RuntimePackage? assignedPackage = null;
        await using var assignment = new TrackingAsyncDisposable();
        dispatcher.ReportInvocationAssignedAsync(Arg.Do<RuntimePackage>(value => assignedPackage = value), Arg.Any<CancellationToken>())
                  .Returns(assignment);
        var runner = Substitute.For<IInvocationRunner>();
        runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>())
              .Returns(async call =>
              {
                  var execution = call.Arg<InvocationExecutionContext>();
                  var invocationId = execution.Package.InvocationId;
                  _ = await AssertEx.NotNull(execution.GenerationAdmissionPolicy).EvaluateAsync(new InvocationGenerationAdmissionContext
                  {
                      InvocationId = invocationId,
                      RequestedContextTokens = 4096,
                      EffectiveContextTokens = 4096,
                      ModelId = "judge.gguf",
                      ProviderName = "llamacpp"
                  });
                  dispatcher.InvocationStateChanged += Raise.EventWith(dispatcher,
                      new InvocationStateChangedEventArgs(State(invocationId, JudgeReply)));
              });
        await using var lease = new FakeLease(installed);
        var executor = Executor(store, snapshot, lease, new JudgeCapacityService(CapacityVerdict.Allow), dispatcher, runner, PassthroughSupervisor());

        await executor.ExecuteAsync(new BenchmarkClaimedWork(2, run.Id, BenchmarkWorkKind.Judge, 1, 2, run, AttemptId), CancellationToken.None);

        var package = AssertEx.NotNull(assignedPackage);
        AssertEx.Equal(BenchmarkJudgePromptV2.SystemPromptFor(primaryOutputTruncated: true), package.ResolvedSystemPrompt);
        using var promptPayload = JsonDocument.Parse(package.ConversationContext[0].Content);
        AssertEx.True(promptPayload.RootElement.GetProperty("primaryOutputTruncated").GetBoolean());
        _ = store.Received(1).MarkJudgeSucceededAsync(Arg.Any<BenchmarkJudgeSuccessCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Execute_SuccessUsesFrozenJudgeContextPersistsStrictResultAndDisposesOwnership()
    {
        var installed = Installed();
        var snapshot = Snapshot(installed);
        var run = Run(snapshot, BenchmarkRunJudgeStates.Running, version: 4);
        var store = Substitute.For<IBenchmarkStore>();
        StubJudgeAttempt(store, installed);
        BenchmarkJudgeSuccessCommand? command = null;
        store.MarkJudgeSucceededAsync(Arg.Do<BenchmarkJudgeSuccessCommand>(value => command = value), Arg.Any<CancellationToken>())
             .Returns(call => run with
             {
                 Judge = JudgeView(BenchmarkRunJudgeStates.Succeeded),
                 LastStreamSequence = call.Arg<BenchmarkJudgeSuccessCommand>().LastStreamSequence,
                 Version = 5
             });
        var capacity = new JudgeCapacityService(CapacityVerdict.Allow);
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        RuntimePackage? assignedPackage = null;
        await using var assignment = new TrackingAsyncDisposable();
        dispatcher.ReportInvocationAssignedAsync(Arg.Do<RuntimePackage>(value => assignedPackage = value), Arg.Any<CancellationToken>())
                  .Returns(assignment);
        var runner = Substitute.For<IInvocationRunner>();
        runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>())
              .Returns(async call =>
              {
                  var execution = call.Arg<InvocationExecutionContext>();
                  var invocationId = execution.Package.InvocationId;
                  _ = await AssertEx.NotNull(execution.GenerationAdmissionPolicy).EvaluateAsync(new InvocationGenerationAdmissionContext
                  {
                      InvocationId = invocationId,
                      RequestedContextTokens = 4096,
                      EffectiveContextTokens = 4096,
                      ModelId = "judge.gguf",
                      ProviderName = "llamacpp"
                  });
                  dispatcher.InvocationStateChanged += Raise.EventWith(dispatcher,
                      new InvocationStateChangedEventArgs(State(invocationId,
                          JudgeReply)));
              });
        await using var lease = new FakeLease(installed);
        var supervisor = PassthroughSupervisor();
        var executor = Executor(store, snapshot, lease, capacity, dispatcher, runner, supervisor);

        await executor.ExecuteAsync(new BenchmarkClaimedWork(2, run.Id, BenchmarkWorkKind.Judge, 1, 2, run, AttemptId), CancellationToken.None);

        var persisted = AssertEx.NotNull(command);
        AssertEx.Equal<int?>(4096, AssertEx.NotNull(capacity.LastRequest).RequiredContextTokens);
        AssertEx.True(persisted.LastStreamSequence > run.LastStreamSequence);
        AssertEx.Contains(Encoding.UTF8.GetString(persisted.JudgeResultJson.Span), "excellent");
        AssertEx.True(assignment.Disposed);
        AssertEx.True(capacity.Reservation.Disposed);
        AssertEx.True(lease.Disposed);
        var package = AssertEx.NotNull(assignedPackage);
        AssertEx.Equal(BenchmarkJudgePromptV2.SystemPrompt, package.ResolvedSystemPrompt);
        using (var promptPayload = JsonDocument.Parse(package.ConversationContext[0].Content))
        {
            AssertEx.Equal(BenchmarkJudgeOutputSchemaV2.Json, promptPayload.RootElement.GetProperty("outputSchema").GetRawText());
            AssertEx.Equal(expected: 1, promptPayload.RootElement.GetProperty("rubric").GetProperty("criteria").GetArrayLength());

            // The graded payload is the visible answer only: reasoning is hidden chain-of-thought, not the answer the
            // rubric scores, and shipping it is what overran the judge window.
            var gradedParts = promptPayload.RootElement.GetProperty("primaryOutputParts");
            AssertEx.Equal(expected: 1, gradedParts.GetArrayLength());
            AssertEx.Equal("output", gradedParts[0].GetProperty("kind").GetString());
            AssertEx.Equal("answer", gradedParts[0].GetProperty("content").GetString());
        }

        // The prompt ASKS for this shape and the parser refuses anything else. Constraining the decode is what makes the
        // two agree: without it a small judge model answers in prose and every judging fails "invalid result".
        var responseSchema = package.ResponseJsonSchema ?? throw new AssertionException("Expected the judge package to carry a response schema.");
        AssertEx.Equal(BenchmarkJudgePolicyVersions.OutputSchemaVersion,
            responseSchema.GetProperty("properties").GetProperty("schemaVersion").GetProperty("const").GetInt32());
        AssertEx.Equal(BenchmarkJudgeOutputSchemaV2.ResponseFormatJson,
            JsonSerializer.Serialize(responseSchema),
            "The judge turn must carry the bound-free response-format schema, not the bounded one the prompt documents.");

        // The server computes the 0..100 score from the criterion scores; the judge never emits an overall.
        AssertEx.Equal<int?>(50, persisted.Score);

        AssertEx.Equal("0", AssertEx.NotNull(package.SamplingOptions).Seed);
        // The judge takes the node default and is deliberately NOT project-tunable: it emits one short constrained JSON
        // object, so a long-reasoning budget would only delay noticing a stuck judge.
        AssertEx.Equal(expected: 900, package.Timeouts.InvocationTimeoutSeconds);
        AssertEx.Equal(expected: 30, package.Timeouts.ToolCallTimeoutSeconds);
        AssertEx.Equal(expected: 60, package.Timeouts.StreamIdleTimeoutSeconds);
        _ = supervisor.Received(1).RunExclusiveBenchmarkAsync(Arg.Is<string>("judge.gguf"),
            ModelRole.Chat,
            Arg.Is<ResolvedLaunchArguments>(arguments => !arguments.ExploreMode && arguments.CtxSize == 4096),
            LlamaServerBenchmarkLaunchPolicy.DeterministicV1,
            Arg.Any<Func<LlamaServerProfilingContext, CancellationToken, Task<bool>>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Execute_ForARevisionStoredUnderAnOlderPromptVersion_RefusesAndNamesTheFix()
    {
        // Fail CLOSED. Reads are deliberately tolerant so the project stays open and re-savable, but judging under the
        // current prompt while the revision promises an older one would file the verdict in the same cohort as
        // verdicts taken under the old wording — exactly what the version exists to prevent.
        var installed = Installed();
        var snapshot = Snapshot(installed);
        var run = Run(snapshot, BenchmarkRunJudgeStates.Running, version: 4);
        var store = Substitute.For<IBenchmarkStore>();
        StubJudgeAttempt(store, installed, promptVersion: BenchmarkJudgePolicyVersions.PromptVersion - 1);
        store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        string? failureMessage = null;
        store.MarkJudgeFailedAsync(run.Id, 2, Arg.Do<string>(message => failureMessage = message), Arg.Any<long>(), Arg.Any<CancellationToken>())
             .Returns(call => run with
             {
                 Judge = JudgeView(BenchmarkRunJudgeStates.Failed, call.ArgAt<string>(2)),
                 Version = 5
             });
        var runner = Substitute.For<IInvocationRunner>();
        await using var lease = new FakeLease(installed);
        var executor = Executor(store, snapshot, lease, new JudgeCapacityService(CapacityVerdict.Allow),
            Substitute.For<IWorkerEventDispatcher>(), runner);

        await executor.ExecuteAsync(new BenchmarkClaimedWork(2, run.Id, BenchmarkWorkKind.Judge, 1, 2, run, AttemptId), CancellationToken.None);

        AssertEx.Equal(BenchmarkJudgeExecutor.OutdatedPolicyVersionMessage, AssertEx.NotNull(failureMessage));
        AssertEx.Contains(failureMessage!, "Re-save the judge", StringComparison.Ordinal);
        await runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default);
    }

    [Test]
    public async Task Execute_ForAnAttemptFrozenUnderAnOlderIdentityScheme_FailsWithTheSupersededReason()
    {
        // D14: the attempt froze its intended identity at enqueue and would write its effective identity now, so a
        // scheme change between the two leaves hashes that cannot be compared. Fail before anything launches.
        var installed = Installed();
        var snapshot = Snapshot(installed);
        var run = Run(snapshot, BenchmarkRunJudgeStates.Running, version: 4);
        var store = Substitute.For<IBenchmarkStore>();
        StubJudgeAttempt(store, installed, launchIdentityScheme: null);
        store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        string? failureMessage = null;
        store.MarkJudgeFailedAsync(run.Id, 2, Arg.Do<string>(message => failureMessage = message), Arg.Any<long>(), Arg.Any<CancellationToken>())
             .Returns(call => run with
             {
                 Judge = JudgeView(BenchmarkRunJudgeStates.Failed, call.ArgAt<string>(2)),
                 Version = 5
             });
        var runner = Substitute.For<IInvocationRunner>();
        await using var lease = new FakeLease(installed);
        var executor = Executor(store, snapshot, lease, new JudgeCapacityService(CapacityVerdict.Allow),
            Substitute.For<IWorkerEventDispatcher>(), runner);

        await executor.ExecuteAsync(new BenchmarkClaimedWork(2, run.Id, BenchmarkWorkKind.Judge, 1, 2, run, AttemptId), CancellationToken.None);

        AssertEx.Contains(AssertEx.NotNull(failureMessage), BenchmarkLaunchIdentityScheme.SupersededReason);
        await runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default);
    }

    [Test]
    public async Task Execute_WhenCapacityRejects_FailsOnlyJudgeWithoutDispatcherOrGeneration()
    {
        var installed = Installed();
        var snapshot = Snapshot(installed);
        var run = Run(snapshot, BenchmarkRunJudgeStates.Running, version: 4);
        var store = Substitute.For<IBenchmarkStore>();
        StubJudgeAttempt(store, installed);
        store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        BenchmarkRunRecord? failed = null;
        store.MarkJudgeFailedAsync(run.Id,
                 2,
                 Arg.Any<string>(),
                 Arg.Any<long>(),
                 Arg.Any<CancellationToken>())
             .Returns(call => failed = run with
             {
                 Judge = JudgeView(BenchmarkRunJudgeStates.Failed, call.ArgAt<string>(2)),
                 LastStreamSequence = call.ArgAt<long>(3),
                 Version = 5
             });
        var capacity = new JudgeCapacityService(CapacityVerdict.RejectInsufficient);
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = Substitute.For<IInvocationRunner>();
        await using var lease = new FakeLease(installed);
        var executor = Executor(store, snapshot, lease, capacity, dispatcher, runner);

        await executor.ExecuteAsync(new BenchmarkClaimedWork(2, run.Id, BenchmarkWorkKind.Judge, 1, 2, run, AttemptId), CancellationToken.None);

        var terminal = AssertEx.NotNull(failed);
        AssertEx.Equal(BenchmarkPrimaryStatus.Succeeded, terminal.PrimaryStatus);
        AssertEx.Equal(BenchmarkRunJudgeStates.Failed, terminal.Judge?.State);
        AssertEx.True(terminal.LastStreamSequence > run.LastStreamSequence);
        _ = dispatcher.DidNotReceiveWithAnyArgs().ReportInvocationAssignedAsync(default!, default);
        await runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default);
        AssertEx.True(lease.Disposed);
    }

    [Test]
    public async Task Execute_WhenCapacityFreesUpDuringTheWait_JudgesInsteadOfFailingTheAttempt()
    {
        // The judge is dequeued by the SAME consumer that just ran the primary, so it routinely asks while the
        // primary's llama-server is still handing back its VRAM. That rejection is transient: re-deciding gets an
        // Allow, and the attempt must reach generation rather than terminalize and force an operator re-judge.
        var installed = Installed();
        var snapshot = Snapshot(installed);
        var run = Run(snapshot, BenchmarkRunJudgeStates.Running, version: 4);
        var store = Substitute.For<IBenchmarkStore>();
        StubJudgeAttempt(store, installed);
        store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        string? failureMessage = null;
        store.MarkJudgeFailedAsync(run.Id, 2, Arg.Do<string>(value => failureMessage = value), Arg.Any<long>(), Arg.Any<CancellationToken>())
             .Returns(call => run with
             {
                 Judge = JudgeView(BenchmarkRunJudgeStates.Failed, call.ArgAt<string>(2)),
                 Version = 5
             });
        var capacity = new JudgeCapacityService(CapacityVerdict.RejectInsufficient,
            CapacityVerdict.RejectInsufficient,
            CapacityVerdict.Allow);
        var runner = Substitute.For<IInvocationRunner>();
        await using var lease = new FakeLease(installed);
        var executor = Executor(store,
            snapshot,
            lease,
            capacity,
            Substitute.For<IWorkerEventDispatcher>(),
            runner,
            admissionRetry: new BenchmarkAdmissionRetry(MaxRetries: 5, TimeSpan.Zero));

        await executor.ExecuteAsync(new BenchmarkClaimedWork(2, run.Id, BenchmarkWorkKind.Judge, 1, 2, run, AttemptId), CancellationToken.None);

        AssertEx.Equal(3, capacity.DecisionCount);
        await runner.ReceivedWithAnyArgs(1).RunAsync(default!, default);
        AssertEx.True(capacity.Reservation.Disposed, "the reservation the third decision handed over must still be released.");
        // Generation itself produced no verdict here, so the attempt still fails — but never for capacity.
        AssertEx.False((failureMessage ?? string.Empty).Contains("capacity", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task Execute_WhenCapacityNeverFrees_FailsAfterTheWaitBudgetAndSaysHowLongItWaited()
    {
        var installed = Installed();
        var snapshot = Snapshot(installed);
        var run = Run(snapshot, BenchmarkRunJudgeStates.Running, version: 4);
        var store = Substitute.For<IBenchmarkStore>();
        StubJudgeAttempt(store, installed);
        store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        string? failureMessage = null;
        store.MarkJudgeFailedAsync(run.Id, 2, Arg.Do<string>(value => failureMessage = value), Arg.Any<long>(), Arg.Any<CancellationToken>())
             .Returns(call => run with
             {
                 Judge = JudgeView(BenchmarkRunJudgeStates.Failed, call.ArgAt<string>(2)),
                 Version = 5
             });
        var capacity = new JudgeCapacityService(CapacityVerdict.RejectInsufficient);
        var runner = Substitute.For<IInvocationRunner>();
        await using var lease = new FakeLease(installed);
        var executor = Executor(store,
            snapshot,
            lease,
            capacity,
            Substitute.For<IWorkerEventDispatcher>(),
            runner,
            admissionRetry: new BenchmarkAdmissionRetry(MaxRetries: 3, TimeSpan.Zero));

        await executor.ExecuteAsync(new BenchmarkClaimedWork(2, run.Id, BenchmarkWorkKind.Judge, 1, 2, run, AttemptId), CancellationToken.None);

        // 3 retries + the first decision, then a terminal failure that tells the operator it was a wait, not a glance.
        AssertEx.Equal(4, capacity.DecisionCount);
        await runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default);
        AssertEx.Contains(AssertEx.NotNull(failureMessage), "No capacity became free after");
    }

    [Test]
    [Arguments("q8_0", "KV cache q8_0")]
    [Arguments(null, "KV cache f16")]
    public async Task Execute_JudgeAdmissionSizesAgainstItsFrozenKvCacheTypeAndLogsWhatItDecidedOn(string? frozenKvCacheType, string loggedKvCacheType)
    {
        // The judge books its OWN frozen KV term — which now rides the ATTEMPT's runtime, not the run snapshot — and the
        // decision is legible afterwards. Auto/f16 must still reach capacity as the unchanged default (null), while the
        // log names the effective type rather than an empty field.
        var installed = Installed();
        var snapshot = Snapshot(installed);
        var run = Run(snapshot, BenchmarkRunJudgeStates.Running, version: 4);
        var store = Substitute.For<IBenchmarkStore>();
        StubJudgeAttempt(store, installed, frozenKvCacheType);
        store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        store.MarkJudgeFailedAsync(run.Id, 2, Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
             .Returns(call => run with
             {
                 Judge = JudgeView(BenchmarkRunJudgeStates.Failed, call.ArgAt<string>(2)),
                 Version = 5
             });
        var capacity = new JudgeCapacityService(CapacityVerdict.RejectInsufficient);
        var logger = new RecordingLogger<BenchmarkJudgeExecutor>();
        await using var lease = new FakeLease(installed);
        var executor = Executor(store,
            snapshot,
            lease,
            capacity,
            Substitute.For<IWorkerEventDispatcher>(),
            Substitute.For<IInvocationRunner>(),
            logger: logger);

        await executor.ExecuteAsync(new BenchmarkClaimedWork(2, run.Id, BenchmarkWorkKind.Judge, 1, 2, run, AttemptId), CancellationToken.None);

        AssertEx.Equal<string?>(frozenKvCacheType, AssertEx.NotNull(capacity.LastRequest).KvCacheType);
        var admission = AssertEx.NotNull(logger.Entries.FirstOrDefault(entry =>
            entry.Level == LogLevel.Information && entry.Message.Contains("capacity admission", StringComparison.Ordinal)));
        foreach (var expected in new[]
                 {
                     run.Id.ToString(),
                     "phase judge",
                     "requested context 4096",
                     "frozen runtime context 4096",
                     loggedKvCacheType,
                     "RejectInsufficient"
                 })
        {
            AssertEx.Contains(admission.Message, expected);
        }
    }

    [Test]
    public async Task Execute_WhenOwnedCancellationWins_UsesWorkItemVersionAndIsIdempotent()
    {
        var installed = Installed();
        var snapshot = Snapshot(installed);
        var run = Run(snapshot, BenchmarkRunJudgeStates.Running, version: 7);
        const int workVersion = 2;
        var store = Substitute.For<IBenchmarkStore>();
        StubJudgeAttempt(store, installed);
        store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        store.MarkJudgeCancelledAsync(run.Id, workVersion, Arg.Any<long>(), Arg.Any<CancellationToken>())
             .Returns(call => run with
             {
                 Judge = JudgeView(BenchmarkRunJudgeStates.Cancelled),
                 Version = run.Version + 1
             });
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        await using var assignment = new TrackingAsyncDisposable();
        dispatcher.ReportInvocationAssignedAsync(Arg.Any<RuntimePackage>(), Arg.Any<CancellationToken>()).Returns(assignment);
        var cancellations = new BenchmarkCancellationRegistry();
        var runner = Substitute.For<IInvocationRunner>();
        runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            AssertEx.True(cancellations.TryCancel(run.Id, BenchmarkWorkKind.Judge));
            return Task.FromCanceled(call.ArgAt<CancellationToken>(1));
        });
        await using var lease = new FakeLease(installed);
        var executor = new BenchmarkJudgeExecutor(store,
            new FixedSnapshotFactory(snapshot),
            new FixedLeaseProvider(lease),
            new JudgeCapacityService(CapacityVerdict.Allow),
            new LocalChatRuntimePackageBuilder(),
            dispatcher,
            runner,
            PassthroughSupervisor(),
            FixedVariantSelector(),
            EndpointBinding(),
            new BenchmarkEventBuffer(Options.Create(new BenchmarkEventBufferOptions())),
            cancellations,
            new StubEnvironmentFacts(),
            new BenchmarkAdmissionRetry(MaxRetries: 0, TimeSpan.Zero),
            Substitute.For<IBenchmarkPythonTestsVerifier>(),
            NullLogger<BenchmarkJudgeExecutor>.Instance);

        await executor.ExecuteAsync(new BenchmarkClaimedWork(2, run.Id, BenchmarkWorkKind.Judge, 1, workVersion, run, AttemptId), CancellationToken.None);

        _ = store.Received(1).MarkJudgeCancelledAsync(run.Id, workVersion, Arg.Any<long>(), Arg.Any<CancellationToken>());
        AssertEx.False(cancellations.TryCancel(run.Id, BenchmarkWorkKind.Judge));
    }

    [Test]
    public async Task Execute_CheckpointsTheJudgeLaunchKeyedByTheClaimedWorkItemBeforeInference()
    {
        var installed = Installed();
        var snapshot = Snapshot(installed);
        var run = Run(snapshot, BenchmarkRunJudgeStates.Running, version: 4);
        var store = Substitute.For<IBenchmarkStore>();
        StubJudgeAttempt(store, installed);
        store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        BenchmarkLaunchReceiptCommand? checkpoint = null;
        store.MarkJudgeLaunchReadyAsync(AttemptId, 2, 2, Arg.Do<BenchmarkLaunchReceiptCommand>(command => checkpoint = command), Arg.Any<string?>(),
                 Arg.Any<CancellationToken>())
             .Returns(true);
        store.MarkJudgeSucceededAsync(Arg.Any<BenchmarkJudgeSuccessCommand>(), Arg.Any<CancellationToken>())
             .Returns(run with
             {
                 Judge = JudgeView(BenchmarkRunJudgeStates.Succeeded),
                 Version = 5
             });
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        await using var assignment = new TrackingAsyncDisposable();
        dispatcher.ReportInvocationAssignedAsync(Arg.Any<RuntimePackage>(), Arg.Any<CancellationToken>()).Returns(assignment);
        var runner = Substitute.For<IInvocationRunner>();
        var checkpointedBeforeInference = false;
        runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>())
              .Returns(call =>
              {
                  checkpointedBeforeInference = checkpoint is not null;
                  var invocationId = call.Arg<InvocationExecutionContext>().Package.InvocationId;
                  dispatcher.InvocationStateChanged += Raise.EventWith(dispatcher,
                      new InvocationStateChangedEventArgs(State(invocationId, JudgeReply)));
                  return Task.CompletedTask;
              });
        await using var lease = new FakeLease(installed);
        var executor = Executor(store, snapshot, lease, new JudgeCapacityService(CapacityVerdict.Allow), dispatcher, runner);

        await executor.ExecuteAsync(new BenchmarkClaimedWork(2, run.Id, BenchmarkWorkKind.Judge, 1, 2, run, AttemptId), CancellationToken.None);

        AssertEx.True(checkpointedBeforeInference, "The judge launch evidence must be durable before the first token is generated.");
        AssertEx.Equal(BenchmarkKvCacheType.SourceAuto, AssertEx.NotNull(checkpoint).KvCacheTypeSource);
        AssertEx.NotNullOrEmpty(checkpoint!.EnvironmentFactsHash);
        _ = store.Received(1)
                 .MarkJudgeLaunchReadyAsync(AttemptId, 2, 2, Arg.Any<BenchmarkLaunchReceiptCommand>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // Verifiable criteria. The run's stored transcript is a thinking model's: reasoning parts around the single
    // visible answer part "answer" (see Run below), so every fixture here verifies against exactly that text.

    [Test]
    public async Task Execute_WhenEveryCriterionIsVerifiable_SpawnsNothingAndJoinsTheCohortOnTheSentinel()
    {
        var installed = Installed();
        var snapshot = Snapshot(installed);
        var run = Run(snapshot, BenchmarkRunJudgeStates.Running, version: 4);
        var store = Substitute.For<IBenchmarkStore>();
        StubJudgeAttempt(store, installed, rubric: VerifiableRubric());
        BenchmarkJudgeSuccessCommand? command = null;
        store.MarkJudgeSucceededAsync(Arg.Do<BenchmarkJudgeSuccessCommand>(value => command = value), Arg.Any<CancellationToken>())
             .Returns(call => run with
             {
                 Judge = JudgeView(BenchmarkRunJudgeStates.Succeeded),
                 Version = 5
             });
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = Substitute.For<IInvocationRunner>();
        var capacity = new JudgeCapacityService(CapacityVerdict.Allow);
        var supervisor = PassthroughSupervisor();
        await using var lease = new FakeLease(installed);
        var executor = Executor(store, snapshot, lease, capacity, dispatcher, runner, supervisor);

        await executor.ExecuteAsync(new BenchmarkClaimedWork(2, run.Id, BenchmarkWorkKind.Judge, 1, 2, run, AttemptId), CancellationToken.None);

        _ = supervisor.DidNotReceive()
                      .RunExclusiveBenchmarkAsync(Arg.Any<string>(),
                          Arg.Any<ModelRole>(),
                          Arg.Any<ResolvedLaunchArguments>(),
                          Arg.Any<LlamaServerBenchmarkLaunchPolicy>(),
                          Arg.Any<Func<LlamaServerProfilingContext, CancellationToken, Task<bool>>>(),
                          Arg.Any<CancellationToken>());
        _ = runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default);
        AssertEx.Equal(0, capacity.DecisionCount, "A rubric that needs no model must not reserve capacity either.");
        AssertEx.False(lease.Disposed, "The judge model was never leased, so there is nothing to dispose.");

        var success = AssertEx.NotNull(command);
        AssertEx.Equal(BenchmarkJudgeExecutionKey.VerifiedSentinel, success.VerifiedExecutionKey);

        // exact(40) passes on "answer", constraint(60) fails its minWords: 40*10*10 / 100 = 40.
        AssertEx.Equal(40, success.Score);
        var result = AssertEx.NotNull(BenchmarkJudgeSerialization.DeserializeResult(success.JudgeResultJson));
        var verifiers = AssertEx.NotNull(result.Verifiers);
        AssertEx.Equal(2, verifiers.Count);
        AssertEx.True(verifiers.Single(static verifier => verifier.Id == "exact_answer").Passed);
        AssertEx.False(verifiers.Single(static verifier => verifier.Id == "long_enough").Passed);
        AssertEx.Equal(BenchmarkJudgeCriterionKinds.Constraint, verifiers.Single(static verifier => verifier.Id == "long_enough").Kind);
        AssertEx.Equal(10, result.Criteria.Single(static score => score.Id == "exact_answer").Score);
        AssertEx.Equal(0, result.Criteria.Single(static score => score.Id == "long_enough").Score);
        AssertEx.Contains(result.Summary, "No judge model was run");
    }

    [Test]
    public async Task Execute_ForAMixedRubric_ShowsTheModelOnlyItsOwnCriteriaAndMergesTheRest()
    {
        var installed = Installed();
        var snapshot = Snapshot(installed);
        var run = Run(snapshot, BenchmarkRunJudgeStates.Running, version: 4);
        var store = Substitute.For<IBenchmarkStore>();
        StubJudgeAttempt(store, installed, rubric: MixedRubric());
        BenchmarkJudgeSuccessCommand? command = null;
        store.MarkJudgeSucceededAsync(Arg.Do<BenchmarkJudgeSuccessCommand>(value => command = value), Arg.Any<CancellationToken>())
             .Returns(call => run with
             {
                 Judge = JudgeView(BenchmarkRunJudgeStates.Succeeded),
                 Version = 5
             });
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        RuntimePackage? assignedPackage = null;
        await using var assignment = new TrackingAsyncDisposable();
        dispatcher.ReportInvocationAssignedAsync(Arg.Do<RuntimePackage>(value => assignedPackage = value), Arg.Any<CancellationToken>())
                  .Returns(assignment);
        var runner = Substitute.For<IInvocationRunner>();
        runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>())
              .Returns(call =>
              {
                  var invocationId = call.Arg<InvocationExecutionContext>().Package.InvocationId;
                  dispatcher.InvocationStateChanged += Raise.EventWith(dispatcher,
                      new InvocationStateChangedEventArgs(State(invocationId, JudgeReply)));
                  return Task.CompletedTask;
              });
        await using var lease = new FakeLease(installed);
        var executor = Executor(store, snapshot, lease, new JudgeCapacityService(CapacityVerdict.Allow), dispatcher, runner, PassthroughSupervisor());

        await executor.ExecuteAsync(new BenchmarkClaimedWork(2, run.Id, BenchmarkWorkKind.Judge, 1, 2, run, AttemptId), CancellationToken.None);

        // The parser demands the reply's criteria array match the rubric it parses against exactly, so the model must
        // be handed the FILTERED rubric — a one-criterion reply against a two-criterion rubric is a failed judging.
        using var promptPayload = JsonDocument.Parse(AssertEx.NotNull(assignedPackage).ConversationContext[0].Content);
        var shown = promptPayload.RootElement.GetProperty("rubric").GetProperty("criteria");
        AssertEx.Equal(1, shown.GetArrayLength());
        AssertEx.Equal("correctness", shown[0].GetProperty("id").GetString());

        var success = AssertEx.NotNull(command);
        AssertEx.Null(success.VerifiedExecutionKey, "A judging that spawned a model keys on the runtime it measured.");

        // correctness(40) scored 5 by the model, exact_answer(60) verified as a pass:
        // (40*5*10 + 60*10*10) / 100 = 80.
        AssertEx.Equal(80, success.Score);
        var result = AssertEx.NotNull(BenchmarkJudgeSerialization.DeserializeResult(success.JudgeResultJson));
        AssertEx.Equal(2, result.Criteria.Count);
        AssertEx.Equal(5, result.Criteria.Single(static score => score.Id == "correctness").Score);
        AssertEx.Equal(10, result.Criteria.Single(static score => score.Id == "exact_answer").Score);
        AssertEx.Equal(1, AssertEx.NotNull(result.Verifiers).Count);
        AssertEx.Equal("good enough", result.Summary);
    }

    [Test]
    public async Task Execute_WhenAVerifierCannotRun_FailsTheAttemptAndNeverScoresIt()
    {
        // The config is one the strict validator would have refused, so reaching the executor means it arrived some
        // other way — a hand-edited row, a policy stored by a future build. Either way the honest answer is a
        // failed judging with a reason, not a 0 that reads as "the answer was bad".
        var installed = Installed();
        var snapshot = Snapshot(installed);
        var run = Run(snapshot, BenchmarkRunJudgeStates.Running, version: 4);
        var store = Substitute.For<IBenchmarkStore>();
        StubJudgeAttempt(store,
            installed,
            rubric: new BenchmarkJudgeRubricV1(BenchmarkJudgePolicyVersions.RubricVersion,
            [
                new BenchmarkJudgeRubricCriterionV1("broken", "Broken", "A config no validator would have passed.", 100,
                    BenchmarkJudgeCriterionKinds.MathAnswer, """{"expected":"not a number"}""")
            ]));
        store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        string? failureMessage = null;
        store.MarkJudgeFailedAsync(run.Id, Arg.Any<long>(), Arg.Do<string>(value => failureMessage = value), Arg.Any<long>(), Arg.Any<CancellationToken>())
             .Returns(call => run with
             {
                 Judge = JudgeView(BenchmarkRunJudgeStates.Failed),
                 Version = 5
             });
        var supervisor = PassthroughSupervisor();
        await using var lease = new FakeLease(installed);
        var executor = Executor(store,
            snapshot,
            lease,
            new JudgeCapacityService(CapacityVerdict.Allow),
            Substitute.For<IWorkerEventDispatcher>(),
            Substitute.For<IInvocationRunner>(),
            supervisor);

        await executor.ExecuteAsync(new BenchmarkClaimedWork(2, run.Id, BenchmarkWorkKind.Judge, 1, 2, run, AttemptId), CancellationToken.None);

        _ = await store.DidNotReceiveWithAnyArgs().MarkJudgeSucceededAsync(default!, default);
        _ = supervisor.DidNotReceive()
                      .RunExclusiveBenchmarkAsync(Arg.Any<string>(),
                          Arg.Any<ModelRole>(),
                          Arg.Any<ResolvedLaunchArguments>(),
                          Arg.Any<LlamaServerBenchmarkLaunchPolicy>(),
                          Arg.Any<Func<LlamaServerProfilingContext, CancellationToken, Task<bool>>>(),
                          Arg.Any<CancellationToken>());
        AssertEx.Contains(AssertEx.NotNull(failureMessage), "cannot be verified");
    }

    /// <summary>
    ///     An item override names its criterion by id. When the rubric no longer has that id — the criterion was
    ///     renamed or dropped after the item was written — applying the override is a no-op, and judging on would
    ///     grade this item against the POLICY's expected answer: a plausible score for a question nobody asked. The
    ///     run is left unranked under its own reason instead, and the model is never leased.
    /// </summary>
    [Test]
    public async Task Execute_WhenAnItemOverrideNamesNoCriterion_FailsTheAttemptRatherThanScoringIt()
    {
        var installed = Installed();
        var snapshot = Snapshot(installed);
        var itemId = Guid.NewGuid();
        var run = Run(snapshot, BenchmarkRunJudgeStates.Running, version: 4) with
        {
            TaskItemId = itemId
        };
        var store = Substitute.For<IBenchmarkStore>();
        StubJudgeAttempt(store, installed);
        store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        store.ListTaskItemsAsync(run.ProjectId, Arg.Any<CancellationToken>())
             .Returns(new[]
             {
                 new BenchmarkTaskItemRecord(itemId, run.ProjectId, null, 0, BenchmarkTaskItemKinds.Prompt, 1, "v1:hash", true,
                     JsonSerializer.SerializeToUtf8Bytes("a prompt"), null,
                     Encoding.UTF8.GetBytes("""{"needle":{"expected":"AX-991"}}"""), null, 1, 0, 0)
             });
        string? failureMessage = null;
        store.MarkJudgeFailedAsync(run.Id, Arg.Any<long>(), Arg.Do<string>(value => failureMessage = value), Arg.Any<long>(), Arg.Any<CancellationToken>())
             .Returns(call => run with
             {
                 Judge = JudgeView(BenchmarkRunJudgeStates.Failed, call.ArgAt<string>(2)),
                 Version = 5
             });
        var supervisor = PassthroughSupervisor();
        await using var lease = new FakeLease(installed);
        var executor = Executor(store,
            snapshot,
            lease,
            new JudgeCapacityService(CapacityVerdict.Allow),
            Substitute.For<IWorkerEventDispatcher>(),
            Substitute.For<IInvocationRunner>(),
            supervisor);

        await executor.ExecuteAsync(new BenchmarkClaimedWork(2, run.Id, BenchmarkWorkKind.Judge, 1, 2, run, AttemptId), CancellationToken.None);

        _ = await store.DidNotReceiveWithAnyArgs().MarkJudgeSucceededAsync(default!, default);
        _ = supervisor.DidNotReceive()
                      .RunExclusiveBenchmarkAsync(Arg.Any<string>(),
                          Arg.Any<ModelRole>(),
                          Arg.Any<ResolvedLaunchArguments>(),
                          Arg.Any<LlamaServerBenchmarkLaunchPolicy>(),
                          Arg.Any<Func<LlamaServerProfilingContext, CancellationToken, Task<bool>>>(),
                          Arg.Any<CancellationToken>());
        AssertEx.True(AssertEx.NotNull(failureMessage).StartsWith(BenchmarkRunJudgeStates.OverrideUnmatchedPrefix, StringComparison.Ordinal),
            "The prefix is what the ranking read turns into the override-unmatched exclusion reason.");
        AssertEx.Contains(failureMessage, "needle", message: "The message names the criterion that matched nothing.");
    }

    /// <summary>Every criterion decided server-side: one that passes on the fixture answer and one that does not.</summary>
    private static BenchmarkJudgeRubricV1 VerifiableRubric() =>
        new(BenchmarkJudgePolicyVersions.RubricVersion,
        [
            new BenchmarkJudgeRubricCriterionV1("exact_answer", "Exact", "The answer text is exact.", 40,
                BenchmarkJudgeCriterionKinds.Exact, """{"expected":"answer"}"""),
            new BenchmarkJudgeRubricCriterionV1("long_enough", "Length", "The answer is long enough.", 60,
                BenchmarkJudgeCriterionKinds.Constraint, """{"minWords":5}""")
        ]);

    private static BenchmarkJudgeRubricV1 MixedRubric() =>
        new(BenchmarkJudgePolicyVersions.RubricVersion,
        [
            new BenchmarkJudgeRubricCriterionV1("correctness", "Correctness", "Is the answer right?", 40),
            new BenchmarkJudgeRubricCriterionV1("exact_answer", "Exact", "The answer text is exact.", 60,
                BenchmarkJudgeCriterionKinds.Exact, """{"expected":"answer"}""")
        ]);

    [Test]
    public async Task Execute_WhenTheExclusiveSpawnIsRefused_RetriesAndOnlyTerminalizesWithTheRefusalReason()
    {
        // The judge is dequeued by the same FIFO consumer as the primary, so a chat that took a lease in between makes
        // profiling's pre-spawn eviction refuse. That is transient: it is waited out, and only an expired budget is
        // terminal — with the refusal's own sentence, not the generic invocation-failed message.
        var installed = Installed();
        var snapshot = Snapshot(installed);
        var run = Run(snapshot, BenchmarkRunJudgeStates.Running, version: 4);
        var store = Substitute.For<IBenchmarkStore>();
        StubJudgeAttempt(store, installed);
        store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        string? failureMessage = null;
        store.MarkJudgeFailedAsync(run.Id, Arg.Any<long>(), Arg.Do<string>(value => failureMessage = value), Arg.Any<long>(), Arg.Any<CancellationToken>())
             .Returns(call => run with
             {
                 Judge = JudgeView(BenchmarkRunJudgeStates.Failed),
                 Version = 5
             });
        var supervisor = RefusingSupervisor();
        await using var lease = new FakeLease(installed);
        var executor = Executor(store,
            snapshot,
            lease,
            new JudgeCapacityService(CapacityVerdict.Allow),
            Substitute.For<IWorkerEventDispatcher>(),
            Substitute.For<IInvocationRunner>(),
            supervisor,
            admissionRetry: new BenchmarkAdmissionRetry(MaxRetries: 2, TimeSpan.Zero));

        await executor.ExecuteAsync(new BenchmarkClaimedWork(2, run.Id, BenchmarkWorkKind.Judge, 1, 2, run, AttemptId), CancellationToken.None);

        _ = supervisor.Received(3).RunExclusiveBenchmarkAsync(Arg.Any<string>(),
            Arg.Any<ModelRole>(),
            Arg.Any<ResolvedLaunchArguments>(),
            Arg.Any<LlamaServerBenchmarkLaunchPolicy>(),
            Arg.Any<Func<LlamaServerProfilingContext, CancellationToken, Task<bool>>>(),
            Arg.Any<CancellationToken>());
        AssertEx.Contains(AssertEx.NotNull(failureMessage), "was still in use after 0 s", StringComparison.Ordinal);
        AssertEx.Contains(failureMessage!, "the benchmark did not run", StringComparison.Ordinal);
    }

    /// <summary>A supervisor whose exclusive spawn always refuses: a warm role for the model is serving inference.</summary>
    private static ILlamaServerProcessSupervisor RefusingSupervisor()
    {
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.RunExclusiveBenchmarkAsync(Arg.Any<string>(),
                      Arg.Any<ModelRole>(),
                      Arg.Any<ResolvedLaunchArguments>(),
                      Arg.Any<LlamaServerBenchmarkLaunchPolicy>(),
                      Arg.Any<Func<LlamaServerProfilingContext, CancellationToken, Task<bool>>>(),
                      Arg.Any<CancellationToken>())
                  .Returns<bool>(call => throw new LlamaServerProfilingRefusedException(call.ArgAt<string>(0), ModelRole.Chat, activeLeases: 1,
                      LlamaServerProfilingRefusalReason.InUse));
        return supervisor;
    }

    private static BenchmarkJudgeExecutor Executor(IBenchmarkStore store,
        BenchmarkRuntimeSnapshotV1 snapshot,
        FakeLease lease,
        ICapacityService capacity,
        IWorkerEventDispatcher dispatcher,
        IInvocationRunner runner,
        ILlamaServerProcessSupervisor? supervisor = null,
        IBenchmarkCancellationRegistry? cancellations = null,
        ILogger<BenchmarkJudgeExecutor>? logger = null,
        BenchmarkAdmissionRetry? admissionRetry = null,
        IBenchmarkPythonTestsVerifier? pythonTests = null) =>
        new(store,
            new FixedSnapshotFactory(snapshot),
            new FixedLeaseProvider(lease),
            capacity,
            new LocalChatRuntimePackageBuilder(),
            dispatcher,
            runner,
            supervisor ?? PassthroughSupervisor(),
            FixedVariantSelector(),
            EndpointBinding(),
            new BenchmarkEventBuffer(Options.Create(new BenchmarkEventBufferOptions())),
            cancellations ?? new BenchmarkCancellationRegistry(),
            new StubEnvironmentFacts(),
            // Default: decide ONCE and never wait, so the tests that assert the rejection path stay instant. Tests
            // about the wait itself pass their own budget with a zero interval.
            admissionRetry ?? new BenchmarkAdmissionRetry(MaxRetries: 0, TimeSpan.Zero),
            pythonTests ?? Substitute.For<IBenchmarkPythonTestsVerifier>(),
            logger ?? NullLogger<BenchmarkJudgeExecutor>.Instance);

    /// <summary>
    ///     Wires the reads the executor makes before it spawns: the attempt it was handed and the policy revision that
    ///     attempt was enqueued under. Both carry the payloads the executor deserializes.
    /// </summary>
    private static void StubJudgeAttempt(IBenchmarkStore store,
        InstalledModelSnapshot installed,
        string? kvCacheType = null,
        int? promptVersion = null,
        BenchmarkJudgeRubricV1? rubric = null,
        int? launchIdentityScheme = LlamaServerLaunchProjection.IdentitySchemeVersion)
    {
        store.GetJudgeAttemptAsync(AttemptId, Arg.Any<CancellationToken>()).Returns(Attempt(installed, kvCacheType, launchIdentityScheme));
        store.GetJudgePolicyRevisionAsync(RevisionId, Arg.Any<CancellationToken>()).Returns(Revision(promptVersion, rubric));
    }

    private static BenchmarkJudgeAttemptRecord Attempt(InstalledModelSnapshot installed,
        string? kvCacheType = null,
        int? launchIdentityScheme = LlamaServerLaunchProjection.IdentitySchemeVersion) =>
        new(AttemptId,
            Guid.NewGuid(),
            1,
            RevisionId,
            1,
            BenchmarkJudgeSerialization.SerializeRuntime(new BenchmarkJudgeRuntimeV1(BenchmarkJudgeRuntimeV1.CurrentSchemaVersion,
                BenchmarkInstalledModelSnapshotMapper.ToSnapshot(installed),
                4096,
                Runtime(4096, kvCacheType),
                BenchmarkFrozenPolicies.DeterministicSampling())),
            null,
            BenchmarkJudgeAttemptStatus.Running,
            null,
            null,
            null,
            1,
            null,
            null,
            1,
            new BenchmarkRunLaunchIntent("cpu", BenchmarkKvCacheType.F16, BenchmarkKvCacheType.SourceAuto, "cpu-variant",
                LlamaServerLaunchProjection.FlashAttentionAuto, "intended", null, launchIdentityScheme));

    private static BenchmarkJudgePolicyRevisionRecord Revision(int? promptVersion = null, BenchmarkJudgeRubricV1? rubric = null) =>
        new(RevisionId, Guid.NewGuid(), 1, BenchmarkJudgeSerialization.SerializePolicy(Policy(promptVersion, rubric)), PolicyHash, null, 1, 1);

    private static BenchmarkJudgePolicyV1 Policy(int? promptVersion = null, BenchmarkJudgeRubricV1? rubric = null) =>
        new(new BenchmarkJudgePolicyModelV1("judge.gguf", V1('c'), [new string('b', 64)]),
            4096,
            promptVersion ?? BenchmarkJudgePolicyVersions.PromptVersion,
            BenchmarkJudgePolicyVersions.OutputSchemaVersion,
            BenchmarkJudgePolicySamplingV1.FromSnapshot(BenchmarkFrozenPolicies.DeterministicSampling()),
            rubric ?? new BenchmarkJudgeRubricV1(BenchmarkJudgePolicyVersions.RubricVersion,
            [
                new BenchmarkJudgeRubricCriterionV1("correctness", "Correctness", "Is the answer right?", 40)
            ]),
            ReferenceAnswer: null);

    private sealed class StubEnvironmentFacts : IRuntimeEnvironmentFactsProvider
    {
        public Task<RuntimeEnvironmentFactsV1> CaptureAsync(GpuVariant variant, CancellationToken ct) =>
            Task.FromResult(new RuntimeEnvironmentFactsV1(1, null, null, null, 42, ["hardware"]));
    }

    private static InvocationState State(Guid invocationId, string content) =>
        new()
        {
            InvocationId = invocationId,
            ConversationId = Guid.NewGuid(),
            Status = InvocationStatus.Completed,
            StreamedContent = content,
            StartedAt = DateTimeOffset.UnixEpoch,
            LastUpdatedAt = DateTimeOffset.UnixEpoch
        };

    private static BenchmarkRunJudgeView JudgeView(string state, string? errorMessage = null) =>
        new(state, AttemptId, null, 1, RevisionId, 1, 1, null, errorMessage, PolicyCurrent: true, ExecutionCurrent: false, null);

    private static BenchmarkRunRecord Run(BenchmarkRuntimeSnapshotV1 snapshot, string judgeState, long version, string? primaryStopReason = null) =>
        new(Guid.NewGuid(),
            snapshot.ProjectId,
            new byte[]
            {
                1
            },
            snapshot.PrimaryModel.ModelName,
            snapshot.PrimaryModel.Origin,
            snapshot.PrimaryModel.ModelContentFingerprint,
            "Agent",
            1,
            snapshot.RequestedContextTokens,
            BenchmarkPrimaryStatus.Succeeded,
            snapshot.RequestedContextTokens,
            10,
            5,
            500,
            // A thinking model's stored transcript: reasoning parts around the visible answer. The judge must be shown
            // the answer only.
            BenchmarkExecutionSerialization.SerializeParts([
                new BenchmarkOutputPart("reasoning", Content: "hidden chain of thought"),
                new BenchmarkOutputPart("output", Content: "answer"),
                new BenchmarkOutputPart("reasoning", Content: "more hidden thought")
            ]),
            1,
            null,
            null,
            version,
            1,
            1,
            1,
            1,
            null,
            null,
            PrimaryStopReason: primaryStopReason,
            Judge: new BenchmarkRunJudgeView(judgeState, null, null, null, null, null, null, null, null, PolicyCurrent: true, ExecutionCurrent: false, null));

    private static InstalledModelSnapshot Installed()
    {
        var revision = V1('a');
        return new InstalledModelSnapshot("judge.gguf",
            revision,
            [],
            revision,
            [
                new InstalledModelPhysicalMember("judge.gguf",
                    InstalledModelPhysicalMemberRole.Weight,
                    12,
                    new string('b', 64),
                    $"sha256:{new string('b', 64)}:12",
                    ["judge.gguf"],
                    true,
                    null)
            ],
            revision,
            LocalModelOrigin.Imported,
            "llamacpp",
            "map-revision",
            "repo/judge",
            "revision",
            "Q4_K_M",
            GgufRole.Chat,
            V1('c'));
    }

    private static BenchmarkRuntimeSnapshotV1 Snapshot(InstalledModelSnapshot installed)
    {
        var model = new BenchmarkInstalledModelSnapshotV1(installed.ModelName,
            installed.RegistryRevision,
            [],
            installed.RegistryAliasSetHash,
            installed.Members.Select(static member => new BenchmarkPhysicalMemberSnapshotV1(member.RelativePath,
                         member.Role,
                         member.SizeBytes,
                         member.Sha256,
                         member.OwningAliases,
                         member.Required,
                         member.MetadataSchemaVersion,
                         member.MemberFingerprint))
                     .ToArray(),
            installed.PhysicalMemberSetHash,
            installed.Origin,
            installed.ProviderName!,
            installed.ProviderMappingRevision,
            installed.RepoId,
            installed.SourceRevision,
            installed.ModelName,
            installed.Quantization,
            "chat",
            installed.ModelContentFingerprint);
        return new BenchmarkRuntimeSnapshotV1(1,
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            "task",
            8192,
            new ResolvedAgentRuntime("prompt", [], null, null, 1, AgentName: "Agent"),
            Runtime(8192),
            BenchmarkFrozenPolicies.DeterministicSampling(),
            model,
            new BenchmarkFreezeDependencySetV1("a", "b", "c", "d", "e", "f"),
            "test",
            1,
            "hash");
    }

    private static string V1(char value) =>
        $"v1:{new string(value, 64)}";

    private static BenchmarkLlamaRuntimeSnapshotV1 Runtime(int contextTokens, string? kvCacheType = null) =>
        new(GpuVariant.Cpu, contextTokens, null, null, null, kvCacheType, kvCacheType, kvCacheType is not null,
            LlamaServerBenchmarkLaunchPolicy.DeterministicV1);

    private static IGpuVariantSelector FixedVariantSelector()
    {
        var selector = Substitute.For<IGpuVariantSelector>();
        selector.SelectVariantAsync(Arg.Any<CancellationToken>()).Returns(GpuVariant.Cpu);
        return selector;
    }

    private static ILlamaServerProcessSupervisor PassthroughSupervisor()
    {
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.RunExclusiveBenchmarkAsync(Arg.Any<string>(),
                      Arg.Any<ModelRole>(),
                      Arg.Any<ResolvedLaunchArguments>(),
                      Arg.Any<LlamaServerBenchmarkLaunchPolicy>(),
                      Arg.Any<Func<LlamaServerProfilingContext, CancellationToken, Task<bool>>>(),
                      Arg.Any<CancellationToken>())
                  .Returns(call =>
                  {
                      var modelName = call.ArgAt<string>(0);
                      var context = new LlamaServerProfilingContext(new LlamaServerEndpoint(modelName, ModelRole.Chat, new Uri("http://127.0.0.1:19001")), []);
                      return call.ArgAt<Func<LlamaServerProfilingContext, CancellationToken, Task<bool>>>(4)(context, call.ArgAt<CancellationToken>(5));
                  });
        return supervisor;
    }

    private static ILlamaServerEndpointBinding EndpointBinding()
    {
        var binding = Substitute.For<ILlamaServerEndpointBinding>();
        binding.Bind(Arg.Any<LlamaServerEndpoint>()).Returns(Substitute.For<IDisposable>());
        return binding;
    }

    private sealed class FixedSnapshotFactory(BenchmarkRuntimeSnapshotV1 snapshot) : IBenchmarkRuntimeSnapshotFactory
    {
        public BenchmarkRuntimeSnapshotV1 Create(BenchmarkRuntimeSnapshotInput input) =>
            throw new NotSupportedException();

        public byte[] Serialize(BenchmarkRuntimeSnapshotV1 snapshot) =>
            throw new NotSupportedException();

        public BenchmarkRuntimeSnapshotV1 Deserialize(ReadOnlySpan<byte> payload) =>
            snapshot;
    }

    private sealed class FixedLeaseProvider(FakeLease lease) : IBenchmarkInstalledModelLeaseProvider
    {
        public Task<IBenchmarkInstalledModelLease> AcquireAsync(string modelName, CancellationToken cancellationToken) =>
            Task.FromResult<IBenchmarkInstalledModelLease>(lease);
    }

    private sealed class FakeLease(InstalledModelSnapshot snapshot) : IBenchmarkInstalledModelLease
    {
        public InstalledModelSnapshot ModelSnapshot { get; } = snapshot;
        InstalledModelSnapshot IBenchmarkInstalledModelLease.Snapshot => ModelSnapshot;
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    ///     Answers each decision with the next verdict, then repeats the last one forever — so a single verdict is a
    ///     constant answer and a sequence models capacity that frees up (or never does) while the phase waits.
    /// </summary>
    private sealed class JudgeCapacityService(params CapacityVerdict[] verdicts) : ICapacityService
    {
        private readonly Queue<CapacityVerdict> _verdicts = new(verdicts);

        public CapacityRequest? LastRequest { get; private set; }
        public int DecisionCount { get; private set; }
        public TrackingDisposable Reservation { get; } = new();

        public Task<CapacityDecision> DecideAsync(string modelName, ModelRole role, CancellationToken ct) =>
            Task.FromResult(new CapacityDecision(Next(), "capacity", false));

        public Task<CapacityDecision> DecideAsync(CapacityRequest request, CancellationToken ct)
        {
            LastRequest = request;
            var verdict = Next();
            return Task.FromResult(new CapacityDecision(verdict,
                "capacity",
                false,
                verdict == CapacityVerdict.Allow ? Reservation : null));
        }

        private CapacityVerdict Next()
        {
            DecisionCount++;
            return _verdicts.Count > 1 ? _verdicts.Dequeue() : _verdicts.Peek();
        }
    }

    private sealed class TrackingDisposable : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() =>
            Disposed = true;
    }

    private sealed class TrackingAsyncDisposable : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
