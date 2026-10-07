namespace XE_Local_AI_Engine.Tests.Testing.Builders;

using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Builds a configured <see cref="INodeRuntimeSettings" /> substitute for tests of consumers that were repointed off
///     <c>IOptions&lt;T&gt;</c> onto the accessor (the appsettings-to-node-settings migration). Defaults mirror
///     the <c>StoredNodeSettings</c> seed defaults; each <c>With*</c> override sets a single migrated value so a test can
///     pin only the knob it asserts on.
/// </summary>
public sealed class StubNodeRuntimeSettings
{
    private int _agentHomeCommandTimeoutSeconds = StoredNodeSettings.DefaultAgentHomeCommandTimeoutSeconds;
    private long _agentHomeMaxPatchBytes = StoredNodeSettings.DefaultAgentHomeMaxPatchBytes;
    private long _agentHomeMaxSelectedFolderBytes = StoredNodeSettings.DefaultAgentHomeMaxSelectedFolderBytes;
    private int _agentHomePrepareTimeoutSeconds = StoredNodeSettings.DefaultAgentHomePrepareTimeoutSeconds;
    private string _defaultModelName = "qwen3.5:0.8b";
    private bool _enableTools = StoredNodeSettings.DefaultEnableTools;
    private long _huggingFaceDiskMarginBytes = StoredNodeSettings.DefaultHuggingFaceDiskMarginBytes;
    private string _huggingFaceDefaultQuant = StoredNodeSettings.DefaultHuggingFaceQuant;
    private TimeSpan _keepModelWarmInterval = TimeSpan.FromSeconds(StoredNodeSettings.DefaultKeepModelWarmIntervalSeconds);
    private bool _keepModelWarmEnabled = StoredNodeSettings.DefaultKeepModelWarmEnabled;
    private string? _keepModelWarmModelName;
    private TimeSpan _llamaIdleTimeToLive = TimeSpan.FromSeconds(StoredNodeSettings.DefaultLlamaIdleTimeToLiveSeconds);
    private int _llamaMaxLoadedProcesses = StoredNodeSettings.DefaultLlamaMaxLoadedProcesses;
    private int _maxPendingToolCallAgeMinutes = StoredNodeSettings.DefaultMaxPendingToolCallAgeMinutes;
    private int _detachedGraceSeconds = StoredNodeSettings.DefaultDetachedGraceSeconds;
    private int _maxResponseSizeMb = StoredNodeSettings.DefaultMaxResponseSizeMb;
    private int _orchestrationIdleTimeoutSeconds = StoredNodeSettings.DefaultOrchestrationIdleTimeoutSeconds;
    private IReadOnlyList<string> _toolCapableModels = ["qwen3:8b"];
    private IReadOnlyList<string> _toolCapableModelsSeed = ["qwen3:8b"];
    private int _chatCacheReuse = StoredNodeSettings.DefaultChatCacheReuse;
    private string _kvCacheType = StoredNodeSettings.DefaultKvCacheType;
    private string _speculativeMode = StoredNodeSettings.DefaultSpeculativeMode;
    private string? _speculativeDraftModelName;
    private int _speculativeDraftMaxTokens = StoredNodeSettings.DefaultSpeculativeDraftMaxTokens;
    private int? _speculativeDraftGpuLayers;
    private string? _rerankerModelName;
    private bool _customToolsEnabled = StoredNodeSettings.DefaultCustomToolsEnabled;
    private bool _webAccessEnabled = StoredNodeSettings.DefaultWebAccessEnabled;
    private string? _webSearchSearxngUrl;
    private Func<CancellationToken, Task<bool>> _toolRelevanceRead = static _ => Task.FromResult(StoredNodeSettings.DefaultToolRelevanceEnabled);
    private bool _autoCheckApplicationUpdates = StoredNodeSettings.DefaultAutoCheckApplicationUpdates;
    private bool _autoCheckRuntimeUpdates = StoredNodeSettings.DefaultAutoCheckRuntimeUpdates;
    private bool _autoProvisionFirstRunModel = StoredNodeSettings.DefaultAutoProvisionFirstRunModel;
    private int _agentHomeMaxRunSeconds = 600;
    private int _webFetchMaxContentChars = StoredNodeSettings.DefaultWebFetchMaxContentChars;
    private ReasoningBudgets _reasoningBudgets = ReasoningBudgets.Default;

    private ChatOutputCap _chatOutputCap = new()
    {
        Mode = StoredNodeSettings.ChatOutputCapModeCap,
        MaxTokens = StoredNodeSettings.DefaultChatOutputCapMaxTokens
    };

    private int _toolPipelineMaxIterationsPerRequest = StoredNodeSettings.DefaultToolPipelineMaxIterationsPerRequest;
    private int _toolPipelineMaxToolResultChars = StoredNodeSettings.DefaultToolPipelineMaxToolResultChars;
    private int _toolPipelineMaxConsecutiveInvalidToolCalls = StoredNodeSettings.DefaultToolPipelineMaxConsecutiveInvalidToolCalls;
    private int _defaultContextTokens = StoredNodeSettings.DefaultDefaultContextTokens;
    private int _providerBudgetRecentMessagesToKeep = StoredNodeSettings.DefaultProviderBudgetRecentMessagesToKeep;
    private int _providerBudgetMaxCumulativeInputTokens = StoredNodeSettings.DefaultProviderBudgetMaxCumulativeInputTokens;
    private int _contextBudgetRecentTurnKeepCount = StoredNodeSettings.DefaultContextBudgetRecentTurnKeepCount;
    private bool _compactionAutoEnabled = StoredNodeSettings.DefaultCompactionAutoEnabled;
    private int _compactionAutoCompactPercent = StoredNodeSettings.DefaultCompactionAutoCompactPercent;
    private int _compactionRecentMessagesVerbatim = StoredNodeSettings.DefaultCompactionRecentMessagesVerbatim;
    private bool _compactionDistillEnabled = StoredNodeSettings.DefaultCompactionDistillEnabled;
    private int _maxInlinedAttachmentChars = StoredNodeSettings.DefaultMaxInlinedAttachmentChars;
    private int _knowledgeChatTopK = StoredNodeSettings.DefaultKnowledgeChatTopK;
    private bool _providerRetryEnabled = StoredNodeSettings.DefaultProviderRetryEnabled;
    private int _providerMaxRetries = StoredNodeSettings.DefaultProviderMaxRetries;
    private int _spawnMaxConcurrent = StoredNodeSettings.DefaultSpawnMaxConcurrent;
    private int _spawnMaxCloud = StoredNodeSettings.DefaultSpawnMaxCloud;
    private int _spawnQueueWaitSeconds = StoredNodeSettings.DefaultSpawnQueueWaitSeconds;
    private bool _knowledgeAdaptiveRerankingEnabled = StoredNodeSettings.DefaultKnowledgeAdaptiveRerankingEnabled;
    private int _knowledgeRetrievalLatencyBudgetMs = StoredNodeSettings.DefaultKnowledgeRetrievalLatencyBudgetMs;
    private bool _knowledgeScheduledReindexEnabled = StoredNodeSettings.DefaultKnowledgeScheduledReindexEnabled;
    private int _knowledgeScheduledReindexIntervalMinutes = StoredNodeSettings.DefaultKnowledgeScheduledReindexIntervalMinutes;
    private bool _knowledgeAgentToolsEnabled = StoredNodeSettings.DefaultKnowledgeAgentToolsEnabled;
    private bool _allowCloudModelAccess = StoredNodeSettings.DefaultAllowCloudModelAccess;
    private bool _allowCloudModelUnattendedRuns = StoredNodeSettings.DefaultAllowCloudModelUnattendedRuns;
    private bool _allowCloudModelWebTools = StoredNodeSettings.DefaultAllowCloudModelWebTools;
    private bool _allowCloudModelMcpTools = StoredNodeSettings.DefaultAllowCloudModelMcpTools;
    private bool _allowCloudModelSubAgents = StoredNodeSettings.DefaultAllowCloudModelSubAgents;
    private string? _playbookAnalysisModelName;
    private string? _playbookEvalModelName;
    private string? _memoryExtractionModelName;
    private bool _chatRetentionEnabled = StoredNodeSettings.DefaultChatRetentionEnabled;
    private int _chatRetentionDays = StoredNodeSettings.DefaultChatRetentionDays;
    private bool _agentExecutionLogRetentionEnabled = StoredNodeSettings.DefaultAgentExecutionLogRetentionEnabled;
    private int _agentExecutionLogRetentionDays = StoredNodeSettings.DefaultAgentExecutionLogRetentionDays;
    private int _nodeDbBackupRetainCount = StoredNodeSettings.DefaultNodeDbBackupRetainCount;
    private long _benchmarkKldCacheMaxBytes = StoredNodeSettings.DefaultBenchmarkKldCacheMaxBytes;
    private int _schedulerHistoryRetentionDays = StoredNodeSettings.DefaultSchedulerHistoryRetentionDays;
    private int _imageMaxLoadedProcesses = StoredNodeSettings.DefaultImageMaxLoadedProcesses;
    private bool _imageTextEncoderOnGpu = StoredNodeSettings.DefaultImageTextEncoderOnGpu;
    private int _graphWorkflowMaxConcurrentRuns = StoredNodeSettings.DefaultGraphWorkflowMaxConcurrentRuns;
    private int _graphWorkflowDefaultNodeTimeoutSeconds = StoredNodeSettings.DefaultGraphWorkflowDefaultNodeTimeoutSeconds;
    private int _workSessionMaxStepsPerRun = StoredNodeSettings.DefaultWorkSessionMaxStepsPerRun;
    private int _workSessionMaxConcurrentSessions = StoredNodeSettings.DefaultWorkSessionMaxConcurrentSessions;
    private int _developmentMaxAttemptDurationSeconds = StoredNodeSettings.DefaultDevelopmentMaxAttemptDurationSeconds;
    private int _developmentMaxToolCalls = StoredNodeSettings.DefaultDevelopmentMaxToolCalls;
    private int _developmentMaxOutputTokens = StoredNodeSettings.DefaultDevelopmentMaxOutputTokens;
    private int _agentHomeMaxInnerToolCalls = StoredNodeSettings.DefaultAgentHomeMaxInnerToolCalls;
    private int _agentHomePatchApplyTimeoutSeconds = StoredNodeSettings.DefaultAgentHomePatchApplyTimeoutSeconds;
    private int _agentHomeRunRetentionDays = StoredNodeSettings.DefaultAgentHomeRunRetentionDays;
    private int _agentHomeRunRetentionMaxRuns = StoredNodeSettings.DefaultAgentHomeRunRetentionMaxRuns;
    private long _agentHomeRunRetentionMaxTotalBytes = StoredNodeSettings.DefaultAgentHomeRunRetentionMaxTotalBytes;

    // The feature switches default ON here, unlike their code defaults: a consumer test exercises the feature unless it says otherwise.
    private bool _developmentEnabled = true;
    private bool _workSessionsEnabled = true;
    private bool _graphWorkflowsEnabled = true;
    private bool _transcriptionEnabled = true;
    private bool _externalAppsEnabled = true;
    private bool _computeEnabled = true;
    private bool _agentHomeEnabled = true;
    private bool _schedulerEnabled = true;
    private bool _devWorkflowsEnabled = true;
    private bool _executionPreviewsEnabled;

    // A READ, not a value: the wait-until-decided gate re-reads the profile on every poll tick, so a test that flips the
    // decision mid-wait needs the substitute to answer differently on the second call. Same shape as the tool-relevance
    // read above, and for the same reason.
    private Func<CancellationToken, Task<string?>> _externalAccessProfileRead =
        static _ => Task.FromResult<string?>(StoredNodeSettings.ExternalAccessProfileRecommended);

    public static StubNodeRuntimeSettings Create()
    {
        return new StubNodeRuntimeSettings();
    }

    public StubNodeRuntimeSettings WithKvCacheType(string kvCacheType)
    {
        ArgumentNullException.ThrowIfNull(kvCacheType);
        _kvCacheType = kvCacheType;
        return this;
    }

    public StubNodeRuntimeSettings WithDefaultModelName(string defaultModelName)
    {
        ArgumentNullException.ThrowIfNull(defaultModelName);
        _defaultModelName = defaultModelName;
        return this;
    }

    public StubNodeRuntimeSettings WithEnableTools(bool enableTools)
    {
        _enableTools = enableTools;
        return this;
    }

    public StubNodeRuntimeSettings WithCustomToolsEnabled(bool customToolsEnabled)
    {
        _customToolsEnabled = customToolsEnabled;
        return this;
    }

    public StubNodeRuntimeSettings WithWebAccessEnabled(bool webAccessEnabled)
    {
        _webAccessEnabled = webAccessEnabled;
        return this;
    }

    public StubNodeRuntimeSettings WithWebSearchSearxngUrl(string? webSearchSearxngUrl)
    {
        _webSearchSearxngUrl = webSearchSearxngUrl;
        return this;
    }

    /// <summary>
    ///     A DELEGATE, not a bool: the tool-relevance read is live per turn, so a test has to be able to express a value
    ///     that changes between two turns of the SAME consumer, and a read that is still pending when the turn is cancelled.
    /// </summary>
    public StubNodeRuntimeSettings WithToolRelevanceEnabled(Func<CancellationToken, Task<bool>> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        _toolRelevanceRead = read;
        return this;
    }

    public StubNodeRuntimeSettings WithToolCapableModels(params string[] toolCapableModels)
    {
        ArgumentNullException.ThrowIfNull(toolCapableModels);
        _toolCapableModels = [.. toolCapableModels];
        return this;
    }

    public StubNodeRuntimeSettings WithToolCapableModelsSeed(params string[] toolCapableModelsSeed)
    {
        ArgumentNullException.ThrowIfNull(toolCapableModelsSeed);
        _toolCapableModelsSeed = [.. toolCapableModelsSeed];
        return this;
    }

    public StubNodeRuntimeSettings WithHuggingFaceDefaultQuant(string huggingFaceDefaultQuant)
    {
        ArgumentNullException.ThrowIfNull(huggingFaceDefaultQuant);
        _huggingFaceDefaultQuant = huggingFaceDefaultQuant;
        return this;
    }

    public StubNodeRuntimeSettings WithHuggingFaceDiskMarginBytes(long huggingFaceDiskMarginBytes)
    {
        _huggingFaceDiskMarginBytes = huggingFaceDiskMarginBytes;
        return this;
    }

    public StubNodeRuntimeSettings WithLlamaMaxLoadedProcesses(int llamaMaxLoadedProcesses)
    {
        _llamaMaxLoadedProcesses = llamaMaxLoadedProcesses;
        return this;
    }

    public StubNodeRuntimeSettings WithLlamaIdleTimeToLive(TimeSpan llamaIdleTimeToLive)
    {
        _llamaIdleTimeToLive = llamaIdleTimeToLive;
        return this;
    }

    public StubNodeRuntimeSettings WithKeepModelWarm(bool enabled,
        string? modelName = null,
        TimeSpan? interval = null)
    {
        _keepModelWarmEnabled = enabled;
        _keepModelWarmModelName = modelName;
        _keepModelWarmInterval = interval ?? _keepModelWarmInterval;
        return this;
    }

    public StubNodeRuntimeSettings WithMaxResponseSizeMb(int maxResponseSizeMb)
    {
        _maxResponseSizeMb = maxResponseSizeMb;
        return this;
    }

    public StubNodeRuntimeSettings WithMaxPendingToolCallAgeMinutes(int maxPendingToolCallAgeMinutes)
    {
        _maxPendingToolCallAgeMinutes = maxPendingToolCallAgeMinutes;
        return this;
    }

    public StubNodeRuntimeSettings WithDetachedGraceSeconds(int detachedGraceSeconds)
    {
        _detachedGraceSeconds = detachedGraceSeconds;
        return this;
    }

    public StubNodeRuntimeSettings WithOrchestrationIdleTimeoutSeconds(int orchestrationIdleTimeoutSeconds)
    {
        _orchestrationIdleTimeoutSeconds = orchestrationIdleTimeoutSeconds;
        return this;
    }

    public StubNodeRuntimeSettings WithAgentHomePrepareTimeoutSeconds(int agentHomePrepareTimeoutSeconds)
    {
        _agentHomePrepareTimeoutSeconds = agentHomePrepareTimeoutSeconds;
        return this;
    }

    public StubNodeRuntimeSettings WithAgentHomeCommandTimeoutSeconds(int agentHomeCommandTimeoutSeconds)
    {
        _agentHomeCommandTimeoutSeconds = agentHomeCommandTimeoutSeconds;
        return this;
    }

    public StubNodeRuntimeSettings WithAgentHomeMaxSelectedFolderBytes(long agentHomeMaxSelectedFolderBytes)
    {
        _agentHomeMaxSelectedFolderBytes = agentHomeMaxSelectedFolderBytes;
        return this;
    }

    public StubNodeRuntimeSettings WithAgentHomeMaxPatchBytes(long agentHomeMaxPatchBytes)
    {
        _agentHomeMaxPatchBytes = agentHomeMaxPatchBytes;
        return this;
    }

    public StubNodeRuntimeSettings WithChatCacheReuse(int chatCacheReuse)
    {
        _chatCacheReuse = chatCacheReuse;
        return this;
    }

    public StubNodeRuntimeSettings WithSpeculativeMode(string speculativeMode)
    {
        ArgumentNullException.ThrowIfNull(speculativeMode);
        _speculativeMode = speculativeMode;
        return this;
    }

    public StubNodeRuntimeSettings WithSpeculativeDraftModelName(string? speculativeDraftModelName)
    {
        _speculativeDraftModelName = speculativeDraftModelName;
        return this;
    }

    public StubNodeRuntimeSettings WithSpeculativeDraftMaxTokens(int speculativeDraftMaxTokens)
    {
        _speculativeDraftMaxTokens = speculativeDraftMaxTokens;
        return this;
    }

    public StubNodeRuntimeSettings WithSpeculativeDraftGpuLayers(int? speculativeDraftGpuLayers)
    {
        _speculativeDraftGpuLayers = speculativeDraftGpuLayers;
        return this;
    }

    public StubNodeRuntimeSettings WithRerankerModelName(string? rerankerModelName)
    {
        _rerankerModelName = rerankerModelName;
        return this;
    }

    /// <summary>Pins a single profile value — the common case for a test that is not exercising the wait.</summary>
    public StubNodeRuntimeSettings WithExternalAccessProfile(string? externalAccessProfile)
    {
        _externalAccessProfileRead = _ => Task.FromResult(externalAccessProfile);
        return this;
    }

    /// <summary>Answers the profile read from <paramref name="read" />, so a test can decide it part-way through a wait.</summary>
    public StubNodeRuntimeSettings WithExternalAccessProfileRead(Func<CancellationToken, Task<string?>> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        _externalAccessProfileRead = read;
        return this;
    }

    public StubNodeRuntimeSettings WithAutoCheckApplicationUpdates(bool autoCheckApplicationUpdates)
    {
        _autoCheckApplicationUpdates = autoCheckApplicationUpdates;
        return this;
    }

    public StubNodeRuntimeSettings WithAutoCheckRuntimeUpdates(bool autoCheckRuntimeUpdates)
    {
        _autoCheckRuntimeUpdates = autoCheckRuntimeUpdates;
        return this;
    }

    public StubNodeRuntimeSettings WithAutoProvisionFirstRunModel(bool autoProvisionFirstRunModel)
    {
        _autoProvisionFirstRunModel = autoProvisionFirstRunModel;
        return this;
    }

    public StubNodeRuntimeSettings WithAgentHomeMaxRunSeconds(int agentHomeMaxRunSeconds)
    {
        _agentHomeMaxRunSeconds = agentHomeMaxRunSeconds;
        return this;
    }

    public StubNodeRuntimeSettings WithReasoningBudgets(ReasoningBudgets reasoningBudgets)
    {
        _reasoningBudgets = reasoningBudgets;
        return this;
    }

    public StubNodeRuntimeSettings WithChatOutputCap(ChatOutputCap chatOutputCap)
    {
        _chatOutputCap = chatOutputCap;
        return this;
    }

    public StubNodeRuntimeSettings WithWebFetchMaxContentChars(int webFetchMaxContentChars)
    {
        _webFetchMaxContentChars = webFetchMaxContentChars;
        return this;
    }

    public StubNodeRuntimeSettings WithToolPipelineMaxIterationsPerRequest(int value)
    {
        _toolPipelineMaxIterationsPerRequest = value;
        return this;
    }

    public StubNodeRuntimeSettings WithToolPipelineMaxToolResultChars(int value)
    {
        _toolPipelineMaxToolResultChars = value;
        return this;
    }

    public StubNodeRuntimeSettings WithToolPipelineMaxConsecutiveInvalidToolCalls(int value)
    {
        _toolPipelineMaxConsecutiveInvalidToolCalls = value;
        return this;
    }

    public StubNodeRuntimeSettings WithDefaultContextTokens(int value)
    {
        _defaultContextTokens = value;
        return this;
    }

    public StubNodeRuntimeSettings WithProviderBudgetRecentMessagesToKeep(int value)
    {
        _providerBudgetRecentMessagesToKeep = value;
        return this;
    }

    public StubNodeRuntimeSettings WithProviderBudgetMaxCumulativeInputTokens(int value)
    {
        _providerBudgetMaxCumulativeInputTokens = value;
        return this;
    }

    public StubNodeRuntimeSettings WithContextBudgetRecentTurnKeepCount(int value)
    {
        _contextBudgetRecentTurnKeepCount = value;
        return this;
    }

    public StubNodeRuntimeSettings WithCompactionAutoEnabled(bool value)
    {
        _compactionAutoEnabled = value;
        return this;
    }

    public StubNodeRuntimeSettings WithCompactionAutoCompactPercent(int value)
    {
        _compactionAutoCompactPercent = value;
        return this;
    }

    public StubNodeRuntimeSettings WithCompactionRecentMessagesVerbatim(int value)
    {
        _compactionRecentMessagesVerbatim = value;
        return this;
    }

    public StubNodeRuntimeSettings WithCompactionDistillEnabled(bool value)
    {
        _compactionDistillEnabled = value;
        return this;
    }

    public StubNodeRuntimeSettings WithMaxInlinedAttachmentChars(int value)
    {
        _maxInlinedAttachmentChars = value;
        return this;
    }

    public StubNodeRuntimeSettings WithKnowledgeChatTopK(int value)
    {
        _knowledgeChatTopK = value;
        return this;
    }

    public StubNodeRuntimeSettings WithProviderRetryEnabled(bool value)
    {
        _providerRetryEnabled = value;
        return this;
    }

    public StubNodeRuntimeSettings WithProviderMaxRetries(int value)
    {
        _providerMaxRetries = value;
        return this;
    }

    public StubNodeRuntimeSettings WithSpawnMaxConcurrent(int value)
    {
        _spawnMaxConcurrent = value;
        return this;
    }

    public StubNodeRuntimeSettings WithSpawnMaxCloud(int value)
    {
        _spawnMaxCloud = value;
        return this;
    }

    public StubNodeRuntimeSettings WithSpawnQueueWaitSeconds(int value)
    {
        _spawnQueueWaitSeconds = value;
        return this;
    }

    public StubNodeRuntimeSettings WithKnowledgeAdaptiveRerankingEnabled(bool value)
    {
        _knowledgeAdaptiveRerankingEnabled = value;
        return this;
    }

    public StubNodeRuntimeSettings WithKnowledgeRetrievalLatencyBudgetMs(int value)
    {
        _knowledgeRetrievalLatencyBudgetMs = value;
        return this;
    }

    public StubNodeRuntimeSettings WithKnowledgeScheduledReindexEnabled(bool value)
    {
        _knowledgeScheduledReindexEnabled = value;
        return this;
    }

    public StubNodeRuntimeSettings WithKnowledgeScheduledReindexIntervalMinutes(int value)
    {
        _knowledgeScheduledReindexIntervalMinutes = value;
        return this;
    }

    public StubNodeRuntimeSettings WithKnowledgeAgentToolsEnabled(bool value)
    {
        _knowledgeAgentToolsEnabled = value;
        return this;
    }

    public StubNodeRuntimeSettings WithAllowCloudModelAccess(bool value)
    {
        _allowCloudModelAccess = value;
        return this;
    }

    public StubNodeRuntimeSettings WithAllowCloudModelUnattendedRuns(bool value)
    {
        _allowCloudModelUnattendedRuns = value;
        return this;
    }

    public StubNodeRuntimeSettings WithAllowCloudModelWebTools(bool value)
    {
        _allowCloudModelWebTools = value;
        return this;
    }

    public StubNodeRuntimeSettings WithAllowCloudModelMcpTools(bool value)
    {
        _allowCloudModelMcpTools = value;
        return this;
    }

    public StubNodeRuntimeSettings WithAllowCloudModelSubAgents(bool value)
    {
        _allowCloudModelSubAgents = value;
        return this;
    }

    public StubNodeRuntimeSettings WithPlaybookAnalysisModelName(string value)
    {
        _playbookAnalysisModelName = value;
        return this;
    }

    public StubNodeRuntimeSettings WithPlaybookEvalModelName(string value)
    {
        _playbookEvalModelName = value;
        return this;
    }

    public StubNodeRuntimeSettings WithMemoryExtractionModelName(string value)
    {
        _memoryExtractionModelName = value;
        return this;
    }

    public StubNodeRuntimeSettings WithChatRetentionEnabled(bool value)
    {
        _chatRetentionEnabled = value;
        return this;
    }

    public StubNodeRuntimeSettings WithChatRetentionDays(int value)
    {
        _chatRetentionDays = value;
        return this;
    }

    public StubNodeRuntimeSettings WithAgentExecutionLogRetentionEnabled(bool value)
    {
        _agentExecutionLogRetentionEnabled = value;
        return this;
    }

    public StubNodeRuntimeSettings WithAgentExecutionLogRetentionDays(int value)
    {
        _agentExecutionLogRetentionDays = value;
        return this;
    }

    public StubNodeRuntimeSettings WithNodeDbBackupRetainCount(int value)
    {
        _nodeDbBackupRetainCount = value;
        return this;
    }

    public StubNodeRuntimeSettings WithBenchmarkKldCacheMaxBytes(long value)
    {
        _benchmarkKldCacheMaxBytes = value;
        return this;
    }

    public StubNodeRuntimeSettings WithSchedulerHistoryRetentionDays(int value)
    {
        _schedulerHistoryRetentionDays = value;
        return this;
    }

    public StubNodeRuntimeSettings WithImageMaxLoadedProcesses(int value)
    {
        _imageMaxLoadedProcesses = value;
        return this;
    }

    public StubNodeRuntimeSettings WithImageTextEncoderOnGpu(bool value)
    {
        _imageTextEncoderOnGpu = value;
        return this;
    }

    public StubNodeRuntimeSettings WithGraphWorkflowMaxConcurrentRuns(int value)
    {
        _graphWorkflowMaxConcurrentRuns = value;
        return this;
    }

    public StubNodeRuntimeSettings WithGraphWorkflowDefaultNodeTimeoutSeconds(int value)
    {
        _graphWorkflowDefaultNodeTimeoutSeconds = value;
        return this;
    }

    public StubNodeRuntimeSettings WithWorkSessionMaxStepsPerRun(int value)
    {
        _workSessionMaxStepsPerRun = value;
        return this;
    }

    public StubNodeRuntimeSettings WithWorkSessionMaxConcurrentSessions(int value)
    {
        _workSessionMaxConcurrentSessions = value;
        return this;
    }

    public StubNodeRuntimeSettings WithDevelopmentMaxAttemptDurationSeconds(int value)
    {
        _developmentMaxAttemptDurationSeconds = value;
        return this;
    }

    public StubNodeRuntimeSettings WithDevelopmentMaxToolCalls(int value)
    {
        _developmentMaxToolCalls = value;
        return this;
    }

    public StubNodeRuntimeSettings WithDevelopmentMaxOutputTokens(int value)
    {
        _developmentMaxOutputTokens = value;
        return this;
    }

    public StubNodeRuntimeSettings WithAgentHomeMaxInnerToolCalls(int value)
    {
        _agentHomeMaxInnerToolCalls = value;
        return this;
    }

    public StubNodeRuntimeSettings WithAgentHomePatchApplyTimeoutSeconds(int value)
    {
        _agentHomePatchApplyTimeoutSeconds = value;
        return this;
    }

    public StubNodeRuntimeSettings WithAgentHomeRunRetentionDays(int value)
    {
        _agentHomeRunRetentionDays = value;
        return this;
    }

    public StubNodeRuntimeSettings WithAgentHomeRunRetentionMaxRuns(int value)
    {
        _agentHomeRunRetentionMaxRuns = value;
        return this;
    }

    public StubNodeRuntimeSettings WithAgentHomeRunRetentionMaxTotalBytes(long value)
    {
        _agentHomeRunRetentionMaxTotalBytes = value;
        return this;
    }

    public StubNodeRuntimeSettings WithDevelopmentEnabled(bool value)
    {
        _developmentEnabled = value;
        return this;
    }

    public StubNodeRuntimeSettings WithWorkSessionsEnabled(bool value)
    {
        _workSessionsEnabled = value;
        return this;
    }

    public StubNodeRuntimeSettings WithGraphWorkflowsEnabled(bool value)
    {
        _graphWorkflowsEnabled = value;
        return this;
    }

    public StubNodeRuntimeSettings WithTranscriptionEnabled(bool value)
    {
        _transcriptionEnabled = value;
        return this;
    }

    public StubNodeRuntimeSettings WithExternalAppsEnabled(bool value)
    {
        _externalAppsEnabled = value;
        return this;
    }

    public StubNodeRuntimeSettings WithComputeEnabled(bool value)
    {
        _computeEnabled = value;
        return this;
    }

    public StubNodeRuntimeSettings WithAgentHomeEnabled(bool value)
    {
        _agentHomeEnabled = value;
        return this;
    }

    public StubNodeRuntimeSettings WithSchedulerEnabled(bool value)
    {
        _schedulerEnabled = value;
        return this;
    }

    public StubNodeRuntimeSettings WithDevWorkflowsEnabled(bool value)
    {
        _devWorkflowsEnabled = value;
        return this;
    }

    public StubNodeRuntimeSettings WithExecutionPreviewsEnabled(bool value)
    {
        _executionPreviewsEnabled = value;
        return this;
    }

    public INodeRuntimeSettings Build()
    {
        var settings = Substitute.For<INodeRuntimeSettings>();
        settings.GetDefaultModelNameAsync(Arg.Any<CancellationToken>()).Returns(_defaultModelName);
        settings.GetEnableToolsAsync(Arg.Any<CancellationToken>()).Returns(_enableTools);
        settings.GetToolCapableModelsAsync(Arg.Any<CancellationToken>()).Returns(_toolCapableModels);
        settings.GetHuggingFaceDefaultQuantAsync(Arg.Any<CancellationToken>()).Returns(_huggingFaceDefaultQuant);
        settings.GetHuggingFaceDiskMarginBytesAsync(Arg.Any<CancellationToken>()).Returns(_huggingFaceDiskMarginBytes);
        settings.GetLlamaMaxLoadedProcessesAsync(Arg.Any<CancellationToken>()).Returns(_llamaMaxLoadedProcesses);
        settings.GetLlamaIdleTimeToLiveAsync(Arg.Any<CancellationToken>())
                .Returns(_llamaIdleTimeToLive);
        settings.GetKeepModelWarmEnabledAsync(Arg.Any<CancellationToken>()).Returns(_keepModelWarmEnabled);
        settings.GetKeepModelWarmModelNameAsync(Arg.Any<CancellationToken>()).Returns(_keepModelWarmModelName);
        settings.GetKeepModelWarmIntervalAsync(Arg.Any<CancellationToken>()).Returns(_keepModelWarmInterval);
        settings.GetMaxResponseSizeMbAsync(Arg.Any<CancellationToken>()).Returns(_maxResponseSizeMb);
        settings.GetRecommendedLlamaCppTagAsync(Arg.Any<CancellationToken>()).Returns(StoredNodeSettings.DefaultRecommendedLlamaCppTag);
        settings.GetOrchestrationIdleTimeoutSecondsAsync(Arg.Any<CancellationToken>()).Returns(_orchestrationIdleTimeoutSeconds);
        settings.GetAgentHomePrepareTimeoutSecondsAsync(Arg.Any<CancellationToken>()).Returns(_agentHomePrepareTimeoutSeconds);
        settings.GetAgentHomeCommandTimeoutSecondsAsync(Arg.Any<CancellationToken>()).Returns(_agentHomeCommandTimeoutSeconds);
        settings.GetAgentHomeMaxSelectedFolderBytesAsync(Arg.Any<CancellationToken>()).Returns(_agentHomeMaxSelectedFolderBytes);
        settings.GetAgentHomeMaxPatchBytesAsync(Arg.Any<CancellationToken>()).Returns(_agentHomeMaxPatchBytes);
        settings.GetMaxPendingToolCallAgeMinutesAsync(Arg.Any<CancellationToken>()).Returns(_maxPendingToolCallAgeMinutes);
        settings.GetDetachedGraceSecondsAsync(Arg.Any<CancellationToken>()).Returns(_detachedGraceSeconds);
        settings.GetChatCacheReuseAsync(Arg.Any<CancellationToken>()).Returns(_chatCacheReuse);
        settings.GetKvCacheTypeAsync(Arg.Any<CancellationToken>()).Returns(_kvCacheType);
        settings.GetSpeculativeModeAsync(Arg.Any<CancellationToken>()).Returns(_speculativeMode);
        settings.GetSpeculativeDraftModelNameAsync(Arg.Any<CancellationToken>()).Returns(_speculativeDraftModelName);
        settings.GetSpeculativeDraftMaxTokensAsync(Arg.Any<CancellationToken>()).Returns(_speculativeDraftMaxTokens);
        settings.GetSpeculativeDraftGpuLayersAsync(Arg.Any<CancellationToken>()).Returns(_speculativeDraftGpuLayers);
        settings.GetRerankerModelNameAsync(Arg.Any<CancellationToken>()).Returns(_rerankerModelName);
        settings.GetCustomToolsEnabledAsync(Arg.Any<CancellationToken>()).Returns(_customToolsEnabled);
        settings.GetWebAccessEnabledAsync(Arg.Any<CancellationToken>()).Returns(_webAccessEnabled);
        settings.GetWebSearchSearxngUrlAsync(Arg.Any<CancellationToken>()).Returns(_webSearchSearxngUrl);
        settings.GetToolRelevanceEnabledAsync(Arg.Any<CancellationToken>()).Returns(call => _toolRelevanceRead(call.Arg<CancellationToken>()));
        settings.GetExternalAccessProfileAsync(Arg.Any<CancellationToken>())
                .Returns(call => _externalAccessProfileRead(call.Arg<CancellationToken>()));
        settings.GetAutoCheckApplicationUpdatesAsync(Arg.Any<CancellationToken>()).Returns(_autoCheckApplicationUpdates);
        settings.GetAutoCheckRuntimeUpdatesAsync(Arg.Any<CancellationToken>()).Returns(_autoCheckRuntimeUpdates);
        settings.GetAutoProvisionFirstRunModelAsync(Arg.Any<CancellationToken>()).Returns(_autoProvisionFirstRunModel);
        settings.GetCustomToolMaxTimeoutSecondsAsync(Arg.Any<CancellationToken>()).Returns(StoredNodeSettings.DefaultCustomToolMaxTimeoutSeconds);
        settings.GetWebFetchTimeoutAsync(Arg.Any<CancellationToken>()).Returns(TimeSpan.FromSeconds(StoredNodeSettings.DefaultWebFetchTimeoutSeconds));
        settings.GetWebFetchMaxContentCharsAsync(Arg.Any<CancellationToken>()).Returns(_webFetchMaxContentChars);
        settings.GetKnowledgeSearchDefaultResultsAsync(Arg.Any<CancellationToken>()).Returns(StoredNodeSettings.DefaultKnowledgeSearchDefaultResults);
        settings.GetKnowledgeSearchMaxResultsAsync(Arg.Any<CancellationToken>()).Returns(StoredNodeSettings.DefaultKnowledgeSearchMaxResults);
        settings.GetReasoningBudgetsAsync(Arg.Any<CancellationToken>()).Returns(_reasoningBudgets);
        settings.GetChatOutputCapAsync(Arg.Any<CancellationToken>()).Returns(_chatOutputCap);
        settings.GetAgentHomeMaxRunSecondsAsync(Arg.Any<CancellationToken>()).Returns(_agentHomeMaxRunSeconds);
        settings.GetDefaultContextTokensAsync(Arg.Any<CancellationToken>()).Returns(_defaultContextTokens);
        settings.GetProviderBudgetRecentMessagesToKeepAsync(Arg.Any<CancellationToken>()).Returns(_providerBudgetRecentMessagesToKeep);
        settings.GetProviderBudgetMaxCumulativeInputTokensAsync(Arg.Any<CancellationToken>()).Returns(_providerBudgetMaxCumulativeInputTokens);
        settings.GetCompactionAutoEnabledAsync(Arg.Any<CancellationToken>()).Returns(_compactionAutoEnabled);
        settings.GetCompactionAutoCompactPercentAsync(Arg.Any<CancellationToken>()).Returns(_compactionAutoCompactPercent);
        settings.GetCompactionRecentMessagesVerbatimAsync(Arg.Any<CancellationToken>()).Returns(_compactionRecentMessagesVerbatim);
        settings.GetCompactionDistillEnabledAsync(Arg.Any<CancellationToken>()).Returns(_compactionDistillEnabled);
        settings.GetMaxInlinedAttachmentCharsAsync(Arg.Any<CancellationToken>()).Returns(_maxInlinedAttachmentChars);
        settings.GetKnowledgeChatTopKAsync(Arg.Any<CancellationToken>()).Returns(_knowledgeChatTopK);
        settings.GetProviderRetryEnabledAsync(Arg.Any<CancellationToken>()).Returns(_providerRetryEnabled);
        settings.GetProviderMaxRetriesAsync(Arg.Any<CancellationToken>()).Returns(_providerMaxRetries);
        settings.GetSpawnMaxConcurrentAsync(Arg.Any<CancellationToken>()).Returns(_spawnMaxConcurrent);
        settings.GetSpawnMaxCloudAsync(Arg.Any<CancellationToken>()).Returns(_spawnMaxCloud);
        settings.GetSpawnQueueWaitSecondsAsync(Arg.Any<CancellationToken>()).Returns(_spawnQueueWaitSeconds);

        // Synchronous twins (composition/ctor path) must mirror the async values so consumers repointed onto the sync
        // getters (e.g. InvocationRunner, the DI factory seeds) observe the same configured knobs.
        settings.GetDefaultModelName().Returns(_defaultModelName);
        settings.GetToolCapableModels().Returns(_toolCapableModels);
        settings.GetToolCapableModelsSeed().Returns(_ => _toolCapableModelsSeed);
        settings.GetOllamaEndpoint().Returns(StoredNodeSettings.DefaultOllamaEndpoint);
        settings.GetHuggingFaceDefaultQuant().Returns(_huggingFaceDefaultQuant);
        settings.GetHuggingFaceDiskMarginBytes().Returns(_huggingFaceDiskMarginBytes);
        settings.GetLlamaMaxLoadedProcesses().Returns(_llamaMaxLoadedProcesses);
        settings.GetLlamaIdleTimeToLive().Returns(_llamaIdleTimeToLive);
        settings.GetMaxResponseSizeMb().Returns(_maxResponseSizeMb);
        settings.GetOrchestrationIdleTimeoutSeconds().Returns(_orchestrationIdleTimeoutSeconds);
        settings.GetMaxPendingToolCallAgeMinutes().Returns(_maxPendingToolCallAgeMinutes);
        settings.GetDetachedGraceSeconds().Returns(_detachedGraceSeconds);
        settings.GetChatCacheReuse().Returns(_chatCacheReuse);
        settings.GetKvCacheType().Returns(_kvCacheType);
        settings.GetSpeculativeMode().Returns(_speculativeMode);
        settings.GetSpeculativeDraftModelName().Returns(_speculativeDraftModelName);
        settings.GetSpeculativeDraftMaxTokens().Returns(_speculativeDraftMaxTokens);
        settings.GetSpeculativeDraftGpuLayers().Returns(_speculativeDraftGpuLayers);
        settings.GetRerankerModelName().Returns(_rerankerModelName);
        settings.GetLlamaReadinessTimeoutCap().Returns(TimeSpan.FromSeconds(StoredNodeSettings.DefaultLlamaReadinessTimeoutCapSeconds));
        settings.GetLlamaChatHttpTimeout().Returns(TimeSpan.FromSeconds(StoredNodeSettings.DefaultLlamaChatHttpTimeoutSeconds));
        settings.GetLlamaEmbeddingHttpTimeout().Returns(TimeSpan.FromSeconds(StoredNodeSettings.DefaultLlamaEmbeddingHttpTimeoutSeconds));
        settings.GetLlamaCpuThreadReserve().Returns(StoredNodeSettings.DefaultLlamaCpuThreadReserve);
        settings.GetLlamaGpuReserveFraction().Returns(StoredNodeSettings.DefaultLlamaGpuReservePercent / 100d);
        settings.GetLlamaRamReserveFraction().Returns(StoredNodeSettings.DefaultLlamaRamReservePercent / 100d);
        settings.GetImageIdleTimeToLive().Returns(TimeSpan.FromSeconds(StoredNodeSettings.DefaultImageIdleTimeToLiveSeconds));
        settings.GetMaxProviderCallsPerInvocation().Returns(StoredNodeSettings.DefaultMaxProviderCallsPerInvocation);
        settings.GetHuggingFaceDownloadConnections().Returns(StoredNodeSettings.DefaultHuggingFaceDownloadConnections);
        settings.GetTranscriptionInferenceTimeout().Returns(TimeSpan.FromMinutes(StoredNodeSettings.DefaultTranscriptionInferenceTimeoutMinutes));
        settings.GetModelFitSafetyMarginFraction().Returns(StoredNodeSettings.DefaultModelFitSafetyMarginPercent / 100d);
        settings.GetToolPipelineMaxIterationsPerRequest().Returns(_toolPipelineMaxIterationsPerRequest);
        settings.GetToolPipelineMaxToolResultChars().Returns(_toolPipelineMaxToolResultChars);
        settings.GetToolPipelineMaxConsecutiveInvalidToolCalls().Returns(_toolPipelineMaxConsecutiveInvalidToolCalls);
        settings.GetContextBudgetRecentTurnKeepCount().Returns(_contextBudgetRecentTurnKeepCount);
        settings.GetKnowledgeAdaptiveRerankingEnabledAsync(Arg.Any<CancellationToken>()).Returns(_knowledgeAdaptiveRerankingEnabled);
        settings.GetKnowledgeRetrievalLatencyBudgetMsAsync(Arg.Any<CancellationToken>()).Returns(_knowledgeRetrievalLatencyBudgetMs);
        settings.GetKnowledgeScheduledReindexEnabled().Returns(_knowledgeScheduledReindexEnabled);
        settings.GetKnowledgeScheduledReindexIntervalMinutes().Returns(_knowledgeScheduledReindexIntervalMinutes);
        settings.GetKnowledgeAgentToolsEnabledAsync(Arg.Any<CancellationToken>()).Returns(_knowledgeAgentToolsEnabled);
        settings.GetKnowledgeAgentToolsEnabled().Returns(_knowledgeAgentToolsEnabled);
        settings.GetAllowCloudModelAccessAsync(Arg.Any<CancellationToken>()).Returns(_allowCloudModelAccess);
        settings.GetAllowCloudModelAccess().Returns(_allowCloudModelAccess);
        settings.GetAllowCloudModelUnattendedRunsAsync(Arg.Any<CancellationToken>()).Returns(_allowCloudModelUnattendedRuns);
        settings.GetAllowCloudModelUnattendedRuns().Returns(_allowCloudModelUnattendedRuns);
        settings.GetAllowCloudModelWebToolsAsync(Arg.Any<CancellationToken>()).Returns(_allowCloudModelWebTools);
        settings.GetAllowCloudModelWebTools().Returns(_allowCloudModelWebTools);
        settings.GetAllowCloudModelMcpToolsAsync(Arg.Any<CancellationToken>()).Returns(_allowCloudModelMcpTools);
        settings.GetAllowCloudModelMcpTools().Returns(_allowCloudModelMcpTools);
        settings.GetAllowCloudModelSubAgentsAsync(Arg.Any<CancellationToken>()).Returns(_allowCloudModelSubAgents);
        settings.GetAllowCloudModelSubAgents().Returns(_allowCloudModelSubAgents);
        settings.GetPlaybookAnalysisModelNameAsync(Arg.Any<CancellationToken>()).Returns(_playbookAnalysisModelName ?? _defaultModelName);
        settings.GetPlaybookEvalModelNameAsync(Arg.Any<CancellationToken>()).Returns(_playbookEvalModelName ?? _defaultModelName);
        settings.GetMemoryExtractionModelNameAsync(Arg.Any<CancellationToken>()).Returns(_memoryExtractionModelName ?? _defaultModelName);
        settings.GetChatRetentionEnabledAsync(Arg.Any<CancellationToken>()).Returns(_chatRetentionEnabled);
        settings.GetChatRetentionDaysAsync(Arg.Any<CancellationToken>()).Returns(_chatRetentionDays);
        settings.GetAgentExecutionLogRetentionEnabledAsync(Arg.Any<CancellationToken>()).Returns(_agentExecutionLogRetentionEnabled);
        settings.GetAgentExecutionLogRetentionDaysAsync(Arg.Any<CancellationToken>()).Returns(_agentExecutionLogRetentionDays);
        settings.GetNodeDbBackupRetainCountAsync(Arg.Any<CancellationToken>()).Returns(_nodeDbBackupRetainCount);
        settings.GetBenchmarkKldCacheMaxBytesAsync(Arg.Any<CancellationToken>()).Returns(_benchmarkKldCacheMaxBytes);
        settings.GetSchedulerHistoryRetentionDaysAsync(Arg.Any<CancellationToken>()).Returns(_schedulerHistoryRetentionDays);
        settings.GetImageMaxLoadedProcesses().Returns(_imageMaxLoadedProcesses);
        settings.GetImageTextEncoderOnGpu().Returns(_imageTextEncoderOnGpu);
        settings.GetGraphWorkflowMaxConcurrentRuns().Returns(_graphWorkflowMaxConcurrentRuns);
        settings.GetGraphWorkflowDefaultNodeTimeoutSeconds().Returns(_graphWorkflowDefaultNodeTimeoutSeconds);
        settings.GetWorkSessionMaxStepsPerRun().Returns(_workSessionMaxStepsPerRun);
        settings.GetWorkSessionMaxConcurrentSessions().Returns(_workSessionMaxConcurrentSessions);
        settings.GetDevelopmentMaxAttemptDurationSeconds().Returns(_developmentMaxAttemptDurationSeconds);
        settings.GetDevelopmentMaxToolCalls().Returns(_developmentMaxToolCalls);
        settings.GetDevelopmentMaxOutputTokens().Returns(_developmentMaxOutputTokens);
        settings.GetAgentHomeMaxInnerToolCallsAsync(Arg.Any<CancellationToken>()).Returns(_agentHomeMaxInnerToolCalls);
        settings.GetAgentHomePatchApplyTimeoutSecondsAsync(Arg.Any<CancellationToken>()).Returns(_agentHomePatchApplyTimeoutSeconds);
        settings.GetAgentHomeRunRetentionDaysAsync(Arg.Any<CancellationToken>()).Returns(_agentHomeRunRetentionDays);
        settings.GetAgentHomeRunRetentionMaxRunsAsync(Arg.Any<CancellationToken>()).Returns(_agentHomeRunRetentionMaxRuns);
        settings.GetAgentHomeRunRetentionMaxTotalBytesAsync(Arg.Any<CancellationToken>()).Returns(_agentHomeRunRetentionMaxTotalBytes);
        settings.GetDevelopmentEnabledAsync(Arg.Any<CancellationToken>()).Returns(_ => _developmentEnabled);
        settings.GetDevelopmentEnabled().Returns(_ => _developmentEnabled);
        settings.GetWorkSessionsEnabledAsync(Arg.Any<CancellationToken>()).Returns(_ => _workSessionsEnabled);
        settings.GetWorkSessionsEnabled().Returns(_ => _workSessionsEnabled);
        settings.GetGraphWorkflowsEnabledAsync(Arg.Any<CancellationToken>()).Returns(_ => _graphWorkflowsEnabled);
        settings.GetGraphWorkflowsEnabled().Returns(_ => _graphWorkflowsEnabled);
        settings.GetTranscriptionEnabledAsync(Arg.Any<CancellationToken>()).Returns(_ => _transcriptionEnabled);
        settings.GetTranscriptionEnabled().Returns(_ => _transcriptionEnabled);
        settings.GetExternalAppsEnabledAsync(Arg.Any<CancellationToken>()).Returns(_ => _externalAppsEnabled);
        settings.GetExternalAppsEnabled().Returns(_ => _externalAppsEnabled);
        settings.GetComputeEnabledAsync(Arg.Any<CancellationToken>()).Returns(_ => _computeEnabled);
        settings.GetComputeEnabled().Returns(_ => _computeEnabled);
        settings.GetAgentHomeEnabledAsync(Arg.Any<CancellationToken>()).Returns(_ => _agentHomeEnabled);
        settings.GetAgentHomeEnabled().Returns(_ => _agentHomeEnabled);
        settings.GetSchedulerEnabledAsync(Arg.Any<CancellationToken>()).Returns(_ => _schedulerEnabled);
        settings.GetSchedulerEnabled().Returns(_ => _schedulerEnabled);
        settings.GetDevWorkflowsEnabledAsync(Arg.Any<CancellationToken>()).Returns(_ => _devWorkflowsEnabled);
        settings.GetDevWorkflowsEnabled().Returns(_ => _devWorkflowsEnabled);
        settings.GetExecutionPreviewsEnabledAsync(Arg.Any<CancellationToken>()).Returns(_ => _executionPreviewsEnabled);
        settings.GetExecutionPreviewsEnabled().Returns(_ => _executionPreviewsEnabled);
        settings.ResolveEffectiveValues(Arg.Any<StoredNodeSettings>())
                .Returns(call =>
                {
                    var stored = call.Arg<StoredNodeSettings>();
                    return new NodeSettingsEffectiveValues
                    {
                        CompactionAutoEnabled = stored.CompactionAutoEnabled ?? _compactionAutoEnabled,
                        CompactionDistillEnabled = stored.CompactionDistillEnabled ?? _compactionDistillEnabled,
                        ProviderRetryEnabled = stored.ProviderRetryEnabled ?? _providerRetryEnabled,
                        KnowledgeAdaptiveRerankingEnabled = stored.KnowledgeAdaptiveRerankingEnabled ?? _knowledgeAdaptiveRerankingEnabled,
                        KnowledgeScheduledReindexEnabled = stored.KnowledgeScheduledReindexEnabled ?? _knowledgeScheduledReindexEnabled,
                        KnowledgeAgentToolsEnabled = stored.KnowledgeAgentToolsEnabled ?? _knowledgeAgentToolsEnabled,
                        AllowCloudModelAccess = stored.AllowCloudModelAccess ?? _allowCloudModelAccess,
                        AllowCloudModelUnattendedRuns = stored.AllowCloudModelUnattendedRuns ?? _allowCloudModelUnattendedRuns,
                        AllowCloudModelWebTools = stored.AllowCloudModelWebTools ?? _allowCloudModelWebTools,
                        AllowCloudModelMcpTools = stored.AllowCloudModelMcpTools ?? _allowCloudModelMcpTools,
                        AllowCloudModelSubAgents = stored.AllowCloudModelSubAgents ?? _allowCloudModelSubAgents,
                        ChatRetentionEnabled = stored.ChatRetentionEnabled ?? _chatRetentionEnabled,
                        AgentExecutionLogRetentionEnabled = stored.AgentExecutionLogRetentionEnabled ?? _agentExecutionLogRetentionEnabled,
                        ImageTextEncoderOnGpu = stored.ImageTextEncoderOnGpu ?? _imageTextEncoderOnGpu,
                        ChatRetentionDays = stored.ChatRetentionDays ?? _chatRetentionDays,
                        AgentExecutionLogRetentionDays = stored.AgentExecutionLogRetentionDays ?? _agentExecutionLogRetentionDays,
                        SchedulerHistoryRetentionDays = stored.SchedulerHistoryRetentionDays ?? _schedulerHistoryRetentionDays,
                        AgentHomeRunRetentionDays = stored.AgentHomeRunRetentionDays ?? _agentHomeRunRetentionDays,
                        DevelopmentEnabled = stored.DevelopmentEnabled ?? _developmentEnabled,
                        WorkSessionsEnabled = stored.WorkSessionsEnabled ?? _workSessionsEnabled,
                        GraphWorkflowsEnabled = stored.GraphWorkflowsEnabled ?? _graphWorkflowsEnabled,
                        TranscriptionEnabled = stored.TranscriptionEnabled ?? _transcriptionEnabled,
                        ExternalAppsEnabled = stored.ExternalAppsEnabled ?? _externalAppsEnabled,
                        ComputeEnabled = stored.ComputeEnabled ?? _computeEnabled,
                        AgentHomeEnabled = stored.AgentHomeEnabled ?? _agentHomeEnabled,
                        SchedulerEnabled = stored.SchedulerEnabled ?? _schedulerEnabled,
                        DevWorkflowsEnabled = stored.DevWorkflowsEnabled ?? _devWorkflowsEnabled,
                        ExecutionPreviewsEnabled = stored.ExecutionPreviewsEnabled ?? _executionPreviewsEnabled
                    };
                });
        return settings;
    }
}
