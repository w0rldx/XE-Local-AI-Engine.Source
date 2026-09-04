namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.DependencyInjection.Modules;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

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
    public void Seed_AssignsOnlyTheKvCacheTypeAndItsQuantizationFlag()
    {
        // The DI seed itself, not a hand-written re-expression of it. Any third assignment added to
        // BuildSeededLlamaServerLaunchPolicyOptions makes the seeded object differ from the provider default on a
        // third member, which breaks invariant 2 (byte-identical argv, launch identity and profile fingerprint) and
        // must break this test. f16 is used because it moves BOTH members the seed is allowed to move.
        using var services = new ServiceCollection()
                             .AddSingleton(StubNodeRuntimeSettings.Create().WithKvCacheType(LlamaServerKvCacheTypes.F16).Build())
                             .BuildServiceProvider();

        var seeded = AddNodeModelRuntimeExtensions.BuildSeededLlamaServerLaunchPolicyOptions(services);
        var providerDefault = new LlamaServerLaunchPolicyOptions();
        var moved = typeof(LlamaServerLaunchPolicyOptions)
                    .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .Where(property => !Equals(property.GetValue(seeded), property.GetValue(providerDefault)))
                    .Select(static property => property.Name)
                    .Order()
                    .ToArray();

        AssertEx.Equal($"{nameof(LlamaServerLaunchPolicyOptions.EnableGpuKvCacheQuantization)},{nameof(LlamaServerLaunchPolicyOptions.KvCacheType)}",
            string.Join(',', moved),
            "The DI seed must assign exactly the KV cache type and its quantization flag.");
        AssertEx.Equal(LlamaServerKvCacheTypes.F16, seeded.KvCacheType);
        AssertEx.False(seeded.EnableGpuKvCacheQuantization);
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
