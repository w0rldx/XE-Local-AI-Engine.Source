namespace XE_Local_AI_Engine.Tests.NodeSettings;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Options;
using XE_Local_AI_Engine.Providers.WhisperCpp.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Restart-gated tunables seeded by one-line module registrations, proven on the real composed host: only the
///     settings store is faked, so the registrations and their order carry each stored value.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class NodeSettingsCompositionSeedingTests
{
    [Test]
    public async Task ProviderCallBudget_FollowsTheStoredValue_OverItsOwnConfigSection()
    {
        // The section sets its own value, so this fails if the node-settings Configure ever runs BEFORE AI.Agent's Bind.
        await using var factory = CreateFactory(new StoredNodeSettings
            {
                MaxProviderCallsPerInvocation = 77
            },
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Agent:ProviderCallBudget:MaxProviderCallsPerInvocation"] = "150"
            });

        var options = factory.Services.GetRequiredService<IOptions<ProviderCallBudgetOptions>>().Value;

        AssertEx.Equal(expected: 77, options.MaxProviderCallsPerInvocation);
    }

    [Test]
    public async Task ProviderCallBudget_WithNothingStored_KeepsItsConfigSectionValue()
    {
        await using var factory = CreateFactory(new StoredNodeSettings(),
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Agent:ProviderCallBudget:MaxProviderCallsPerInvocation"] = "150"
            });

        var options = factory.Services.GetRequiredService<IOptions<ProviderCallBudgetOptions>>().Value;

        AssertEx.Equal(expected: 150, options.MaxProviderCallsPerInvocation);
    }

    [Test]
    public async Task ImageAndTranscriptionRuntimes_FollowTheStoredTimeouts()
    {
        await using var factory = CreateFactory(new StoredNodeSettings
        {
            ImageIdleTimeToLiveSeconds = 120,
            TranscriptionInferenceTimeoutMinutes = 45
        });

        AssertEx.Equal(TimeSpan.FromSeconds(120), factory.Services.GetRequiredService<StableDiffusionRuntimeOptions>().IdleTimeToLive);
        AssertEx.Equal(TimeSpan.FromMinutes(45), factory.Services.GetRequiredService<WhisperRuntimeOptions>().InferenceTimeout);
    }

    [Test]
    public async Task ImageAndTranscriptionRuntimes_WithNothingStored_KeepTheProviderDefaults()
    {
        await using var factory = CreateFactory(new StoredNodeSettings());

        AssertEx.Equal(new StableDiffusionRuntimeOptions().IdleTimeToLive,
            factory.Services.GetRequiredService<StableDiffusionRuntimeOptions>().IdleTimeToLive);
        AssertEx.Equal(new WhisperRuntimeOptions().InferenceTimeout,
            factory.Services.GetRequiredService<WhisperRuntimeOptions>().InferenceTimeout);
    }

    [Test]
    public async Task AgentHomeRunRetention_FollowsTheStoredDays()
    {
        await using var factory = CreateFactory(new StoredNodeSettings
            {
                AgentHomeRunRetentionDays = 7
            },
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["AgentHome:RunRetention:RetentionDays"] = "90"
            });

        var options = factory.Services.GetRequiredService<IOptions<AgentHomeRunRetentionOptions>>().Value;

        AssertEx.Equal(expected: 7, options.RetentionDays);
    }

    private static TestServerWebAppFactory CreateFactory(StoredNodeSettings stored, IReadOnlyDictionary<string, string?>? configuration = null)
    {
        var store = Substitute.For<INodeSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(stored);
        store.Load(Arg.Any<CancellationToken>()).Returns(stored);

        return new TestServerWebAppFactory
        {
            AdditionalConfiguration = configuration,
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<INodeSettingsStore>();
                services.AddSingleton(store);
            }
        };
    }
}
