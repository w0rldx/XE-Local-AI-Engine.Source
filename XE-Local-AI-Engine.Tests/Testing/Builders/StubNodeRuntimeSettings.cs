namespace XE_Local_AI_Engine.Tests.Testing.Builders;

using NSubstitute;
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
    private int _chatCacheReuse = StoredNodeSettings.DefaultChatCacheReuse;
    private string _kvCacheType = StoredNodeSettings.DefaultKvCacheType;
    private string _speculativeMode = StoredNodeSettings.DefaultSpeculativeMode;
    private string? _speculativeDraftModelName;
    private int _speculativeDraftMaxTokens = StoredNodeSettings.DefaultSpeculativeDraftMaxTokens;
    private int? _speculativeDraftGpuLayers;
    private string? _rerankerModelName;
    private bool _customToolsEnabled = StoredNodeSettings.DefaultCustomToolsEnabled;

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

    public StubNodeRuntimeSettings WithToolCapableModels(params string[] toolCapableModels)
    {
        ArgumentNullException.ThrowIfNull(toolCapableModels);
        _toolCapableModels = [.. toolCapableModels];
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

        // Synchronous twins (composition/ctor path) must mirror the async values so consumers repointed onto the sync
        // getters (e.g. InvocationRunner, the DI factory seeds) observe the same configured knobs.
        settings.GetDefaultModelName().Returns(_defaultModelName);
        settings.GetToolCapableModels().Returns(_toolCapableModels);
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
        return settings;
    }
}
