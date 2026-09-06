namespace XE_Local_AI_Engine.Tests.NodeSettings;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The runtime-settings accessor is the single read surface for migrated knobs and must honor the precedence
///     stored &gt; appsettings seed &gt; hardcoded default for every field: a stored value wins, an absent stored value
///     falls back to the appsettings seed, and an absent seed falls back to the hardcoded default.
/// </summary>
public sealed class NodeRuntimeSettingsTests
{
    [Test]
    public async Task StoredValuesPresent_OverrideSeed()
    {
        var sut = CreateSut(new StoredNodeSettings
            {
                DefaultModelName = "stored-model",
                EnableTools = false,
                ToolCapableModels = ["stored-tool-model"],
                OllamaEndpoint = "http://stored:1234",
                LlamaMaxLoadedProcesses = 9,
                LlamaIdleTimeToLiveSeconds = 1200,
                KeepModelWarmEnabled = true,
                KeepModelWarmModelName = "stored-warm-model",
                KeepModelWarmIntervalSeconds = 240,
                MaxResponseSizeMb = 42,
                RecommendedLlamaCppTag = "b8888",
                OrchestrationIdleTimeoutSeconds = 333
            },
            seedConfiguration: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Ollama:Endpoint"] = "http://seed:9999"
            });

        AssertEx.Equal("stored-model", await sut.GetDefaultModelNameAsync());
        AssertEx.Equal(expected: false, await sut.GetEnableToolsAsync());
        AssertEx.Equal("stored-tool-model", (await sut.GetToolCapableModelsAsync())[0]);
        AssertEx.Equal("http://stored:1234", await sut.GetOllamaEndpointAsync());
        AssertEx.Equal(expected: 9, await sut.GetLlamaMaxLoadedProcessesAsync());
        AssertEx.Equal(TimeSpan.FromSeconds(1200), await sut.GetLlamaIdleTimeToLiveAsync());
        AssertEx.Equal(expected: true, await sut.GetKeepModelWarmEnabledAsync());
        AssertEx.Equal("stored-warm-model", await sut.GetKeepModelWarmModelNameAsync());
        AssertEx.Equal(TimeSpan.FromSeconds(240), await sut.GetKeepModelWarmIntervalAsync());
        AssertEx.Equal(expected: 42, await sut.GetMaxResponseSizeMbAsync());
        AssertEx.Equal("b8888", await sut.GetRecommendedLlamaCppTagAsync());
        AssertEx.Equal(expected: 333, await sut.GetOrchestrationIdleTimeoutSecondsAsync());
    }

    [Test]
    public async Task StoredAbsent_UsesAppsettingsSeed()
    {
        var sut = CreateSut(new StoredNodeSettings(),
            seedConfiguration: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Ollama:Endpoint"] = "http://seed:9999",
                ["HuggingFace:DefaultQuant"] = "Q6_K",
                ["HuggingFace:DiskMarginBytes"] = "2000000000",
                ["Agent:Orchestration:IdleTimeoutSeconds"] = "444"
            },
            localChat: new LocalChatAgentOptions
            {
                DefaultModel = "seed-model",
                EnableTools = false
            },
            agentHome: new AgentHomeOptions
            {
                ToolCapableModels = ["seed-tool-model"],
                PrepareTimeoutSeconds = 111,
                CommandTimeoutSeconds = 222
            },
            workerNode: new WorkerNodeOptions
            {
                NodeName = "n",
                MaxResponseSizeMb = 77,
                MaxPendingToolCallAgeMinutes = 33
            });

        AssertEx.Equal("seed-model", await sut.GetDefaultModelNameAsync());
        AssertEx.Equal(expected: false, await sut.GetEnableToolsAsync());
        AssertEx.Equal("seed-tool-model", (await sut.GetToolCapableModelsAsync())[0]);
        AssertEx.Equal("http://seed:9999", await sut.GetOllamaEndpointAsync());
        AssertEx.Equal("Q6_K", await sut.GetHuggingFaceDefaultQuantAsync());
        AssertEx.Equal(expected: 2_000_000_000L, await sut.GetHuggingFaceDiskMarginBytesAsync());
        AssertEx.Equal(expected: 77, await sut.GetMaxResponseSizeMbAsync());
        AssertEx.Equal(expected: 444, await sut.GetOrchestrationIdleTimeoutSecondsAsync());
        AssertEx.Equal(expected: 111, await sut.GetAgentHomePrepareTimeoutSecondsAsync());
        AssertEx.Equal(expected: 222, await sut.GetAgentHomeCommandTimeoutSecondsAsync());
        AssertEx.Equal(expected: 33, await sut.GetMaxPendingToolCallAgeMinutesAsync());
    }

    [Test]
    public async Task DetachedGraceSeconds_HonoursStoredThenSeedThenDefault()
    {
        // The disconnect grace is the one stream-budget knob an operator edits, so it follows the stored-node-setting
        // chain rather than a plain options key — including the case that matters most: a stored 0 (never cancel) must
        // WIN over a positive seed rather than being read as "unset" and re-seeded.
        var empty = new Dictionary<string, string?>(StringComparer.Ordinal);
        var seedWorkerNode = new WorkerNodeOptions
        {
            NodeName = "n",
            DetachedGraceSeconds = 600
        };

        var stored = CreateSut(new StoredNodeSettings
        {
            DetachedGraceSeconds = 45
        }, empty, workerNode: seedWorkerNode);
        AssertEx.Equal(expected: 45, await stored.GetDetachedGraceSecondsAsync());

        var storedZero = CreateSut(new StoredNodeSettings
        {
            DetachedGraceSeconds = 0
        }, empty, workerNode: seedWorkerNode);
        AssertEx.Equal(expected: 0, await storedZero.GetDetachedGraceSecondsAsync());

        var seeded = CreateSut(new StoredNodeSettings(), empty, workerNode: seedWorkerNode);
        AssertEx.Equal(expected: 600, await seeded.GetDetachedGraceSecondsAsync());

        var defaulted = CreateSut(new StoredNodeSettings(), empty);
        AssertEx.Equal(StoredNodeSettings.DefaultDetachedGraceSeconds, await defaulted.GetDetachedGraceSecondsAsync());
    }

    // Deliberately a SYNC test: the sync twin exists because the reaper's timer callback is structurally synchronous,
    // and asserting it from an async test trips the "await the async overload instead" analyzers (CA1849/S6966).
    [Test]
    public void DetachedGraceSeconds_SyncTwin_ResolvesTheSameValue()
    {
        var sut = CreateSut(new StoredNodeSettings
            {
                DetachedGraceSeconds = 45
            },
            new Dictionary<string, string?>(StringComparer.Ordinal),
            workerNode: new WorkerNodeOptions
            {
                NodeName = "n",
                DetachedGraceSeconds = 600
            });

        AssertEx.Equal(expected: 45, sut.GetDetachedGraceSeconds());
    }

    [Test]
    public async Task StoredAndSeedAbsent_UsesHardcodedDefault()
    {
        // No stored value, no Ollama/HF seed in configuration, default Options instances.
        var sut = CreateSut(new StoredNodeSettings(), seedConfiguration: new Dictionary<string, string?>(StringComparer.Ordinal));

        AssertEx.Equal(StoredNodeSettings.DefaultOllamaEndpoint, await sut.GetOllamaEndpointAsync());
        AssertEx.Equal(StoredNodeSettings.DefaultHuggingFaceQuant, await sut.GetHuggingFaceDefaultQuantAsync());
        AssertEx.Equal(StoredNodeSettings.DefaultHuggingFaceDiskMarginBytes, await sut.GetHuggingFaceDiskMarginBytesAsync());
        AssertEx.Equal(expected: StoredNodeSettings.DefaultLlamaMaxLoadedProcesses, await sut.GetLlamaMaxLoadedProcessesAsync());
        AssertEx.Equal(TimeSpan.FromSeconds(StoredNodeSettings.DefaultLlamaIdleTimeToLiveSeconds), await sut.GetLlamaIdleTimeToLiveAsync());
        AssertEx.Equal(expected: StoredNodeSettings.DefaultKeepModelWarmEnabled, await sut.GetKeepModelWarmEnabledAsync());
        AssertEx.Null(await sut.GetKeepModelWarmModelNameAsync());
        AssertEx.Equal(TimeSpan.FromSeconds(StoredNodeSettings.DefaultKeepModelWarmIntervalSeconds), await sut.GetKeepModelWarmIntervalAsync());
        AssertEx.Equal(LlamaCppReleasePins.PinnedTag, await sut.GetRecommendedLlamaCppTagAsync());
    }

    [Test]
    [Arguments(null, false)]
    [Arguments(false, false)]
    [Arguments(true, true)]
    public async Task ToolRelevanceEnabled_ResolvesStoredValueOverTheHardcodedOff(bool? stored, bool expected)
    {
        // No appsettings seed exists for this switch, so the precedence is stored > hardcoded off and nothing else.
        var sut = CreateSut(new StoredNodeSettings
            {
                ToolRelevanceEnabled = stored
            },
            seedConfiguration: new Dictionary<string, string?>(StringComparer.Ordinal));

        AssertEx.Equal(expected, await sut.GetToolRelevanceEnabledAsync());
    }

    [Test]
    public async Task SpeculativeAndCacheReuse_StoredValuesPresent_OverrideDefaults()
    {
        var sut = CreateSut(new StoredNodeSettings
            {
                ChatCacheReuse = 512,
                SpeculativeMode = "draft-simple",
                SpeculativeDraftModelName = "draft-model",
                SpeculativeDraftMaxTokens = 5,
                SpeculativeDraftGpuLayers = 12
            },
            seedConfiguration: new Dictionary<string, string?>(StringComparer.Ordinal));

        AssertEx.Equal(expected: 512, await sut.GetChatCacheReuseAsync());
        AssertEx.Equal("draft-simple", await sut.GetSpeculativeModeAsync());
        AssertEx.Equal("draft-model", await sut.GetSpeculativeDraftModelNameAsync());
        AssertEx.Equal(expected: 5, await sut.GetSpeculativeDraftMaxTokensAsync());
        AssertEx.Equal(expected: 12, await sut.GetSpeculativeDraftGpuLayersAsync());
    }

    [Test]
    public async Task SpeculativeAndCacheReuse_StoredAbsent_UsesHardcodedDefaults()
    {
        var sut = CreateSut(new StoredNodeSettings(), seedConfiguration: new Dictionary<string, string?>(StringComparer.Ordinal));

        AssertEx.Equal(expected: StoredNodeSettings.DefaultChatCacheReuse, await sut.GetChatCacheReuseAsync());
        AssertEx.Equal(StoredNodeSettings.DefaultSpeculativeMode, await sut.GetSpeculativeModeAsync());
        AssertEx.Null(await sut.GetSpeculativeDraftModelNameAsync());
        AssertEx.Equal(expected: StoredNodeSettings.DefaultSpeculativeDraftMaxTokens, await sut.GetSpeculativeDraftMaxTokensAsync());
        AssertEx.Null(await sut.GetSpeculativeDraftGpuLayersAsync());
    }

    [Test]
    public async Task SpeculativeMode_FallsBackToDisabled_WhenStoredUnknown()
    {
        // A malformed stored mode (Normalize would null it, but the accessor guards independently as well).
        var sut = CreateSut(new StoredNodeSettings
            {
                SpeculativeMode = "not-a-real-mode"
            },
            seedConfiguration: new Dictionary<string, string?>(StringComparer.Ordinal));

        AssertEx.Equal(StoredNodeSettings.DefaultSpeculativeMode, await sut.GetSpeculativeModeAsync());
    }

    [Test]
    public async Task KvCacheType_StoredValuePresent_OverridesTheDefault()
    {
        var sut = CreateSut(new StoredNodeSettings
            {
                KvCacheType = "q4_0"
            },
            seedConfiguration: new Dictionary<string, string?>(StringComparer.Ordinal));

        AssertEx.Equal("q4_0", await sut.GetKvCacheTypeAsync());
    }

    [Test]
    public void KvCacheType_SyncTwin_MirrorsTheAsyncAccessor()
    {
        // The DI seed reads the SYNCHRONOUS getter at host build, so it must resolve the same value.
        var sut = CreateSut(new StoredNodeSettings
            {
                KvCacheType = "q4_0"
            },
            seedConfiguration: new Dictionary<string, string?>(StringComparer.Ordinal));

        AssertEx.Equal("q4_0", sut.GetKvCacheType());
    }

    [Test]
    public async Task KvCacheType_StoredAbsentOrUnknown_UsesTheDefault()
    {
        // Unset is the byte-identical-default path: the DI seed then builds options equal to the provider's own.
        var unset = CreateSut(new StoredNodeSettings(), seedConfiguration: new Dictionary<string, string?>(StringComparer.Ordinal));
        AssertEx.Equal(StoredNodeSettings.DefaultKvCacheType, await unset.GetKvCacheTypeAsync());

        var unknown = CreateSut(new StoredNodeSettings
            {
                KvCacheType = "not-a-real-type"
            },
            seedConfiguration: new Dictionary<string, string?>(StringComparer.Ordinal));
        AssertEx.Equal(StoredNodeSettings.DefaultKvCacheType, await unknown.GetKvCacheTypeAsync());
    }

    [Test]
    public async Task RecommendedTag_FallsBackToPin_WhenStoredMalformed()
    {
        // A malformed stored tag (Normalize would null it, but guard the accessor independently as well).
        var sut = CreateSut(new StoredNodeSettings
            {
                RecommendedLlamaCppTag = "garbage"
            },
            seedConfiguration: new Dictionary<string, string?>(StringComparer.Ordinal));

        AssertEx.Equal(LlamaCppReleasePins.PinnedTag, await sut.GetRecommendedLlamaCppTagAsync());
    }

    private static NodeRuntimeSettings CreateSut(StoredNodeSettings stored,
        IDictionary<string, string?> seedConfiguration,
        LocalChatAgentOptions? localChat = null,
        AgentHomeOptions? agentHome = null,
        WorkerNodeOptions? workerNode = null)
    {
        var store = Substitute.For<INodeSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(stored);
        store.Load(Arg.Any<CancellationToken>()).Returns(stored);

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(seedConfiguration).Build();

        return new NodeRuntimeSettings(store,
            configuration,
            Options.Create(localChat ?? new LocalChatAgentOptions()),
            Options.Create(agentHome ?? new AgentHomeOptions()),
            Options.Create(workerNode ?? new WorkerNodeOptions
            {
                NodeName = "test-node"
            }));
    }
}
