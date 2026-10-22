namespace XE_Local_AI_Engine.Tests.Invocation;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Capacity.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The warm-decision half of the model-readiness step, exercised without building the whole invocation runner. Two
///     rules carry the weight: only the llama.cpp runtime is warmable (a cloud-routed or Ollama model must never trigger
///     a local cold-load), and every non-cancellation failure degrades to "no warm" / "window unknown" rather than
///     failing the turn — a cancellation still propagates, because the turn is terminating.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class LocalRuntimeWarmerTests
{
    [Test]
    public async Task ResolveWarmableProviderAsync_WhenTheModelRoutesToCloud_SkipsProviderResolutionEntirely()
    {
        // The resolver maps an UNMAPPED model name to the default local provider, so a cloud model id would otherwise
        // resolve to llama-server and fail its cold-load. The cloud check must come first and short-circuit.
        var resolver = CreateLlamaCppResolver();
        var warmer = CreateWarmer(resolver, cloudSelected: true);

        var provider = await warmer.ResolveWarmableProviderAsync("gpt-5.6-terra", Guid.NewGuid(), CancellationToken.None);

        AssertEx.Null(provider);
        await resolver.DidNotReceive().ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ResolveWarmableProviderAsync_WhenTheProviderIsNotLlamaCpp_IsNotWarmable()
    {
        var ollama = Substitute.For<ILocalModelProvider>();
        ollama.ProviderName.Returns("ollama");
        var resolver = Substitute.For<ILocalModelProviderResolver>();
        resolver.ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(ollama));

        var warmer = CreateWarmer(resolver);

        AssertEx.Null(await warmer.ResolveWarmableProviderAsync("qwen3.5:0.8b", Guid.NewGuid(), CancellationToken.None));
    }

    [Test]
    public async Task ResolveWarmableProviderAsync_ForTheLlamaCppProvider_IsWarmable()
    {
        var warmer = CreateWarmer(CreateLlamaCppResolver());

        var provider = AssertEx.NotNull(await warmer.ResolveWarmableProviderAsync("model.gguf", Guid.NewGuid(), CancellationToken.None));

        AssertEx.Equal(LlamaServerProviderConstants.ProviderName, provider.ProviderName);
    }

    [Test]
    public async Task ResolveWarmableProviderAsync_WhenResolutionFails_SkipsTheWarmInsteadOfFailingTheTurn()
    {
        var resolver = Substitute.For<ILocalModelProviderResolver>();
        resolver.ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new InvalidOperationException("model map unavailable"));

        var warmer = CreateWarmer(resolver);

        AssertEx.Null(await warmer.ResolveWarmableProviderAsync("model.gguf", Guid.NewGuid(), CancellationToken.None));
    }

    /// <summary>R7-03: a local turn loading its model during a training run sat in "loading model" with no reason.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task PrepareLocalRuntimeAsync_LoadingWhileATrainingRunHoldsTheNode_SaysSoAndOnlyThen(bool trainingHoldsTheNode)
    {
        var gate = new GpuWorkGate();
        using var training = trainingHoldsTheNode ? gate.TryBeginExclusive(GpuWorkKind.TrainingRun) : null;
        using var benchmarkPoll = trainingHoldsTheNode ? null : gate.TryBeginShared(GpuWorkKind.Benchmark);
        var resolver = Substitute.For<ILocalModelProviderResolver>();
        var provider = CreateLlamaCppProvider(effectiveContextTokens: 8192);
        resolver.ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(provider));
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var invocationId = Guid.NewGuid();

        _ = await CreateWarmer(resolver, gate: gate).PrepareLocalRuntimeAsync("model.gguf", dispatcher, invocationId,
            new InvocationRunner.StreamState(), turnStartedTimestamp: 0, CancellationToken.None);

        var notices = dispatcher.ReceivedCalls()
                                .Where(static call => call.GetMethodInfo().Name == nameof(IWorkerEventDispatcher.ReportTurnNoticeAsync))
                                .Select(static call => (TurnNoticePayload)call.GetArguments()[0]!)
                                .ToArray();
        if (trainingHoldsTheNode)
        {
            var notice = notices.Single();
            AssertEx.Equal(TurnNoticeKind.QueuedBehindGpuWork, notice.Kind);
            AssertEx.Equal(invocationId, notice.InvocationId);
            AssertEx.Equal("This message was queued behind a running training run.", notice.Message);
        }
        else
        {
            AssertEx.Empty(notices, "A benchmark poll's shared hold does not make a model load wait.");
        }
    }

    [Test]
    public async Task PrepareLocalRuntimeAsync_ForACloudModel_ReturnsItsDeclaredWindowWithoutWarming()
    {
        var resolver = CreateLlamaCppResolver();
        var cloudModels = Substitute.For<ICloudModelResolver>();
        cloudModels.ResolveContextWindowAsync("gpt-5.6-terra", Arg.Any<CancellationToken>()).Returns(new CloudModelContextWindow { ContextTokens = 400_000 });

        var result = await CreateWarmer(resolver, cloudSelected: true, cloudModels: cloudModels).PrepareLocalRuntimeAsync("gpt-5.6-terra",
            Substitute.For<IWorkerEventDispatcher>(), Guid.NewGuid(), new InvocationRunner.StreamState(), turnStartedTimestamp: 0, CancellationToken.None);

        AssertEx.Equal(400_000, result.EffectiveContextTokens);
        AssertEx.Null(result.WarmFailure);
        await resolver.DidNotReceive().ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PrepareLocalRuntimeAsync_ForANonWarmableLocalModel_NeverAsksForACloudWindow()
    {
        // An Ollama model is skipped like a cloud one, but has no declared window: the default must stay, and the cloud
        // config must not even be read for it.
        var ollama = Substitute.For<ILocalModelProvider>();
        ollama.ProviderName.Returns("ollama");
        var resolver = Substitute.For<ILocalModelProviderResolver>();
        resolver.ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(ollama));
        var cloudModels = Substitute.For<ICloudModelResolver>();
        cloudModels.ResolveContextWindowAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new CloudModelContextWindow { ContextTokens = 400_000 });

        var result = await CreateWarmer(resolver, cloudModels: cloudModels).PrepareLocalRuntimeAsync("qwen3.5:0.8b",
            Substitute.For<IWorkerEventDispatcher>(), Guid.NewGuid(), new InvocationRunner.StreamState(), turnStartedTimestamp: 0, CancellationToken.None);

        AssertEx.Null(result.EffectiveContextTokens);
        await cloudModels.DidNotReceive().ResolveContextWindowAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ResolveDeclaredRemoteContextAsync_ForAnExternalModel_ReturnsItsRegisteredLength()
    {
        var resolver = CreateLlamaCppResolver();
        var trust = new FakeModelTrustResolver().Register("local-box", "qwen3", contextLength: 65_536);
        var warmer = CreateWarmer(resolver, trust: trust);

        AssertEx.Equal(65_536, await warmer.ResolveDeclaredRemoteContextAsync("ext:local-box/qwen3", Guid.NewGuid(), CancellationToken.None));
        await resolver.DidNotReceive().ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ResolveDeclaredRemoteContextAsync_ForASelectedCloudModel_ReturnsItsCatalogWindow()
    {
        var resolver = CreateLlamaCppResolver();
        var cloudModels = Substitute.For<ICloudModelResolver>();
        cloudModels.ResolveContextWindowAsync("gpt-5.6-terra", Arg.Any<CancellationToken>()).Returns(new CloudModelContextWindow { ContextTokens = 272_000 });
        var warmer = CreateWarmer(resolver, cloudSelected: true, cloudModels: cloudModels);

        AssertEx.Equal(272_000, await warmer.ResolveDeclaredRemoteContextAsync("gpt-5.6-terra", Guid.NewGuid(), CancellationToken.None));
        await resolver.DidNotReceive().ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ResolveDeclaredRemoteContextAsync_ForALocalModel_IsNullAndNeverReadsTheCloudConfig()
    {
        var cloudModels = Substitute.For<ICloudModelResolver>();
        cloudModels.ResolveContextWindowAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new CloudModelContextWindow { ContextTokens = 272_000 });
        var warmer = CreateWarmer(CreateLlamaCppResolver(), cloudModels: cloudModels);

        AssertEx.Null(await warmer.ResolveDeclaredRemoteContextAsync("model.gguf", Guid.NewGuid(), CancellationToken.None));
        await cloudModels.DidNotReceive().ResolveContextWindowAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ResolveDeclaredRemoteContextAsync_WhenTheCloudReadFails_IsNull_ButACancellationPropagates()
    {
        var failing = Substitute.For<ICloudModelResolver>();
        failing.ResolveContextWindowAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("config unreadable"));
        AssertEx.Null(await CreateWarmer(CreateLlamaCppResolver(), cloudSelected: true, cloudModels: failing)
            .ResolveDeclaredRemoteContextAsync("gpt-5.6-terra", Guid.NewGuid(), CancellationToken.None));

        var cancelled = Substitute.For<ICloudModelResolver>();
        cancelled.ResolveContextWindowAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).ThrowsAsync(new OperationCanceledException());
        var warmer = CreateWarmer(CreateLlamaCppResolver(), cloudSelected: true, cloudModels: cancelled);
        await AssertEx.ThrowsAsync<OperationCanceledException>(() =>
            warmer.ResolveDeclaredRemoteContextAsync("gpt-5.6-terra", Guid.NewGuid(), CancellationToken.None));
    }

    [Test]
    public async Task ResolveEffectiveContextTokensAsync_ReturnsTheLaunchedWindow()
    {
        var warmer = CreateWarmer(CreateLlamaCppResolver());
        var provider = CreateLlamaCppProvider(effectiveContextTokens: 8192);

        AssertEx.Equal(8192, await warmer.ResolveEffectiveContextTokensAsync(provider, "model.gguf", Guid.NewGuid(), CancellationToken.None));
    }

    [Test]
    public async Task ResolveEffectiveContextTokensAsync_WhenTheRuntimeReportsNoWindow_IsNullSoTheDefaultIsKept()
    {
        var warmer = CreateWarmer(CreateLlamaCppResolver());

        // Both "no runtime info" and a non-positive window mean "unknown": the caller keeps its configured default.
        AssertEx.Null(await warmer.ResolveEffectiveContextTokensAsync(CreateLlamaCppProvider(effectiveContextTokens: null), "model.gguf", Guid.NewGuid(), CancellationToken.None));
        AssertEx.Null(await warmer.ResolveEffectiveContextTokensAsync(CreateLlamaCppProvider(effectiveContextTokens: 0), "model.gguf", Guid.NewGuid(), CancellationToken.None));
    }

    [Test]
    public async Task ResolveEffectiveContextTokensAsync_WhenTheReadFails_IsNull_ButACancellationPropagates()
    {
        var warmer = CreateWarmer(CreateLlamaCppResolver());

        var failing = Substitute.For<ILocalModelProvider>();
        failing.GetRuntimeInfoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("probe failed"));
        AssertEx.Null(await warmer.ResolveEffectiveContextTokensAsync(failing, "model.gguf", Guid.NewGuid(), CancellationToken.None));

        var cancelled = Substitute.For<ILocalModelProvider>();
        cancelled.GetRuntimeInfoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new OperationCanceledException());
        await AssertEx.ThrowsAsync<OperationCanceledException>(() =>
            warmer.ResolveEffectiveContextTokensAsync(cancelled, "model.gguf", Guid.NewGuid(), CancellationToken.None));
    }

    [Test]
    public void BuildGenerationAdmissionRejectionMessage_MapsEachReasonCodeToItsOwnRefusal()
    {
        var context = new InvocationGenerationAdmissionContext
        {
            InvocationId = Guid.NewGuid(),
            RequestedContextTokens = 32768,
            EffectiveContextTokens = 4096,
            ModelId = "model.gguf",
            ProviderName = LlamaServerProviderConstants.ProviderName
        };

        AssertEx.Equal("Effective context unavailable.",
            LocalRuntimeWarmer.BuildGenerationAdmissionRejectionMessage(InvocationGenerationAdmissionReasonCodes.EffectiveContextUnavailable, context));
        AssertEx.Equal("Requested context 32768 tokens exceeds effective context 4096 tokens.",
            LocalRuntimeWarmer.BuildGenerationAdmissionRejectionMessage(InvocationGenerationAdmissionReasonCodes.EffectiveContextInsufficient, context));
        AssertEx.Equal("Invocation generation was rejected by policy.",
            LocalRuntimeWarmer.BuildGenerationAdmissionRejectionMessage("something-else", context));
    }

    [Test]
    public void BuildGenerationAdmissionRejectionMessage_WithoutAKnownWindow_FallsBackToTheGenericRefusal()
    {
        // The "insufficient" arm names both token counts, so it may only be reached when the effective window is
        // actually known — an unknown window would otherwise render a refusal with a hole where the number belongs.
        var context = new InvocationGenerationAdmissionContext
        {
            InvocationId = Guid.NewGuid(),
            RequestedContextTokens = 32768,
            EffectiveContextTokens = null,
            ModelId = "model.gguf",
            ProviderName = LlamaServerProviderConstants.ProviderName
        };

        AssertEx.Equal("Invocation generation was rejected by policy.",
            LocalRuntimeWarmer.BuildGenerationAdmissionRejectionMessage(InvocationGenerationAdmissionReasonCodes.EffectiveContextInsufficient, context));
    }

    private static LocalRuntimeWarmer CreateWarmer(ILocalModelProviderResolver resolver, bool cloudSelected = false, GpuWorkGate? gate = null,
        ICloudModelResolver? cloudModels = null, FakeModelTrustResolver? trust = null)
    {
        var cloudFactory = Substitute.For<IActiveCloudChatClientFactory>();
        cloudFactory.IsCloudProviderSelected(Arg.Any<string?>()).Returns(cloudSelected);

        return new LocalRuntimeWarmer(resolver, cloudFactory, trust ?? new FakeModelTrustResolver(), cloudModels ?? Substitute.For<ICloudModelResolver>(),
            NullLogger<LocalRuntimeWarmer>.Instance, TimeProvider.System, gate ?? new GpuWorkGate());
    }

    private static ILocalModelProvider CreateLlamaCppProvider(int? effectiveContextTokens)
    {
        var provider = Substitute.For<ILocalModelProvider>();
        provider.ProviderName.Returns(LlamaServerProviderConstants.ProviderName);
        provider.GetRuntimeInfoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(effectiveContextTokens is { } effective
                    ? new LocalModelRuntimeInfo
                    {
                        EffectiveContextTokens = effective
                    }
                    : null));

        return provider;
    }

    private static ILocalModelProviderResolver CreateLlamaCppResolver()
    {
        // The provider substitute is built BEFORE the Returns() call: configuring one substitute inside another's
        // Returns() makes NSubstitute attribute the inner setup to the outer call and throws.
        var provider = CreateLlamaCppProvider(effectiveContextTokens: 4096);
        var resolver = Substitute.For<ILocalModelProviderResolver>();
        resolver.ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(provider));

        return resolver;
    }
}
