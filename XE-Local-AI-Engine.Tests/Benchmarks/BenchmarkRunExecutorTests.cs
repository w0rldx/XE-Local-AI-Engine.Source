namespace XE_Local_AI_Engine.Tests.Benchmarks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class BenchmarkRunExecutorTests
{
    [Test]
    public async Task Execute_WhenExecutionSnapshotFingerprintChanged_FailsBeforeCapacityDispatcherOrGeneration()
    {
        var run = Run(BenchmarkPrimaryStatus.Running, version: 2);
        var expected = Installed("model.gguf", 'a');
        var actual = Installed("model.gguf", 'b');
        var store = Substitute.For<IBenchmarkStore>();
        store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        BenchmarkRunRecord? failed = null;
        store.MarkPrimaryFailedAsync(run.Id,
                 run.Version,
                 Arg.Do<string>(message => AssertEx.Contains(message, "installed model changed")),
                 Arg.Do<long>(sequence => AssertEx.True(sequence > 0)),
                 Arg.Any<string?>(),
                 Arg.Any<CancellationToken>())
             .Returns(call => failed = run with
             {
                 PrimaryStatus = BenchmarkPrimaryStatus.Failed,
                 PrimaryErrorMessage = call.ArgAt<string>(2),
                 LastStreamSequence = call.ArgAt<long>(3),
                 Version = run.Version + 1
             });
        var snapshot = Snapshot(expected);
        var capacity = new RecordingCapacityService();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var runner = Substitute.For<IInvocationRunner>();
        await using var lease = new FakeLease(actual);
        var executor = new BenchmarkRunExecutor(store,
            new FixedSnapshotFactory(snapshot),
            new FixedLeaseProvider(lease),
            capacity,
            Substitute.For<ILocalChatRuntimePackageBuilder>(),
            dispatcher,
            runner,
            PassthroughSupervisor(),
            FixedVariantSelector(),
            EndpointBinding(),
            Buffer(),
            new BenchmarkCancellationRegistry(),
            new RecordingEnvironmentFacts(),
            Substitute.For<IBenchmarkJudgeRuntimeResolver>(),
            Substitute.For<IBenchmarkPairwisePlanner>(),
            new BenchmarkAdmissionRetry(MaxRetries: 0, TimeSpan.Zero),
            NullLogger<BenchmarkRunExecutor>.Instance);

        await executor.ExecuteAsync(new BenchmarkClaimedWork(1, run.Id, BenchmarkWorkKind.Primary, 1, 2, run), CancellationToken.None);

        AssertEx.NotNull(failed);
        AssertEx.Equal(0, capacity.DecisionCount);
        _ = dispatcher.DidNotReceiveWithAnyArgs().ReportInvocationAssignedAsync(default!, default);
        await runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default);
        AssertEx.True(lease.Disposed);
    }

    [Test]
    public async Task Execute_QueuedUnderAnOlderIdentityScheme_FailsWithTheSupersededReason()
    {
        // D14: the run froze its intended identity at enqueue and would write its effective identity now. A scheme
        // change between the two makes them incomparable, so the row is failed BEFORE it leases or spawns anything —
        // which is what stops it writing an effective identity the compare UI would render as drift.
        var run = Run(BenchmarkPrimaryStatus.Running, version: 2) with
        {
            PrimaryLaunchIntent = Intent(launchIdentityScheme: null)
        };
        var installed = Installed("model.gguf", 'a');
        var store = Substitute.For<IBenchmarkStore>();
        store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        string? failureMessage = null;
        store.MarkPrimaryFailedAsync(run.Id, run.Version, Arg.Do<string>(message => failureMessage = message), Arg.Any<long>(),
                 Arg.Any<string?>(), Arg.Any<CancellationToken>())
             .Returns(call => run with
             {
                 PrimaryStatus = BenchmarkPrimaryStatus.Failed,
                 PrimaryErrorMessage = call.ArgAt<string>(2),
                 Version = run.Version + 1
             });
        var capacity = new RecordingCapacityService();
        var runner = Substitute.For<IInvocationRunner>();
        await using var lease = new FakeLease(installed);
        var executor = Executor(store, Snapshot(installed), lease, capacity, Substitute.For<IWorkerEventDispatcher>(), runner,
            new BenchmarkCancellationRegistry());

        await executor.ExecuteAsync(new BenchmarkClaimedWork(1, run.Id, BenchmarkWorkKind.Primary, 1, 2, run), CancellationToken.None);

        AssertEx.Contains(AssertEx.NotNull(failureMessage), BenchmarkLaunchIdentityScheme.SupersededReason);
        AssertEx.Equal(0, capacity.DecisionCount);
        await runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default);
        AssertEx.False(lease.Disposed, "the row is failed before a model lease is ever acquired.");
    }

    [Test]
    public async Task Execute_CurrentIdentityScheme_ExecutesNormally_AndNoRecordedIntentIsNotDrained()
    {
        // The guard must be a no-op for current work AND for a row that recorded no intent at all. Both reach the
        // model-fingerprint check below it and fail there instead, which is the reach the guard must not shorten.
        foreach (var intent in new[]
                 {
                     Intent(LlamaServerLaunchProjection.IdentitySchemeVersion),
                     null
                 })
        {
            var run = Run(BenchmarkPrimaryStatus.Running, version: 2) with
            {
                PrimaryLaunchIntent = intent
            };
            var store = Substitute.For<IBenchmarkStore>();
            store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
            string? failureMessage = null;
            store.MarkPrimaryFailedAsync(run.Id, run.Version, Arg.Do<string>(message => failureMessage = message), Arg.Any<long>(),
                     Arg.Any<string?>(), Arg.Any<CancellationToken>())
                 .Returns(call => run with
                 {
                     PrimaryStatus = BenchmarkPrimaryStatus.Failed,
                     PrimaryErrorMessage = call.ArgAt<string>(2),
                     Version = run.Version + 1
                 });
            await using var lease = new FakeLease(Installed("model.gguf", 'b'));
            var executor = Executor(store, Snapshot(Installed("model.gguf", 'a')), lease, new RecordingCapacityService(),
                Substitute.For<IWorkerEventDispatcher>(), Substitute.For<IInvocationRunner>(), new BenchmarkCancellationRegistry());

            await executor.ExecuteAsync(new BenchmarkClaimedWork(1, run.Id, BenchmarkWorkKind.Primary, 1, 2, run), CancellationToken.None);

            AssertEx.Contains(AssertEx.NotNull(failureMessage), "installed model changed");
            AssertEx.False(failureMessage!.Contains(BenchmarkLaunchIdentityScheme.SupersededReason, StringComparison.Ordinal),
                "the cutover guard must not fire for current work or for a row with no recorded intent.");
        }
    }

    [Test]
    public async Task Execute_SuccessUsesFrozenContextPersistsCanonicalPartsAndDisposesOwnedResources()
    {
        var run = Run(BenchmarkPrimaryStatus.Running, version: 2);
        var installed = Installed("model.gguf", 'a');
        var store = Substitute.For<IBenchmarkStore>();
        BenchmarkPrimarySuccessCommand? command = null;
        store.MarkPrimarySucceededAsync(Arg.Do<BenchmarkPrimarySuccessCommand>(value => command = value), Arg.Any<CancellationToken>())
             .Returns(call => run with
             {
                 PrimaryStatus = BenchmarkPrimaryStatus.Succeeded,
                 LastStreamSequence = call.Arg<BenchmarkPrimarySuccessCommand>().LastStreamSequence,
                 Version = 3
             });
        var capacity = new RecordingCapacityService();
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
                      RequestedContextTokens = 8192,
                      EffectiveContextTokens = 8192,
                      ModelId = "model.gguf",
                      ProviderName = "llamacpp"
                  });
                  // Two growth points, i.e. two streamed deltas: the terminal write must persist them as ONE part.
                  dispatcher.InvocationStateChanged += Raise.EventWith(dispatcher,
                      new InvocationStateChangedEventArgs(State(invocationId, InvocationStatus.Running, "ans")));
                  dispatcher.InvocationStateChanged += Raise.EventWith(dispatcher,
                      new InvocationStateChangedEventArgs(State(invocationId, InvocationStatus.Completed, "answer", 20, 100, "stop")));
              });
        await using var lease = new FakeLease(installed);
        var cancellationRegistry = new BenchmarkCancellationRegistry();
        var supervisor = PassthroughSupervisor();
        var executor = Executor(store, Snapshot(installed), lease, capacity, dispatcher, runner, cancellationRegistry, supervisor);

        await executor.ExecuteAsync(new BenchmarkClaimedWork(1, run.Id, BenchmarkWorkKind.Primary, 1, 2, run), CancellationToken.None);

        var persisted = AssertEx.NotNull(command);
        AssertEx.Equal<int?>(8192, AssertEx.NotNull(capacity.LastRequest).RequiredContextTokens);
        var persistedParts = BenchmarkExecutionSerialization.DeserializeParts(persisted.OutputPartsJson.Span);
        AssertEx.ContainsSingle(persistedParts, static part => part.Kind == "output" && part.Content == "answer");
        AssertEx.Equal(expected: 1, persistedParts.Count, "Adjacent same-kind deltas are coalesced into one stored part.");
        AssertEx.Equal<int?>(20, persisted.TotalTokens);
        AssertEx.Equal<double?>(200d, persisted.TokensPerSecond);
        AssertEx.Equal("stop", persisted.PrimaryStopReason, "The run must persist why generation stopped, verbatim.");
        AssertEx.True(persisted.LastStreamSequence > 0);
        AssertEx.True(assignment.Disposed);
        AssertEx.True(AssertEx.NotNull(capacity.Reservation).Disposed);
        AssertEx.True(lease.Disposed);
        var package = AssertEx.NotNull(assignedPackage);
        AssertEx.Null(package.ResponseJsonSchema, "Only the judge is decode-constrained; the primary measurement is not.");
        AssertEx.Equal<float?>(0, AssertEx.NotNull(package.SamplingOptions).Temperature);
        AssertEx.Equal("0", package.SamplingOptions!.Seed);
        AssertEx.Equal(8192, package.SamplingOptions.NumCtx);
        AssertEx.Equal(expected: 900, package.Timeouts.InvocationTimeoutSeconds, "A run with no project budget takes the node default.");
        AssertEx.Equal(expected: 30, package.Timeouts.ToolCallTimeoutSeconds);
        AssertEx.Equal(expected: 60, package.Timeouts.StreamIdleTimeoutSeconds);
        _ = supervisor.Received(1).RunExclusiveBenchmarkAsync(Arg.Is<string>("model.gguf"),
            ModelRole.Chat,
            Arg.Is<ResolvedLaunchArguments>(arguments => !arguments.ExploreMode && arguments.CtxSize == 8192),
            LlamaServerBenchmarkLaunchPolicy.DeterministicV1,
            Arg.Any<Func<LlamaServerProfilingContext, CancellationToken, Task<bool>>>(),
            Arg.Any<CancellationToken>());
        AssertEx.False(cancellationRegistry.TryCancel(run.Id, BenchmarkWorkKind.Primary));
        _ = store.Received(1).MarkPrimarySucceededAsync(Arg.Any<BenchmarkPrimarySuccessCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Execute_WhenTheRuntimeReportedTimings_PersistsTheSplitAndDerivesTokensPerSecondFromDecode()
    {
        // The blended figure this replaces divided the turn's TOTAL tokens by its wall clock, so a long prompt dragged
        // the reported speed down and the same model looked slower purely for having been asked a longer question.
        // tokens/second must now mean decode speed (tg): 89 tokens over 1011.5 ms, NOT 20 tokens over 100 ms.
        var run = Run(BenchmarkPrimaryStatus.Running, version: 2);
        var installed = Installed("model.gguf", 'a');
        var store = Substitute.For<IBenchmarkStore>();
        BenchmarkPrimarySuccessCommand? command = null;
        store.MarkPrimarySucceededAsync(Arg.Do<BenchmarkPrimarySuccessCommand>(value => command = value), Arg.Any<CancellationToken>())
             .Returns(call => run with
             {
                 PrimaryStatus = BenchmarkPrimaryStatus.Succeeded,
                 LastStreamSequence = call.Arg<BenchmarkPrimarySuccessCommand>().LastStreamSequence,
                 Version = 3
             });
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        await using var assignment = new TrackingAsyncDisposable();
        dispatcher.ReportInvocationAssignedAsync(Arg.Any<RuntimePackage>(), Arg.Any<CancellationToken>()).Returns(assignment);
        var throughput = new InvocationThroughput(TimeToFirstTokenMs: 180.25,
            PromptTokens: 123,
            PromptMs: 456.5,
            GenerationTokens: 89,
            GenerationMs: 1011.5,
            CachedPromptTokens: 7,
            SegmentCount: 2);
        var runner = Substitute.For<IInvocationRunner>();
        runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>())
              .Returns(async call =>
              {
                  var execution = call.Arg<InvocationExecutionContext>();
                  var invocationId = execution.Package.InvocationId;
                  _ = await AssertEx.NotNull(execution.GenerationAdmissionPolicy).EvaluateAsync(new InvocationGenerationAdmissionContext
                  {
                      InvocationId = invocationId,
                      RequestedContextTokens = 8192,
                      EffectiveContextTokens = 8192,
                      ModelId = "model.gguf",
                      ProviderName = "llamacpp"
                  });
                  dispatcher.InvocationStateChanged += Raise.EventWith(dispatcher,
                      new InvocationStateChangedEventArgs(State(invocationId, InvocationStatus.Completed, "answer", 20, 100, "stop", throughput)));
              });
        await using var lease = new FakeLease(installed);
        var executor = Executor(store, Snapshot(installed), lease, new RecordingCapacityService(), dispatcher, runner,
            new BenchmarkCancellationRegistry(), PassthroughSupervisor());

        await executor.ExecuteAsync(new BenchmarkClaimedWork(1, run.Id, BenchmarkWorkKind.Primary, 1, 2, run), CancellationToken.None);

        var persisted = AssertEx.NotNull(command);
        var measured = AssertEx.NotNull(persisted.Throughput);
        AssertEx.Equal<double?>(180.25, measured.TtftMs);
        AssertEx.Equal<int?>(123, measured.PromptTokens);
        AssertEx.Equal<double?>(456.5, measured.PromptMs);
        AssertEx.Equal<int?>(89, measured.GenerationTokens);
        AssertEx.Equal<double?>(1011.5, measured.GenerationMs);
        AssertEx.Equal<int?>(7, measured.CachedPromptTokens);
        AssertEx.Equal<int?>(2, measured.SegmentCount, "A tool-calling turn's request count must reach the store, or its sums cannot be read honestly.");
        AssertEx.Equal<double?>(89 * 1000d / 1011.5, persisted.TokensPerSecond,
            "tokens/second is decode throughput now, not the turn's total tokens over its wall clock.");
        // The blended numbers stay exactly as they were, so nothing downstream that already read them changes meaning.
        AssertEx.Equal<int?>(20, persisted.TotalTokens);
        AssertEx.Equal<long>(100, persisted.DurationMs);
    }

    [Test]
    public async Task Execute_WhenGenerationHitsTheTokenBudget_SucceedsButPersistsTheLengthStopReason()
    {
        // The live failure this exists for: a 16k-token answer cut mid-sentence was persisted Succeeded and judged
        // 96/100, indistinguishable from a complete one. The status is deliberately unchanged — the measurement IS
        // real — so the stop reason is the only thing that can carry the truth downstream.
        var run = Run(BenchmarkPrimaryStatus.Running, version: 2);
        var installed = Installed("model.gguf", 'a');
        var store = Substitute.For<IBenchmarkStore>();
        BenchmarkPrimarySuccessCommand? command = null;
        store.MarkPrimarySucceededAsync(Arg.Do<BenchmarkPrimarySuccessCommand>(value => command = value), Arg.Any<CancellationToken>())
             .Returns(call => run with
             {
                 PrimaryStatus = BenchmarkPrimaryStatus.Succeeded,
                 LastStreamSequence = call.Arg<BenchmarkPrimarySuccessCommand>().LastStreamSequence,
                 Version = 3
             });
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        await using var assignment = new TrackingAsyncDisposable();
        dispatcher.ReportInvocationAssignedAsync(Arg.Any<RuntimePackage>(), Arg.Any<CancellationToken>()).Returns(assignment);
        var runner = Substitute.For<IInvocationRunner>();
        runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>())
              .Returns(async call =>
              {
                  var execution = call.Arg<InvocationExecutionContext>();
                  var invocationId = execution.Package.InvocationId;
                  _ = await AssertEx.NotNull(execution.GenerationAdmissionPolicy).EvaluateAsync(new InvocationGenerationAdmissionContext
                  {
                      InvocationId = invocationId,
                      RequestedContextTokens = 8192,
                      EffectiveContextTokens = 8192,
                      ModelId = "model.gguf",
                      ProviderName = "llamacpp"
                  });
                  dispatcher.InvocationStateChanged += Raise.EventWith(dispatcher,
                      new InvocationStateChangedEventArgs(State(invocationId, InvocationStatus.Completed, "cut off mid-", 16384, 100, "length")));
              });
        await using var lease = new FakeLease(installed);
        var executor = Executor(store, Snapshot(installed), lease, new RecordingCapacityService(), dispatcher, runner,
            new BenchmarkCancellationRegistry(), PassthroughSupervisor());

        await executor.ExecuteAsync(new BenchmarkClaimedWork(1, run.Id, BenchmarkWorkKind.Primary, 1, 2, run), CancellationToken.None);

        var persisted = AssertEx.NotNull(command);
        AssertEx.Equal("length", persisted.PrimaryStopReason);
        // Reaching MarkPrimarySucceededAsync at all is the assertion: the failure path terminalizes through
        // MarkPrimaryFailedAsync instead and never gets here.
        _ = store.Received(1).MarkPrimarySucceededAsync(Arg.Any<BenchmarkPrimarySuccessCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Execute_WhenTheTurnEmittedOnlyReasoning_PersistsTheIncompleteStopReason()
    {
        // The provider reports `stop` here: from its side the turn ended normally. But every token went into the
        // scratchpad, so the judge would grade an EMPTY transcript and the ranking would seat that score beside runs
        // that answered. Only the node can see the difference, so only the node can record it.
        var run = Run(BenchmarkPrimaryStatus.Running, version: 2);
        var installed = Installed("model.gguf", 'a');
        var store = Substitute.For<IBenchmarkStore>();
        BenchmarkPrimarySuccessCommand? command = null;
        store.MarkPrimarySucceededAsync(Arg.Do<BenchmarkPrimarySuccessCommand>(value => command = value), Arg.Any<CancellationToken>())
             .Returns(call => run with
             {
                 PrimaryStatus = BenchmarkPrimaryStatus.Succeeded,
                 LastStreamSequence = call.Arg<BenchmarkPrimarySuccessCommand>().LastStreamSequence,
                 Version = 3
             });
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        await using var assignment = new TrackingAsyncDisposable();
        dispatcher.ReportInvocationAssignedAsync(Arg.Any<RuntimePackage>(), Arg.Any<CancellationToken>()).Returns(assignment);
        var runner = Substitute.For<IInvocationRunner>();
        runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>())
              .Returns(async call =>
              {
                  var execution = call.Arg<InvocationExecutionContext>();
                  var invocationId = execution.Package.InvocationId;
                  _ = await AssertEx.NotNull(execution.GenerationAdmissionPolicy).EvaluateAsync(Admission(invocationId));
                  dispatcher.InvocationStateChanged += Raise.EventWith(dispatcher,
                      new InvocationStateChangedEventArgs(State(invocationId, InvocationStatus.Completed, content: "", 4096, 100, "stop",
                          thinkingContent: "Let me think about this for a very long time.")));
              });
        await using var lease = new FakeLease(installed);
        var executor = Executor(store, Snapshot(installed), lease, new RecordingCapacityService(), dispatcher, runner,
            new BenchmarkCancellationRegistry(), PassthroughSupervisor());

        await executor.ExecuteAsync(new BenchmarkClaimedWork(1, run.Id, BenchmarkWorkKind.Primary, 1, 2, run), CancellationToken.None);

        var persisted = AssertEx.NotNull(command);
        AssertEx.Equal(BenchmarkPrimaryStopReasons.Incomplete, persisted.PrimaryStopReason,
            "a turn that emitted only reasoning answered nothing, whatever the provider called it");
        _ = store.Received(1).MarkPrimarySucceededAsync(Arg.Any<BenchmarkPrimarySuccessCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Execute_WhenTheTurnEndedOnAnUnansweredToolCall_PersistsTheIncompleteStopReason()
    {
        // The other silent shape: text, then a tool call, then nothing. The provider reports `tool_calls`, which reads
        // downstream as a finished answer even though the agent is still waiting for a result that never came.
        var run = Run(BenchmarkPrimaryStatus.Running, version: 2);
        var installed = Installed("model.gguf", 'a');
        var store = Substitute.For<IBenchmarkStore>();
        BenchmarkPrimarySuccessCommand? command = null;
        store.MarkPrimarySucceededAsync(Arg.Do<BenchmarkPrimarySuccessCommand>(value => command = value), Arg.Any<CancellationToken>())
             .Returns(call => run with
             {
                 PrimaryStatus = BenchmarkPrimaryStatus.Succeeded,
                 LastStreamSequence = call.Arg<BenchmarkPrimarySuccessCommand>().LastStreamSequence,
                 Version = 3
             });
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        await using var assignment = new TrackingAsyncDisposable();
        dispatcher.ReportInvocationAssignedAsync(Arg.Any<RuntimePackage>(), Arg.Any<CancellationToken>()).Returns(assignment);
        var runner = Substitute.For<IInvocationRunner>();
        runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>())
              .Returns(async call =>
              {
                  var execution = call.Arg<InvocationExecutionContext>();
                  var invocationId = execution.Package.InvocationId;
                  _ = await AssertEx.NotNull(execution.GenerationAdmissionPolicy).EvaluateAsync(Admission(invocationId));
                  dispatcher.InvocationStateChanged += Raise.EventWith(dispatcher,
                      new InvocationStateChangedEventArgs(State(invocationId, InvocationStatus.Running, "Let me look that up.")));
                  dispatcher.ToolCallLifecycleChanged += Raise.EventWith(dispatcher,
                      new ToolCallLifecycleChangedEventArgs(new ToolCallLifecyclePayload
                      {
                          InvocationId = invocationId,
                          ToolCallId = "call-1",
                          ToolName = "web_search",
                          Phase = ToolCallLifecyclePhase.Requested,
                          Arguments = "{}"
                      }));
                  dispatcher.InvocationStateChanged += Raise.EventWith(dispatcher,
                      new InvocationStateChangedEventArgs(State(invocationId, InvocationStatus.Completed, "Let me look that up.", 4096, 100,
                          "tool_calls")));
              });
        await using var lease = new FakeLease(installed);
        var executor = Executor(store, Snapshot(installed), lease, new RecordingCapacityService(), dispatcher, runner,
            new BenchmarkCancellationRegistry(), PassthroughSupervisor());

        await executor.ExecuteAsync(new BenchmarkClaimedWork(1, run.Id, BenchmarkWorkKind.Primary, 1, 2, run), CancellationToken.None);

        var persisted = AssertEx.NotNull(command);
        AssertEx.Equal(BenchmarkPrimaryStopReasons.Incomplete, persisted.PrimaryStopReason,
            "a transcript ending on a tool call nothing answered is not a finished answer");
    }

    [Test]
    public async Task Execute_UsesTheRunsFrozenGenerationTimeoutAndRecordsATimeoutAsItsStopReason()
    {
        // Live: a 27B reasoning run was cancelled at 307 s under the old pinned 300 s, before it could finish OR reach
        // the context ceiling — the clock was measuring the harness. The project now owns the budget, and a run that
        // still runs out of it says so instead of failing with an unattributable "the invocation failed".
        var run = Run(BenchmarkPrimaryStatus.Running, version: 2, invocationTimeoutSeconds: 1800);
        var installed = Installed("model.gguf", 'a');
        var store = Substitute.For<IBenchmarkStore>();
        store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        string? stopReason = null;
        store.MarkPrimaryFailedAsync(run.Id,
                 run.Version,
                 Arg.Any<string>(),
                 Arg.Any<long>(),
                 Arg.Do<string?>(reason => stopReason = reason),
                 Arg.Any<CancellationToken>())
             .Returns(run with
             {
                 PrimaryStatus = BenchmarkPrimaryStatus.Failed,
                 Version = 3
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
                  var timedOut = State(invocationId, InvocationStatus.Failed, "half an ans");
                  timedOut.FailureCategory = FailureCategory.Timeout;
                  dispatcher.InvocationStateChanged += Raise.EventWith(dispatcher, new InvocationStateChangedEventArgs(timedOut));
                  return Task.CompletedTask;
              });
        await using var lease = new FakeLease(installed);
        var executor = Executor(store, Snapshot(installed), lease, new RecordingCapacityService(), dispatcher, runner,
            new BenchmarkCancellationRegistry(), PassthroughSupervisor());

        await executor.ExecuteAsync(new BenchmarkClaimedWork(1, run.Id, BenchmarkWorkKind.Primary, 1, 2, run), CancellationToken.None);

        AssertEx.Equal(expected: 1800, AssertEx.NotNull(assignedPackage).Timeouts.InvocationTimeoutSeconds);
        AssertEx.Equal(expected: 30, assignedPackage!.Timeouts.ToolCallTimeoutSeconds, "Tool-call and stream-idle stay pinned: they bound a STALL.");
        AssertEx.Equal(expected: 60, assignedPackage.Timeouts.StreamIdleTimeoutSeconds);
        AssertEx.Equal("timeout", stopReason);
    }

    [Test]
    public async Task Execute_ReplaysTheFrozenReasoningBudgetAndItsFrozenEnforceability()
    {
        // Both halves are FROZEN, not re-resolved: the budget so the run replays the number it was created with, and
        // the enforceability answer so a re-detection between freeze and run cannot quietly turn a cap on or off.
        // llama-server accepts the budget field and ignores it on a template with no reasoning end marker, so sending
        // one there would advertise a cap that never held.
        var run = Run(BenchmarkPrimaryStatus.Running, version: 2);
        var installed = Installed("model.gguf", 'a');
        var store = Substitute.For<IBenchmarkStore>();
        store.MarkPrimarySucceededAsync(Arg.Any<BenchmarkPrimarySuccessCommand>(), Arg.Any<CancellationToken>())
             .Returns(run with
             {
                 PrimaryStatus = BenchmarkPrimaryStatus.Succeeded,
                 Version = 3
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
                  _ = await AssertEx.NotNull(execution.GenerationAdmissionPolicy).EvaluateAsync(Admission(invocationId));
                  dispatcher.InvocationStateChanged += Raise.EventWith(dispatcher,
                      new InvocationStateChangedEventArgs(State(invocationId, InvocationStatus.Completed, "answer", 100, 50, "stop")));
              });
        await using var lease = new FakeLease(installed);
        var snapshot = Snapshot(installed) with
        {
            PrimarySampling = BenchmarkFrozenPolicies.DeterministicSampling(maxOutputTokens: null,
                reasoningBudgetTokens: 2048,
                reasoningBudgetEnforceable: false)
        };
        var executor = Executor(store, snapshot, lease, new RecordingCapacityService(), dispatcher, runner,
            new BenchmarkCancellationRegistry(), PassthroughSupervisor());

        await executor.ExecuteAsync(new BenchmarkClaimedWork(1, run.Id, BenchmarkWorkKind.Primary, 1, 2, run), CancellationToken.None);

        var package = AssertEx.NotNull(assignedPackage);
        AssertEx.Equal<int?>(2048, AssertEx.NotNull(package.SamplingOptions).ReasoningBudgetTokens);
        AssertEx.False(package.ReasoningBudgetEnforceable, "the frozen capability said this model cannot enforce a budget");
    }

    [Test]
    public async Task Execute_WhenTheBudgetRanOutInsideTheReasoning_RecordsReasoningLengthRatherThanPlainLength()
    {
        // Still truncated for ranking and judging, but it names the reasoning budget as the thing to raise. A plain
        // `length` here sends the operator to the output budget, which is not what ran out.
        var run = Run(BenchmarkPrimaryStatus.Running, version: 2);
        var installed = Installed("model.gguf", 'a');
        var store = Substitute.For<IBenchmarkStore>();
        BenchmarkPrimarySuccessCommand? command = null;
        store.MarkPrimarySucceededAsync(Arg.Do<BenchmarkPrimarySuccessCommand>(value => command = value), Arg.Any<CancellationToken>())
             .Returns(run with
             {
                 PrimaryStatus = BenchmarkPrimaryStatus.Succeeded,
                 Version = 3
             });
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        await using var assignment = new TrackingAsyncDisposable();
        dispatcher.ReportInvocationAssignedAsync(Arg.Any<RuntimePackage>(), Arg.Any<CancellationToken>()).Returns(assignment);
        var runner = Substitute.For<IInvocationRunner>();
        runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>())
              .Returns(async call =>
              {
                  var execution = call.Arg<InvocationExecutionContext>();
                  var invocationId = execution.Package.InvocationId;
                  _ = await AssertEx.NotNull(execution.GenerationAdmissionPolicy).EvaluateAsync(Admission(invocationId));
                  dispatcher.InvocationStateChanged += Raise.EventWith(dispatcher,
                      new InvocationStateChangedEventArgs(State(invocationId, InvocationStatus.Completed, content: "", 16384, 100, "length",
                          thinkingContent: "still thinking when the budget ran out")));
              });
        await using var lease = new FakeLease(installed);
        var executor = Executor(store, Snapshot(installed), lease, new RecordingCapacityService(), dispatcher, runner,
            new BenchmarkCancellationRegistry(), PassthroughSupervisor());

        await executor.ExecuteAsync(new BenchmarkClaimedWork(1, run.Id, BenchmarkWorkKind.Primary, 1, 2, run), CancellationToken.None);

        AssertEx.Equal(BenchmarkPrimaryStopReasons.ReasoningLength, AssertEx.NotNull(command).PrimaryStopReason);
        AssertEx.True(BenchmarkPrimaryStopReasons.IsTruncated(BenchmarkPrimaryStopReasons.ReasoningLength),
            "it must still read as truncated, or ranking would exclude one shape and rank the other");
    }

    [Test]
    public async Task Execute_WhenCapacityFreesUpDuringTheWait_RunsInsteadOfFailingTheRun()
    {
        // A capacity rejection means something holds the bytes RIGHT NOW — a preceding run's llama-server still
        // releasing its VRAM, typically. The run must wait for that instead of terminalizing on the first no.
        var run = Run(BenchmarkPrimaryStatus.Running, version: 2);
        var installed = Installed("model.gguf", 'a');
        var store = Substitute.For<IBenchmarkStore>();
        store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        store.MarkPrimaryFailedAsync(run.Id,
                 run.Version,
                 Arg.Any<string>(),
                 Arg.Any<long>(),
                 Arg.Any<string?>(),
                 Arg.Any<CancellationToken>())
             .Returns(run with
             {
                 PrimaryStatus = BenchmarkPrimaryStatus.Failed,
                 Version = 3
             });
        var capacity = new SequencedCapacityService(CapacityVerdict.RejectInsufficient,
            CapacityVerdict.RejectInsufficient,
            CapacityVerdict.Allow);
        var runner = Substitute.For<IInvocationRunner>();
        await using var lease = new FakeLease(installed);
        var executor = Executor(store,
            Snapshot(installed),
            lease,
            capacity,
            Substitute.For<IWorkerEventDispatcher>(),
            runner,
            new BenchmarkCancellationRegistry(),
            PassthroughSupervisor(),
            admissionRetry: new BenchmarkAdmissionRetry(MaxRetries: 5, TimeSpan.Zero));

        await executor.ExecuteAsync(new BenchmarkClaimedWork(1, run.Id, BenchmarkWorkKind.Primary, 1, 2, run), CancellationToken.None);

        AssertEx.Equal(3, capacity.DecisionCount);
        await runner.ReceivedWithAnyArgs(1).RunAsync(default!, default);
        AssertEx.True(capacity.Reservation.Disposed, "the reservation the third decision handed over must still be released.");
    }

    [Test]
    public async Task Execute_WhenRunnerCancels_TerminalizesPrimaryAndDisposesOwnedResources()
    {
        var run = Run(BenchmarkPrimaryStatus.Running, version: 2);
        var installed = Installed("model.gguf", 'a');
        var store = Substitute.For<IBenchmarkStore>();
        store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        BenchmarkRunRecord? cancelled = null;
        store.MarkPrimaryCancelledAsync(run.Id, run.Version, Arg.Any<long>(), Arg.Any<CancellationToken>())
             .Returns(call => cancelled = run with
             {
                 PrimaryStatus = BenchmarkPrimaryStatus.Cancelled,
                 LastStreamSequence = call.ArgAt<long>(2),
                 Version = 3
             });
        var capacity = new RecordingCapacityService();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        await using var assignment = new TrackingAsyncDisposable();
        dispatcher.ReportInvocationAssignedAsync(Arg.Any<RuntimePackage>(), Arg.Any<CancellationToken>())
                  .Returns(assignment);
        var cancellationRegistry = new BenchmarkCancellationRegistry();
        var runner = Substitute.For<IInvocationRunner>();
        runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>())
              .Returns(call =>
              {
                  AssertEx.True(cancellationRegistry.TryCancel(run.Id, BenchmarkWorkKind.Primary));
                  return Task.FromCanceled(call.ArgAt<CancellationToken>(1));
              });
        await using var lease = new FakeLease(installed);
        var executor = Executor(store,
            Snapshot(installed),
            lease,
            capacity,
            dispatcher,
            runner,
            cancellationRegistry);

        await executor.ExecuteAsync(new BenchmarkClaimedWork(1, run.Id, BenchmarkWorkKind.Primary, 1, 2, run), CancellationToken.None);

        AssertEx.Equal(BenchmarkPrimaryStatus.Cancelled, AssertEx.NotNull(cancelled).PrimaryStatus);
        AssertEx.True(cancelled!.LastStreamSequence > 0);
        AssertEx.True(assignment.Disposed);
        AssertEx.True(AssertEx.NotNull(capacity.Reservation).Disposed);
        AssertEx.True(lease.Disposed);
    }

    [Test]
    public async Task Execute_GrowsThePairwiseCohortOnSuccessOnly_NotOnACancellation()
    {
        // Every other test here hands the executor a bare planner substitute, so "the planner is never called" would
        // pass all of them. This is the one that would fail if the EnsurePairsAsync call were deleted — and the one
        // that would fail if it moved out of the success path, where a cancelled run would start enqueueing
        // comparisons against an answer that does not exist.
        var planner = Substitute.For<IBenchmarkPairwisePlanner>();
        var run = Run(BenchmarkPrimaryStatus.Running, version: 2);
        var installed = Installed("model.gguf", 'a');
        var store = Substitute.For<IBenchmarkStore>();
        store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        store.MarkPrimarySucceededAsync(Arg.Any<BenchmarkPrimarySuccessCommand>(), Arg.Any<CancellationToken>())
             .Returns(run with
             {
                 PrimaryStatus = BenchmarkPrimaryStatus.Succeeded,
                 Version = 3
             });
        store.MarkPrimaryCancelledAsync(run.Id, run.Version, Arg.Any<long>(), Arg.Any<CancellationToken>())
             .Returns(run with
             {
                 PrimaryStatus = BenchmarkPrimaryStatus.Cancelled,
                 Version = 3
             });
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        await using var assignment = new TrackingAsyncDisposable();
        dispatcher.ReportInvocationAssignedAsync(Arg.Any<RuntimePackage>(), Arg.Any<CancellationToken>()).Returns(assignment);
        var cancellations = new BenchmarkCancellationRegistry();
        var cancelNext = false;
        var runner = Substitute.For<IInvocationRunner>();
        runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>())
              .Returns(async call =>
              {
                  if (cancelNext)
                  {
                      AssertEx.True(cancellations.TryCancel(run.Id, BenchmarkWorkKind.Primary));
                      await Task.FromCanceled(call.ArgAt<CancellationToken>(1)).ConfigureAwait(false);
                      return;
                  }

                  var execution = call.Arg<InvocationExecutionContext>();
                  var invocationId = execution.Package.InvocationId;
                  _ = await AssertEx.NotNull(execution.GenerationAdmissionPolicy).EvaluateAsync(new InvocationGenerationAdmissionContext
                  {
                      InvocationId = invocationId,
                      RequestedContextTokens = 8192,
                      EffectiveContextTokens = 8192,
                      ModelId = "model.gguf",
                      ProviderName = "llamacpp"
                  });
                  dispatcher.InvocationStateChanged += Raise.EventWith(dispatcher,
                      new InvocationStateChangedEventArgs(State(invocationId, InvocationStatus.Completed, "answer", 20, 100, "stop")));
              });
        await using var lease = new FakeLease(installed);
        var executor = Executor(store, Snapshot(installed), lease, new RecordingCapacityService(), dispatcher, runner, cancellations,
            pairwisePlanner: planner);

        await executor.ExecuteAsync(new BenchmarkClaimedWork(1, run.Id, BenchmarkWorkKind.Primary, 1, 2, run), CancellationToken.None);
        _ = await planner.Received(1).EnsurePairsAsync(run.ProjectId, Arg.Any<CancellationToken>());

        cancelNext = true;
        await executor.ExecuteAsync(new BenchmarkClaimedWork(1, run.Id, BenchmarkWorkKind.Primary, 1, 2, run), CancellationToken.None);

        _ = await planner.Received(1).EnsurePairsAsync(run.ProjectId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Execute_WhenHostStops_LeavesRunningWorkForStartupRecovery()
    {
        var run = Run(BenchmarkPrimaryStatus.Running, version: 2);
        var installed = Installed("model.gguf", 'a');
        var store = Substitute.For<IBenchmarkStore>();
        var capacity = new RecordingCapacityService();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        await using var assignment = new TrackingAsyncDisposable();
        dispatcher.ReportInvocationAssignedAsync(Arg.Any<RuntimePackage>(), Arg.Any<CancellationToken>())
                  .Returns(assignment);
        var runner = Substitute.For<IInvocationRunner>();
        runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>())
              .Returns(call => Task.FromCanceled(call.ArgAt<CancellationToken>(1)));
        await using var lease = new FakeLease(installed);
        var executor = Executor(store,
            Snapshot(installed),
            lease,
            capacity,
            dispatcher,
            runner,
            new BenchmarkCancellationRegistry());
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        await AssertEx.ThrowsAsync<OperationCanceledException>(() =>
            executor.ExecuteAsync(new BenchmarkClaimedWork(1, run.Id, BenchmarkWorkKind.Primary, 1, 2, run), stopping.Token));

        _ = store.DidNotReceive().MarkPrimaryCancelledAsync(run.Id, run.Version, Arg.Any<long>(), Arg.Any<CancellationToken>());
        _ = store.DidNotReceive().MarkPrimaryFailedAsync(run.Id, run.Version, Arg.Any<string>(), Arg.Any<long>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        AssertEx.True(assignment.Disposed);
        AssertEx.True(AssertEx.NotNull(capacity.Reservation).Disposed);
        AssertEx.True(lease.Disposed);
    }

    [Test]
    public async Task Execute_CapturesEnvironmentFactsBeforeCapacityAndCheckpointsTheReceiptBeforeInference()
    {
        var run = Run(BenchmarkPrimaryStatus.Running, version: 2) with
        {
            PrimaryLaunchIntent = new BenchmarkRunLaunchIntent("cuda", BenchmarkKvCacheType.Q8_0, BenchmarkKvCacheType.SourceAuto,
                null, LlamaServerLaunchProjection.FlashAttentionOn, "intended", "manifest-sha",
                LlamaServerLaunchProjection.IdentitySchemeVersion)
        };
        var installed = Installed("model.gguf", 'a');
        var store = Substitute.For<IBenchmarkStore>();
        BenchmarkLaunchReceiptCommand? checkpoint = null;
        store.MarkPrimaryLaunchReadyAsync(run.Id, 1, 2, Arg.Do<BenchmarkLaunchReceiptCommand>(command => checkpoint ??= command), Arg.Any<CancellationToken>())
             .Returns(true);
        store.MarkPrimarySucceededAsync(Arg.Any<BenchmarkPrimarySuccessCommand>(), Arg.Any<CancellationToken>())
             .Returns(call => run with
             {
                 PrimaryStatus = BenchmarkPrimaryStatus.Succeeded,
                 Version = 3
             });
        var environmentFacts = new RecordingEnvironmentFacts();
        var capacity = new RecordingCapacityService(environmentFacts);
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        await using var assignment = new TrackingAsyncDisposable();
        dispatcher.ReportInvocationAssignedAsync(Arg.Any<RuntimePackage>(), Arg.Any<CancellationToken>()).Returns(assignment);
        var runner = Substitute.For<IInvocationRunner>();
        var checkpointedBeforeInference = false;
        runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>())
              .Returns(async call =>
              {
                  checkpointedBeforeInference = checkpoint is not null;
                  var execution = call.Arg<InvocationExecutionContext>();
                  var invocationId = execution.Package.InvocationId;
                  _ = await AssertEx.NotNull(execution.GenerationAdmissionPolicy).EvaluateAsync(new InvocationGenerationAdmissionContext
                  {
                      InvocationId = invocationId,
                      RequestedContextTokens = 8192,
                      EffectiveContextTokens = 8192,
                      ModelId = "model.gguf",
                      ProviderName = "llamacpp"
                  });
                  dispatcher.InvocationStateChanged += Raise.EventWith(dispatcher,
                      new InvocationStateChangedEventArgs(State(invocationId, InvocationStatus.Completed, "answer", 20, 100)));
              });
        await using var lease = new FakeLease(installed);
        var executor = Executor(store, Snapshot(installed), lease, capacity, dispatcher, runner, new BenchmarkCancellationRegistry(),
            PassthroughSupervisor(Receipt()), environmentFacts);

        await executor.ExecuteAsync(new BenchmarkClaimedWork(1, run.Id, BenchmarkWorkKind.Primary, 1, 2, run), CancellationToken.None);

        AssertEx.Equal(expected: 1, environmentFacts.Captures);
        AssertEx.Equal<GpuVariant?>(GpuVariant.Cpu, environmentFacts.Variant);
        AssertEx.Equal(expected: 1, capacity.EnvironmentCapturesAtDecision, "Environment facts must be captured before capacity is reserved.");
        AssertEx.True(checkpointedBeforeInference, "The launch receipt must be durable before the first token is generated.");
        var command = AssertEx.NotNull(checkpoint);
        AssertEx.Equal("cuda", command.EffectiveBackend);
        AssertEx.Equal<int?>(33, command.PlacementOffloaded);
        AssertEx.Equal<int?>(33, command.PlacementTotal);
        AssertEx.Equal(BenchmarkKvCacheType.SourceAuto, command.KvCacheTypeSource);
        AssertEx.Equal(Receipt().LaunchProjection.ComputeIdentity(), command.EffectiveLaunchIdentity);
        AssertEx.NotNullOrEmpty(command.ReceiptHash);
        AssertEx.NotNullOrEmpty(command.EnvironmentFactsHash);
        AssertEx.NotNullOrEmpty(command.ReceiptJson);
    }

    [Test]
    public async Task Execute_WhenTheCheckpointStoreFails_StillFinishesTheRun()
    {
        var run = Run(BenchmarkPrimaryStatus.Running, version: 2);
        var installed = Installed("model.gguf", 'a');
        var store = Substitute.For<IBenchmarkStore>();
        store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        store.MarkPrimaryLaunchReadyAsync(run.Id, 1, 2, Arg.Any<BenchmarkLaunchReceiptCommand>(), Arg.Any<CancellationToken>())
             .Returns<bool>(_ => throw new BenchmarkConflictException("VersionConflict"));
        BenchmarkPrimarySuccessCommand? succeeded = null;
        store.MarkPrimarySucceededAsync(Arg.Do<BenchmarkPrimarySuccessCommand>(command => succeeded = command), Arg.Any<CancellationToken>())
             .Returns(run with
             {
                 PrimaryStatus = BenchmarkPrimaryStatus.Succeeded,
                 Version = 3
             });
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        await using var assignment = new TrackingAsyncDisposable();
        dispatcher.ReportInvocationAssignedAsync(Arg.Any<RuntimePackage>(), Arg.Any<CancellationToken>()).Returns(assignment);
        var runner = Substitute.For<IInvocationRunner>();
        runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>())
              .Returns(async call =>
              {
                  var execution = call.Arg<InvocationExecutionContext>();
                  var invocationId = execution.Package.InvocationId;
                  _ = await AssertEx.NotNull(execution.GenerationAdmissionPolicy).EvaluateAsync(new InvocationGenerationAdmissionContext
                  {
                      InvocationId = invocationId,
                      RequestedContextTokens = 8192,
                      EffectiveContextTokens = 8192,
                      ModelId = "model.gguf",
                      ProviderName = "llamacpp"
                  });
                  dispatcher.InvocationStateChanged += Raise.EventWith(dispatcher,
                      new InvocationStateChangedEventArgs(State(invocationId, InvocationStatus.Completed, "answer", 20, 100)));
              });
        await using var lease = new FakeLease(installed);
        var executor = Executor(store, Snapshot(installed), lease, new RecordingCapacityService(), dispatcher, runner,
            new BenchmarkCancellationRegistry(), PassthroughSupervisor(Receipt()));

        await executor.ExecuteAsync(new BenchmarkClaimedWork(1, run.Id, BenchmarkWorkKind.Primary, 1, 2, run), CancellationToken.None);

        AssertEx.NotNull(succeeded, "A checkpoint that loses a version race must not cost the run its measurement.");
        _ = store.DidNotReceiveWithAnyArgs().MarkPrimaryFailedAsync(Guid.Empty, default, default!, default, default, default);
    }

    [Test]
    public async Task Execute_AdmissionSizesAgainstTheFrozenRuntimeContext()
    {
        var run = Run(BenchmarkPrimaryStatus.Running, version: 2);
        var installed = Installed("model.gguf", 'a');
        var store = Substitute.For<IBenchmarkStore>();
        store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        store.MarkPrimaryFailedAsync(run.Id, run.Version, Arg.Any<string>(), Arg.Any<long>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
             .Returns(run with
             {
                 PrimaryStatus = BenchmarkPrimaryStatus.Failed,
                 Version = 3
             });
        var capacity = new RecordingCapacityService();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        await using var assignment = new TrackingAsyncDisposable();
        dispatcher.ReportInvocationAssignedAsync(Arg.Any<RuntimePackage>(), Arg.Any<CancellationToken>()).Returns(assignment);
        var runner = Substitute.For<IInvocationRunner>();
        await using var lease = new FakeLease(installed);
        var executor = Executor(store, Snapshot(installed, frozenContextTokens: 12288), lease, capacity, dispatcher, runner,
            new BenchmarkCancellationRegistry());

        await executor.ExecuteAsync(new BenchmarkClaimedWork(1, run.Id, BenchmarkWorkKind.Primary, 1, 2, run), CancellationToken.None);

        AssertEx.Equal<int?>(12288, AssertEx.NotNull(capacity.LastRequest).RequiredContextTokens);
    }

    [Test]
    public async Task Execute_AdmissionSizesAgainstTheFrozenKvCacheTypeAndLogsWhatItDecidedOn()
    {
        // The ledger books the KV term the run will really hold, and the decision is legible afterwards: the requested
        // and frozen contexts differ here on purpose, so a line that logged the wrong one is distinguishable.
        var run = Run(BenchmarkPrimaryStatus.Running, version: 2);
        var installed = Installed("model.gguf", 'a');
        var store = Substitute.For<IBenchmarkStore>();
        store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        store.MarkPrimaryFailedAsync(run.Id, run.Version, Arg.Any<string>(), Arg.Any<long>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
             .Returns(run with
             {
                 PrimaryStatus = BenchmarkPrimaryStatus.Failed,
                 Version = 3
             });
        var capacity = new RecordingCapacityService();
        var logger = new RecordingLogger<BenchmarkRunExecutor>();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        await using var assignment = new TrackingAsyncDisposable();
        dispatcher.ReportInvocationAssignedAsync(Arg.Any<RuntimePackage>(), Arg.Any<CancellationToken>()).Returns(assignment);
        await using var lease = new FakeLease(installed);
        var executor = Executor(store,
            Snapshot(installed, frozenContextTokens: 12288, frozenKvCacheType: "q4_0"),
            lease,
            capacity,
            dispatcher,
            Substitute.For<IInvocationRunner>(),
            new BenchmarkCancellationRegistry(),
            logger: logger);

        await executor.ExecuteAsync(new BenchmarkClaimedWork(1, run.Id, BenchmarkWorkKind.Primary, 1, 2, run), CancellationToken.None);

        AssertEx.Equal("q4_0", AssertEx.NotNull(capacity.LastRequest).KvCacheType);
        var admission = AssertEx.NotNull(logger.Entries.FirstOrDefault(entry =>
            entry.Level == LogLevel.Information && entry.Message.Contains("capacity admission", StringComparison.Ordinal)));
        foreach (var expected in new[]
                 {
                     run.Id.ToString(),
                     "phase primary",
                     "model.gguf",
                     "requested context 8192",
                     "frozen runtime context 12288",
                     "KV cache q4_0",
                     "Allow"
                 })
        {
            AssertEx.Contains(admission.Message, expected);
        }
    }

    [Test]
    public async Task Execute_WithNoFrozenKvCacheType_AdmitsWithTheFp16DefaultAndLogsIt()
    {
        var run = Run(BenchmarkPrimaryStatus.Running, version: 2);
        var installed = Installed("model.gguf", 'a');
        var store = Substitute.For<IBenchmarkStore>();
        store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        store.MarkPrimaryFailedAsync(run.Id, run.Version, Arg.Any<string>(), Arg.Any<long>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
             .Returns(run with
             {
                 PrimaryStatus = BenchmarkPrimaryStatus.Failed,
                 Version = 3
             });
        var capacity = new RecordingCapacityService();
        var logger = new RecordingLogger<BenchmarkRunExecutor>();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        await using var assignment = new TrackingAsyncDisposable();
        dispatcher.ReportInvocationAssignedAsync(Arg.Any<RuntimePackage>(), Arg.Any<CancellationToken>()).Returns(assignment);
        await using var lease = new FakeLease(installed);
        var executor = Executor(store, Snapshot(installed), lease, capacity, dispatcher, Substitute.For<IInvocationRunner>(),
            new BenchmarkCancellationRegistry(), logger: logger);

        await executor.ExecuteAsync(new BenchmarkClaimedWork(1, run.Id, BenchmarkWorkKind.Primary, 1, 2, run), CancellationToken.None);

        AssertEx.Null(AssertEx.NotNull(capacity.LastRequest).KvCacheType, "Auto/f16 must reach capacity as the unchanged default.");
        AssertEx.True(logger.HasEntry(LogLevel.Information, "KV cache f16"),
            "The log names the effective type, so an f16 run is not an empty field.");
    }

    [Test]
    public async Task Execute_WhenTheSpawnFailsBeforeReadiness_RecordsEnvironmentFactsWithNoReceiptAndKeepsTheSanitizedReason()
    {
        var run = Run(BenchmarkPrimaryStatus.Running, version: 2);
        var installed = Installed("model.gguf", 'a');
        var store = Substitute.For<IBenchmarkStore>();
        store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        BenchmarkLaunchReceiptCommand? checkpoint = null;
        store.MarkPrimaryLaunchReadyAsync(run.Id, 1, 2, Arg.Do<BenchmarkLaunchReceiptCommand>(command => checkpoint = command), Arg.Any<CancellationToken>())
             .Returns(true);
        string? failure = null;
        store.MarkPrimaryFailedAsync(run.Id, run.Version, Arg.Do<string>(message => failure = message), Arg.Any<long>(), Arg.Any<string?>(),
                 Arg.Any<CancellationToken>())
             .Returns(run with
             {
                 PrimaryStatus = BenchmarkPrimaryStatus.Failed,
                 Version = 3
             });
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.RunExclusiveBenchmarkAsync(Arg.Any<string>(),
                      Arg.Any<ModelRole>(),
                      Arg.Any<ResolvedLaunchArguments>(),
                      Arg.Any<LlamaServerBenchmarkLaunchPolicy>(),
                      Arg.Any<Func<LlamaServerProfilingContext, CancellationToken, Task<bool>>>(),
                      Arg.Any<CancellationToken>())
                  .Returns<bool>(_ => throw new LlamaRuntimeException("llama-server exited before it became ready."));
        await using var lease = new FakeLease(installed);
        var executor = Executor(store, Snapshot(installed), lease, new RecordingCapacityService(), Substitute.For<IWorkerEventDispatcher>(),
            Substitute.For<IInvocationRunner>(), new BenchmarkCancellationRegistry(), supervisor);

        await executor.ExecuteAsync(new BenchmarkClaimedWork(1, run.Id, BenchmarkWorkKind.Primary, 1, 2, run), CancellationToken.None);

        var command = AssertEx.NotNull(checkpoint);
        AssertEx.Null(command.ReceiptJson, "A spawn that never reached readiness records no receipt.");
        AssertEx.Null(command.ReceiptHash);
        AssertEx.Null(command.EffectiveBackend);
        AssertEx.NotNullOrEmpty(command.EnvironmentFactsJson);
        AssertEx.Equal("llama-server exited before it became ready.", failure);
        _ = supervisor.Received(1).RunExclusiveBenchmarkAsync(Arg.Any<string>(),
            Arg.Any<ModelRole>(),
            Arg.Any<ResolvedLaunchArguments>(),
            Arg.Any<LlamaServerBenchmarkLaunchPolicy>(),
            Arg.Any<Func<LlamaServerProfilingContext, CancellationToken, Task<bool>>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Execute_WhenTheExclusiveSpawnIsRefused_WaitsAndRetriesInsteadOfFailingTheRun()
    {
        // A chat that took a lease after the run was claimed makes profiling's pre-spawn eviction refuse. That is a
        // transient the chat clears itself, exactly like a capacity rejection — terminalizing here would fail durable
        // queued work whose attempt pins to 1, so the operator loses the measurement to a request that was ending.
        var run = Run(BenchmarkPrimaryStatus.Running, version: 2);
        var installed = Installed("model.gguf", 'a');
        var store = Substitute.For<IBenchmarkStore>();
        store.MarkPrimarySucceededAsync(Arg.Any<BenchmarkPrimarySuccessCommand>(), Arg.Any<CancellationToken>())
             .Returns(call => run with
             {
                 PrimaryStatus = BenchmarkPrimaryStatus.Succeeded,
                 LastStreamSequence = call.Arg<BenchmarkPrimarySuccessCommand>().LastStreamSequence,
                 Version = 3
             });
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        await using var assignment = new TrackingAsyncDisposable();
        dispatcher.ReportInvocationAssignedAsync(Arg.Any<RuntimePackage>(), Arg.Any<CancellationToken>()).Returns(assignment);
        var runner = CompletingRunner(dispatcher);
        await using var lease = new FakeLease(installed);
        var supervisor = RefusingSupervisor(refusals: 1);
        var executor = Executor(store, Snapshot(installed), lease, new RecordingCapacityService(), dispatcher, runner,
            new BenchmarkCancellationRegistry(), supervisor,
            admissionRetry: new BenchmarkAdmissionRetry(MaxRetries: 2, TimeSpan.Zero));

        await executor.ExecuteAsync(new BenchmarkClaimedWork(1, run.Id, BenchmarkWorkKind.Primary, 1, 2, run), CancellationToken.None);

        _ = supervisor.Received(2).RunExclusiveBenchmarkAsync(Arg.Any<string>(),
            Arg.Any<ModelRole>(),
            Arg.Any<ResolvedLaunchArguments>(),
            Arg.Any<LlamaServerBenchmarkLaunchPolicy>(),
            Arg.Any<Func<LlamaServerProfilingContext, CancellationToken, Task<bool>>>(),
            Arg.Any<CancellationToken>());
        _ = store.Received(1).MarkPrimarySucceededAsync(Arg.Any<BenchmarkPrimarySuccessCommand>(), Arg.Any<CancellationToken>());
        _ = store.DidNotReceiveWithAnyArgs().MarkPrimaryFailedAsync(Guid.Empty, default, default!, default, default, default);
    }

    [Test]
    public async Task Execute_WhenTheExclusiveSpawnStaysRefused_FailsWithTheRefusalReasonRatherThanTheGenericMessage()
    {
        var run = Run(BenchmarkPrimaryStatus.Running, version: 2);
        var installed = Installed("model.gguf", 'a');
        var store = Substitute.For<IBenchmarkStore>();
        store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        string? failure = null;
        store.MarkPrimaryFailedAsync(run.Id, run.Version, Arg.Do<string>(message => failure = message), Arg.Any<long>(), Arg.Any<string?>(),
                 Arg.Any<CancellationToken>())
             .Returns(run with
             {
                 PrimaryStatus = BenchmarkPrimaryStatus.Failed,
                 Version = 3
             });
        await using var lease = new FakeLease(installed);
        var supervisor = RefusingSupervisor(refusals: int.MaxValue);
        var executor = Executor(store, Snapshot(installed), lease, new RecordingCapacityService(), Substitute.For<IWorkerEventDispatcher>(),
            Substitute.For<IInvocationRunner>(), new BenchmarkCancellationRegistry(), supervisor,
            admissionRetry: new BenchmarkAdmissionRetry(MaxRetries: 2, TimeSpan.Zero));

        await executor.ExecuteAsync(new BenchmarkClaimedWork(1, run.Id, BenchmarkWorkKind.Primary, 1, 2, run), CancellationToken.None);

        _ = supervisor.Received(3).RunExclusiveBenchmarkAsync(Arg.Any<string>(),
            Arg.Any<ModelRole>(),
            Arg.Any<ResolvedLaunchArguments>(),
            Arg.Any<LlamaServerBenchmarkLaunchPolicy>(),
            Arg.Any<Func<LlamaServerProfilingContext, CancellationToken, Task<bool>>>(),
            Arg.Any<CancellationToken>());
        AssertEx.Contains(AssertEx.NotNull(failure), "model.gguf (Chat) was still in use after 0 s", StringComparison.Ordinal);
        AssertEx.Contains(failure!, "the benchmark did not run", StringComparison.Ordinal);
        AssertEx.False(failure!.Contains("Retry when the model is idle", StringComparison.Ordinal),
            "A terminal row must not carry the SKIP sentence's advice to retry.");
    }

    [Test]
    public async Task Execute_WhenCapacityAlreadyWaited_LeavesTheSpawnWaitOnlyWhatTheBudgetHasLeft()
    {
        // Both waits are sequential and both hold the queue's shared GPU-work admission, so they draw from ONE phase
        // budget. Two capacity retries out of three leave the spawn exactly one: two calls, not four.
        var run = Run(BenchmarkPrimaryStatus.Running, version: 2);
        var installed = Installed("model.gguf", 'a');
        var store = Substitute.For<IBenchmarkStore>();
        store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        store.MarkPrimaryFailedAsync(run.Id, run.Version, Arg.Any<string>(), Arg.Any<long>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
             .Returns(run with
             {
                 PrimaryStatus = BenchmarkPrimaryStatus.Failed,
                 Version = 3
             });
        var capacity = new SequencedCapacityService(CapacityVerdict.RejectInsufficient,
            CapacityVerdict.RejectInsufficient,
            CapacityVerdict.Allow);
        await using var lease = new FakeLease(installed);
        var supervisor = RefusingSupervisor(refusals: int.MaxValue);
        var executor = Executor(store, Snapshot(installed), lease, capacity, Substitute.For<IWorkerEventDispatcher>(),
            Substitute.For<IInvocationRunner>(), new BenchmarkCancellationRegistry(), supervisor,
            admissionRetry: new BenchmarkAdmissionRetry(MaxRetries: 3, TimeSpan.Zero));

        await executor.ExecuteAsync(new BenchmarkClaimedWork(1, run.Id, BenchmarkWorkKind.Primary, 1, 2, run), CancellationToken.None);

        AssertEx.Equal(expected: 3, capacity.DecisionCount, "Two of the phase's three retries went to capacity.");
        _ = supervisor.Received(2).RunExclusiveBenchmarkAsync(Arg.Any<string>(),
            Arg.Any<ModelRole>(),
            Arg.Any<ResolvedLaunchArguments>(),
            Arg.Any<LlamaServerBenchmarkLaunchPolicy>(),
            Arg.Any<Func<LlamaServerProfilingContext, CancellationToken, Task<bool>>>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>A runner that completes the invocation, so a test can assert on the primary's success path.</summary>
    private static IInvocationRunner CompletingRunner(IWorkerEventDispatcher dispatcher)
    {
        var runner = Substitute.For<IInvocationRunner>();
        runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>())
              .Returns(async call =>
              {
                  var execution = call.Arg<InvocationExecutionContext>();
                  var invocationId = execution.Package.InvocationId;

                  // The effective context is only known once the admission policy has been evaluated, and the run
                  // cannot succeed without it.
                  _ = await AssertEx.NotNull(execution.GenerationAdmissionPolicy).EvaluateAsync(new InvocationGenerationAdmissionContext
                  {
                      InvocationId = invocationId,
                      RequestedContextTokens = 8192,
                      EffectiveContextTokens = 8192,
                      ModelId = "model.gguf",
                      ProviderName = "llamacpp"
                  });
                  dispatcher.InvocationStateChanged += Raise.EventWith(dispatcher,
                      new InvocationStateChangedEventArgs(State(invocationId, InvocationStatus.Completed, "answer", 20, 100, "stop")));
              });
        return runner;
    }

    /// <summary>
    ///     A supervisor whose exclusive spawn refuses the first <paramref name="refusals" /> times — the pre-spawn
    ///     eviction found the model serving inference — and then runs the body like <see cref="PassthroughSupervisor" />.
    /// </summary>
    private static ILlamaServerProcessSupervisor RefusingSupervisor(int refusals)
    {
        var remaining = refusals;
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
                      if (remaining-- > 0)
                      {
                          throw new LlamaServerProfilingRefusedException(modelName, ModelRole.Chat, activeLeases: 1,
                              LlamaServerProfilingRefusalReason.InUse);
                      }

                      var context = new LlamaServerProfilingContext(new LlamaServerEndpoint(modelName, ModelRole.Chat, new Uri("http://127.0.0.1:19000")), []);
                      return call.ArgAt<Func<LlamaServerProfilingContext, CancellationToken, Task<bool>>>(4)(context, call.ArgAt<CancellationToken>(5));
                  });
        return supervisor;
    }

    private static LlamaServerLaunchReceipt Receipt() =>
        new(LlamaServerLaunchReceipt.CurrentVersion,
            GpuVariant.Cuda,
            "linux",
            "b10201",
            "exe-sha",
            "manifest-sha",
            LlamaServerLaunchProjection.From(GpuVariant.Cuda, ResolvedLaunchArguments.Replay(8192, 33), plan: null, ModelRole.Chat),
            new LlamaServerLaunchAuxAssets(false, false, false),
            new LlamaServerLaunchPlacement(LlamaServerPlacementOutcome.Full, 33, 33),
            8192,
            LlamaServerBenchmarkLaunchPolicy.DeterministicV1);

    private static BenchmarkRunRecord Run(BenchmarkPrimaryStatus status, long version, int? invocationTimeoutSeconds = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), new byte[]
            {
                1
            }, "model.gguf", LocalModelOrigin.Imported, V1('a'), "Agent", 1, 8192,
            status, null, null, null, null, null, 0, null, null, version, 1, 1, null, 1)
        {
            InvocationTimeoutSeconds = invocationTimeoutSeconds
        };

    private static BenchmarkRunLaunchIntent Intent(int? launchIdentityScheme) =>
        new("cuda", "q8_0", "auto", null, LlamaServerLaunchProjection.FlashAttentionOn, new string('a', 64), null, launchIdentityScheme);

    private static BenchmarkRunExecutor Executor(IBenchmarkStore store,
        BenchmarkRuntimeSnapshotV1 snapshot,
        FakeLease lease,
        ICapacityService capacity,
        IWorkerEventDispatcher dispatcher,
        IInvocationRunner runner,
        IBenchmarkCancellationRegistry cancellations,
        ILlamaServerProcessSupervisor? supervisor = null,
        IRuntimeEnvironmentFactsProvider? environmentFacts = null,
        ILogger<BenchmarkRunExecutor>? logger = null,
        BenchmarkAdmissionRetry? admissionRetry = null,
        IBenchmarkPairwisePlanner? pairwisePlanner = null) =>
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
            Buffer(),
            cancellations,
            environmentFacts ?? new RecordingEnvironmentFacts(),
            Substitute.For<IBenchmarkJudgeRuntimeResolver>(),
            pairwisePlanner ?? Substitute.For<IBenchmarkPairwisePlanner>(),
            // Default: decide ONCE and never wait, so every test but the wait tests stays instant.
            admissionRetry ?? new BenchmarkAdmissionRetry(MaxRetries: 0, TimeSpan.Zero),
            logger ?? NullLogger<BenchmarkRunExecutor>.Instance);

    private static InvocationState State(Guid invocationId,
        InvocationStatus status,
        string content,
        int? totalTokens = null,
        long? durationMs = null,
        string? finishReason = null,
        InvocationThroughput? throughput = null,
        string thinkingContent = "") =>
        new()
        {
            InvocationId = invocationId,
            ConversationId = Guid.NewGuid(),
            Status = status,
            StreamedContent = content,
            StreamedThinkingContent = thinkingContent,
            StartedAt = DateTimeOffset.UnixEpoch,
            LastUpdatedAt = DateTimeOffset.UnixEpoch,
            TotalTokens = totalTokens,
            GenerationDurationMs = durationMs,
            FinishReason = finishReason,
            Throughput = throughput
        };

    private static InvocationGenerationAdmissionContext Admission(Guid invocationId) =>
        new()
        {
            InvocationId = invocationId,
            RequestedContextTokens = 8192,
            EffectiveContextTokens = 8192,
            ModelId = "model.gguf",
            ProviderName = "llamacpp"
        };

    private static InstalledModelSnapshot Installed(string name, char fingerprintCharacter)
    {
        var revision = V1('c');
        var fingerprint = V1(fingerprintCharacter);
        return new InstalledModelSnapshot(name,
            revision,
            [],
            revision,
            [
                new InstalledModelPhysicalMember(name,
                    InstalledModelPhysicalMemberRole.Weight,
                    12,
                    new string('d', 64),
                    $"sha256:{new string('d', 64)}:12",
                    [name],
                    true,
                    null)
            ],
            revision,
            LocalModelOrigin.Imported,
            "llamacpp",
            "map-revision",
            "repo/model",
            "revision",
            "Q4_K_M",
            GgufRole.Chat,
            fingerprint);
    }

    private static BenchmarkRuntimeSnapshotV1 Snapshot(InstalledModelSnapshot model, int frozenContextTokens = 8192, string? frozenKvCacheType = null)
    {
        var frozen = new BenchmarkInstalledModelSnapshotV1(model.ModelName,
            model.RegistryRevision,
            [],
            model.RegistryAliasSetHash,
            model.Members.Select(static member => new BenchmarkPhysicalMemberSnapshotV1(member.RelativePath,
                     member.Role,
                     member.SizeBytes,
                     member.Sha256,
                     member.OwningAliases,
                     member.Required,
                     member.MetadataSchemaVersion,
                     member.MemberFingerprint))
                 .ToArray(),
            model.PhysicalMemberSetHash,
            model.Origin,
            model.ProviderName!,
            model.ProviderMappingRevision,
            model.RepoId,
            model.SourceRevision,
            model.ModelName,
            model.Quantization,
            "chat",
            model.ModelContentFingerprint);
        return new BenchmarkRuntimeSnapshotV1(1,
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            "task",
            8192,
            new ResolvedAgentRuntime("prompt", [], null, null, 1, AgentName: "Agent"),
            Runtime(frozenContextTokens, frozenKvCacheType),
            BenchmarkFrozenPolicies.DeterministicSampling(),
            frozen,
            new BenchmarkFreezeDependencySetV1("a", "b", "c", "d", "e", null),
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

    private static ILlamaServerProcessSupervisor PassthroughSupervisor(LlamaServerLaunchReceipt? receipt = null)
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
                      var context = new LlamaServerProfilingContext(new LlamaServerEndpoint(modelName, ModelRole.Chat, new Uri("http://127.0.0.1:19000")), [])
                      {
                          LaunchReceipt = receipt
                      };
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

    private static BenchmarkEventBuffer Buffer() =>
        new(Options.Create(new BenchmarkEventBufferOptions()));

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

    /// <summary>Answers each decision with the next verdict, then repeats the last one — capacity that frees up, or not.</summary>
    private sealed class SequencedCapacityService(params CapacityVerdict[] verdicts) : ICapacityService
    {
        private readonly Queue<CapacityVerdict> _verdicts = new(verdicts);

        public int DecisionCount { get; private set; }
        public TrackingDisposable Reservation { get; } = new();

        public Task<CapacityDecision> DecideAsync(string modelName, ModelRole role, CancellationToken ct) =>
            DecideAsync(new CapacityRequest(modelName, role), ct);

        public Task<CapacityDecision> DecideAsync(CapacityRequest request, CancellationToken ct)
        {
            DecisionCount++;
            var verdict = _verdicts.Count > 1 ? _verdicts.Dequeue() : _verdicts.Peek();
            return Task.FromResult(new CapacityDecision(verdict,
                "capacity",
                false,
                verdict == CapacityVerdict.Allow ? Reservation : null));
        }
    }

    private sealed class RecordingCapacityService(RecordingEnvironmentFacts? environmentFacts = null) : ICapacityService
    {
        public int DecisionCount { get; private set; }
        public int EnvironmentCapturesAtDecision { get; private set; }
        public CapacityRequest? LastRequest { get; private set; }
        public TrackingDisposable? Reservation { get; private set; }

        public Task<CapacityDecision> DecideAsync(string modelName, ModelRole role, CancellationToken ct)
        {
            DecisionCount++;
            return Task.FromResult(new CapacityDecision(CapacityVerdict.Allow, "allowed", false));
        }

        public Task<CapacityDecision> DecideAsync(CapacityRequest request, CancellationToken ct)
        {
            DecisionCount++;
            EnvironmentCapturesAtDecision = environmentFacts?.Captures ?? 0;
            LastRequest = request;
            Reservation = new TrackingDisposable();
            return Task.FromResult(new CapacityDecision(CapacityVerdict.Allow, "allowed", false, Reservation));
        }
    }

    private sealed class RecordingEnvironmentFacts : IRuntimeEnvironmentFactsProvider
    {
        public int Captures { get; private set; }
        public GpuVariant? Variant { get; private set; }

        public Task<RuntimeEnvironmentFactsV1> CaptureAsync(GpuVariant variant, CancellationToken ct)
        {
            Captures++;
            Variant = variant;
            return Task.FromResult(new RuntimeEnvironmentFactsV1(1, null, null, null, 42, ["hardware"]));
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
