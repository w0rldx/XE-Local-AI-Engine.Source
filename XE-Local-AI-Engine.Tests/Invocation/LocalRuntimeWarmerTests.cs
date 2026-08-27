namespace XE_Local_AI_Engine.Tests.Invocation;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
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

    private static LocalRuntimeWarmer CreateWarmer(ILocalModelProviderResolver resolver, bool cloudSelected = false)
    {
        var cloudFactory = Substitute.For<IActiveCloudChatClientFactory>();
        cloudFactory.IsCloudProviderSelected(Arg.Any<string?>()).Returns(cloudSelected);

        return new LocalRuntimeWarmer(resolver, cloudFactory, new FakeModelTrustResolver(), NullLogger<LocalRuntimeWarmer>.Instance);
    }

    private static ILocalModelProvider CreateLlamaCppProvider(int? effectiveContextTokens)
    {
        var provider = Substitute.For<ILocalModelProvider>();
        provider.ProviderName.Returns(LlamaServerProviderConstants.ProviderName);
        provider.GetRuntimeInfoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(effectiveContextTokens is { } effective ? new LocalModelRuntimeInfo(effective) : null));

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
