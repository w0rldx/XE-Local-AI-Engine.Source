namespace XE_Local_AI_Engine.Tests.Invocation;

using System.Diagnostics;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation.Dispatch;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Providers.OpenAICompat;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
using static InvocationRunnerTestSupport;

[Category(TestCategories.Unit)]
public sealed class InvocationRunnerEffortDispatchTests
{
    // real-timer: one stubbed local warm, and the floor a turn that summed TWO of them must clear. The floor sits
    // well under the pair and well over a single warm, so neither a fast machine nor a loaded one changes the verdict.
    private static readonly TimeSpan WarmDelay = TimeSpan.FromMilliseconds(40);

    private const long TwoWarmFloorMs = 60L;

    // ---- reasoning effort `auto` -------------------------------------------------------------------------------

    /// <summary>
    ///     The dispatcher is registered (scoped, as in the composition root) through a factory that THROWS, so a turn
    ///     whose effort is not <c>auto</c> proves the runner never RESOLVED it: that path opens no scope.
    /// </summary>
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("none")]
    [Arguments("on")]
    [Arguments("minimal")]
    [Arguments("low")]
    [Arguments("medium")]
    [Arguments("high")]
    [Arguments("xhigh")]
    public async Task Dispatch_WhenEffortIsNotAuto_DispatcherIsNeverResolvedOrInvoked(string? reasoningEffort)
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        InvocationAgentDefinition? built = null;
        var runner = CreateRunner(invocationAgentFactory: CreateFactory(CreateUpdates("ok"), onCreate: definition => built = definition),
            eventDispatcher: dispatcher,
            reasoningEffortDispatcherFactory: static _ => throw new InvalidOperationException("The dispatcher must never be resolved on a non-auto turn."));
        var package = RuntimePackageBuilder.Valid().WithReasoningEffort(reasoningEffort).Build();

        await RunAsync(runner, package);

        // No throw means no resolve. The definition must also be exactly what it was before this feature existed:
        // the authored effort, the resolved model, and untouched sampling.
        var definitionBuilt = AssertEx.NotNull(built);
        AssertEx.True(string.Equals(reasoningEffort, definitionBuilt.ReasoningEffort, StringComparison.Ordinal),
            "the authored effort must reach the definition untouched");
        AssertEx.Equal("qwen3.5:0.8b", definitionBuilt.ModelId);
        AssertEx.Null(definitionBuilt.Sampling?.MaxOutputTokens);
        await dispatcher.DidNotReceive().ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload => payload.Kind == TurnNoticeKind.EffortDispatched));
    }

    /// <summary>
    ///     The other half of the proof: the same throwing factory IS reached on an <c>auto</c> turn, so the test above
    ///     cannot pass by simply never wiring the dispatcher at all.
    /// </summary>
    [Test]
    public async Task Dispatch_WhenTierIsNormalAndNoSwap_EmitsNoNotice()
    {
        // The common case. A notice on every ordinary turn would be noise, so NORMAL with the model unchanged is
        // silent — the effort still changes, it is just not worth interrupting the reader for.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        InvocationAgentDefinition? built = null;
        using var stub = new StubReasoningEffortDispatcher(Decision(ReasoningTier.Normal, "qwen3.5:0.8b", "medium", ReasoningDispatchReasons.Balanced));
        var runner = CreateRunner(invocationAgentFactory: CreateFactory(CreateUpdates("ok"), onCreate: definition => built = definition),
            eventDispatcher: dispatcher,
            reasoningEffortDispatcherFactory: _ => stub);
        var package = RuntimePackageBuilder.Valid().WithReasoningEffort("auto").Build();

        await RunAsync(runner, package);

        AssertEx.Equal(expected: 1, stub.Invocations);
        AssertEx.Equal("medium", AssertEx.NotNull(built).ReasoningEffort, "the dispatched effort replaces `auto` before the definition is built");
        await dispatcher.DidNotReceive().ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload => payload.Kind == TurnNoticeKind.EffortDispatched));
    }

    [Test]
    public async Task Dispatch_WhenModelSwapped_EmitsEffortDispatchedNotice()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        InvocationAgentDefinition? built = null;
        using var stub = new StubReasoningEffortDispatcher(Decision(ReasoningTier.Fast, "qwen3-1.7b", "low", ReasoningDispatchReasons.ShortTurn));
        var runner = CreateRunner(invocationAgentFactory: CreateFactory(CreateUpdates("ok"), onCreate: definition => built = definition),
            eventDispatcher: dispatcher,
            reasoningEffortDispatcherFactory: _ => stub);
        var package = RuntimePackageBuilder.Valid().WithReasoningEffort("auto").AllowingAutoModelSwap().Build();

        await RunAsync(runner, package);

        var definitionBuilt = AssertEx.NotNull(built);
        AssertEx.Equal("qwen3-1.7b", definitionBuilt.ModelId, "the turn runs on the model the dispatcher chose");
        AssertEx.Equal("low", definitionBuilt.ReasoningEffort);
        AssertEx.Null(definitionBuilt.Sampling?.MaxOutputTokens, "no tier caps the send's output");
        await dispatcher.Received(1).ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload =>
            payload.InvocationId == package.InvocationId
            && payload.Kind == TurnNoticeKind.EffortDispatched
            && payload.Detail == ReasoningDispatchReasons.ShortTurn
            && payload.Message.Contains("qwen3-1.7b", StringComparison.Ordinal)));
    }

    [Test]
    public async Task Dispatch_WhenModelSwapped_RecordsTheServedModelOnTheInvocationState()
    {
        // The invocation state is seeded with the model the PACKAGE named, and BOTH the persisted message row and the
        // run envelope's provider attribution are read from it. A swapped turn that leaves it alone is recorded, and
        // measured, against a model that never saw the turn — the fast model's tokens and latency land on the big
        // model's row.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        using var stub = new StubReasoningEffortDispatcher(Decision(ReasoningTier.Fast, "qwen3-1.7b", "low", ReasoningDispatchReasons.ShortTurn));
        var runner = CreateRunner(agentUpdates: CreateUpdates("ok"), eventDispatcher: dispatcher, reasoningEffortDispatcherFactory: _ => stub);
        var package = RuntimePackageBuilder.Valid().WithReasoningEffort("auto").AllowingAutoModelSwap().Build();

        await RunAsync(runner, package);

        await dispatcher.Received(1).ReportServedModelAsync(package.InvocationId, "qwen3-1.7b");
    }

    [Test]
    [Arguments("auto")]
    [Arguments("medium")]
    public async Task Dispatch_WhenTheModelIsNotSwapped_NeverRecordsAServedModel(string reasoningEffort)
    {
        // Both silent shapes: an `auto` turn the dispatcher chose not to swap, and an ordinary turn that never reaches
        // the dispatcher at all. The seeded model is already correct on each, and a redundant report would rewrite the
        // state on every turn for nothing.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        using var stub = new StubReasoningEffortDispatcher(Decision(ReasoningTier.Fast, "qwen3.5:0.8b", "low", ReasoningDispatchReasons.FastModelUnset));
        var runner = CreateRunner(agentUpdates: CreateUpdates("ok"), eventDispatcher: dispatcher, reasoningEffortDispatcherFactory: _ => stub);
        var package = RuntimePackageBuilder.Valid().WithReasoningEffort(reasoningEffort).Build();

        await RunAsync(runner, package);

        await dispatcher.DidNotReceive().ReportServedModelAsync(Arg.Any<Guid>(), Arg.Any<string>());
    }

    [Test]
    public async Task Dispatch_WhenTheFallbackRerunsOnTheOriginalModel_NeverRecordsTheFastModelAsServed()
    {
        // The other half of the rule: the fast model did not serve this turn, the original one did, and the seeded
        // state already names it. Reporting the fast model here would put a model that produced nothing on the row.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var observed = new List<InvocationAgentDefinition>();
        using var stub = new StubReasoningEffortDispatcher(Decision(ReasoningTier.Fast, "qwen3-1.7b", "low", ReasoningDispatchReasons.ShortTurn));
        var runner = CreateRunner(invocationAgentFactory: CreateModelRoutedFactory("qwen3-1.7b", observed),
            eventDispatcher: dispatcher,
            providerStreamResilience: NoRetryResilience(),
            reasoningEffortDispatcherFactory: _ => stub);

        await RunAsync(runner, RuntimePackageBuilder.Valid().WithReasoningEffort("auto").AllowingAutoModelSwap().Build());

        AssertEx.Equal(expected: 2, observed.Count, "the re-run must have happened, or this proves nothing");
        await dispatcher.DidNotReceive().ReportServedModelAsync(Arg.Any<Guid>(), Arg.Any<string>());
    }

    [Test]
    public async Task Dispatch_WhenSwappedSendFailsAfterFirstToken_StillEmitsExactlyOneEffortDispatchedNotice()
    {
        // A swapped turn withholds its notice until the send resolves, precisely so a fallback cannot leave the reader
        // with two contradictory rows. When the send streams and THEN fails there is no fallback to announce — and a
        // turn that ends with no effort notice at all is the one outcome that must not happen. The notice names
        // the model that actually served; the failure itself is reported as on any other turn.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var observed = new List<InvocationAgentDefinition>();
        using var stub = new StubReasoningEffortDispatcher(Decision(ReasoningTier.Fast, "qwen3-1.7b", "low", ReasoningDispatchReasons.ShortTurn));
        var runner = CreateRunner(invocationAgentFactory: CreateModelRoutedFactory("qwen3-1.7b", observed, emitATokenBeforeFailing: true),
            eventDispatcher: dispatcher,
            providerStreamResilience: NoRetryResilience(),
            reasoningEffortDispatcherFactory: _ => stub);

        await RunAsync(runner, RuntimePackageBuilder.Valid().WithReasoningEffort("auto").AllowingAutoModelSwap().Build());

        AssertEx.Equal(expected: 1, observed.Count, "a turn that has already streamed must not be re-run");
        await dispatcher.Received(1).ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload =>
            payload.Kind == TurnNoticeKind.EffortDispatched
            && payload.Detail == ReasoningDispatchReasons.ShortTurn
            && payload.Message.Contains("qwen3-1.7b", StringComparison.Ordinal)));
        await dispatcher.Received(1).ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload => payload.Kind == TurnNoticeKind.EffortDispatched));
    }

    [Test]
    public async Task Dispatch_WhenSwapAdmitted_DisposesTheReservationBeforeTheScope()
    {
        // Declaration order is load-bearing: `using` disposes in REVERSE order, so the scope is declared first and
        // released LAST — after the ledger reservation produced by the CapacityService that lives inside it. Reversing
        // the two would tear down the scoped services while a live reservation still referred to them.
        using var reservation = new SpyReservation();
        using var stub = new StubReasoningEffortDispatcher(Decision(ReasoningTier.Fast, "qwen3-1.7b", "low", ReasoningDispatchReasons.ShortTurn, reservation: reservation));
        var runner = CreateRunner(agentUpdates: CreateUpdates("ok"), reasoningEffortDispatcherFactory: _ => stub);

        await RunAsync(runner, RuntimePackageBuilder.Valid().WithReasoningEffort("auto").AllowingAutoModelSwap().Build());

        AssertEx.True(reservation.DisposedAtTicks.HasValue, "the ledger reservation must be released");
        AssertEx.True(stub.DisposedAtTicks.HasValue, "the per-turn scope must be torn down");
        AssertEx.True(reservation.DisposedAtTicks!.Value <= stub.DisposedAtTicks!.Value,
            "the ledger reservation must be released before the scope that produced it");
    }

    [Test]
    public async Task Dispatch_WhenSwappedSendFailsBeforeFirstToken_RetriesOnOriginalModelAtLowAndEmitsFastModelUnavailable()
    {
        // The fast model went away between the capacity probe and the send. Nothing reached the client, so the turn
        // re-runs once on the model it was authorized for — and COMPLETES. A failed turn here would break the
        // dispatcher's "never fails a turn" contract.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var observed = new List<InvocationAgentDefinition>();
        using var stub = new StubReasoningEffortDispatcher(Decision(ReasoningTier.Fast, "qwen3-1.7b", "low", ReasoningDispatchReasons.ShortTurn));
        var runner = CreateRunner(invocationAgentFactory: CreateModelRoutedFactory("qwen3-1.7b", observed),
            eventDispatcher: dispatcher,
            providerStreamResilience: NoRetryResilience(),
            reasoningEffortDispatcherFactory: _ => stub);
        var package = RuntimePackageBuilder.Valid().WithReasoningEffort("auto").AllowingAutoModelSwap().Build();

        await RunAsync(runner, package);

        AssertEx.Equal(expected: 2, observed.Count, "exactly one re-run");
        AssertEx.Equal("qwen3-1.7b", observed[0].ModelId);
        AssertEx.Equal("qwen3.5:0.8b", observed[1].ModelId, "the re-run uses the model the turn was authorised for");
        AssertEx.Equal("low", observed[1].ReasoningEffort, "the re-run keeps the tier the dispatcher chose and gives up only the model");
        await dispatcher.Received(1).ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload =>
            payload.Kind == TurnNoticeKind.EffortDispatched && payload.Detail == ReasoningDispatchReasons.FastModelUnavailable));
        // ONE notice for the turn. A swapped turn stays silent until the send has resolved precisely so a fallback
        // does not leave the reader with two contradictory "reasoning effort resolved" rows for one answer.
        await dispatcher.Received(1).ReportTurnNoticeAsync(Arg.Is<TurnNoticePayload>(payload =>
            payload.Kind == TurnNoticeKind.EffortDispatched));
    }

    [Test]
    public void AddModelReadiness_SumsEveryWarmAndStaysNullUntilOneHappens()
    {
        // Fixed arithmetic, no clock: the runner test above measures the real two-warm path but reads a wall-clock
        // threshold, which a last-write-wins regression could still clear on a slow worker. This one cannot.
        var stream = new InvocationRunner.StreamState();

        AssertEx.Null(stream.ModelReadinessDurationMs, "no warm happened, and null is what says so — zero would claim a proven warm start");

        stream.AddModelReadiness(1_500d);
        AssertEx.Equal(expected: 1_500d, stream.ModelReadinessDurationMs);

        stream.AddModelReadiness(2_500d);
        AssertEx.Equal(expected: 4_000d, stream.ModelReadinessDurationMs, "the second warm adds to the first rather than replacing it");
    }

    [Test]
    public async Task Dispatch_WhenTheFallbackWarmsASecondTime_ReportsBothWarmsAsOneReadinessTotal()
    {
        // Two local warms in one turn: the dispatched fast model is warmed, its send fails before first output, and the
        // original model is warmed again for the re-run. The whole-turn clock contains BOTH, so assigning the second
        // warm's duration charged the turn only half the cold start it actually paid — the readiness total has to sum.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var observed = new List<InvocationAgentDefinition>();
        using var stub = new StubReasoningEffortDispatcher(Decision(ReasoningTier.Fast, "qwen3-1.7b", "low", ReasoningDispatchReasons.ShortTurn));

        var provider = Substitute.For<ILocalModelProvider>();
        provider.ProviderName.Returns(LlamaServerProviderConstants.ProviderName);
        // A measurable warm: the summed pair clears the floor below, a single warm cannot. Delays only ever run long
        // under load, so the discrimination holds on a slow machine.
        provider.WarmModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Task.Delay(WarmDelay));

        var resolver = Substitute.For<ILocalModelProviderResolver>();
        resolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(LlamaServerProviderConstants.ProviderName));
        resolver.ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(provider));

        var runner = CreateRunner(invocationAgentFactory: CreateModelRoutedFactory("qwen3-1.7b", observed),
            eventDispatcher: dispatcher,
            providerResolver: resolver,
            providerStreamResilience: NoRetryResilience(),
            reasoningEffortDispatcherFactory: _ => stub);
        var package = RuntimePackageBuilder.Valid().WithReasoningEffort("auto").AllowingAutoModelSwap().Build();

        await RunAsync(runner, package);

        AssertEx.Equal(expected: 2, observed.Count, "exactly one re-run");
        await provider.Received(2).WarmModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await dispatcher.Received(1)
                        .ReportTurnTelemetryAsync(package.InvocationId,
                            Arg.Is<long?>(static value => value >= TwoWarmFloorMs),
                            Arg.Any<TurnUsageTotals?>());
    }

    [Test]
    public async Task Dispatch_WhenSwappedSendFailsAfterFirstToken_DoesNotRetry()
    {
        // Once a token has reached the client there is nothing to re-run into: the turn fails exactly as any other
        // mid-stream failure does.
        var observed = new List<InvocationAgentDefinition>();
        using var stub = new StubReasoningEffortDispatcher(Decision(ReasoningTier.Fast, "qwen3-1.7b", "low", ReasoningDispatchReasons.ShortTurn));
        var runner = CreateRunner(invocationAgentFactory: CreateModelRoutedFactory("qwen3-1.7b", observed, emitATokenBeforeFailing: true),
            providerStreamResilience: NoRetryResilience(),
            reasoningEffortDispatcherFactory: _ => stub);

        await RunAsync(runner, RuntimePackageBuilder.Valid().WithReasoningEffort("auto").AllowingAutoModelSwap().Build());

        AssertEx.Equal(expected: 1, observed.Count, "a turn that has already streamed must not be re-run");
    }

    [Test]
    public async Task Dispatch_WhenFallbackRerunsOnOriginalModel_HoldsNoFastReservation()
    {
        // The fast reservation books the small model's bytes and one of the loaded-process slots. Carrying it into the
        // re-run double-books the ledger against a model that is no longer being loaded, and can starve the original
        // model's own spawn on a node at the process cap — the exact failure the re-run exists to avoid.
        using var reservation = new SpyReservation();
        var observed = new List<InvocationAgentDefinition>();
        long? secondRunStartedAt = null;
        var factory = CreateModelRoutedFactory("qwen3-1.7b", observed);
        using var stub = new StubReasoningEffortDispatcher(Decision(ReasoningTier.Fast, "qwen3-1.7b", "low", ReasoningDispatchReasons.ShortTurn, reservation: reservation));
        var spyFactory = Substitute.For<IInvocationAgentFactory>();
        spyFactory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
                  .Returns(callInfo =>
                  {
                      if (observed.Count == 1)
                      {
                          secondRunStartedAt ??= Stopwatch.GetTimestamp();
                      }

                      return factory.CreateAsync(callInfo.Arg<InvocationAgentDefinition>(), callInfo.Arg<CancellationToken>());
                  });

        var runner = CreateRunner(invocationAgentFactory: spyFactory,
            providerStreamResilience: NoRetryResilience(),
            reasoningEffortDispatcherFactory: _ => stub);

        await RunAsync(runner, RuntimePackageBuilder.Valid().WithReasoningEffort("auto").AllowingAutoModelSwap().Build());

        AssertEx.True(reservation.DisposedAtTicks.HasValue, "the fast reservation must be released");
        AssertEx.True(secondRunStartedAt.HasValue, "the re-run must have started");
        AssertEx.True(reservation.DisposedAtTicks!.Value <= secondRunStartedAt!.Value, "the fast reservation must be released BEFORE the re-run begins");
        // Dispose is idempotent at its source, so the `using` at turn end running a second time must not throw.
        AssertEx.Equal(expected: 2, reservation.DisposeCount, "released in the catch, then again by the turn-end using");
    }

    [Test]
    public async Task Dispatch_WhenTheExternalOriginalBindingChangesBeforeTheFallback_RefusesTheFallbackSend()
    {
        // The turn is authorized for a declared-LOCAL external model and swaps to a node-local fast model. The fast
        // send fails before any output, and while it fails the operator flips that connection to Cloud. The fallback
        // re-runs the ORIGINAL model inside the pin scope opened before the swap — so unless that scope also carries
        // the ORIGINAL model's pin, the fallback falls through to the transport's weaker unpinned rule and honors a
        // Local->Cloud escalation the pin exists to refuse.
        var recorder = new OpenAiWireRecorder();
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model());
        var observed = new List<InvocationAgentDefinition>();
        using var stub = new StubReasoningEffortDispatcher(Decision(ReasoningTier.Fast, "qwen3-1.7b", "low", ReasoningDispatchReasons.ShortTurn));
        var runner = CreateRunner(invocationAgentFactory: CreateExternalFallbackFactory("qwen3-1.7b",
                registry,
                recorder,
                observed,
                onFastModelSend: () => registry.Replace(ExternalProviderTestData.Connection(locality: ExternalProviderLocality.Cloud),
                    ExternalProviderTestData.Model())),
            providerStreamResilience: NoRetryResilience(),
            reasoningEffortDispatcherFactory: _ => stub,
            externalProviderRegistry: registry);

        await RunAsync(runner, RuntimePackageBuilder.Valid().WithModel(ExternalProviderTestData.ModelId).WithReasoningEffort("auto").AllowingAutoModelSwap().Build());

        AssertEx.Equal(expected: 2, observed.Count, "the fallback must have been attempted");
        AssertEx.Empty(recorder.Requests, "the prompt must never reach the changed endpoint");
    }

    [Test]
    public async Task Dispatch_WhenTheExternalOriginalBindingIsUnchanged_TheFallbackStillSends()
    {
        // The other half of the pin: an untouched binding must not be turned into a refusal by pinning the original
        // model as well. The fallback sends, on the endpoint the turn was authorized for, and the turn completes.
        var recorder = new OpenAiWireRecorder();
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model());
        var observed = new List<InvocationAgentDefinition>();
        using var stub = new StubReasoningEffortDispatcher(Decision(ReasoningTier.Fast, "qwen3-1.7b", "low", ReasoningDispatchReasons.ShortTurn));
        var runner = CreateRunner(invocationAgentFactory: CreateExternalFallbackFactory("qwen3-1.7b", registry, recorder, observed),
            providerStreamResilience: NoRetryResilience(),
            reasoningEffortDispatcherFactory: _ => stub,
            externalProviderRegistry: registry);

        await RunAsync(runner, RuntimePackageBuilder.Valid().WithModel(ExternalProviderTestData.ModelId).WithReasoningEffort("auto").AllowingAutoModelSwap().Build());

        AssertEx.Equal(expected: 1, recorder.Requests.Count, "the fallback must reach the pinned endpoint");
        AssertEx.Equal("http://127.0.0.1:18099/v1/chat/completions", recorder.LastRequest.Uri?.AbsoluteUri);
    }

    [Test]
    public async Task RunAsync_WhenTheTurnNeverSwapsModels_ResolvesExactlyOnePin()
    {
        // Byte-identical guard for the pin set. Resolving BOTH the dispatched and the original model must not widen
        // the ambient set on any turn that did not swap: a non-`auto` turn and an `auto` turn that stayed on its own
        // model both name the same id twice, and the resolver de-duplicates.
        var nonAutoPins = await RunNoSwapTurnAndCapturePinsAsync("medium");
        var autoPins = await RunNoSwapTurnAndCapturePinsAsync("auto");

        foreach (var pins in new[]
                 {
                     nonAutoPins,
                     autoPins
                 })
        {
            AssertEx.Equal(expected: 1, pins.Count, "a turn that never swapped must pin exactly the model it runs");
            AssertEx.Equal(ExternalProviderTestData.ModelId, pins[0].ModelId);
        }
    }

    [Test]
    public async Task Dispatch_WhenEffortIsAuto_ResolvesTheDispatcherFromTheTurnScope()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var resolveCount = 0;
        var runner = CreateRunner(eventDispatcher: dispatcher,
            agentUpdates: CreateUpdates("ok"),
            reasoningEffortDispatcherFactory: _ =>
            {
                Interlocked.Increment(ref resolveCount);
                throw new InvalidOperationException("resolved-on-auto");
            });
        var package = RuntimePackageBuilder.Valid().WithReasoningEffort("auto").Build();

        await RunAsync(runner, package);

        // The resolve happened exactly once, and its failure became the turn's failure instead of a completed turn.
        AssertEx.Equal(expected: 1, Volatile.Read(ref resolveCount), "an `auto` turn must resolve the dispatcher from its scope");
        await dispatcher.Received(1).ReportInvocationFailedAsync(package.InvocationId, Arg.Any<string>(), Arg.Any<FailureCategory>());
        await dispatcher.DidNotReceiveWithAnyArgs().ReportInvocationCompletedAsync(Arg.Any<Guid>());
    }

    [Test]
    [Arguments(32768)]
    [Arguments(4096)]
    public async Task Dispatch_WhenTierIsFast_LeavesTheSendsSamplingUntouched(int effectiveContextTokens)
    {
        // No tier caps the output. A FAST turn differs from a non-`auto` turn only in its effort, on a wide window as
        // much as on a narrow one — which is what keeps both context budgeters' output RESERVATION where it was and
        // keeps a small window from being starved by a reservation a non-`auto` turn would never have made.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        InvocationAgentDefinition? built = null;
        using var stub = new StubReasoningEffortDispatcher(Decision(ReasoningTier.Fast, "qwen3.5:0.8b", "low", ReasoningDispatchReasons.ShortTurn));
        var runner = CreateRunner(invocationAgentFactory: CreateFactory(CreateUpdates("ok"), onCreate: definition => built = definition),
            eventDispatcher: dispatcher,
            providerResolver: CreateLlamaCppResolver(effectiveContextTokens),
            reasoningEffortDispatcherFactory: _ => stub);
        var package = RuntimePackageBuilder.Valid().WithReasoningEffort("auto").Build();

        await RunAsync(runner, package);

        AssertEx.Null(AssertEx.NotNull(built).Sampling?.MaxOutputTokens);
        await dispatcher.DidNotReceive().ReportInvocationFailedAsync(package.InvocationId, Arg.Any<string>(), FailureCategory.ContextWindowExceeded);
    }

    [Test]
    public async Task Dispatch_WhenTheFallbackRerunsOnTheOriginalModel_UsesTheOriginalModelsWindow()
    {
        // The swapped model was warmed at 4096 and the turn policy was sized against THAT window. Re-running the
        // original model on it would measure a long conversation against the fast model's window and drop history the
        // authorized model would have kept — and would thread 4096 as the num_ctx of a process launched at 32768.
        var observed = new List<InvocationAgentDefinition>();
        using var stub = new StubReasoningEffortDispatcher(Decision(ReasoningTier.Fast, "qwen3-1.7b", "low", ReasoningDispatchReasons.ShortTurn));
        var runner = CreateRunner(invocationAgentFactory: CreateModelRoutedFactory("qwen3-1.7b", observed),
            providerResolver: CreatePerModelLlamaCppResolver(new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["qwen3-1.7b"] = 4096,
                ["qwen3.5:0.8b"] = 32768
            }),
            providerStreamResilience: NoRetryResilience(),
            reasoningEffortDispatcherFactory: _ => stub);

        await RunAsync(runner, RuntimePackageBuilder.Valid().WithReasoningEffort("auto").AllowingAutoModelSwap().Build());

        AssertEx.Equal(expected: 2, observed.Count, "exactly one re-run");
        AssertEx.Equal(expected: 4096, observed[0].EffectiveContextTokens, "the swapped send is sized against the fast model's window");
        AssertEx.Equal(expected: 32768, observed[1].EffectiveContextTokens, "the re-run is sized against the window of the model it actually runs on");
    }

    [Test]
    public async Task Dispatch_WhenASwappedTurnWouldOfferTools_DoesNotRetry()
    {
        // The re-run re-enters RunSingleAgentAsync, which owns the tool-relevance drain and its ToolsFiltered notice.
        // The dispatcher's own gate refuses a swap on any tool-bearing turn, so this shape is unreachable through the
        // real dispatcher; the guard makes that dependency explicit rather than load-bearing by coincidence.
        var observed = new List<InvocationAgentDefinition>();
        using var stub = new StubReasoningEffortDispatcher(Decision(ReasoningTier.Fast, "qwen3-1.7b", "low", ReasoningDispatchReasons.ShortTurn));
        var runner = CreateRunner(invocationAgentFactory: CreateModelRoutedFactory("qwen3-1.7b", observed),
            providerStreamResilience: NoRetryResilience(),
            reasoningEffortDispatcherFactory: _ => stub);

        await RunAsync(runner, RuntimePackageBuilder.Valid().WithReasoningEffort("auto").AllowingAutoModelSwap().WithAllowedTool("test-tool").Build());

        AssertEx.Equal(expected: 1, observed.Count, "a tool-bearing turn must not be re-run");
    }
}
