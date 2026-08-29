namespace XE_Local_AI_Engine.Tests.ExternalProviders;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation.Policy;
using XE_Local_AI_Engine.Providers.OpenAICompat;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The declared context window of an external model, from the readiness step through to the turn budgeter.
/// </summary>
/// <remarks>
///     <para>
///         This is the path that would have been cosmetic if implemented anywhere else. The readiness step returns
///         <c>EffectiveContextTokens: null</c> for every non-warmable model BEFORE <c>GetRuntimeInfoAsync</c> is ever
///         called, so a provider that reports the declared window would never have been asked. Null then means the turn
///         budgeter keeps its conservative default — an operator declaring a 64k window would still have had their
///         history trimmed to fit 8192 tokens.
///     </para>
///     <para>
///         The runner does exactly one thing with the value — <c>turnPolicy.WithEffectiveContext(...)</c> — so
///         asserting the readiness result and then that fold is the whole chain.
///     </para>
/// </remarks>
public sealed class ExternalModelContextBudgetTests
{
    private const string ExternalModel = "ext:local-box/qwen3";
    private const int DefaultContextTokens = 8192;

    [Test]
    public async Task PrepareLocalRuntimeAsync_ForAnExternalModel_ReturnsTheDeclaredWindowWithoutWarming()
    {
        var resolver = Substitute.For<ILocalModelProviderResolver>();
        var warmer = CreateWarmer(resolver, new FakeModelTrustResolver().Register("local-box", "qwen3", contextLength: 65536));

        var result = await PrepareAsync(warmer);

        AssertEx.Equal(65536, result.EffectiveContextTokens);
        AssertEx.Equal(ExternalProviderConstants.ProviderName, result.ProviderName);
        // Nothing was warmed: the node does not own the process serving this model.
        await resolver.DidNotReceive().ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PrepareLocalRuntimeAsync_ForAnExternalModelWithNoDeclaredWindow_KeepsTheFallback()
    {
        var warmer = CreateWarmer(Substitute.For<ILocalModelProviderResolver>(), new FakeModelTrustResolver().Register("local-box", "qwen3"));

        var result = await PrepareAsync(warmer);

        // Null, not zero and not a guess: the budgeter's conservative default is safer than a window the server may
        // not actually have.
        AssertEx.Null(result.EffectiveContextTokens);
    }

    [Test]
    public async Task PrepareLocalRuntimeAsync_ForAnUnresolvedExternalModel_KeepsTheFallback()
    {
        var warmer = CreateWarmer(Substitute.For<ILocalModelProviderResolver>(), new FakeModelTrustResolver());

        AssertEx.Null((await PrepareAsync(warmer)).EffectiveContextTokens);
    }

    [Test]
    public async Task TheDeclaredWindowReachesTheTurnBudgeter()
    {
        var warmer = CreateWarmer(Substitute.For<ILocalModelProviderResolver>(), new FakeModelTrustResolver().Register("local-box", "qwen3", contextLength: 65536));
        var policy = PolicyWithDefaultWindow();

        // The exact fold InvocationRunner performs with the readiness result.
        var folded = policy.WithEffectiveContext((await PrepareAsync(warmer)).EffectiveContextTokens);

        AssertEx.Equal(65536, folded.ContextCapacityTokens);
    }

    [Test]
    public async Task WithNoDeclaredWindow_TheTurnBudgeterKeepsItsDefault()
    {
        var warmer = CreateWarmer(Substitute.For<ILocalModelProviderResolver>(), new FakeModelTrustResolver().Register("local-box", "qwen3"));

        var folded = PolicyWithDefaultWindow().WithEffectiveContext((await PrepareAsync(warmer)).EffectiveContextTokens);

        AssertEx.Equal(DefaultContextTokens, folded.ContextCapacityTokens);
    }

    private static TurnPolicy PolicyWithDefaultWindow()
    {
        return new TurnPolicy
        {
            InvocationTimeout = TimeSpan.FromMinutes(5),
            StreamIdleTimeout = TimeSpan.FromMinutes(1),
            StreamIdleTimeoutMessage = "idle",
            ToolResultTimeout = TimeSpan.FromMinutes(1),
            ContextCapacityTokens = DefaultContextTokens,
            RequestedContextTokens = null,
            ReservedOutputTokens = 1024,
            // Everything below is required by the policy but irrelevant to the context fold under test.
            MaxToolIterationsPerRequest = 8,
            MaxConsecutiveInvalidToolCallsPerTool = 3,
            RetryEnabled = false,
            MaxRetries = 0,
            BaseRetryDelay = TimeSpan.Zero,
            MaxRetryDelay = TimeSpan.Zero,
            CircuitBreakerEnabled = false,
            CircuitBreakerFailureThreshold = 0,
            CircuitBreakerBreakDuration = TimeSpan.Zero
        };
    }

    private static LocalRuntimeWarmer CreateWarmer(ILocalModelProviderResolver resolver, FakeModelTrustResolver trust)
    {
        var cloudFactory = Substitute.For<IActiveCloudChatClientFactory>();
        _ = cloudFactory.IsCloudProviderSelected(Arg.Any<string?>()).Returns(false);
        return new LocalRuntimeWarmer(resolver, cloudFactory, trust, NullLogger<LocalRuntimeWarmer>.Instance);
    }

    private static async Task<LocalRuntimePreparationResult> PrepareAsync(LocalRuntimeWarmer warmer)
    {
        return await warmer.PrepareLocalRuntimeAsync(ExternalModel,
            Substitute.For<IWorkerEventDispatcher>(),
            Guid.NewGuid(),
            new InvocationRunner.StreamState(),
            turnStartedTimestamp: 0,
            CancellationToken.None);
    }
}
