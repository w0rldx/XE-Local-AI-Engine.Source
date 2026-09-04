namespace XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1;

using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Response for <c>GET api/local/v1/node-settings</c>. Surfaces the effective stored values for every user-editable
///     node setting plus the per-field bounds the React form renders ranges from. Fields are grouped: the original chat
///     timeout, then the "general" migrated knobs (always shown), then the developer-only advanced knobs (the React lane
///     gates their visibility; the backend always returns them).
/// </summary>
public sealed record NodeSettingsResponse
{
    public int MaxMessageRequestTimeoutSeconds { get; init; }

    public int MinMessageRequestTimeoutSeconds { get; init; }

    public int MaxAllowedMessageRequestTimeoutSeconds { get; init; }

    public string? DefaultModelName { get; init; }

    public bool? EnableTools { get; init; }

    /// <summary>
    ///     Node kill-switch for the user-defined custom tools feature. <see langword="null" /> reads as off (default).
    ///     DANGER: enabling this allows agents to run user-defined tools that execute host commands, launch programs, and
    ///     make network requests. Off by default; each call still requires operator approval per the per-agent allow-list
    ///     and the forced per-call approval gate.
    /// </summary>
    public bool? CustomToolsEnabled { get; init; }

    public IReadOnlyList<string>? ToolCapableModels { get; init; }

    public string? OllamaEndpoint { get; init; }

    public string? HuggingFaceDefaultQuant { get; init; }

    public int? LlamaMaxLoadedProcesses { get; init; }

    public int MinLlamaMaxLoadedProcesses { get; init; }

    public int MaxAllowedLlamaMaxLoadedProcesses { get; init; }

    public int? LlamaIdleTimeToLiveSeconds { get; init; }

    public int MinLlamaIdleTimeToLiveSeconds { get; init; }

    public int MaxAllowedLlamaIdleTimeToLiveSeconds { get; init; }

    public bool? KeepModelWarmEnabled { get; init; }

    public string? KeepModelWarmModelName { get; init; }

    public int? KeepModelWarmIntervalSeconds { get; init; }

    public int MinKeepModelWarmIntervalSeconds { get; init; }

    public int MaxAllowedKeepModelWarmIntervalSeconds { get; init; }

    public int? MaxResponseSizeMb { get; init; }

    public int MinMaxResponseSizeMb { get; init; }

    public int MaxAllowedMaxResponseSizeMb { get; init; }

    public string? RecommendedLlamaCppTag { get; init; }

    public int? ChatCacheReuse { get; init; }

    public int MinChatCacheReuse { get; init; }

    public int MaxAllowedChatCacheReuse { get; init; }

    public string? SpeculativeMode { get; init; }

    /// <summary>KV-cache element type for GPU chat spawns: <c>f16</c> | <c>q8_0</c> | <c>q4_0</c>. Null means the node default.</summary>
    public string? KvCacheType { get; init; }

    public string? SpeculativeDraftModelName { get; init; }

    public int? SpeculativeDraftMaxTokens { get; init; }

    public int MinSpeculativeDraftMaxTokens { get; init; }

    public int MaxAllowedSpeculativeDraftMaxTokens { get; init; }

    public int? SpeculativeDraftGpuLayers { get; init; }

    public int MinSpeculativeDraftGpuLayers { get; init; }

    public int MaxAllowedSpeculativeDraftGpuLayers { get; init; }

    /// <summary>
    ///     Installed cross-encoder reranker model name for the knowledge-base search rerank stage.
    ///     <see langword="null" />/blank leaves reranking OFF.
    /// </summary>
    public string? RerankerModelName { get; init; }

    public string? AutoEffortFastModelName { get; init; }

    public long? HuggingFaceDiskMarginBytes { get; init; }

    public int? OrchestrationIdleTimeoutSeconds { get; init; }

    public int MinOrchestrationIdleTimeoutSeconds { get; init; }

    public int MaxAllowedOrchestrationIdleTimeoutSeconds { get; init; }

    public int? AgentHomePrepareTimeoutSeconds { get; init; }

    public int? AgentHomeCommandTimeoutSeconds { get; init; }

    public int MinAgentHomeTimeoutSeconds { get; init; }

    public int MaxAllowedAgentHomeTimeoutSeconds { get; init; }

    public long? AgentHomeMaxSelectedFolderBytes { get; init; }

    public long? AgentHomeMaxPatchBytes { get; init; }

    public int? MaxPendingToolCallAgeMinutes { get; init; }

    public int MinMaxPendingToolCallAgeMinutes { get; init; }

    public int MaxAllowedMaxPendingToolCallAgeMinutes { get; init; }

    /// <summary>Seconds a run with no attached client keeps going before it is cancelled. <c>0</c> never cancels.</summary>
    public int? DetachedGraceSeconds { get; init; }

    public int MinDetachedGraceSeconds { get; init; }

    public int MaxAllowedDetachedGraceSeconds { get; init; }

    /// <summary>Node-level master flag for the client voice feature. <see langword="null" /> reads as off.</summary>
    public bool? VoiceFeatureEnabled { get; init; }

    /// <summary>Preferred browser voice identifier; unmatched legacy values safely fall back to a browser voice.</summary>
    public string? DefaultVoiceProfile { get; init; }

    /// <summary>
    ///     Operator override of usage cost rates, keyed by model NAME → its USD-per-1M input/output rate. Flattened from
    ///     the stored <see cref="NodeUsageRateSettings" />. <see langword="null" /> means no override (the usage-summary
    ///     cost estimate falls back to the built-in default rate table). Local runtimes are always free regardless.
    /// </summary>
    public IReadOnlyDictionary<string, ModelRate>? UsageRates { get; init; }
}

/// <summary>
///     Body for <c>PUT api/local/v1/node-settings</c>. EVERY field is OPTIONAL — including the chat timeout: a
///     <see langword="null" /> request field keeps the current stored value (the mapper merges into the loaded
///     <see cref="StoredNodeSettings" />). Provided values are validated at the boundary by
///     <see cref="NodeSettingsEndpointValidators" /> (ranges, URL format, tag format, array element constraints).
/// </summary>
public sealed record SaveNodeSettingsRequest
{
    public int? MaxMessageRequestTimeoutSeconds { get; init; }

    public string? DefaultModelName { get; init; }

    public bool? EnableTools { get; init; }

    /// <summary>
    ///     Node kill-switch for the user-defined custom tools feature. <see langword="null" /> keeps the current stored
    ///     value. DANGER: enabling this allows agents to run user-defined tools that execute host commands, launch
    ///     programs, and make network requests. Off by default; each call still requires operator approval per the
    ///     per-agent allow-list and the forced per-call approval gate.
    /// </summary>
    public bool? CustomToolsEnabled { get; init; }

    public IReadOnlyList<string>? ToolCapableModels { get; init; }

    public string? OllamaEndpoint { get; init; }

    public string? HuggingFaceDefaultQuant { get; init; }

    public int? LlamaMaxLoadedProcesses { get; init; }

    public int? LlamaIdleTimeToLiveSeconds { get; init; }

    public bool? KeepModelWarmEnabled { get; init; }

    public string? KeepModelWarmModelName { get; init; }

    public int? KeepModelWarmIntervalSeconds { get; init; }

    public int? MaxResponseSizeMb { get; init; }

    public string? RecommendedLlamaCppTag { get; init; }

    public int? ChatCacheReuse { get; init; }

    public string? SpeculativeMode { get; init; }

    /// <summary>
    ///     KV-cache element type for GPU chat spawns: <c>f16</c> | <c>q8_0</c> | <c>q4_0</c>. Changing it invalidates
    ///     every frozen inference profile on this node.
    /// </summary>
    public string? KvCacheType { get; init; }

    public string? SpeculativeDraftModelName { get; init; }

    public int? SpeculativeDraftMaxTokens { get; init; }

    public int? SpeculativeDraftGpuLayers { get; init; }

    /// <summary>Installed cross-encoder reranker model name for knowledge-base search rerank. Empty/blank disables reranking.</summary>
    public string? RerankerModelName { get; init; }

    public string? AutoEffortFastModelName { get; init; }

    public long? HuggingFaceDiskMarginBytes { get; init; }

    public int? OrchestrationIdleTimeoutSeconds { get; init; }

    public int? AgentHomePrepareTimeoutSeconds { get; init; }

    public int? AgentHomeCommandTimeoutSeconds { get; init; }

    public long? AgentHomeMaxSelectedFolderBytes { get; init; }

    public long? AgentHomeMaxPatchBytes { get; init; }

    public int? MaxPendingToolCallAgeMinutes { get; init; }

    /// <summary>Seconds a run with no attached client keeps going before it is cancelled. <c>0</c> never cancels.</summary>
    public int? DetachedGraceSeconds { get; init; }

    public bool? VoiceFeatureEnabled { get; init; }

    public string? DefaultVoiceProfile { get; init; }

    /// <summary>
    ///     Operator override of usage cost rates, keyed by model NAME → its USD-per-1M input/output rate. <see langword="null" />
    ///     keeps the currently stored override; a supplied map REPLACES it (an empty map clears the override — the store's
    ///     <c>Normalize</c> collapses it to null). Negative / non-finite rates are rejected at the boundary with a 400.
    /// </summary>
    public IReadOnlyDictionary<string, ModelRate>? UsageRates { get; init; }
}
