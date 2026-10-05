namespace XE_Local_AI_Engine.Tests.NodeSettings;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Client.Services.Invocation.Resilience;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Memory;
using XE_Local_AI_Engine.Client.Services.ModelFit.Fit;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Client.Services.Persistence;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Options;
using XE_Local_AI_Engine.Providers.WhisperCpp.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The runtime-settings accessor is the single read surface for migrated knobs and must honor the precedence
///     stored &gt; appsettings seed &gt; hardcoded default for every field: a stored value wins, an absent stored value
///     falls back to the appsettings seed, and an absent seed falls back to the hardcoded default.
/// </summary>
[Category(TestCategories.Unit)]
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

    [Test]
    public async Task ExternalAccessProfile_ReturnsTheStoredValueAndNullWhenUndecided()
    {
        // No fallback here, unlike every other getter on this surface: null is the ANSWER (nobody has decided), so
        // seeding it would silently decide for the operator and let the three gated services fire.
        var undecided = CreateSut(new StoredNodeSettings(), seedConfiguration: new Dictionary<string, string?>(StringComparer.Ordinal));
        AssertEx.Null(await undecided.GetExternalAccessProfileAsync());

        var offline = CreateSut(new StoredNodeSettings
            {
                ExternalAccessProfile = StoredNodeSettings.ExternalAccessProfileOffline
            },
            seedConfiguration: new Dictionary<string, string?>(StringComparer.Ordinal));
        AssertEx.Equal(StoredNodeSettings.ExternalAccessProfileOffline, await offline.GetExternalAccessProfileAsync());

        var pending = CreateSut(new StoredNodeSettings
            {
                ExternalAccessProfile = StoredNodeSettings.ExternalAccessProfilePending
            },
            seedConfiguration: new Dictionary<string, string?>(StringComparer.Ordinal));
        AssertEx.Equal(StoredNodeSettings.ExternalAccessProfilePending, await pending.GetExternalAccessProfileAsync());
    }

    [Test]
    public async Task AutoCheckSwitches_DefaultToOnWhenUnset()
    {
        // The whole no-regression promise rests on this: an upgraded node's file predates all three members, and if
        // unset read as OFF every existing install would silently stop checking for updates.
        var sut = CreateSut(new StoredNodeSettings(), seedConfiguration: new Dictionary<string, string?>(StringComparer.Ordinal));

        AssertEx.Equal(expected: true, await sut.GetAutoCheckApplicationUpdatesAsync());
        AssertEx.Equal(expected: true, await sut.GetAutoCheckRuntimeUpdatesAsync());
        AssertEx.Equal(expected: true, await sut.GetAutoProvisionFirstRunModelAsync());
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task AutoCheckSwitches_HonourTheStoredValue(bool stored)
    {
        var sut = CreateSut(new StoredNodeSettings
            {
                AutoCheckApplicationUpdates = stored,
                AutoCheckRuntimeUpdates = stored,
                AutoProvisionFirstRunModel = stored
            },
            seedConfiguration: new Dictionary<string, string?>(StringComparer.Ordinal));

        AssertEx.Equal(stored, await sut.GetAutoCheckApplicationUpdatesAsync());
        AssertEx.Equal(stored, await sut.GetAutoCheckRuntimeUpdatesAsync());
        AssertEx.Equal(stored, await sut.GetAutoProvisionFirstRunModelAsync());
    }

    [Test]
    public async Task Tunables_UnsetAndUnseeded_EqualTheConstantsTheyReplaced()
    {
        // Behaviour-unchanged proof: each getter is compared against the provider/service default it replaced, not against the
        // StoredNodeSettings mirror, so a drifted Default* constant fails here.
        var sut = CreateSut(new StoredNodeSettings(), seedConfiguration: new Dictionary<string, string?>(StringComparer.Ordinal));
        var supervisor = new LlamaServerSupervisorOptions();
        var launchPolicy = new LlamaServerLaunchPolicyOptions();

        AssertEx.Equal(supervisor.ReadinessTimeoutCap, sut.GetLlamaReadinessTimeoutCap());
        AssertEx.Equal(supervisor.HttpNetworkTimeout, sut.GetLlamaChatHttpTimeout());
        AssertEx.Equal(supervisor.EmbeddingHttpNetworkTimeout, sut.GetLlamaEmbeddingHttpTimeout());
        AssertEx.Null(sut.GetLlamaChatCacheRamMiB());
        AssertEx.Equal(launchPolicy.CpuThreadReserve, sut.GetLlamaCpuThreadReserve());
        // Exact double equality is the point: the fraction is serialized into the launch-policy fingerprint.
        AssertEx.Equal(LlamaServerLaunchPolicyOptions.DefaultGpuReserveFraction, sut.GetLlamaGpuReserveFraction());
        AssertEx.Equal(LlamaServerLaunchPolicyOptions.DefaultRamReserveFraction, sut.GetLlamaRamReserveFraction());
        AssertEx.Equal(new StableDiffusionRuntimeOptions().IdleTimeToLive, sut.GetImageIdleTimeToLive());
        AssertEx.Equal(new WhisperRuntimeOptions().InferenceTimeout, sut.GetTranscriptionInferenceTimeout());
        AssertEx.Equal(new ProviderCallBudgetOptions().MaxProviderCallsPerInvocation, sut.GetMaxProviderCallsPerInvocation());
        AssertEx.Equal(new HuggingFaceOptions().DownloadConnections, sut.GetHuggingFaceDownloadConnections());
        AssertEx.Equal(new AgentHomeRunRetentionOptions().RetentionDays, await sut.GetAgentHomeRunRetentionDaysAsync());
        AssertEx.Equal(MemoryFitEstimator.DefaultSafetyMarginFraction, sut.GetModelFitSafetyMarginFraction());
        AssertEx.Equal(new AgentHomeOptions().MaxRunSeconds, await sut.GetAgentHomeMaxRunSecondsAsync());
        AssertEx.Equal(expected: 300, await sut.GetCustomToolMaxTimeoutSecondsAsync());
        AssertEx.Equal(TimeSpan.FromSeconds(20), await sut.GetWebFetchTimeoutAsync());
        AssertEx.Equal(expected: 12_000, await sut.GetWebFetchMaxContentCharsAsync());
        AssertEx.Equal(expected: 5, await sut.GetKnowledgeSearchDefaultResultsAsync());
        AssertEx.Equal(expected: 20, await sut.GetKnowledgeSearchMaxResultsAsync());
    }

    /// <summary>
    ///     Operator decision 3 (model-matrix 2026-10-04): minimal 1024, low 2048, medium 8192, high 24576, and an
    ///     unspecified effort takes the low rung; a stored value wins per rung.
    /// </summary>
    [Test]
    public async Task ReasoningBudgets_DefaultToTheShippedLadderAndStoredValuesWin()
    {
        var unset = CreateSut(new StoredNodeSettings(), seedConfiguration: new Dictionary<string, string?>(StringComparer.Ordinal));
        var budgets = await unset.GetReasoningBudgetsAsync();
        AssertEx.Equal((1024, 2048, 8192, 24576, "low"), (budgets.Minimal, budgets.Low, budgets.Medium, budgets.High, budgets.UnspecifiedEffort));

        var stored = CreateSut(new StoredNodeSettings
            {
                ReasoningBudgetLowTokens = 512,
                ReasoningBudgetHighTokens = 16384,
                DefaultReasoningEffort = "medium"
            },
            seedConfiguration: new Dictionary<string, string?>(StringComparer.Ordinal));
        budgets = await stored.GetReasoningBudgetsAsync();
        AssertEx.Equal((1024, 512, 8192, 16384, "medium"), (budgets.Minimal, budgets.Low, budgets.Medium, budgets.High, budgets.UnspecifiedEffort));
    }

    /// <summary>Operator decision 4 (model-matrix F2): cap plus notice, at most 16384, by default; stored values win.</summary>
    [Test]
    public async Task ChatOutputCap_DefaultsToCapAt16384AndStoredValuesWin()
    {
        var unset = CreateSut(new StoredNodeSettings(), seedConfiguration: new Dictionary<string, string?>(StringComparer.Ordinal));
        AssertEx.Equal(new ChatOutputCap
        {
            Mode = "cap",
            MaxTokens = 16_384
        }, await unset.GetChatOutputCapAsync());

        var stored = CreateSut(new StoredNodeSettings
            {
                ChatOutputCapMode = "off",
                ChatOutputCapMaxTokens = 4096
            },
            seedConfiguration: new Dictionary<string, string?>(StringComparer.Ordinal));
        AssertEx.Equal(new ChatOutputCap
        {
            Mode = "off",
            MaxTokens = 4096
        }, await stored.GetChatOutputCapAsync());
    }

    [Test]
    public async Task Tunables_StoredAbsent_UseTheAppsettingsSeed()
    {
        var sut = CreateSut(new StoredNodeSettings(),
            seedConfiguration: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["HuggingFace:DownloadConnections"] = "8",
                ["StableDiffusionRuntime:IdleTimeToLive"] = "00:05:00",
                ["Agent:ProviderCallBudget:MaxProviderCallsPerInvocation"] = "150",
                ["AgentHome:RunRetention:RetentionDays"] = "0"
            },
            agentHome: new AgentHomeOptions
            {
                MaxRunSeconds = 1200
            });

        AssertEx.Equal(expected: 8, sut.GetHuggingFaceDownloadConnections());
        AssertEx.Equal(TimeSpan.FromMinutes(5), sut.GetImageIdleTimeToLive());
        AssertEx.Equal(expected: 150, sut.GetMaxProviderCallsPerInvocation());
        AssertEx.Equal(expected: 0, await sut.GetAgentHomeRunRetentionDaysAsync(), "0 turns the age limit off and must survive as a seed.");
        AssertEx.Equal(expected: 1200, await sut.GetAgentHomeMaxRunSecondsAsync());
    }

    [Test]
    public async Task Tunables_Stored_WinOverSeedAndDefault()
    {
        var sut = CreateSut(new StoredNodeSettings
            {
                LlamaReadinessTimeoutCapSeconds = 900,
                LlamaChatHttpTimeoutSeconds = 7200,
                LlamaEmbeddingHttpTimeoutSeconds = 120,
                LlamaChatCacheRamMiB = 0,
                LlamaCpuThreadReserve = 2,
                LlamaGpuReservePercent = 10,
                LlamaRamReservePercent = 20,
                ImageIdleTimeToLiveSeconds = 60,
                ModelFitSafetyMarginPercent = 20,
                MaxProviderCallsPerInvocation = 50,
                CustomToolMaxTimeoutSeconds = 600,
                WebFetchTimeoutSeconds = 45,
                WebFetchMaxContentChars = 30_000,
                KnowledgeSearchDefaultResults = 3,
                KnowledgeSearchMaxResults = 10,
                HuggingFaceDownloadConnections = 2,
                TranscriptionInferenceTimeoutMinutes = 90,
                AgentHomeMaxRunSeconds = 1800,
                AgentHomeRunRetentionDays = 7
            },
            seedConfiguration: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["HuggingFace:DownloadConnections"] = "8",
                ["Agent:ProviderCallBudget:MaxProviderCallsPerInvocation"] = "150"
            });

        AssertEx.Equal(TimeSpan.FromSeconds(900), sut.GetLlamaReadinessTimeoutCap());
        AssertEx.Equal(TimeSpan.FromSeconds(7200), sut.GetLlamaChatHttpTimeout());
        AssertEx.Equal(TimeSpan.FromSeconds(120), sut.GetLlamaEmbeddingHttpTimeout());
        AssertEx.Equal(expected: 0, sut.GetLlamaChatCacheRamMiB());
        AssertEx.Equal(expected: 2, sut.GetLlamaCpuThreadReserve());
        AssertEx.Equal(expected: 0.1d, sut.GetLlamaGpuReserveFraction());
        AssertEx.Equal(expected: 0.2d, sut.GetLlamaRamReserveFraction());
        AssertEx.Equal(TimeSpan.FromSeconds(60), sut.GetImageIdleTimeToLive());
        AssertEx.Equal(expected: 0.2d, sut.GetModelFitSafetyMarginFraction());
        AssertEx.Equal(expected: 50, sut.GetMaxProviderCallsPerInvocation());
        AssertEx.Equal(expected: 600, await sut.GetCustomToolMaxTimeoutSecondsAsync());
        AssertEx.Equal(TimeSpan.FromSeconds(45), await sut.GetWebFetchTimeoutAsync());
        AssertEx.Equal(expected: 30_000, await sut.GetWebFetchMaxContentCharsAsync());
        AssertEx.Equal(expected: 3, await sut.GetKnowledgeSearchDefaultResultsAsync());
        AssertEx.Equal(expected: 10, await sut.GetKnowledgeSearchMaxResultsAsync());
        AssertEx.Equal(expected: 2, sut.GetHuggingFaceDownloadConnections());
        AssertEx.Equal(TimeSpan.FromMinutes(90), sut.GetTranscriptionInferenceTimeout());
        AssertEx.Equal(expected: 1800, await sut.GetAgentHomeMaxRunSecondsAsync());
        AssertEx.Equal(expected: 7, await sut.GetAgentHomeRunRetentionDaysAsync());
    }

    [Test]
    public async Task ChatKnobs_UnsetAndUnseeded_EqualTheOptionsDefaultsTheyReplaced()
    {
        var sut = CreateSut(new StoredNodeSettings(), new Dictionary<string, string?>(StringComparer.Ordinal));

        var toolPipeline = new AgentToolPipelineOptions();
        AssertEx.Equal(toolPipeline.MaximumToolIterationsPerRequest, sut.GetToolPipelineMaxIterationsPerRequest());
        AssertEx.Equal(toolPipeline.MaxToolResultCharacters, sut.GetToolPipelineMaxToolResultChars());
        AssertEx.Equal(toolPipeline.MaxConsecutiveInvalidToolCallsPerTool, sut.GetToolPipelineMaxConsecutiveInvalidToolCalls());
        var providerBudget = new ProviderCallBudgetOptions();
        AssertEx.Equal(providerBudget.DefaultContextTokens, await sut.GetDefaultContextTokensAsync());
        AssertEx.Equal(new ConversationContextBudgetOptions().DefaultContextTokens, await sut.GetDefaultContextTokensAsync());
        AssertEx.Equal(providerBudget.RecentMessagesToKeep, await sut.GetProviderBudgetRecentMessagesToKeepAsync());
        AssertEx.Equal(providerBudget.MaxCumulativeInputTokens, await sut.GetProviderBudgetMaxCumulativeInputTokensAsync());
        AssertEx.Equal(new ConversationContextBudgetOptions().RecentTurnKeepCount, sut.GetContextBudgetRecentTurnKeepCount());
        var compaction = new ConversationCompactionOptions();
        AssertEx.Equal(compaction.AutoCompactEnabled, await sut.GetCompactionAutoEnabledAsync());
        AssertEx.Equal((int)Math.Round(compaction.AutoCompactFraction * 100), await sut.GetCompactionAutoCompactPercentAsync());
        AssertEx.Equal(compaction.RecentMessagesToKeepVerbatim, await sut.GetCompactionRecentMessagesVerbatimAsync());
        AssertEx.Equal(compaction.DistillEnabled, await sut.GetCompactionDistillEnabledAsync());
        var localChat = new LocalChatAgentOptions();
        AssertEx.Equal(localChat.MaxInlinedAttachmentChars, await sut.GetMaxInlinedAttachmentCharsAsync());
        AssertEx.Equal(localChat.KnowledgeChatTopK, await sut.GetKnowledgeChatTopKAsync());
        var resilience = new ProviderResilienceOptions();
        AssertEx.Equal(resilience.RetryEnabled, await sut.GetProviderRetryEnabledAsync());
        AssertEx.Equal(resilience.MaxRetries, await sut.GetProviderMaxRetriesAsync());
        var spawn = new SpawnOptions();
        AssertEx.Equal(spawn.MaxConcurrentSpawns, await sut.GetSpawnMaxConcurrentAsync());
        AssertEx.Equal(spawn.MaxCloudSpawns, await sut.GetSpawnMaxCloudAsync());
        AssertEx.Equal(spawn.QueueWaitSeconds, await sut.GetSpawnQueueWaitSecondsAsync());
    }

    [Test]
    public async Task ChatKnobs_StoredAbsent_UseTheAppsettingsSeed()
    {
        var sut = CreateSut(new StoredNodeSettings(),
            seedConfiguration: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Agent:ToolPipeline:MaximumToolIterationsPerRequest"] = "60",
                ["Agent:ToolPipeline:MaxToolResultCharacters"] = "20000",
                ["Agent:ToolPipeline:MaxConsecutiveInvalidToolCallsPerTool"] = "5",
                ["Agent:ProviderCallBudget:DefaultContextTokens"] = "16384",
                ["Agent:ProviderCallBudget:RecentMessagesToKeep"] = "10",
                ["Agent:ProviderCallBudget:MaxCumulativeInputTokens"] = "2000000",
                ["Agent:ConversationContextBudget:RecentTurnKeepCount"] = "6",
                ["Agent:ConversationCompaction:AutoCompactEnabled"] = "false",
                ["Agent:ConversationCompaction:AutoCompactFraction"] = "0.6",
                ["Agent:ConversationCompaction:RecentMessagesToKeepVerbatim"] = "12",
                ["Agent:ConversationCompaction:DistillEnabled"] = "false",
                ["Agent:ProviderResilience:RetryEnabled"] = "false",
                ["Agent:ProviderResilience:MaxRetries"] = "4",
                ["Spawn:MaxConcurrentSpawns"] = "5",
                ["Spawn:MaxCloudSpawns"] = "0",
                ["Spawn:QueueWaitSeconds"] = "0"
            },
            localChat: new LocalChatAgentOptions
            {
                MaxInlinedAttachmentChars = 90_000,
                KnowledgeChatTopK = 8
            });

        AssertEx.Equal(expected: 60, sut.GetToolPipelineMaxIterationsPerRequest());
        AssertEx.Equal(expected: 20_000, sut.GetToolPipelineMaxToolResultChars());
        AssertEx.Equal(expected: 5, sut.GetToolPipelineMaxConsecutiveInvalidToolCalls());
        AssertEx.Equal(expected: 16_384, await sut.GetDefaultContextTokensAsync());
        AssertEx.Equal(expected: 10, await sut.GetProviderBudgetRecentMessagesToKeepAsync());
        AssertEx.Equal(expected: 2_000_000, await sut.GetProviderBudgetMaxCumulativeInputTokensAsync());
        AssertEx.Equal(expected: 6, sut.GetContextBudgetRecentTurnKeepCount());
        AssertEx.False(await sut.GetCompactionAutoEnabledAsync());
        AssertEx.Equal(expected: 60, await sut.GetCompactionAutoCompactPercentAsync(), "the 0.6 fraction seeds the stored percent");
        AssertEx.Equal(expected: 12, await sut.GetCompactionRecentMessagesVerbatimAsync());
        AssertEx.False(await sut.GetCompactionDistillEnabledAsync());
        AssertEx.Equal(expected: 90_000, await sut.GetMaxInlinedAttachmentCharsAsync());
        AssertEx.Equal(expected: 8, await sut.GetKnowledgeChatTopKAsync());
        AssertEx.False(await sut.GetProviderRetryEnabledAsync());
        AssertEx.Equal(expected: 4, await sut.GetProviderMaxRetriesAsync());
        AssertEx.Equal(expected: 5, await sut.GetSpawnMaxConcurrentAsync());
        AssertEx.Equal(expected: 0, await sut.GetSpawnMaxCloudAsync(), "0 forbids cloud sub-agents and must survive as a seed.");
        AssertEx.Equal(expected: 0, await sut.GetSpawnQueueWaitSecondsAsync(), "0 rejects at once and must survive as a seed.");
    }

    [Test]
    public async Task DefaultContextTokens_FallsBackToTheTurnBudgetKey_WhenOnlyThatIsConfigured()
    {
        // One stored knob replaced two config keys of the same meaning; a node that only ever set the turn-budget one keeps it.
        var sut = CreateSut(new StoredNodeSettings(),
            seedConfiguration: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Agent:ConversationContextBudget:DefaultContextTokens"] = "32768"
            });

        AssertEx.Equal(expected: 32_768, await sut.GetDefaultContextTokensAsync());
    }

    [Test]
    public async Task ChatKnobs_SeedBelowTheOptionsFloor_FallsBackToTheDefault()
    {
        // A seed the bound options validator would refuse is never handed to a consumer as the effective value.
        var sut = CreateSut(new StoredNodeSettings(),
            seedConfiguration: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Spawn:MaxConcurrentSpawns"] = "0",
                ["Agent:ConversationCompaction:AutoCompactFraction"] = "0.1"
            });

        AssertEx.Equal(StoredNodeSettings.DefaultSpawnMaxConcurrent, await sut.GetSpawnMaxConcurrentAsync());
        AssertEx.Equal(StoredNodeSettings.DefaultCompactionAutoCompactPercent, await sut.GetCompactionAutoCompactPercentAsync());
    }

    [Test]
    public async Task ChatKnobs_Stored_WinOverSeedAndDefault()
    {
        var sut = CreateSut(new StoredNodeSettings
            {
                ToolPipelineMaxIterationsPerRequest = 80,
                ToolPipelineMaxToolResultChars = 100_000,
                ToolPipelineMaxConsecutiveInvalidToolCalls = 7,
                DefaultContextTokens = 65_536,
                ProviderBudgetRecentMessagesToKeep = 20,
                ProviderBudgetMaxCumulativeInputTokens = 8_000_000,
                ContextBudgetRecentTurnKeepCount = 9,
                CompactionAutoEnabled = true,
                CompactionAutoCompactPercent = 90,
                CompactionRecentMessagesVerbatim = 16,
                CompactionDistillEnabled = true,
                MaxInlinedAttachmentChars = 200_000,
                KnowledgeChatTopK = 12,
                ProviderRetryEnabled = true,
                ProviderMaxRetries = 7,
                SpawnMaxConcurrent = 1,
                SpawnMaxCloud = 2,
                SpawnQueueWaitSeconds = 30
            },
            seedConfiguration: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Agent:ToolPipeline:MaximumToolIterationsPerRequest"] = "60",
                ["Agent:ProviderCallBudget:DefaultContextTokens"] = "16384",
                ["Agent:ConversationContextBudget:RecentTurnKeepCount"] = "6",
                ["Agent:ConversationCompaction:AutoCompactEnabled"] = "false",
                ["Agent:ConversationCompaction:AutoCompactFraction"] = "0.6",
                ["Agent:ConversationCompaction:DistillEnabled"] = "false",
                ["Agent:ProviderResilience:RetryEnabled"] = "false",
                ["Spawn:MaxConcurrentSpawns"] = "5"
            },
            localChat: new LocalChatAgentOptions
            {
                MaxInlinedAttachmentChars = 90_000,
                KnowledgeChatTopK = 8
            });

        AssertEx.Equal(expected: 80, sut.GetToolPipelineMaxIterationsPerRequest());
        AssertEx.Equal(expected: 100_000, sut.GetToolPipelineMaxToolResultChars());
        AssertEx.Equal(expected: 7, sut.GetToolPipelineMaxConsecutiveInvalidToolCalls());
        AssertEx.Equal(expected: 65_536, await sut.GetDefaultContextTokensAsync());
        AssertEx.Equal(expected: 20, await sut.GetProviderBudgetRecentMessagesToKeepAsync());
        AssertEx.Equal(expected: 8_000_000, await sut.GetProviderBudgetMaxCumulativeInputTokensAsync());
        AssertEx.Equal(expected: 9, sut.GetContextBudgetRecentTurnKeepCount());
        AssertEx.True(await sut.GetCompactionAutoEnabledAsync());
        AssertEx.Equal(expected: 90, await sut.GetCompactionAutoCompactPercentAsync());
        AssertEx.Equal(expected: 16, await sut.GetCompactionRecentMessagesVerbatimAsync());
        AssertEx.True(await sut.GetCompactionDistillEnabledAsync());
        AssertEx.Equal(expected: 200_000, await sut.GetMaxInlinedAttachmentCharsAsync());
        AssertEx.Equal(expected: 12, await sut.GetKnowledgeChatTopKAsync());
        AssertEx.True(await sut.GetProviderRetryEnabledAsync());
        AssertEx.Equal(expected: 7, await sut.GetProviderMaxRetriesAsync());
        AssertEx.Equal(expected: 1, await sut.GetSpawnMaxConcurrentAsync());
        AssertEx.Equal(expected: 2, await sut.GetSpawnMaxCloudAsync());
        AssertEx.Equal(expected: 30, await sut.GetSpawnQueueWaitSecondsAsync());
    }

    [Test]
    public async Task KnowledgeAndUsageKnobs_UnsetAndUnseeded_EqualTheOptionsDefaultsTheyReplaced()
    {
        var sut = CreateSut(new StoredNodeSettings(), new Dictionary<string, string?>(StringComparer.Ordinal));

        var knowledge = new KnowledgeBaseOptions();
        AssertEx.Equal(knowledge.AdaptiveRerankingEnabled, await sut.GetKnowledgeAdaptiveRerankingEnabledAsync());
        AssertEx.Equal(knowledge.RetrievalLatencyBudgetMilliseconds, await sut.GetKnowledgeRetrievalLatencyBudgetMsAsync());
        AssertEx.Equal(knowledge.ScheduledModelReindexEnabled, sut.GetKnowledgeScheduledReindexEnabled());
        AssertEx.Equal(knowledge.ScheduledModelReindexIntervalMinutes, sut.GetKnowledgeScheduledReindexIntervalMinutes());
        AssertEx.Equal(knowledge.AgentToolsEnabled, await sut.GetKnowledgeAgentToolsEnabledAsync());
        AssertEx.False(await sut.GetAllowCloudModelAccessAsync(), "Node-local data must never reach a cloud model by default.");
        var chatRetention = new ChatRetentionOptions();
        AssertEx.Equal(chatRetention.Enabled, await sut.GetChatRetentionEnabledAsync());
        AssertEx.Equal(chatRetention.RetentionDays, await sut.GetChatRetentionDaysAsync());
        var logRetention = new AgentExecutionLogRetentionOptions();
        AssertEx.Equal(logRetention.Enabled, await sut.GetAgentExecutionLogRetentionEnabledAsync());
        AssertEx.Equal(logRetention.RetentionDays, await sut.GetAgentExecutionLogRetentionDaysAsync());
        AssertEx.Equal(new NodeDbBackupOptions().RetainCount, await sut.GetNodeDbBackupRetainCountAsync());
        AssertEx.Equal(64L * 1024 * 1024 * 1024, await sut.GetBenchmarkKldCacheMaxBytesAsync());
        AssertEx.Equal(new SchedulerOptions().HistoryRetentionDays, await sut.GetSchedulerHistoryRetentionDaysAsync());
    }

    [Test]
    public async Task KnowledgeAndUsageKnobs_StoredAbsent_UseTheAppsettingsSeed()
    {
        var sut = CreateSut(new StoredNodeSettings(),
            seedConfiguration: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["KnowledgeBase:AdaptiveRerankingEnabled"] = "false",
                ["KnowledgeBase:RetrievalLatencyBudgetMilliseconds"] = "900",
                ["KnowledgeBase:ScheduledModelReindexEnabled"] = "false",
                ["KnowledgeBase:ScheduledModelReindexIntervalMinutes"] = "15",
                ["KnowledgeBase:AgentToolsEnabled"] = "false",
                ["KnowledgeBase:AllowCloudModelAccess"] = "true",
                ["ChatRetention:Enabled"] = "true",
                ["ChatRetention:RetentionDays"] = "90",
                ["AgentExecutionLogRetention:Enabled"] = "false",
                ["AgentExecutionLogRetention:RetentionDays"] = "14",
                ["NodeDbBackup:RetainCount"] = "7",
                ["Benchmarks:KldCacheMaxBytes"] = "2147483648",
                ["Scheduler:HistoryRetentionDays"] = "60"
            });

        AssertEx.False(await sut.GetKnowledgeAdaptiveRerankingEnabledAsync());
        AssertEx.Equal(expected: 900, await sut.GetKnowledgeRetrievalLatencyBudgetMsAsync());
        AssertEx.False(sut.GetKnowledgeScheduledReindexEnabled());
        AssertEx.Equal(expected: 15, sut.GetKnowledgeScheduledReindexIntervalMinutes());
        AssertEx.False(await sut.GetKnowledgeAgentToolsEnabledAsync());
        AssertEx.True(await sut.GetAllowCloudModelAccessAsync(), "An existing opt-in in appsettings must survive the migration.");
        AssertEx.True(await sut.GetChatRetentionEnabledAsync());
        AssertEx.Equal(expected: 90, await sut.GetChatRetentionDaysAsync());
        AssertEx.False(await sut.GetAgentExecutionLogRetentionEnabledAsync());
        AssertEx.Equal(expected: 14, await sut.GetAgentExecutionLogRetentionDaysAsync());
        AssertEx.Equal(expected: 7, await sut.GetNodeDbBackupRetainCountAsync());
        AssertEx.Equal(expected: 2_147_483_648L, await sut.GetBenchmarkKldCacheMaxBytesAsync());
        AssertEx.Equal(expected: 60, await sut.GetSchedulerHistoryRetentionDaysAsync());
    }

    [Test]
    public async Task RetentionSeeds_BelowOneDay_FallBackToTheDefault()
    {
        // A zero or negative window puts the cutoff at or past now and would purge everything, so such a seed is never used.
        var sut = CreateSut(new StoredNodeSettings(),
            seedConfiguration: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ChatRetention:RetentionDays"] = "0",
                ["AgentExecutionLogRetention:RetentionDays"] = "-3"
            });

        AssertEx.Equal(StoredNodeSettings.DefaultChatRetentionDays, await sut.GetChatRetentionDaysAsync());
        AssertEx.Equal(StoredNodeSettings.DefaultAgentExecutionLogRetentionDays, await sut.GetAgentExecutionLogRetentionDaysAsync());
    }

    [Test]
    public async Task KnowledgeAndUsageKnobs_Stored_WinOverSeedAndDefault()
    {
        var sut = CreateSut(new StoredNodeSettings
            {
                KnowledgeAdaptiveRerankingEnabled = true,
                KnowledgeRetrievalLatencyBudgetMs = 2000,
                KnowledgeScheduledReindexEnabled = true,
                KnowledgeScheduledReindexIntervalMinutes = 240,
                KnowledgeAgentToolsEnabled = true,
                AllowCloudModelAccess = false,
                ChatRetentionEnabled = false,
                ChatRetentionDays = 365,
                AgentExecutionLogRetentionEnabled = true,
                AgentExecutionLogRetentionDays = 120,
                NodeDbBackupRetainCount = 10,
                BenchmarkKldCacheMaxBytes = 8L * 1024 * 1024 * 1024,
                SchedulerHistoryRetentionDays = 5
            },
            seedConfiguration: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["KnowledgeBase:AdaptiveRerankingEnabled"] = "false",
                ["KnowledgeBase:RetrievalLatencyBudgetMilliseconds"] = "900",
                ["KnowledgeBase:ScheduledModelReindexEnabled"] = "false",
                ["KnowledgeBase:ScheduledModelReindexIntervalMinutes"] = "15",
                ["KnowledgeBase:AgentToolsEnabled"] = "false",
                ["KnowledgeBase:AllowCloudModelAccess"] = "true",
                ["ChatRetention:Enabled"] = "true",
                ["ChatRetention:RetentionDays"] = "90",
                ["AgentExecutionLogRetention:Enabled"] = "false",
                ["NodeDbBackup:RetainCount"] = "7",
                ["Scheduler:HistoryRetentionDays"] = "60"
            });

        AssertEx.True(await sut.GetKnowledgeAdaptiveRerankingEnabledAsync());
        AssertEx.Equal(expected: 2000, await sut.GetKnowledgeRetrievalLatencyBudgetMsAsync());
        AssertEx.True(sut.GetKnowledgeScheduledReindexEnabled());
        AssertEx.Equal(expected: 240, sut.GetKnowledgeScheduledReindexIntervalMinutes());
        AssertEx.True(await sut.GetKnowledgeAgentToolsEnabledAsync());
        AssertEx.False(await sut.GetAllowCloudModelAccessAsync(), "A stored opt-out beats an appsettings opt-in.");
        AssertEx.False(await sut.GetChatRetentionEnabledAsync());
        AssertEx.Equal(expected: 365, await sut.GetChatRetentionDaysAsync());
        AssertEx.True(await sut.GetAgentExecutionLogRetentionEnabledAsync());
        AssertEx.Equal(expected: 120, await sut.GetAgentExecutionLogRetentionDaysAsync());
        AssertEx.Equal(expected: 10, await sut.GetNodeDbBackupRetainCountAsync());
        AssertEx.Equal(8L * 1024 * 1024 * 1024, await sut.GetBenchmarkKldCacheMaxBytesAsync());
        AssertEx.Equal(expected: 5, await sut.GetSchedulerHistoryRetentionDaysAsync());
    }

    [Test]
    public void OfferSwitchTwins_FollowStoredOverSeedOverDefault()
    {
        // The synchronous twins the tool-offer seam reads must resolve exactly like their async getters.
        var unset = CreateSut(new StoredNodeSettings(), new Dictionary<string, string?>(StringComparer.Ordinal));
        var seeded = CreateSut(new StoredNodeSettings(),
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["KnowledgeBase:AgentToolsEnabled"] = "false",
                ["KnowledgeBase:AllowCloudModelAccess"] = "true"
            });
        var stored = CreateSut(new StoredNodeSettings
            {
                KnowledgeAgentToolsEnabled = true,
                AllowCloudModelAccess = false
            },
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["KnowledgeBase:AgentToolsEnabled"] = "false",
                ["KnowledgeBase:AllowCloudModelAccess"] = "true"
            });

        AssertEx.True(unset.GetKnowledgeAgentToolsEnabled());
        AssertEx.False(unset.GetAllowCloudModelAccess());
        AssertEx.False(seeded.GetKnowledgeAgentToolsEnabled());
        AssertEx.True(seeded.GetAllowCloudModelAccess());
        AssertEx.True(stored.GetKnowledgeAgentToolsEnabled());
        AssertEx.False(stored.GetAllowCloudModelAccess());
    }

    [Test]
    public async Task BackgroundModels_InheritTheStoredDefaultModel_WhenNothingNamesThem()
    {
        // The gap this closes: the old composition seed read appsettings only, so a default model chosen in Node Settings
        // never reached the analysis, eval and extraction agents.
        var sut = CreateSut(new StoredNodeSettings
            {
                DefaultModelName = "stored-default:7b"
            },
            new Dictionary<string, string?>(StringComparer.Ordinal),
            localChat: new LocalChatAgentOptions
            {
                DefaultModel = "appsettings-default:1b"
            });

        AssertEx.Equal("stored-default:7b", await sut.GetPlaybookAnalysisModelNameAsync());
        AssertEx.Equal("stored-default:7b", await sut.GetPlaybookEvalModelNameAsync());
        AssertEx.Equal("stored-default:7b", await sut.GetMemoryExtractionModelNameAsync());
    }

    [Test]
    [Arguments("ext:remote-connection/gpt-x", ModelTrustLocality.Local)]
    [Arguments("gpt-5-codex", ModelTrustLocality.Cloud)]
    [Arguments("ext:unknown/model", ModelTrustLocality.Unresolved)]
    public async Task BackgroundModels_NeverInheritANonLocalStoredDefault_FallToTheAppsettingsSeed(string storedDefault, ModelTrustLocality locality)
    {
        // The stored default may be a cloud or ext: id (the chat policy admits them); a background step must not follow it off-node.
        var trust = Substitute.For<IModelTrustResolver>();
        trust.ResolveAsync(storedDefault, Arg.Any<CancellationToken>()).Returns(locality);
        var sut = CreateSut(new StoredNodeSettings
            {
                DefaultModelName = storedDefault
            },
            new Dictionary<string, string?>(StringComparer.Ordinal),
            localChat: new LocalChatAgentOptions
            {
                DefaultModel = "appsettings-default:1b"
            },
            modelTrustResolver: trust);

        AssertEx.Equal(storedDefault, await sut.GetDefaultModelNameAsync());
        AssertEx.Equal("appsettings-default:1b", await sut.GetPlaybookAnalysisModelNameAsync());
        AssertEx.Equal("appsettings-default:1b", await sut.GetPlaybookEvalModelNameAsync());
        AssertEx.Equal("appsettings-default:1b", await sut.GetMemoryExtractionModelNameAsync());
    }

    [Test]
    public async Task BackgroundModels_ExplicitLocalOverride_WinsOverANonLocalStoredDefault()
    {
        var trust = Substitute.For<IModelTrustResolver>();
        trust.ResolveAsync("gpt-5-codex", Arg.Any<CancellationToken>()).Returns(ModelTrustLocality.Cloud);
        var sut = CreateSut(new StoredNodeSettings
            {
                DefaultModelName = "gpt-5-codex",
                PlaybookAnalysisModelName = "local-analysis:4b"
            },
            new Dictionary<string, string?>(StringComparer.Ordinal),
            localChat: new LocalChatAgentOptions
            {
                DefaultModel = "appsettings-default:1b"
            },
            modelTrustResolver: trust);

        AssertEx.Equal("local-analysis:4b", await sut.GetPlaybookAnalysisModelNameAsync());
        AssertEx.Equal("appsettings-default:1b", await sut.GetMemoryExtractionModelNameAsync());
    }

    [Test]
    public async Task BackgroundModels_FollowStoredOverSectionSeedOverOllamaChatModel()
    {
        var seeds = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Ollama:ChatModel"] = "ollama-override:1b",
            ["PlaybookEval:ModelName"] = "eval-seed:3b"
        };
        var sut = CreateSut(new StoredNodeSettings
            {
                DefaultModelName = "stored-default:7b",
                MemoryExtractionModelName = "stored-extraction:8b"
            },
            seeds);

        // Analysis names nothing of its own, so the out-of-band Ollama override the runner also honours wins over the default.
        AssertEx.Equal("ollama-override:1b", await sut.GetPlaybookAnalysisModelNameAsync());
        AssertEx.Equal("eval-seed:3b", await sut.GetPlaybookEvalModelNameAsync());
        AssertEx.Equal("stored-extraction:8b", await sut.GetMemoryExtractionModelNameAsync());
    }

    [Test]
    public async Task RuntimeAndWorkspaceKnobs_UnsetAndUnseeded_EqualTheOptionsDefaultsTheyReplaced()
    {
        var sut = CreateSut(new StoredNodeSettings(), new Dictionary<string, string?>(StringComparer.Ordinal));

        var image = new StableDiffusionRuntimeOptions();
        AssertEx.Equal(image.MaxLoadedProcesses, sut.GetImageMaxLoadedProcesses());
        AssertEx.Equal(image.TextEncoderOnGpu, sut.GetImageTextEncoderOnGpu());
        var graph = new GraphWorkflowOptions();
        AssertEx.Equal(graph.MaxConcurrentRuns, sut.GetGraphWorkflowMaxConcurrentRuns());
        AssertEx.Equal(graph.DefaultNodeTimeoutSeconds, sut.GetGraphWorkflowDefaultNodeTimeoutSeconds());
        var sessions = new WorkSessionOptions();
        AssertEx.Equal(sessions.MaxStepsPerRun, sut.GetWorkSessionMaxStepsPerRun());
        AssertEx.Equal(sessions.MaxConcurrentSessions, sut.GetWorkSessionMaxConcurrentSessions());
        var development = new DevelopmentOptions();
        AssertEx.Equal(development.MaxAttemptDurationSeconds, sut.GetDevelopmentMaxAttemptDurationSeconds());
        AssertEx.Equal(development.MaxToolCalls, sut.GetDevelopmentMaxToolCalls());
        AssertEx.Equal(development.MaxOutputTokens, sut.GetDevelopmentMaxOutputTokens());
        var agentHome = new AgentHomeOptions();
        AssertEx.Equal(agentHome.MaxInnerToolCalls, await sut.GetAgentHomeMaxInnerToolCallsAsync());
        AssertEx.Equal(agentHome.PatchApplyTimeoutSeconds, await sut.GetAgentHomePatchApplyTimeoutSecondsAsync());
        var retention = new AgentHomeRunRetentionOptions();
        AssertEx.Equal(retention.MaxRuns, await sut.GetAgentHomeRunRetentionMaxRunsAsync());
        AssertEx.Equal(retention.MaxTotalBytes, await sut.GetAgentHomeRunRetentionMaxTotalBytesAsync());
    }

    [Test]
    public async Task RuntimeAndWorkspaceKnobs_StoredAbsent_UseTheAppsettingsSeed()
    {
        var sut = CreateSut(new StoredNodeSettings(),
            seedConfiguration: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["StableDiffusionRuntime:MaxLoadedProcesses"] = "2",
                ["StableDiffusionRuntime:TextEncoderOnGpu"] = "true",
                ["GraphWorkflows:MaxConcurrentRuns"] = "8",
                ["GraphWorkflows:DefaultNodeTimeoutSeconds"] = "900",
                ["WorkSessions:MaxStepsPerRun"] = "50",
                ["WorkSessions:MaxConcurrentSessions"] = "2",
                ["Development:MaxAttemptDurationSeconds"] = "3600",
                ["Development:MaxToolCalls"] = "128",
                ["Development:MaxOutputTokens"] = "65536",
                ["AgentHome:RunRetention:MaxRuns"] = "0",
                ["AgentHome:RunRetention:MaxTotalBytes"] = "0"
            },
            agentHome: new AgentHomeOptions
            {
                MaxInnerToolCalls = 48,
                PatchApplyTimeoutSeconds = 300
            });

        AssertEx.Equal(expected: 2, sut.GetImageMaxLoadedProcesses());
        AssertEx.True(sut.GetImageTextEncoderOnGpu());
        AssertEx.Equal(expected: 8, sut.GetGraphWorkflowMaxConcurrentRuns());
        AssertEx.Equal(expected: 900, sut.GetGraphWorkflowDefaultNodeTimeoutSeconds());
        AssertEx.Equal(expected: 50, sut.GetWorkSessionMaxStepsPerRun());
        AssertEx.Equal(expected: 2, sut.GetWorkSessionMaxConcurrentSessions());
        AssertEx.Equal(expected: 3600, sut.GetDevelopmentMaxAttemptDurationSeconds());
        AssertEx.Equal(expected: 128, sut.GetDevelopmentMaxToolCalls());
        AssertEx.Equal(expected: 65536, sut.GetDevelopmentMaxOutputTokens());
        AssertEx.Equal(expected: 48, await sut.GetAgentHomeMaxInnerToolCallsAsync());
        AssertEx.Equal(expected: 300, await sut.GetAgentHomePatchApplyTimeoutSecondsAsync());
        AssertEx.Equal(expected: 0, await sut.GetAgentHomeRunRetentionMaxRunsAsync(), "0 turns the count limit off and must survive as a seed.");
        AssertEx.Equal(expected: 0L, await sut.GetAgentHomeRunRetentionMaxTotalBytesAsync(), "0 turns the byte limit off and must survive as a seed.");
    }

    [Test]
    public async Task RuntimeAndWorkspaceKnobs_Stored_WinOverSeedAndDefault()
    {
        var sut = CreateSut(new StoredNodeSettings
            {
                ImageMaxLoadedProcesses = 3,
                ImageTextEncoderOnGpu = false,
                GraphWorkflowMaxConcurrentRuns = 16,
                GraphWorkflowDefaultNodeTimeoutSeconds = 120,
                WorkSessionMaxStepsPerRun = 10,
                WorkSessionMaxConcurrentSessions = 4,
                DevelopmentMaxAttemptDurationSeconds = 600,
                DevelopmentMaxToolCalls = 32,
                DevelopmentMaxOutputTokens = 4096,
                AgentHomeMaxInnerToolCalls = 12,
                AgentHomePatchApplyTimeoutSeconds = 60,
                AgentHomeRunRetentionMaxRuns = 50,
                AgentHomeRunRetentionMaxTotalBytes = 1024L * 1024 * 1024
            },
            seedConfiguration: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["StableDiffusionRuntime:MaxLoadedProcesses"] = "2",
                ["StableDiffusionRuntime:TextEncoderOnGpu"] = "true",
                ["GraphWorkflows:MaxConcurrentRuns"] = "8",
                ["WorkSessions:MaxStepsPerRun"] = "50",
                ["Development:MaxToolCalls"] = "128",
                ["AgentHome:RunRetention:MaxRuns"] = "0"
            },
            agentHome: new AgentHomeOptions
            {
                MaxInnerToolCalls = 48
            });

        AssertEx.Equal(expected: 3, sut.GetImageMaxLoadedProcesses());
        AssertEx.False(sut.GetImageTextEncoderOnGpu(), "A stored off beats an appsettings on.");
        AssertEx.Equal(expected: 16, sut.GetGraphWorkflowMaxConcurrentRuns());
        AssertEx.Equal(expected: 120, sut.GetGraphWorkflowDefaultNodeTimeoutSeconds());
        AssertEx.Equal(expected: 10, sut.GetWorkSessionMaxStepsPerRun());
        AssertEx.Equal(expected: 4, sut.GetWorkSessionMaxConcurrentSessions());
        AssertEx.Equal(expected: 600, sut.GetDevelopmentMaxAttemptDurationSeconds());
        AssertEx.Equal(expected: 32, sut.GetDevelopmentMaxToolCalls());
        AssertEx.Equal(expected: 4096, sut.GetDevelopmentMaxOutputTokens());
        AssertEx.Equal(expected: 12, await sut.GetAgentHomeMaxInnerToolCallsAsync());
        AssertEx.Equal(expected: 60, await sut.GetAgentHomePatchApplyTimeoutSecondsAsync());
        AssertEx.Equal(expected: 50, await sut.GetAgentHomeRunRetentionMaxRunsAsync());
        AssertEx.Equal(1024L * 1024 * 1024, await sut.GetAgentHomeRunRetentionMaxTotalBytesAsync());
    }

    private static NodeRuntimeSettings CreateSut(StoredNodeSettings stored,
        IDictionary<string, string?> seedConfiguration,
        LocalChatAgentOptions? localChat = null,
        AgentHomeOptions? agentHome = null,
        WorkerNodeOptions? workerNode = null,
        IModelTrustResolver? modelTrustResolver = null)
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
            }),
            new Lazy<IModelTrustResolver>(modelTrustResolver ?? Substitute.For<IModelTrustResolver>()));
    }
}
