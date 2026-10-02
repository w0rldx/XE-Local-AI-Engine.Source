namespace XE_Local_AI_Engine.Tests.NodeSettings;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
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
    public async Task ToolPipeline_FollowsTheStoredValues_OverItsOwnConfigSection()
    {
        // The section sets every value, so this fails if the node-settings Configure ever runs BEFORE AI.Agent's Bind.
        await using var factory = CreateFactory(new StoredNodeSettings
            {
                ToolPipelineMaxIterationsPerRequest = 12,
                ToolPipelineMaxToolResultChars = 4096,
                ToolPipelineMaxConsecutiveInvalidToolCalls = 9
            },
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Agent:ToolPipeline:MaximumToolIterationsPerRequest"] = "60",
                ["Agent:ToolPipeline:MaxToolResultCharacters"] = "20000",
                ["Agent:ToolPipeline:MaxConsecutiveInvalidToolCallsPerTool"] = "5"
            });

        var options = factory.Services.GetRequiredService<IOptions<AgentToolPipelineOptions>>().Value;

        AssertEx.Equal(expected: 12, options.MaximumToolIterationsPerRequest);
        AssertEx.Equal(expected: 4096, options.MaxToolResultCharacters);
        AssertEx.Equal(expected: 9, options.MaxConsecutiveInvalidToolCallsPerTool);
    }

    [Test]
    public async Task ScheduledReindex_FollowsTheStoredValues_OverItsOwnConfigSection()
    {
        // The section sets both, so this fails if the node-settings Configure ever runs BEFORE the KnowledgeBase Bind.
        await using var factory = CreateFactory(new StoredNodeSettings
            {
                KnowledgeScheduledReindexEnabled = false,
                KnowledgeScheduledReindexIntervalMinutes = 30
            },
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["KnowledgeBase:ScheduledModelReindexEnabled"] = "true",
                ["KnowledgeBase:ScheduledModelReindexIntervalMinutes"] = "90"
            });

        var options = factory.Services.GetRequiredService<IOptions<KnowledgeBaseOptions>>().Value;

        AssertEx.False(options.ScheduledModelReindexEnabled);
        AssertEx.Equal(expected: 30, options.ScheduledModelReindexIntervalMinutes);
    }

    [Test]
    public async Task ScheduledReindex_WithNothingStored_KeepsItsConfigSectionValues()
    {
        await using var factory = CreateFactory(new StoredNodeSettings(),
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["KnowledgeBase:ScheduledModelReindexEnabled"] = "false",
                ["KnowledgeBase:ScheduledModelReindexIntervalMinutes"] = "90"
            });

        var options = factory.Services.GetRequiredService<IOptions<KnowledgeBaseOptions>>().Value;

        AssertEx.False(options.ScheduledModelReindexEnabled);
        AssertEx.Equal(expected: 90, options.ScheduledModelReindexIntervalMinutes);
    }

    [Test]
    public async Task ToolPipeline_WithNothingStored_KeepsItsConfigSectionValues()
    {
        await using var factory = CreateFactory(new StoredNodeSettings(),
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Agent:ToolPipeline:MaximumToolIterationsPerRequest"] = "60",
                ["Agent:ToolPipeline:MaxToolResultCharacters"] = "20000",
                ["Agent:ToolPipeline:MaxConsecutiveInvalidToolCallsPerTool"] = "5"
            });

        var options = factory.Services.GetRequiredService<IOptions<AgentToolPipelineOptions>>().Value;

        AssertEx.Equal(expected: 60, options.MaximumToolIterationsPerRequest);
        AssertEx.Equal(expected: 20_000, options.MaxToolResultCharacters);
        AssertEx.Equal(expected: 5, options.MaxConsecutiveInvalidToolCallsPerTool);
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
    public async Task ImageRuntime_FollowsTheStoredCapAndEncoderPlacement_OverItsOwnConfigSection()
    {
        await using var factory = CreateFactory(new StoredNodeSettings
            {
                ImageMaxLoadedProcesses = 3,
                ImageTextEncoderOnGpu = true
            },
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["StableDiffusionRuntime:MaxLoadedProcesses"] = "2",
                ["StableDiffusionRuntime:TextEncoderOnGpu"] = "false"
            });

        var options = factory.Services.GetRequiredService<StableDiffusionRuntimeOptions>();

        AssertEx.Equal(expected: 3, options.MaxLoadedProcesses);
        AssertEx.True(options.TextEncoderOnGpu);
    }

    [Test]
    public async Task GraphWorkflows_FollowTheStoredValues_OverTheirOwnConfigSection()
    {
        // The section sets both, so this fails if the node-settings Configure ever runs BEFORE the GraphWorkflows Bind.
        await using var factory = CreateFactory(new StoredNodeSettings
            {
                GraphWorkflowMaxConcurrentRuns = 9,
                GraphWorkflowDefaultNodeTimeoutSeconds = 120
            },
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["GraphWorkflows:MaxConcurrentRuns"] = "2",
                ["GraphWorkflows:DefaultNodeTimeoutSeconds"] = "900"
            });

        var options = factory.Services.GetRequiredService<IOptions<GraphWorkflowOptions>>().Value;

        AssertEx.Equal(expected: 9, options.MaxConcurrentRuns);
        AssertEx.Equal(expected: 120, options.DefaultNodeTimeoutSeconds);
    }

    [Test]
    public async Task WorkSessions_FollowTheStoredValues_OverTheirOwnConfigSection()
    {
        await using var factory = CreateFactory(new StoredNodeSettings
            {
                WorkSessionMaxStepsPerRun = 40,
                WorkSessionMaxConcurrentSessions = 3
            },
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["WorkSessions:MaxStepsPerRun"] = "10",
                ["WorkSessions:MaxConcurrentSessions"] = "2"
            });

        var options = factory.Services.GetRequiredService<IOptions<WorkSessionOptions>>().Value;

        AssertEx.Equal(expected: 40, options.MaxStepsPerRun);
        AssertEx.Equal(expected: 3, options.MaxConcurrentSessions);
    }

    [Test]
    public async Task Development_FollowsTheStoredValues_OverItsOwnConfigSection()
    {
        await using var factory = CreateFactory(new StoredNodeSettings
            {
                DevelopmentMaxAttemptDurationSeconds = 600,
                DevelopmentMaxToolCalls = 16,
                DevelopmentMaxOutputTokens = 4096
            },
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Development:MaxAttemptDurationSeconds"] = "3600",
                ["Development:MaxToolCalls"] = "128",
                ["Development:MaxOutputTokens"] = "65536"
            });

        var options = factory.Services.GetRequiredService<IOptions<DevelopmentOptions>>().Value;

        AssertEx.Equal(expected: 600, options.MaxAttemptDurationSeconds);
        AssertEx.Equal(expected: 16, options.MaxToolCalls);
        AssertEx.Equal(expected: 4096, options.MaxOutputTokens);
    }

    [Test]
    public async Task RuntimeAndWorkspaceOptions_WithNothingStored_KeepTheirConfigSectionValues()
    {
        await using var factory = CreateFactory(new StoredNodeSettings(),
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["StableDiffusionRuntime:MaxLoadedProcesses"] = "2",
                ["StableDiffusionRuntime:TextEncoderOnGpu"] = "true",
                ["GraphWorkflows:MaxConcurrentRuns"] = "2",
                ["WorkSessions:MaxStepsPerRun"] = "10",
                ["Development:MaxToolCalls"] = "128"
            });

        var image = factory.Services.GetRequiredService<StableDiffusionRuntimeOptions>();
        AssertEx.Equal(expected: 2, image.MaxLoadedProcesses);
        AssertEx.True(image.TextEncoderOnGpu);
        AssertEx.Equal(expected: 2, factory.Services.GetRequiredService<IOptions<GraphWorkflowOptions>>().Value.MaxConcurrentRuns);
        AssertEx.Equal(expected: 10, factory.Services.GetRequiredService<IOptions<WorkSessionOptions>>().Value.MaxStepsPerRun);
        AssertEx.Equal(expected: 128, factory.Services.GetRequiredService<IOptions<DevelopmentOptions>>().Value.MaxToolCalls);
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
