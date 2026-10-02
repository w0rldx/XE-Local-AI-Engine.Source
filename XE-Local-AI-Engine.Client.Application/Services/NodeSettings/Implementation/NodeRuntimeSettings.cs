namespace XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     Resolves the effective value of each migrated runtime knob with the precedence
///     <c>stored &gt; appsettings seed &gt; hardcoded default</c>.
/// </summary>
/// <remarks>
///     The stored value comes from the cached <see cref="INodeSettingsStore" />; the appsettings seed is captured from
///     the bound options/configuration at construction, so first-run behaviour matches plain appsettings. For knobs
///     with no config section (the llama.cpp supervisor cap/TTL) the seed IS the hardcoded default.
/// </remarks>
public sealed class NodeRuntimeSettings : INodeRuntimeSettings
{
    private readonly bool _enableToolsSeed;
    private readonly string _defaultModelSeed;
    private readonly long _hfDiskMarginSeed;
    private readonly string _hfQuantSeed;
    private readonly int _maxResponseSizeMbSeed;
    private readonly int _maxPendingToolCallAgeMinutesSeed;
    private readonly int _detachedGraceSecondsSeed;
    private readonly long _agentHomeMaxPatchBytesSeed;
    private readonly long _agentHomeMaxSelectedFolderBytesSeed;
    private readonly int _agentHomeCommandTimeoutSeed;
    private readonly int _agentHomePrepareTimeoutSeed;
    private readonly int _agentHomeMaxRunSecondsSeed;
    private readonly int _agentHomeRunRetentionDaysSeed;
    private readonly int _hfDownloadConnectionsSeed;
    private readonly TimeSpan _imageIdleTimeToLiveSeed;
    private readonly int _maxProviderCallsPerInvocationSeed;
    private readonly int _orchestrationIdleTimeoutSeed;
    private readonly int _transcriptionIdleTimeoutMinutesSeed;
    private readonly string _ollamaEndpointSeed;
    private readonly Lazy<IModelTrustResolver> _modelTrustResolver;
    private readonly INodeSettingsStore _store;
    private readonly IReadOnlyList<string> _toolCapableModelsSeed;
    private readonly int _toolPipelineMaxIterationsSeed;
    private readonly int _toolPipelineMaxToolResultCharsSeed;
    private readonly int _toolPipelineMaxConsecutiveInvalidToolCallsSeed;
    private readonly int _defaultContextTokensSeed;
    private readonly int _providerBudgetRecentMessagesToKeepSeed;
    private readonly int _providerBudgetMaxCumulativeInputTokensSeed;
    private readonly int _contextBudgetRecentTurnKeepCountSeed;
    private readonly bool _compactionAutoEnabledSeed;
    private readonly int _compactionAutoCompactPercentSeed;
    private readonly int _compactionRecentMessagesVerbatimSeed;
    private readonly bool _compactionDistillEnabledSeed;
    private readonly int _maxInlinedAttachmentCharsSeed;
    private readonly int _knowledgeChatTopKSeed;
    private readonly bool _providerRetryEnabledSeed;
    private readonly int _providerMaxRetriesSeed;
    private readonly int _spawnMaxConcurrentSeed;
    private readonly int _spawnMaxCloudSeed;
    private readonly int _spawnQueueWaitSecondsSeed;
    private readonly bool _knowledgeAdaptiveRerankingEnabledSeed;
    private readonly int _knowledgeRetrievalLatencyBudgetMsSeed;
    private readonly bool _knowledgeScheduledReindexEnabledSeed;
    private readonly int _knowledgeScheduledReindexIntervalMinutesSeed;
    private readonly bool _knowledgeAgentToolsEnabledSeed;
    private readonly bool _allowCloudModelAccessSeed;
    private readonly string? _playbookAnalysisModelNameSeed;
    private readonly string? _playbookEvalModelNameSeed;
    private readonly string? _memoryExtractionModelNameSeed;
    private readonly bool _chatRetentionEnabledSeed;
    private readonly int _chatRetentionDaysSeed;
    private readonly bool _agentExecutionLogRetentionEnabledSeed;
    private readonly int _agentExecutionLogRetentionDaysSeed;
    private readonly int _nodeDbBackupRetainCountSeed;
    private readonly long _benchmarkKldCacheMaxBytesSeed;
    private readonly int _schedulerHistoryRetentionDaysSeed;
    private readonly int _imageMaxLoadedProcessesSeed;
    private readonly bool _imageTextEncoderOnGpuSeed;
    private readonly int _graphWorkflowMaxConcurrentRunsSeed;
    private readonly int _graphWorkflowDefaultNodeTimeoutSecondsSeed;
    private readonly int _workSessionMaxStepsPerRunSeed;
    private readonly int _workSessionMaxConcurrentSessionsSeed;
    private readonly int _developmentMaxAttemptDurationSecondsSeed;
    private readonly int _developmentMaxToolCallsSeed;
    private readonly int _developmentMaxOutputTokensSeed;
    private readonly int _agentHomeMaxInnerToolCallsSeed;
    private readonly int _agentHomePatchApplyTimeoutSecondsSeed;
    private readonly int _agentHomeRunRetentionMaxRunsSeed;
    private readonly long _agentHomeRunRetentionMaxTotalBytesSeed;

    public NodeRuntimeSettings(INodeSettingsStore store,
        IConfiguration configuration,
        IOptions<LocalChatAgentOptions> localChatOptions,
        IOptions<AgentHomeOptions> agentHomeOptions,
        IOptions<WorkerNodeOptions> workerNodeOptions,
        Lazy<IModelTrustResolver> modelTrustResolver)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        // No DI cycle (the resolver's graph reads INodeSettingsStore, never INodeRuntimeSettings); Lazy only defers its
        // data-protected stores past composition time, when this accessor is first resolved.
        _modelTrustResolver = modelTrustResolver ?? throw new ArgumentNullException(nameof(modelTrustResolver));
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(localChatOptions);
        ArgumentNullException.ThrowIfNull(agentHomeOptions);
        ArgumentNullException.ThrowIfNull(workerNodeOptions);

        var localChat = localChatOptions.Value;
        var agentHome = agentHomeOptions.Value;
        var workerNode = workerNodeOptions.Value;

        _defaultModelSeed = localChat.DefaultModel;
        _enableToolsSeed = localChat.EnableTools;
        _toolCapableModelsSeed = agentHome.ToolCapableModels;
        _agentHomePrepareTimeoutSeed = agentHome.PrepareTimeoutSeconds;
        _agentHomeCommandTimeoutSeed = agentHome.CommandTimeoutSeconds;
        _agentHomeMaxSelectedFolderBytesSeed = agentHome.MaxSelectedFolderBytes;
        _agentHomeMaxPatchBytesSeed = agentHome.MaxPatchBytes;
        _agentHomeMaxRunSecondsSeed = agentHome.MaxRunSeconds;
        _maxResponseSizeMbSeed = workerNode.MaxResponseSizeMb;
        _maxPendingToolCallAgeMinutesSeed = workerNode.MaxPendingToolCallAgeMinutes;
        _detachedGraceSecondsSeed = workerNode.DetachedGraceSeconds;
        _maxInlinedAttachmentCharsSeed = localChat.MaxInlinedAttachmentChars;
        _knowledgeChatTopKSeed = localChat.KnowledgeChatTopK;

        // Chat/agent-run seeds come from configuration for both reasons above (a Configure-d-from-this cycle, or modules not every context
        // runs). A value below its options validator's floor is ignored, so the seed is never one the bound options would refuse.
        _toolPipelineMaxIterationsSeed = IntSeed(configuration, "Agent:ToolPipeline:MaximumToolIterationsPerRequest", 1,
            StoredNodeSettings.DefaultToolPipelineMaxIterationsPerRequest);
        _toolPipelineMaxToolResultCharsSeed = IntSeed(configuration, "Agent:ToolPipeline:MaxToolResultCharacters", 1024,
            StoredNodeSettings.DefaultToolPipelineMaxToolResultChars);
        _toolPipelineMaxConsecutiveInvalidToolCallsSeed = IntSeed(configuration, "Agent:ToolPipeline:MaxConsecutiveInvalidToolCallsPerTool", 1,
            StoredNodeSettings.DefaultToolPipelineMaxConsecutiveInvalidToolCalls);
        // One stored knob replaces two config keys of the same meaning: the provider-round key wins, then the turn-budget key.
        _defaultContextTokensSeed = IntSeed(configuration, "Agent:ProviderCallBudget:DefaultContextTokens", 1,
            IntSeed(configuration, "Agent:ConversationContextBudget:DefaultContextTokens", 1, StoredNodeSettings.DefaultDefaultContextTokens));
        _providerBudgetRecentMessagesToKeepSeed = IntSeed(configuration, "Agent:ProviderCallBudget:RecentMessagesToKeep", 2,
            StoredNodeSettings.DefaultProviderBudgetRecentMessagesToKeep);
        _providerBudgetMaxCumulativeInputTokensSeed = IntSeed(configuration, "Agent:ProviderCallBudget:MaxCumulativeInputTokens", 1024,
            StoredNodeSettings.DefaultProviderBudgetMaxCumulativeInputTokens);
        _contextBudgetRecentTurnKeepCountSeed = IntSeed(configuration, "Agent:ConversationContextBudget:RecentTurnKeepCount", 2,
            StoredNodeSettings.DefaultContextBudgetRecentTurnKeepCount);
        _compactionAutoEnabledSeed = configuration.GetValue<bool?>("Agent:ConversationCompaction:AutoCompactEnabled")
                                     ?? StoredNodeSettings.DefaultCompactionAutoEnabled;
        var configuredAutoCompactFraction = configuration.GetValue<double?>("Agent:ConversationCompaction:AutoCompactFraction");
        _compactionAutoCompactPercentSeed = configuredAutoCompactFraction is >= 0.3 and <= 0.95
            ? (int)Math.Round(configuredAutoCompactFraction.Value * 100, MidpointRounding.AwayFromZero)
            : StoredNodeSettings.DefaultCompactionAutoCompactPercent;
        _compactionRecentMessagesVerbatimSeed = IntSeed(configuration, "Agent:ConversationCompaction:RecentMessagesToKeepVerbatim", 2,
            StoredNodeSettings.DefaultCompactionRecentMessagesVerbatim);
        _compactionDistillEnabledSeed = configuration.GetValue<bool?>("Agent:ConversationCompaction:DistillEnabled")
                                        ?? StoredNodeSettings.DefaultCompactionDistillEnabled;
        _providerRetryEnabledSeed = configuration.GetValue<bool?>("Agent:ProviderResilience:RetryEnabled")
                                    ?? StoredNodeSettings.DefaultProviderRetryEnabled;
        _providerMaxRetriesSeed = IntSeed(configuration, "Agent:ProviderResilience:MaxRetries", 0, StoredNodeSettings.DefaultProviderMaxRetries);
        _spawnMaxConcurrentSeed = IntSeed(configuration, "Spawn:MaxConcurrentSpawns", 1, StoredNodeSettings.DefaultSpawnMaxConcurrent);
        _spawnMaxCloudSeed = IntSeed(configuration, "Spawn:MaxCloudSpawns", 0, StoredNodeSettings.DefaultSpawnMaxCloud);
        _spawnQueueWaitSecondsSeed = IntSeed(configuration, "Spawn:QueueWaitSeconds", 0, StoredNodeSettings.DefaultSpawnQueueWaitSeconds);

        // Knowledge, privacy and usage seeds come from configuration too: KnowledgeBaseOptions is Configure-d FROM this accessor, and the
        // retention, backup, benchmark and scheduler options are registered by modules not every context runs.
        _knowledgeAdaptiveRerankingEnabledSeed = BoolSeed(configuration, "KnowledgeBase:AdaptiveRerankingEnabled",
            StoredNodeSettings.DefaultKnowledgeAdaptiveRerankingEnabled);
        _knowledgeRetrievalLatencyBudgetMsSeed = IntSeed(configuration, "KnowledgeBase:RetrievalLatencyBudgetMilliseconds", 1,
            StoredNodeSettings.DefaultKnowledgeRetrievalLatencyBudgetMs);
        _knowledgeScheduledReindexEnabledSeed = BoolSeed(configuration, "KnowledgeBase:ScheduledModelReindexEnabled",
            StoredNodeSettings.DefaultKnowledgeScheduledReindexEnabled);
        _knowledgeScheduledReindexIntervalMinutesSeed = IntSeed(configuration, "KnowledgeBase:ScheduledModelReindexIntervalMinutes", 1,
            StoredNodeSettings.DefaultKnowledgeScheduledReindexIntervalMinutes);
        _knowledgeAgentToolsEnabledSeed = BoolSeed(configuration, "KnowledgeBase:AgentToolsEnabled", StoredNodeSettings.DefaultKnowledgeAgentToolsEnabled);
        _allowCloudModelAccessSeed = BoolSeed(configuration, "KnowledgeBase:AllowCloudModelAccess", StoredNodeSettings.DefaultAllowCloudModelAccess);
        // The background-model seeds keep the Ollama:ChatModel override the invocation runner also honours; blank falls to the default model.
        var ollamaChatModel = NonBlank(configuration.GetValue<string>("Ollama:ChatModel"));
        _playbookAnalysisModelNameSeed = NonBlank(configuration.GetValue<string>("PlaybookAnalysis:ModelName")) ?? ollamaChatModel;
        _playbookEvalModelNameSeed = NonBlank(configuration.GetValue<string>("PlaybookEval:ModelName")) ?? ollamaChatModel;
        _memoryExtractionModelNameSeed = NonBlank(configuration.GetValue<string>("MemoryExtraction:ExtractionModelName")) ?? ollamaChatModel;
        _chatRetentionEnabledSeed = BoolSeed(configuration, "ChatRetention:Enabled", StoredNodeSettings.DefaultChatRetentionEnabled);
        _chatRetentionDaysSeed = IntSeed(configuration, "ChatRetention:RetentionDays", 1, StoredNodeSettings.DefaultChatRetentionDays);
        _agentExecutionLogRetentionEnabledSeed = BoolSeed(configuration, "AgentExecutionLogRetention:Enabled",
            StoredNodeSettings.DefaultAgentExecutionLogRetentionEnabled);
        _agentExecutionLogRetentionDaysSeed = IntSeed(configuration, "AgentExecutionLogRetention:RetentionDays", 1,
            StoredNodeSettings.DefaultAgentExecutionLogRetentionDays);
        _nodeDbBackupRetainCountSeed = IntSeed(configuration, "NodeDbBackup:RetainCount", 1, StoredNodeSettings.DefaultNodeDbBackupRetainCount);
        var configuredKldBytes = configuration.GetValue<long?>("Benchmarks:KldCacheMaxBytes");
        _benchmarkKldCacheMaxBytesSeed = configuredKldBytes is > 0 ? configuredKldBytes.Value : StoredNodeSettings.DefaultBenchmarkKldCacheMaxBytes;
        _schedulerHistoryRetentionDaysSeed = IntSeed(configuration, "Scheduler:HistoryRetentionDays", 1,
            StoredNodeSettings.DefaultSchedulerHistoryRetentionDays);

        // Runtime and workspace seeds come from configuration too: the graph-workflow, work-session and development options are
        // Configure-d FROM this accessor, and the image options are seeded from it in a host-build factory.
        _imageMaxLoadedProcessesSeed = IntSeed(configuration, "StableDiffusionRuntime:MaxLoadedProcesses", 1,
            StoredNodeSettings.DefaultImageMaxLoadedProcesses);
        _imageTextEncoderOnGpuSeed = BoolSeed(configuration, "StableDiffusionRuntime:TextEncoderOnGpu", StoredNodeSettings.DefaultImageTextEncoderOnGpu);
        _graphWorkflowMaxConcurrentRunsSeed = IntSeed(configuration, "GraphWorkflows:MaxConcurrentRuns", 1,
            StoredNodeSettings.DefaultGraphWorkflowMaxConcurrentRuns);
        _graphWorkflowDefaultNodeTimeoutSecondsSeed = IntSeed(configuration, "GraphWorkflows:DefaultNodeTimeoutSeconds", 1,
            StoredNodeSettings.DefaultGraphWorkflowDefaultNodeTimeoutSeconds);
        _workSessionMaxStepsPerRunSeed = IntSeed(configuration, "WorkSessions:MaxStepsPerRun", 1, StoredNodeSettings.DefaultWorkSessionMaxStepsPerRun);
        _workSessionMaxConcurrentSessionsSeed = IntSeed(configuration, "WorkSessions:MaxConcurrentSessions", 1,
            StoredNodeSettings.DefaultWorkSessionMaxConcurrentSessions);
        _developmentMaxAttemptDurationSecondsSeed = IntSeed(configuration, "Development:MaxAttemptDurationSeconds", 1,
            StoredNodeSettings.DefaultDevelopmentMaxAttemptDurationSeconds);
        _developmentMaxToolCallsSeed = IntSeed(configuration, "Development:MaxToolCalls", 1, StoredNodeSettings.DefaultDevelopmentMaxToolCalls);
        _developmentMaxOutputTokensSeed = IntSeed(configuration, "Development:MaxOutputTokens", 1, StoredNodeSettings.DefaultDevelopmentMaxOutputTokens);
        _agentHomeMaxInnerToolCallsSeed = agentHome.MaxInnerToolCalls;
        _agentHomePatchApplyTimeoutSecondsSeed = agentHome.PatchApplyTimeoutSeconds;
        // 0 is a valid retention seed: it turns that limit off.
        _agentHomeRunRetentionMaxRunsSeed = IntSeed(configuration, "AgentHome:RunRetention:MaxRuns", 0,
            StoredNodeSettings.DefaultAgentHomeRunRetentionMaxRuns);
        var configuredRetentionBytes = configuration.GetValue<long?>("AgentHome:RunRetention:MaxTotalBytes");
        _agentHomeRunRetentionMaxTotalBytesSeed = configuredRetentionBytes is >= 0
            ? configuredRetentionBytes.Value
            : StoredNodeSettings.DefaultAgentHomeRunRetentionMaxTotalBytes;

        // From configuration, not IOptions<OrchestrationAgentOptions>, to avoid a DI cycle: OrchestrationAgentOptions is itself Configure-d FROM
        // this accessor at the composition root, so taking IOptions<OrchestrationAgentOptions> here would depend on the option it configures.
        _orchestrationIdleTimeoutSeed = configuration.GetValue<int?>("Agent:Orchestration:IdleTimeoutSeconds")
                                        ?? StoredNodeSettings.DefaultOrchestrationIdleTimeoutSeconds;

        // From configuration rather than IOptions<TranscriptionOptions> for the same reason the Hugging Face seeds are: the transcription
        // options are registered by a module that not every host or test context runs, and this accessor is constructed in all of them.
        var configuredTranscriptionIdleTimeout = configuration.GetValue<int?>($"{TranscriptionOptions.Section}:IdleTimeoutMinutes");
        _transcriptionIdleTimeoutMinutesSeed = configuredTranscriptionIdleTimeout is > 0
            ? configuredTranscriptionIdleTimeout.Value
            : StoredNodeSettings.DefaultTranscriptionIdleTimeoutMinutes;

        // HuggingFaceOptions is registered as a plain singleton only after AddHuggingFaceGgufStore runs, which is not guaranteed in every
        // host/test context, so the HF seeds are read from configuration directly, mirroring the Options defaults.
        var configuredQuant = configuration.GetValue<string>("HuggingFace:DefaultQuant");
        _hfQuantSeed = string.IsNullOrWhiteSpace(configuredQuant) ? StoredNodeSettings.DefaultHuggingFaceQuant : configuredQuant;

        var configuredMargin = configuration.GetValue<long?>("HuggingFace:DiskMarginBytes");
        _hfDiskMarginSeed = configuredMargin is > 0 ? configuredMargin.Value : StoredNodeSettings.DefaultHuggingFaceDiskMarginBytes;

        // Seeds below come from configuration for the reasons above: their options are registered by modules not every context runs,
        // or (Agent:ProviderCallBudget) are Configure-d FROM this accessor, so an IOptions<T> dependency would be a cycle.
        var configuredConnections = configuration.GetValue<int?>("HuggingFace:DownloadConnections");
        _hfDownloadConnectionsSeed = configuredConnections is > 0 ? configuredConnections.Value : StoredNodeSettings.DefaultHuggingFaceDownloadConnections;

        var configuredImageTtl = configuration.GetValue<TimeSpan?>("StableDiffusionRuntime:IdleTimeToLive");
        _imageIdleTimeToLiveSeed = configuredImageTtl is { } imageTtl && imageTtl > TimeSpan.Zero
            ? imageTtl
            : TimeSpan.FromSeconds(StoredNodeSettings.DefaultImageIdleTimeToLiveSeconds);

        var configuredProviderCalls = configuration.GetValue<int?>("Agent:ProviderCallBudget:MaxProviderCallsPerInvocation");
        _maxProviderCallsPerInvocationSeed = configuredProviderCalls is > 0
            ? configuredProviderCalls.Value
            : StoredNodeSettings.DefaultMaxProviderCallsPerInvocation;

        // 0 is a valid seed here: it turns the age limit off, and an absent stored value must keep that.
        var configuredRetentionDays = configuration.GetValue<int?>("AgentHome:RunRetention:RetentionDays");
        _agentHomeRunRetentionDaysSeed = configuredRetentionDays is >= 0 ? configuredRetentionDays.Value : StoredNodeSettings.DefaultAgentHomeRunRetentionDays;

        // Ollama:Endpoint is not bound to an Options class today; it is read directly from configuration at host build.
        var configuredOllama = configuration.GetValue<string>("Ollama:Endpoint");
        _ollamaEndpointSeed = string.IsNullOrWhiteSpace(configuredOllama)
            ? StoredNodeSettings.DefaultOllamaEndpoint
            : configuredOllama;
    }

    public async Task<string> GetDefaultModelNameAsync(CancellationToken cancellationToken = default) =>
        ResolveDefaultModelName(await LoadAsync(cancellationToken));

    public async Task<bool> GetEnableToolsAsync(CancellationToken cancellationToken = default)
    {
        var stored = await LoadAsync(cancellationToken);
        return stored.EnableTools ?? _enableToolsSeed;
    }

    public async Task<bool> GetCustomToolsEnabledAsync(CancellationToken cancellationToken = default)
    {
        var stored = await LoadAsync(cancellationToken);
        // No appsettings seed: a host-execution feature has no config-file default, so the fallback IS the hardcoded off.
        return stored.CustomToolsEnabled ?? StoredNodeSettings.DefaultCustomToolsEnabled;
    }

    public async Task<bool> GetWebAccessEnabledAsync(CancellationToken cancellationToken = default)
    {
        var stored = await LoadAsync(cancellationToken);
        return stored.WebAccessEnabled ?? StoredNodeSettings.DefaultWebAccessEnabled;
    }

    public async Task<string?> GetWebSearchSearxngUrlAsync(CancellationToken cancellationToken = default)
    {
        var stored = await LoadAsync(cancellationToken);
        return stored.WebSearchSearxngUrl;
    }

    public async Task<bool> GetToolRelevanceEnabledAsync(CancellationToken cancellationToken = default)
    {
        var stored = await LoadAsync(cancellationToken);
        // No appsettings seed: nothing sets Agent:ToolRelevance:Enabled, so the fallback IS the hardcoded off.
        return stored.ToolRelevanceEnabled ?? StoredNodeSettings.DefaultToolRelevanceEnabled;
    }

    public async Task<string?> GetExternalAccessProfileAsync(CancellationToken cancellationToken = default)
    {
        var stored = await LoadAsync(cancellationToken);
        // Verbatim, and deliberately NO fallback: null is the answer (undecided), not an absent value to be seeded.
        // Normalize has already nulled anything that is not one of the four literals.
        return stored.ExternalAccessProfile;
    }

    public async Task<bool> GetAutoCheckApplicationUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var stored = await LoadAsync(cancellationToken);
        // No appsettings seed: nothing configures this, so the fallback IS the hardcoded on — an upgraded node whose
        // file predates the member keeps checking exactly as it did before.
        return stored.AutoCheckApplicationUpdates ?? StoredNodeSettings.DefaultAutoCheckApplicationUpdates;
    }

    public async Task<bool> GetAutoCheckRuntimeUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var stored = await LoadAsync(cancellationToken);
        // No appsettings seed: nothing configures this, so the fallback IS the hardcoded on.
        return stored.AutoCheckRuntimeUpdates ?? StoredNodeSettings.DefaultAutoCheckRuntimeUpdates;
    }

    public async Task<bool> GetAutoProvisionFirstRunModelAsync(CancellationToken cancellationToken = default)
    {
        var stored = await LoadAsync(cancellationToken);
        // No appsettings seed of its own: FirstRunModel:Enabled is a SEPARATE, earlier gate, not this one's seed, so
        // the fallback IS the hardcoded on.
        return stored.AutoProvisionFirstRunModel ?? StoredNodeSettings.DefaultAutoProvisionFirstRunModel;
    }

    public async Task<IReadOnlyList<string>> GetToolCapableModelsAsync(CancellationToken cancellationToken = default) =>
        ResolveToolCapableModels(await LoadAsync(cancellationToken));

    public async Task<string> GetOllamaEndpointAsync(CancellationToken cancellationToken = default) =>
        ResolveOllamaEndpoint(await LoadAsync(cancellationToken));

    public async Task<string> GetHuggingFaceDefaultQuantAsync(CancellationToken cancellationToken = default) =>
        ResolveHuggingFaceDefaultQuant(await LoadAsync(cancellationToken));

    public async Task<long> GetHuggingFaceDiskMarginBytesAsync(CancellationToken cancellationToken = default) =>
        ResolveHuggingFaceDiskMarginBytes(await LoadAsync(cancellationToken));

    public async Task<int> GetLlamaMaxLoadedProcessesAsync(CancellationToken cancellationToken = default) =>
        ResolveLlamaMaxLoadedProcesses(await LoadAsync(cancellationToken));

    public async Task<TimeSpan> GetLlamaIdleTimeToLiveAsync(CancellationToken cancellationToken = default) =>
        ResolveLlamaIdleTimeToLive(await LoadAsync(cancellationToken));

    public async Task<bool> GetKeepModelWarmEnabledAsync(CancellationToken cancellationToken = default) =>
        ResolveKeepModelWarmEnabled(await LoadAsync(cancellationToken));

    public async Task<string?> GetKeepModelWarmModelNameAsync(CancellationToken cancellationToken = default) =>
        ResolveKeepModelWarmModelName(await LoadAsync(cancellationToken));

    public async Task<TimeSpan> GetKeepModelWarmIntervalAsync(CancellationToken cancellationToken = default) =>
        ResolveKeepModelWarmInterval(await LoadAsync(cancellationToken));

    public async Task<int> GetMaxResponseSizeMbAsync(CancellationToken cancellationToken = default) =>
        ResolveMaxResponseSizeMb(await LoadAsync(cancellationToken));

    public async Task<string> GetRecommendedLlamaCppTagAsync(CancellationToken cancellationToken = default)
    {
        var stored = await LoadAsync(cancellationToken);
        return StoredNodeSettings.IsValidRecommendedLlamaCppTag(stored.RecommendedLlamaCppTag)
            ? stored.RecommendedLlamaCppTag!
            : LlamaCppReleasePins.PinnedTag;
    }

    public async Task<int> GetOrchestrationIdleTimeoutSecondsAsync(CancellationToken cancellationToken = default) =>
        ResolveOrchestrationIdleTimeoutSeconds(await LoadAsync(cancellationToken));

    public async Task<int> GetAgentHomePrepareTimeoutSecondsAsync(CancellationToken cancellationToken = default)
    {
        var stored = await LoadAsync(cancellationToken);
        return stored.AgentHomePrepareTimeoutSeconds ?? _agentHomePrepareTimeoutSeed;
    }

    public async Task<int> GetAgentHomeCommandTimeoutSecondsAsync(CancellationToken cancellationToken = default)
    {
        var stored = await LoadAsync(cancellationToken);
        return stored.AgentHomeCommandTimeoutSeconds ?? _agentHomeCommandTimeoutSeed;
    }

    public async Task<long> GetAgentHomeMaxSelectedFolderBytesAsync(CancellationToken cancellationToken = default)
    {
        var stored = await LoadAsync(cancellationToken);
        return stored.AgentHomeMaxSelectedFolderBytes ?? _agentHomeMaxSelectedFolderBytesSeed;
    }

    public async Task<long> GetAgentHomeMaxPatchBytesAsync(CancellationToken cancellationToken = default)
    {
        var stored = await LoadAsync(cancellationToken);
        return stored.AgentHomeMaxPatchBytes ?? _agentHomeMaxPatchBytesSeed;
    }

    public async Task<int> GetMaxPendingToolCallAgeMinutesAsync(CancellationToken cancellationToken = default) =>
        ResolveMaxPendingToolCallAgeMinutes(await LoadAsync(cancellationToken));

    public async Task<int> GetDetachedGraceSecondsAsync(CancellationToken cancellationToken = default) =>
        ResolveDetachedGraceSeconds(await LoadAsync(cancellationToken));

    public async Task<int> GetChatCacheReuseAsync(CancellationToken cancellationToken = default) =>
        ResolveChatCacheReuse(await LoadAsync(cancellationToken));

    public async Task<string> GetSpeculativeModeAsync(CancellationToken cancellationToken = default) =>
        ResolveSpeculativeMode(await LoadAsync(cancellationToken));

    public async Task<string> GetKvCacheTypeAsync(CancellationToken cancellationToken = default) =>
        ResolveKvCacheType(await LoadAsync(cancellationToken));

    public async Task<string?> GetSpeculativeDraftModelNameAsync(CancellationToken cancellationToken = default) =>
        ResolveSpeculativeDraftModelName(await LoadAsync(cancellationToken));

    public async Task<int> GetSpeculativeDraftMaxTokensAsync(CancellationToken cancellationToken = default) =>
        ResolveSpeculativeDraftMaxTokens(await LoadAsync(cancellationToken));

    public async Task<int?> GetSpeculativeDraftGpuLayersAsync(CancellationToken cancellationToken = default) =>
        ResolveSpeculativeDraftGpuLayers(await LoadAsync(cancellationToken));

    public async Task<string?> GetRerankerModelNameAsync(CancellationToken cancellationToken = default) =>
        ResolveRerankerModelName(await LoadAsync(cancellationToken));

    public async Task<string?> GetAutoEffortFastModelNameAsync(CancellationToken cancellationToken = default) =>
        ResolveAutoEffortFastModelName(await LoadAsync(cancellationToken));

    public async Task<int> GetCustomToolMaxTimeoutSecondsAsync(CancellationToken cancellationToken = default)
    {
        var stored = await LoadAsync(cancellationToken);
        return stored.CustomToolMaxTimeoutSeconds ?? StoredNodeSettings.DefaultCustomToolMaxTimeoutSeconds;
    }

    public async Task<TimeSpan> GetWebFetchTimeoutAsync(CancellationToken cancellationToken = default)
    {
        var stored = await LoadAsync(cancellationToken);
        return TimeSpan.FromSeconds(stored.WebFetchTimeoutSeconds ?? StoredNodeSettings.DefaultWebFetchTimeoutSeconds);
    }

    public async Task<int> GetWebFetchMaxContentCharsAsync(CancellationToken cancellationToken = default)
    {
        var stored = await LoadAsync(cancellationToken);
        return stored.WebFetchMaxContentChars ?? StoredNodeSettings.DefaultWebFetchMaxContentChars;
    }

    public async Task<int> GetKnowledgeSearchDefaultResultsAsync(CancellationToken cancellationToken = default)
    {
        var stored = await LoadAsync(cancellationToken);
        return stored.KnowledgeSearchDefaultResults ?? StoredNodeSettings.DefaultKnowledgeSearchDefaultResults;
    }

    public async Task<int> GetKnowledgeSearchMaxResultsAsync(CancellationToken cancellationToken = default)
    {
        var stored = await LoadAsync(cancellationToken);
        return stored.KnowledgeSearchMaxResults ?? StoredNodeSettings.DefaultKnowledgeSearchMaxResults;
    }

    public async Task<int> GetAgentHomeMaxRunSecondsAsync(CancellationToken cancellationToken = default)
    {
        var stored = await LoadAsync(cancellationToken);
        return stored.AgentHomeMaxRunSeconds ?? _agentHomeMaxRunSecondsSeed;
    }

    public async Task<int> GetDefaultContextTokensAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).DefaultContextTokens ?? _defaultContextTokensSeed;

    public async Task<int> GetProviderBudgetRecentMessagesToKeepAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).ProviderBudgetRecentMessagesToKeep ?? _providerBudgetRecentMessagesToKeepSeed;

    public async Task<int> GetProviderBudgetMaxCumulativeInputTokensAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).ProviderBudgetMaxCumulativeInputTokens ?? _providerBudgetMaxCumulativeInputTokensSeed;

    public async Task<bool> GetCompactionAutoEnabledAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).CompactionAutoEnabled ?? _compactionAutoEnabledSeed;

    public async Task<int> GetCompactionAutoCompactPercentAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).CompactionAutoCompactPercent ?? _compactionAutoCompactPercentSeed;

    public async Task<int> GetCompactionRecentMessagesVerbatimAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).CompactionRecentMessagesVerbatim ?? _compactionRecentMessagesVerbatimSeed;

    public async Task<bool> GetCompactionDistillEnabledAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).CompactionDistillEnabled ?? _compactionDistillEnabledSeed;

    public async Task<int> GetMaxInlinedAttachmentCharsAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).MaxInlinedAttachmentChars ?? _maxInlinedAttachmentCharsSeed;

    public async Task<int> GetKnowledgeChatTopKAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).KnowledgeChatTopK ?? _knowledgeChatTopKSeed;

    public async Task<bool> GetProviderRetryEnabledAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).ProviderRetryEnabled ?? _providerRetryEnabledSeed;

    public async Task<int> GetProviderMaxRetriesAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).ProviderMaxRetries ?? _providerMaxRetriesSeed;

    public async Task<int> GetSpawnMaxConcurrentAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).SpawnMaxConcurrent ?? _spawnMaxConcurrentSeed;

    public async Task<int> GetSpawnMaxCloudAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).SpawnMaxCloud ?? _spawnMaxCloudSeed;

    public async Task<int> GetSpawnQueueWaitSecondsAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).SpawnQueueWaitSeconds ?? _spawnQueueWaitSecondsSeed;

    public async Task<bool> GetKnowledgeAdaptiveRerankingEnabledAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).KnowledgeAdaptiveRerankingEnabled ?? _knowledgeAdaptiveRerankingEnabledSeed;

    public async Task<int> GetKnowledgeRetrievalLatencyBudgetMsAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).KnowledgeRetrievalLatencyBudgetMs ?? _knowledgeRetrievalLatencyBudgetMsSeed;

    public async Task<bool> GetKnowledgeAgentToolsEnabledAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).KnowledgeAgentToolsEnabled ?? _knowledgeAgentToolsEnabledSeed;

    public async Task<bool> GetAllowCloudModelAccessAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).AllowCloudModelAccess ?? _allowCloudModelAccessSeed;

    public async Task<string> GetPlaybookAnalysisModelNameAsync(CancellationToken cancellationToken = default)
    {
        var stored = await LoadAsync(cancellationToken);
        return NonBlank(stored.PlaybookAnalysisModelName) ?? _playbookAnalysisModelNameSeed ?? await ResolveNodeLocalDefaultModelNameAsync(stored, cancellationToken);
    }

    public async Task<string> GetPlaybookEvalModelNameAsync(CancellationToken cancellationToken = default)
    {
        var stored = await LoadAsync(cancellationToken);
        return NonBlank(stored.PlaybookEvalModelName) ?? _playbookEvalModelNameSeed ?? await ResolveNodeLocalDefaultModelNameAsync(stored, cancellationToken);
    }

    public async Task<string> GetMemoryExtractionModelNameAsync(CancellationToken cancellationToken = default)
    {
        var stored = await LoadAsync(cancellationToken);
        return NonBlank(stored.MemoryExtractionModelName) ?? _memoryExtractionModelNameSeed ?? await ResolveNodeLocalDefaultModelNameAsync(stored, cancellationToken);
    }

    public NodeSettingsEffectiveValues ResolveEffectiveValues(StoredNodeSettings stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        return new NodeSettingsEffectiveValues
        {
            CompactionAutoEnabled = stored.CompactionAutoEnabled ?? _compactionAutoEnabledSeed,
            CompactionDistillEnabled = stored.CompactionDistillEnabled ?? _compactionDistillEnabledSeed,
            ProviderRetryEnabled = stored.ProviderRetryEnabled ?? _providerRetryEnabledSeed,
            KnowledgeAdaptiveRerankingEnabled = stored.KnowledgeAdaptiveRerankingEnabled ?? _knowledgeAdaptiveRerankingEnabledSeed,
            KnowledgeScheduledReindexEnabled = stored.KnowledgeScheduledReindexEnabled ?? _knowledgeScheduledReindexEnabledSeed,
            KnowledgeAgentToolsEnabled = stored.KnowledgeAgentToolsEnabled ?? _knowledgeAgentToolsEnabledSeed,
            AllowCloudModelAccess = stored.AllowCloudModelAccess ?? _allowCloudModelAccessSeed,
            ChatRetentionEnabled = stored.ChatRetentionEnabled ?? _chatRetentionEnabledSeed,
            AgentExecutionLogRetentionEnabled = stored.AgentExecutionLogRetentionEnabled ?? _agentExecutionLogRetentionEnabledSeed,
            ImageTextEncoderOnGpu = stored.ImageTextEncoderOnGpu ?? _imageTextEncoderOnGpuSeed,
            ChatRetentionDays = stored.ChatRetentionDays ?? _chatRetentionDaysSeed,
            AgentExecutionLogRetentionDays = stored.AgentExecutionLogRetentionDays ?? _agentExecutionLogRetentionDaysSeed,
            SchedulerHistoryRetentionDays = stored.SchedulerHistoryRetentionDays ?? _schedulerHistoryRetentionDaysSeed,
            AgentHomeRunRetentionDays = stored.AgentHomeRunRetentionDays ?? _agentHomeRunRetentionDaysSeed
        };
    }

    public async Task<bool> GetChatRetentionEnabledAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).ChatRetentionEnabled ?? _chatRetentionEnabledSeed;

    public async Task<int> GetChatRetentionDaysAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).ChatRetentionDays ?? _chatRetentionDaysSeed;

    public async Task<bool> GetAgentExecutionLogRetentionEnabledAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).AgentExecutionLogRetentionEnabled ?? _agentExecutionLogRetentionEnabledSeed;

    public async Task<int> GetAgentExecutionLogRetentionDaysAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).AgentExecutionLogRetentionDays ?? _agentExecutionLogRetentionDaysSeed;

    public async Task<int> GetNodeDbBackupRetainCountAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).NodeDbBackupRetainCount ?? _nodeDbBackupRetainCountSeed;

    public async Task<long> GetBenchmarkKldCacheMaxBytesAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).BenchmarkKldCacheMaxBytes ?? _benchmarkKldCacheMaxBytesSeed;

    public async Task<int> GetSchedulerHistoryRetentionDaysAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).SchedulerHistoryRetentionDays ?? _schedulerHistoryRetentionDaysSeed;

    public async Task<int> GetAgentHomeMaxInnerToolCallsAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).AgentHomeMaxInnerToolCalls ?? _agentHomeMaxInnerToolCallsSeed;

    public async Task<int> GetAgentHomePatchApplyTimeoutSecondsAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).AgentHomePatchApplyTimeoutSeconds ?? _agentHomePatchApplyTimeoutSecondsSeed;

    public async Task<int> GetAgentHomeRunRetentionDaysAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).AgentHomeRunRetentionDays ?? _agentHomeRunRetentionDaysSeed;

    public async Task<int> GetAgentHomeRunRetentionMaxRunsAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).AgentHomeRunRetentionMaxRuns ?? _agentHomeRunRetentionMaxRunsSeed;

    public async Task<long> GetAgentHomeRunRetentionMaxTotalBytesAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).AgentHomeRunRetentionMaxTotalBytes ?? _agentHomeRunRetentionMaxTotalBytesSeed;

    public string GetDefaultModelName() =>
        ResolveDefaultModelName(LoadStored());

    public IReadOnlyList<string> GetToolCapableModels() =>
        ResolveToolCapableModels(LoadStored());

    public string GetOllamaEndpoint() =>
        ResolveOllamaEndpoint(LoadStored());

    public string GetHuggingFaceDefaultQuant() =>
        ResolveHuggingFaceDefaultQuant(LoadStored());

    public long GetHuggingFaceDiskMarginBytes() =>
        ResolveHuggingFaceDiskMarginBytes(LoadStored());

    public int GetLlamaMaxLoadedProcesses() =>
        ResolveLlamaMaxLoadedProcesses(LoadStored());

    public TimeSpan GetLlamaIdleTimeToLive() =>
        ResolveLlamaIdleTimeToLive(LoadStored());

    public TimeSpan GetTranscriptionIdleTimeout() =>
        ResolveTranscriptionIdleTimeout(LoadStored());

    public int GetMaxResponseSizeMb() =>
        ResolveMaxResponseSizeMb(LoadStored());

    public int GetOrchestrationIdleTimeoutSeconds() =>
        ResolveOrchestrationIdleTimeoutSeconds(LoadStored());

    public int GetMaxPendingToolCallAgeMinutes() =>
        ResolveMaxPendingToolCallAgeMinutes(LoadStored());

    public int GetDetachedGraceSeconds() =>
        ResolveDetachedGraceSeconds(LoadStored());

    public int GetChatCacheReuse() =>
        ResolveChatCacheReuse(LoadStored());

    public string GetSpeculativeMode() =>
        ResolveSpeculativeMode(LoadStored());

    public string GetKvCacheType() =>
        ResolveKvCacheType(LoadStored());

    public string? GetSpeculativeDraftModelName() =>
        ResolveSpeculativeDraftModelName(LoadStored());

    public int GetSpeculativeDraftMaxTokens() =>
        ResolveSpeculativeDraftMaxTokens(LoadStored());

    public int? GetSpeculativeDraftGpuLayers() =>
        ResolveSpeculativeDraftGpuLayers(LoadStored());

    public string? GetRerankerModelName() =>
        ResolveRerankerModelName(LoadStored());

    public TimeSpan GetLlamaReadinessTimeoutCap() =>
        TimeSpan.FromSeconds(LoadStored().LlamaReadinessTimeoutCapSeconds ?? StoredNodeSettings.DefaultLlamaReadinessTimeoutCapSeconds);

    public TimeSpan GetLlamaChatHttpTimeout() =>
        TimeSpan.FromSeconds(LoadStored().LlamaChatHttpTimeoutSeconds ?? StoredNodeSettings.DefaultLlamaChatHttpTimeoutSeconds);

    public TimeSpan GetLlamaEmbeddingHttpTimeout() =>
        TimeSpan.FromSeconds(LoadStored().LlamaEmbeddingHttpTimeoutSeconds ?? StoredNodeSettings.DefaultLlamaEmbeddingHttpTimeoutSeconds);

    public int? GetLlamaChatCacheRamMiB() =>
        LoadStored().LlamaChatCacheRamMiB;

    public int GetLlamaCpuThreadReserve() =>
        LoadStored().LlamaCpuThreadReserve ?? StoredNodeSettings.DefaultLlamaCpuThreadReserve;

    // Percent / 100.0 is correctly rounded, so the defaults reproduce the 0.05 / 0.15 literals exactly and the launch-policy
    // fingerprint that serializes them stays byte-identical on a node that never set either knob.
    public double GetLlamaGpuReserveFraction() =>
        (LoadStored().LlamaGpuReservePercent ?? StoredNodeSettings.DefaultLlamaGpuReservePercent) / 100d;

    public double GetLlamaRamReserveFraction() =>
        (LoadStored().LlamaRamReservePercent ?? StoredNodeSettings.DefaultLlamaRamReservePercent) / 100d;

    public TimeSpan GetImageIdleTimeToLive() =>
        LoadStored().ImageIdleTimeToLiveSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : _imageIdleTimeToLiveSeed;

    public int GetMaxProviderCallsPerInvocation() =>
        LoadStored().MaxProviderCallsPerInvocation ?? _maxProviderCallsPerInvocationSeed;

    public int GetHuggingFaceDownloadConnections() =>
        LoadStored().HuggingFaceDownloadConnections ?? _hfDownloadConnectionsSeed;

    public TimeSpan GetTranscriptionInferenceTimeout() =>
        TimeSpan.FromMinutes(LoadStored().TranscriptionInferenceTimeoutMinutes ?? StoredNodeSettings.DefaultTranscriptionInferenceTimeoutMinutes);

    public double GetModelFitSafetyMarginFraction() =>
        (LoadStored().ModelFitSafetyMarginPercent ?? StoredNodeSettings.DefaultModelFitSafetyMarginPercent) / 100d;

    public int GetToolPipelineMaxIterationsPerRequest() =>
        LoadStored().ToolPipelineMaxIterationsPerRequest ?? _toolPipelineMaxIterationsSeed;

    public int GetToolPipelineMaxToolResultChars() =>
        LoadStored().ToolPipelineMaxToolResultChars ?? _toolPipelineMaxToolResultCharsSeed;

    public int GetToolPipelineMaxConsecutiveInvalidToolCalls() =>
        LoadStored().ToolPipelineMaxConsecutiveInvalidToolCalls ?? _toolPipelineMaxConsecutiveInvalidToolCallsSeed;

    public int GetContextBudgetRecentTurnKeepCount() =>
        LoadStored().ContextBudgetRecentTurnKeepCount ?? _contextBudgetRecentTurnKeepCountSeed;

    public bool GetKnowledgeScheduledReindexEnabled() =>
        LoadStored().KnowledgeScheduledReindexEnabled ?? _knowledgeScheduledReindexEnabledSeed;

    public int GetKnowledgeScheduledReindexIntervalMinutes() =>
        LoadStored().KnowledgeScheduledReindexIntervalMinutes ?? _knowledgeScheduledReindexIntervalMinutesSeed;

    public bool GetKnowledgeAgentToolsEnabled() =>
        LoadStored().KnowledgeAgentToolsEnabled ?? _knowledgeAgentToolsEnabledSeed;

    public bool GetAllowCloudModelAccess() =>
        LoadStored().AllowCloudModelAccess ?? _allowCloudModelAccessSeed;

    public int GetImageMaxLoadedProcesses() =>
        LoadStored().ImageMaxLoadedProcesses ?? _imageMaxLoadedProcessesSeed;

    public bool GetImageTextEncoderOnGpu() =>
        LoadStored().ImageTextEncoderOnGpu ?? _imageTextEncoderOnGpuSeed;

    public int GetGraphWorkflowMaxConcurrentRuns() =>
        LoadStored().GraphWorkflowMaxConcurrentRuns ?? _graphWorkflowMaxConcurrentRunsSeed;

    public int GetGraphWorkflowDefaultNodeTimeoutSeconds() =>
        LoadStored().GraphWorkflowDefaultNodeTimeoutSeconds ?? _graphWorkflowDefaultNodeTimeoutSecondsSeed;

    public int GetWorkSessionMaxStepsPerRun() =>
        LoadStored().WorkSessionMaxStepsPerRun ?? _workSessionMaxStepsPerRunSeed;

    public int GetWorkSessionMaxConcurrentSessions() =>
        LoadStored().WorkSessionMaxConcurrentSessions ?? _workSessionMaxConcurrentSessionsSeed;

    public int GetDevelopmentMaxAttemptDurationSeconds() =>
        LoadStored().DevelopmentMaxAttemptDurationSeconds ?? _developmentMaxAttemptDurationSecondsSeed;

    public int GetDevelopmentMaxToolCalls() =>
        LoadStored().DevelopmentMaxToolCalls ?? _developmentMaxToolCallsSeed;

    public int GetDevelopmentMaxOutputTokens() =>
        LoadStored().DevelopmentMaxOutputTokens ?? _developmentMaxOutputTokensSeed;

    private static int IntSeed(IConfiguration configuration, string key, int floor, int fallback) =>
        configuration.GetValue<int?>(key) is { } configured && configured >= floor ? configured : fallback;

    private static bool BoolSeed(IConfiguration configuration, string key, bool fallback) =>
        configuration.GetValue<bool?>(key) ?? fallback;

    private static string? NonBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private string ResolveDefaultModelName(StoredNodeSettings stored) =>
        string.IsNullOrWhiteSpace(stored.DefaultModelName) ? _defaultModelSeed : stored.DefaultModelName;

    // A background picker inherits the stored default chat model only when it is node-local: the default may be a cloud or `ext:` id
    // (the chat policy admits those), and a background step must never send conversation content off-node. Else the appsettings seed.
    private async Task<string> ResolveNodeLocalDefaultModelNameAsync(StoredNodeSettings stored, CancellationToken cancellationToken)
    {
        var inherited = ResolveDefaultModelName(stored);
        return await BackgroundModelLocalityGuard.IsNodeLocalAsync(inherited, _modelTrustResolver.Value, cancellationToken) ? inherited : _defaultModelSeed;
    }

    private IReadOnlyList<string> ResolveToolCapableModels(StoredNodeSettings stored) =>
        stored.ToolCapableModels is { Count: > 0 } models ? models : _toolCapableModelsSeed;

    private string ResolveOllamaEndpoint(StoredNodeSettings stored) =>
        string.IsNullOrWhiteSpace(stored.OllamaEndpoint) ? _ollamaEndpointSeed : stored.OllamaEndpoint;

    private string ResolveHuggingFaceDefaultQuant(StoredNodeSettings stored) =>
        string.IsNullOrWhiteSpace(stored.HuggingFaceDefaultQuant) ? _hfQuantSeed : stored.HuggingFaceDefaultQuant;

    private long ResolveHuggingFaceDiskMarginBytes(StoredNodeSettings stored) =>
        stored.HuggingFaceDiskMarginBytes ?? _hfDiskMarginSeed;

    private static int ResolveLlamaMaxLoadedProcesses(StoredNodeSettings stored) =>
        stored.LlamaMaxLoadedProcesses ?? StoredNodeSettings.DefaultLlamaMaxLoadedProcesses;

    private static TimeSpan ResolveLlamaIdleTimeToLive(StoredNodeSettings stored) =>
        TimeSpan.FromSeconds(stored.LlamaIdleTimeToLiveSeconds ?? StoredNodeSettings.DefaultLlamaIdleTimeToLiveSeconds);

    private TimeSpan ResolveTranscriptionIdleTimeout(StoredNodeSettings stored) =>
        TimeSpan.FromMinutes(stored.TranscriptionIdleTimeoutMinutes ?? _transcriptionIdleTimeoutMinutesSeed);

    private static bool ResolveKeepModelWarmEnabled(StoredNodeSettings stored) =>
        stored.KeepModelWarmEnabled ?? StoredNodeSettings.DefaultKeepModelWarmEnabled;

    private static string? ResolveKeepModelWarmModelName(StoredNodeSettings stored) =>
        string.IsNullOrWhiteSpace(stored.KeepModelWarmModelName) ? null : stored.KeepModelWarmModelName;

    private static TimeSpan ResolveKeepModelWarmInterval(StoredNodeSettings stored) =>
        TimeSpan.FromSeconds(stored.KeepModelWarmIntervalSeconds ?? StoredNodeSettings.DefaultKeepModelWarmIntervalSeconds);

    private int ResolveMaxResponseSizeMb(StoredNodeSettings stored) =>
        stored.MaxResponseSizeMb ?? _maxResponseSizeMbSeed;

    private int ResolveOrchestrationIdleTimeoutSeconds(StoredNodeSettings stored) =>
        stored.OrchestrationIdleTimeoutSeconds ?? _orchestrationIdleTimeoutSeed;

    private int ResolveMaxPendingToolCallAgeMinutes(StoredNodeSettings stored) =>
        stored.MaxPendingToolCallAgeMinutes ?? _maxPendingToolCallAgeMinutesSeed;

    private int ResolveDetachedGraceSeconds(StoredNodeSettings stored) =>
        stored.DetachedGraceSeconds ?? _detachedGraceSecondsSeed;

    // The speculative-decoding + cache-reuse knobs have no appsettings section today: the seed IS the hardcoded default
    // (mirrors the llama.cpp supervisor cap/TTL), so these resolvers coalesce the stored value against the Default* const.
    private static int ResolveChatCacheReuse(StoredNodeSettings stored) =>
        stored.ChatCacheReuse ?? StoredNodeSettings.DefaultChatCacheReuse;

    private static string ResolveSpeculativeMode(StoredNodeSettings stored) =>
        StoredNodeSettings.IsValidSpeculativeMode(stored.SpeculativeMode) && !string.IsNullOrWhiteSpace(stored.SpeculativeMode)
            ? stored.SpeculativeMode
            : StoredNodeSettings.DefaultSpeculativeMode;

    private static string ResolveKvCacheType(StoredNodeSettings stored) =>
        StoredNodeSettings.IsValidKvCacheType(stored.KvCacheType) && !string.IsNullOrWhiteSpace(stored.KvCacheType)
            ? stored.KvCacheType
            : StoredNodeSettings.DefaultKvCacheType;

    private static string? ResolveSpeculativeDraftModelName(StoredNodeSettings stored) =>
        string.IsNullOrWhiteSpace(stored.SpeculativeDraftModelName) ? null : stored.SpeculativeDraftModelName;

    private static int ResolveSpeculativeDraftMaxTokens(StoredNodeSettings stored) =>
        stored.SpeculativeDraftMaxTokens ?? StoredNodeSettings.DefaultSpeculativeDraftMaxTokens;

    private static int? ResolveSpeculativeDraftGpuLayers(StoredNodeSettings stored) =>
        stored.SpeculativeDraftGpuLayers;

    // Reranking has no appsettings section: the stored name is the only source, blank → null (off).
    private static string? ResolveRerankerModelName(StoredNodeSettings stored) =>
        string.IsNullOrWhiteSpace(stored.RerankerModelName) ? null : stored.RerankerModelName;

    private static string? ResolveAutoEffortFastModelName(StoredNodeSettings stored) =>
        string.IsNullOrWhiteSpace(stored.AutoEffortFastModelName) ? null : stored.AutoEffortFastModelName;

    private async Task<StoredNodeSettings> LoadAsync(CancellationToken cancellationToken)
    {
        // The production stores never return null; coalesce defensively so a substitute that leaves a load unconfigured
        // (returns null) degrades to the seed/default precedence instead of throwing on a null stored object.
        return await _store.LoadAsync(cancellationToken) ?? new StoredNodeSettings();
    }

    // The synchronous property readers above cannot await, so they go through NodeSettingsStore.Load — the XML-doc-declared synchronous twin
    // of LoadAsync kept for exactly this composition/startup path. The token is None because a property getter has none to forward.
    private StoredNodeSettings LoadStored()
    {
#pragma warning disable MA0045 // Documented synchronous twin (NodeSettingsStore.Load beside LoadAsync); the callers are synchronous property getters.
        return _store.Load(CancellationToken.None) ?? new StoredNodeSettings();
#pragma warning restore MA0045
    }
}
