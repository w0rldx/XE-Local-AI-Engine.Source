namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>Validation + role-mapping coverage for <see cref="LlamaServerLaunchPolicyOptions" />.</summary>
public sealed class LlamaServerLaunchPolicyOptionsTests
{
    [Test]
    public void Validate_DefaultOptions_Passes()
    {
        new LlamaServerLaunchPolicyOptions().Validate();
    }

    [Test]
    public async Task Validate_NonPositiveChatContext_Throws()
    {
        await AssertValidationThrowsAsync(new LlamaServerLaunchPolicyOptions
        {
            ChatContextTokens = 0
        });
    }

    [Test]
    public async Task Validate_NegativeSafetyMargin_Throws()
    {
        await AssertValidationThrowsAsync(new LlamaServerLaunchPolicyOptions
        {
            ContextSafetyMarginTokens = -1
        });
    }

    [Test]
    public async Task Validate_EmptyKvCacheType_Throws()
    {
        await AssertValidationThrowsAsync(new LlamaServerLaunchPolicyOptions
        {
            KvCacheType = "  "
        });
    }

    [Test]
    public async Task Validate_UnknownKvCacheType_Throws()
    {
        await AssertValidationThrowsAsync(new LlamaServerLaunchPolicyOptions
        {
            KvCacheType = "q5_1"
        });
    }

    [Test]
    public void SeededFromUnsetNodeSettings_EqualsTheProviderDefault()
    {
        // Byte-identical default (invariant 2). With the node setting unset the DI seed builds these two values; every
        // other member keeps its initializer default, so the seeded object must be equal to the provider's own default
        // on every field its consumers read — the launch argv, the launch identity and the inference-profile
        // fingerprint are then all bit-for-bit what they were before this knob existed.
        var providerDefault = new LlamaServerLaunchPolicyOptions();
        var seeded = new LlamaServerLaunchPolicyOptions
        {
            KvCacheType = StoredNodeSettings.DefaultKvCacheType,
            EnableGpuKvCacheQuantization = !string.Equals(StoredNodeSettings.DefaultKvCacheType, LlamaServerKvCacheTypes.F16, StringComparison.Ordinal)
        };

        AssertEx.Equal(providerDefault.KvCacheType, seeded.KvCacheType);
        AssertEx.Equal(providerDefault.EnableGpuKvCacheQuantization, seeded.EnableGpuKvCacheQuantization);
        AssertEx.Equal(providerDefault.ChatContextTokens, seeded.ChatContextTokens);
        AssertEx.Equal(providerDefault.EmbeddingContextTokens, seeded.EmbeddingContextTokens);
        AssertEx.Equal(providerDefault.RerankerContextTokens, seeded.RerankerContextTokens);
        AssertEx.Equal(providerDefault.ContextSafetyMarginTokens, seeded.ContextSafetyMarginTokens);
        AssertEx.Equal(providerDefault.DeterministicContextTokensOverride, seeded.DeterministicContextTokensOverride);
        AssertEx.Equal(providerDefault.EnableCpuThreadPolicy, seeded.EnableCpuThreadPolicy);
        AssertEx.Equal(providerDefault.AssumeSimultaneousMultithreading, seeded.AssumeSimultaneousMultithreading);
        AssertEx.Equal(providerDefault.CpuThreadReserve, seeded.CpuThreadReserve);
        AssertEx.Equal(providerDefault.CpuThreadCount, seeded.CpuThreadCount);
        AssertEx.Equal(providerDefault.CpuThreadsBatchCount, seeded.CpuThreadsBatchCount);

        // The fold constant the fingerprint omits at, the options default, and the node-settings default are one value.
        AssertEx.Equal(LlamaServerKvCacheTypes.Q8_0, StoredNodeSettings.DefaultKvCacheType);
        AssertEx.Equal(LlamaServerKvCacheTypes.Q8_0, providerDefault.KvCacheType);
    }

    [Test]
    public async Task Validate_NegativeThreadReserve_Throws()
    {
        await AssertValidationThrowsAsync(new LlamaServerLaunchPolicyOptions
        {
            CpuThreadReserve = -1
        });
    }

    [Test]
    public async Task Validate_NonPositiveExplicitThreadCount_Throws()
    {
        await AssertValidationThrowsAsync(new LlamaServerLaunchPolicyOptions
        {
            CpuThreadCount = 0
        });
    }

    [Test]
    public void ContextTokensForRole_MapsEachRoleToItsDefault()
    {
        var options = new LlamaServerLaunchPolicyOptions();

        AssertEx.Equal(options.ChatContextTokens, options.ContextTokensForRole(ModelRole.Chat));
        AssertEx.Equal(options.EmbeddingContextTokens, options.ContextTokensForRole(ModelRole.Embedding));
        AssertEx.Equal(options.RerankerContextTokens, options.ContextTokensForRole(ModelRole.Reranker));
    }

    private static Task AssertValidationThrowsAsync(LlamaServerLaunchPolicyOptions options)
    {
        // Validate() throws synchronously; ThrowsAsync catches a synchronous throw from the delegate too.
        return AssertEx.ThrowsAsync<InvalidOperationException>(() =>
        {
            options.Validate();
            return Task.CompletedTask;
        });
    }
}
