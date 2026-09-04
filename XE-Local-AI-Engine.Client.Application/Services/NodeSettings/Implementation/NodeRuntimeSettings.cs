namespace XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     Resolves the effective value of each migrated runtime knob with the precedence
///     <c>stored &gt; appsettings seed &gt; hardcoded default</c>. The stored value comes from the cached
///     <see cref="INodeSettingsStore" />; the appsettings seed is captured from the bound options/configuration at
///     construction (so first-run behavior is unchanged from today's appsettings). For knobs without a config section
///     today (the llama.cpp supervisor cap/TTL) the seed IS the hardcoded default.
/// </summary>
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
    private readonly int _orchestrationIdleTimeoutSeed;
    private readonly string _ollamaEndpointSeed;
    private readonly INodeSettingsStore _store;
    private readonly IReadOnlyList<string> _toolCapableModelsSeed;

    public NodeRuntimeSettings(INodeSettingsStore store,
        IConfiguration configuration,
        IOptions<LocalChatAgentOptions> localChatOptions,
        IOptions<AgentHomeOptions> agentHomeOptions,
        IOptions<WorkerNodeOptions> workerNodeOptions)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
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
        _maxResponseSizeMbSeed = workerNode.MaxResponseSizeMb;
        _maxPendingToolCallAgeMinutesSeed = workerNode.MaxPendingToolCallAgeMinutes;
        _detachedGraceSecondsSeed = workerNode.DetachedGraceSeconds;

        // Orchestration idle-timeout seed is read from configuration (not IOptions<OrchestrationAgentOptions>) to avoid a
        // DI cycle: OrchestrationAgentOptions is itself Configure-d FROM this accessor at the composition root (so the
        // AI.Agent factory, which cannot reference INodeRuntimeSettings, still gets the stored value). Taking
        // IOptions<OrchestrationAgentOptions> here would make the accessor depend on the option it configures.
        _orchestrationIdleTimeoutSeed = configuration.GetValue<int?>("Agent:Orchestration:IdleTimeoutSeconds")
                                        ?? StoredNodeSettings.DefaultOrchestrationIdleTimeoutSeconds;

        // HuggingFaceOptions is registered as a plain singleton only after AddHuggingFaceGgufStore runs, which is not
        // guaranteed in every host/test context, so the HF seeds are read from configuration directly (mirroring the
        // Options defaults) instead of taking an IOptions<HuggingFaceOptions> dependency.
        var configuredQuant = configuration.GetValue<string>("HuggingFace:DefaultQuant");
        _hfQuantSeed = string.IsNullOrWhiteSpace(configuredQuant) ? StoredNodeSettings.DefaultHuggingFaceQuant : configuredQuant;

        var configuredMargin = configuration.GetValue<long?>("HuggingFace:DiskMarginBytes");
        _hfDiskMarginSeed = configuredMargin is > 0 ? configuredMargin.Value : StoredNodeSettings.DefaultHuggingFaceDiskMarginBytes;

        // Ollama:Endpoint is not bound to an Options class today; it is read directly from configuration at host build.
        var configuredOllama = configuration.GetValue<string>("Ollama:Endpoint");
        _ollamaEndpointSeed = string.IsNullOrWhiteSpace(configuredOllama)
            ? StoredNodeSettings.DefaultOllamaEndpoint
            : configuredOllama;
    }

    public async Task<string> GetDefaultModelNameAsync(CancellationToken cancellationToken = default) =>
        ResolveDefaultModelName(await LoadAsync(cancellationToken).ConfigureAwait(false));

    public async Task<bool> GetEnableToolsAsync(CancellationToken cancellationToken = default)
    {
        var stored = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return stored.EnableTools ?? _enableToolsSeed;
    }

    public async Task<bool> GetCustomToolsEnabledAsync(CancellationToken cancellationToken = default)
    {
        var stored = await LoadAsync(cancellationToken).ConfigureAwait(false);
        // No appsettings seed: a host-execution feature has no config-file default, so the fallback IS the hardcoded off.
        return stored.CustomToolsEnabled ?? StoredNodeSettings.DefaultCustomToolsEnabled;
    }

    public async Task<IReadOnlyList<string>> GetToolCapableModelsAsync(CancellationToken cancellationToken = default) =>
        ResolveToolCapableModels(await LoadAsync(cancellationToken).ConfigureAwait(false));

    public async Task<string> GetOllamaEndpointAsync(CancellationToken cancellationToken = default) =>
        ResolveOllamaEndpoint(await LoadAsync(cancellationToken).ConfigureAwait(false));

    public async Task<string> GetHuggingFaceDefaultQuantAsync(CancellationToken cancellationToken = default) =>
        ResolveHuggingFaceDefaultQuant(await LoadAsync(cancellationToken).ConfigureAwait(false));

    public async Task<long> GetHuggingFaceDiskMarginBytesAsync(CancellationToken cancellationToken = default) =>
        ResolveHuggingFaceDiskMarginBytes(await LoadAsync(cancellationToken).ConfigureAwait(false));

    public async Task<int> GetLlamaMaxLoadedProcessesAsync(CancellationToken cancellationToken = default) =>
        ResolveLlamaMaxLoadedProcesses(await LoadAsync(cancellationToken).ConfigureAwait(false));

    public async Task<TimeSpan> GetLlamaIdleTimeToLiveAsync(CancellationToken cancellationToken = default) =>
        ResolveLlamaIdleTimeToLive(await LoadAsync(cancellationToken).ConfigureAwait(false));

    public async Task<bool> GetKeepModelWarmEnabledAsync(CancellationToken cancellationToken = default) =>
        ResolveKeepModelWarmEnabled(await LoadAsync(cancellationToken).ConfigureAwait(false));

    public async Task<string?> GetKeepModelWarmModelNameAsync(CancellationToken cancellationToken = default) =>
        ResolveKeepModelWarmModelName(await LoadAsync(cancellationToken).ConfigureAwait(false));

    public async Task<TimeSpan> GetKeepModelWarmIntervalAsync(CancellationToken cancellationToken = default) =>
        ResolveKeepModelWarmInterval(await LoadAsync(cancellationToken).ConfigureAwait(false));

    public async Task<int> GetMaxResponseSizeMbAsync(CancellationToken cancellationToken = default) =>
        ResolveMaxResponseSizeMb(await LoadAsync(cancellationToken).ConfigureAwait(false));

    public async Task<string> GetRecommendedLlamaCppTagAsync(CancellationToken cancellationToken = default)
    {
        var stored = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return StoredNodeSettings.IsValidRecommendedLlamaCppTag(stored.RecommendedLlamaCppTag)
            ? stored.RecommendedLlamaCppTag!
            : LlamaCppReleasePins.PinnedTag;
    }

    public async Task<int> GetOrchestrationIdleTimeoutSecondsAsync(CancellationToken cancellationToken = default) =>
        ResolveOrchestrationIdleTimeoutSeconds(await LoadAsync(cancellationToken).ConfigureAwait(false));

    public async Task<int> GetAgentHomePrepareTimeoutSecondsAsync(CancellationToken cancellationToken = default)
    {
        var stored = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return stored.AgentHomePrepareTimeoutSeconds ?? _agentHomePrepareTimeoutSeed;
    }

    public async Task<int> GetAgentHomeCommandTimeoutSecondsAsync(CancellationToken cancellationToken = default)
    {
        var stored = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return stored.AgentHomeCommandTimeoutSeconds ?? _agentHomeCommandTimeoutSeed;
    }

    public async Task<long> GetAgentHomeMaxSelectedFolderBytesAsync(CancellationToken cancellationToken = default)
    {
        var stored = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return stored.AgentHomeMaxSelectedFolderBytes ?? _agentHomeMaxSelectedFolderBytesSeed;
    }

    public async Task<long> GetAgentHomeMaxPatchBytesAsync(CancellationToken cancellationToken = default)
    {
        var stored = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return stored.AgentHomeMaxPatchBytes ?? _agentHomeMaxPatchBytesSeed;
    }

    public async Task<int> GetMaxPendingToolCallAgeMinutesAsync(CancellationToken cancellationToken = default) =>
        ResolveMaxPendingToolCallAgeMinutes(await LoadAsync(cancellationToken).ConfigureAwait(false));

    public async Task<int> GetDetachedGraceSecondsAsync(CancellationToken cancellationToken = default) =>
        ResolveDetachedGraceSeconds(await LoadAsync(cancellationToken).ConfigureAwait(false));

    public async Task<int> GetChatCacheReuseAsync(CancellationToken cancellationToken = default) =>
        ResolveChatCacheReuse(await LoadAsync(cancellationToken).ConfigureAwait(false));

    public async Task<string> GetSpeculativeModeAsync(CancellationToken cancellationToken = default) =>
        ResolveSpeculativeMode(await LoadAsync(cancellationToken).ConfigureAwait(false));

    public async Task<string> GetKvCacheTypeAsync(CancellationToken cancellationToken = default) =>
        ResolveKvCacheType(await LoadAsync(cancellationToken).ConfigureAwait(false));

    public async Task<string?> GetSpeculativeDraftModelNameAsync(CancellationToken cancellationToken = default) =>
        ResolveSpeculativeDraftModelName(await LoadAsync(cancellationToken).ConfigureAwait(false));

    public async Task<int> GetSpeculativeDraftMaxTokensAsync(CancellationToken cancellationToken = default) =>
        ResolveSpeculativeDraftMaxTokens(await LoadAsync(cancellationToken).ConfigureAwait(false));

    public async Task<int?> GetSpeculativeDraftGpuLayersAsync(CancellationToken cancellationToken = default) =>
        ResolveSpeculativeDraftGpuLayers(await LoadAsync(cancellationToken).ConfigureAwait(false));

    public async Task<string?> GetRerankerModelNameAsync(CancellationToken cancellationToken = default) =>
        ResolveRerankerModelName(await LoadAsync(cancellationToken).ConfigureAwait(false));

    public async Task<string?> GetAutoEffortFastModelNameAsync(CancellationToken cancellationToken = default) =>
        ResolveAutoEffortFastModelName(await LoadAsync(cancellationToken).ConfigureAwait(false));

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

    private string ResolveDefaultModelName(StoredNodeSettings stored) =>
        string.IsNullOrWhiteSpace(stored.DefaultModelName) ? _defaultModelSeed : stored.DefaultModelName;

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
        return await _store.LoadAsync(cancellationToken).ConfigureAwait(false) ?? new StoredNodeSettings();
    }

    private StoredNodeSettings LoadStored()
    {
        return _store.Load() ?? new StoredNodeSettings();
    }
}
