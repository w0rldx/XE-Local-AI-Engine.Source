namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Transport-neutral node-settings administration. The agentic patch and view deliberately contain only the
///     approved core fields; trusted HTTP callers use the separately named full-settings methods.
/// </summary>
public interface INodeSettingsAdministrationService
{
    Task<StoredNodeSettings> GetTrustedSettingsAsync(CancellationToken cancellationToken = default);

    Task<NodeSettingsAgenticView> GetAgenticViewAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Saves a trusted whole-record merge. <paramref name="merge" /> is the caller's merge, not its result: it is
    ///     re-applied to the record the store holds AT WRITE TIME, because the HTTP save is a partial merge in
    ///     disguise — every field the request omits is resolved from the record handed in here, so resolving them from
    ///     a snapshot loaded before validation discards whatever a sibling writer stored in that window.
    /// </summary>
    Task<NodeSettingsAdministrationResult> SaveTrustedMergedAsync(Func<StoredNodeSettings, StoredNodeSettings> merge,
        CancellationToken cancellationToken = default);

    Task<NodeSettingsAdministrationResult> ApplyAgenticPatchAsync(NodeSettingsAgenticPatch patch,
        CancellationToken cancellationToken = default);
}

public sealed record NodeSettingsAgenticPatch
{
    public string? DefaultModelName { get; init; }
    public bool? EnableTools { get; init; }
    public IReadOnlyList<string>? ToolCapableModels { get; init; }
    public string? HuggingFaceDefaultQuant { get; init; }
    public int? LlamaMaxLoadedProcesses { get; init; }
    public int? LlamaIdleTimeToLiveSeconds { get; init; }
    public bool? KeepModelWarmEnabled { get; init; }
    public string? KeepModelWarmModelName { get; init; }
    public int? KeepModelWarmIntervalSeconds { get; init; }
    public int? MaxMessageRequestTimeoutSeconds { get; init; }
    public int? ChatCacheReuse { get; init; }
    public string? SpeculativeMode { get; init; }
    public string? SpeculativeDraftModelName { get; init; }
    public int? SpeculativeDraftMaxTokens { get; init; }
    public int? SpeculativeDraftGpuLayers { get; init; }
    public string? KvCacheType { get; init; }
    public string? RerankerModelName { get; init; }
    public string? AutoEffortFastModelName { get; init; }
}

public sealed record NodeSettingsAgenticView(
    string? DefaultModelName,
    bool? EnableTools,
    IReadOnlyList<string>? ToolCapableModels,
    string? HuggingFaceDefaultQuant,
    int? LlamaMaxLoadedProcesses,
    int? LlamaIdleTimeToLiveSeconds,
    bool? KeepModelWarmEnabled,
    string? KeepModelWarmModelName,
    int? KeepModelWarmIntervalSeconds,
    int MaxMessageRequestTimeoutSeconds,
    int? ChatCacheReuse,
    string? SpeculativeMode,
    string? SpeculativeDraftModelName,
    int? SpeculativeDraftMaxTokens,
    int? SpeculativeDraftGpuLayers,
    string? KvCacheType,
    string? RerankerModelName,
    string? AutoEffortFastModelName);

public sealed record NodeSettingsAdministrationResult(
    bool Updated,
    StoredNodeSettings Settings,
    IReadOnlyList<NodeSettingsValidationError> ValidationErrors)
{
    /// <summary>
    ///     Whether the save was refused because the stored record kept changing under it, as opposed to being
    ///     rejected by validation. Both read as <see cref="Updated" /> <see langword="false" />, but a conflict carries
    ///     no validation errors and nothing the caller sent was wrong: the same request retried usually succeeds.
    /// </summary>
    public bool Conflicted { get; init; }

    public static NodeSettingsAdministrationResult Saved(StoredNodeSettings settings) =>
        new(true, settings, []);

    public static NodeSettingsAdministrationResult Rejected(StoredNodeSettings settings,
        IReadOnlyList<NodeSettingsValidationError> errors) =>
        new(false, settings, errors);

    /// <summary>The save was abandoned unwritten after every attempt found the record changed. Named
    ///     <c>Conflict</c> rather than <c>Conflicted</c> only because a type cannot hold a method and a property of
    ///     the same name.</summary>
    public static NodeSettingsAdministrationResult Conflict(StoredNodeSettings latest) =>
        new(false, latest, [])
        {
            Conflicted = true
        };
}

/// <summary>Transport-neutral field validation for the restricted agentic settings patch.</summary>
public static class NodeSettingsAgenticPatchValidation
{
    public static IReadOnlyList<NodeSettingsValidationError> Validate(NodeSettingsAgenticPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);

        if (patch.ToolCapableModels is { } models
            && (models.Count == 0 || models.Any(static model => string.IsNullOrWhiteSpace(model))))
        {
            return Reject(NodeSettingsField.ToolCapableModels,
                "Tool-capable models must be a non-empty list of non-blank model names.");
        }

        if (patch.MaxMessageRequestTimeoutSeconds is { } timeout
            && !IsBetween(timeout, StoredNodeSettings.MinMaxMessageRequestTimeoutSeconds, StoredNodeSettings.MaxMaxMessageRequestTimeoutSeconds))
        {
            return Reject(NodeSettingsField.MaxMessageRequestTimeoutSeconds, "Maximum message request timeout is outside the supported range.");
        }

        if (patch.LlamaMaxLoadedProcesses is { } maxLoaded
            && !IsBetween(maxLoaded, StoredNodeSettings.MinLlamaMaxLoadedProcesses, StoredNodeSettings.MaxLlamaMaxLoadedProcesses))
        {
            return Reject(NodeSettingsField.LlamaMaxLoadedProcesses, "Maximum loaded llama.cpp processes is outside the supported range.");
        }

        if (patch.LlamaIdleTimeToLiveSeconds is { } idleSeconds
            && !IsBetween(idleSeconds, StoredNodeSettings.MinLlamaIdleTimeToLiveSeconds, StoredNodeSettings.MaxLlamaIdleTimeToLiveSeconds))
        {
            return Reject(NodeSettingsField.LlamaIdleTimeToLiveSeconds, "llama.cpp idle time-to-live is outside the supported range.");
        }

        if (patch.KeepModelWarmIntervalSeconds is { } warmInterval
            && !IsBetween(warmInterval, StoredNodeSettings.MinKeepModelWarmIntervalSeconds, StoredNodeSettings.MaxKeepModelWarmIntervalSeconds))
        {
            return Reject(NodeSettingsField.KeepModelWarmIntervalSeconds, "Keep-model-warm interval is outside the supported range.");
        }

        if (patch.ChatCacheReuse is { } cacheReuse
            && !IsBetween(cacheReuse, StoredNodeSettings.MinChatCacheReuse, StoredNodeSettings.MaxChatCacheReuse))
        {
            return Reject(NodeSettingsField.ChatCacheReuse, "Chat cache reuse is outside the supported range.");
        }

        if (!string.IsNullOrWhiteSpace(patch.SpeculativeMode)
            && !StoredNodeSettings.IsValidSpeculativeMode(patch.SpeculativeMode))
        {
            return Reject(NodeSettingsField.SpeculativeMode, "Unknown speculative decoding mode.");
        }

        if (patch.SpeculativeDraftMaxTokens is { } draftTokens
            && !IsBetween(draftTokens, StoredNodeSettings.MinSpeculativeDraftMaxTokens, StoredNodeSettings.MaxSpeculativeDraftMaxTokens))
        {
            return Reject(NodeSettingsField.SpeculativeDraftMaxTokens, "Speculative draft tokens is outside the supported range.");
        }

        if (!string.IsNullOrWhiteSpace(patch.KvCacheType)
            && !StoredNodeSettings.IsValidKvCacheType(patch.KvCacheType))
        {
            return Reject(NodeSettingsField.KvCacheType, "Unknown KV cache type.");
        }

        if (patch.SpeculativeDraftGpuLayers is { } draftLayers
            && !IsBetween(draftLayers, StoredNodeSettings.MinSpeculativeDraftGpuLayers, StoredNodeSettings.MaxSpeculativeDraftGpuLayers))
        {
            return Reject(NodeSettingsField.SpeculativeDraftGpuLayers, "Speculative draft GPU layers is outside the supported range.");
        }

        return [];
    }

    private static bool IsBetween(int value, int minimum, int maximum) =>
        value >= minimum && value <= maximum;

    private static IReadOnlyList<NodeSettingsValidationError> Reject(NodeSettingsField field, string message) =>
        [new NodeSettingsValidationError(field, message)];
}
