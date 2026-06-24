namespace XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Response for <c>GET api/local/v1/node-settings</c>. Surfaces the effective stored values for every user-editable
///     node setting plus the per-field bounds the React form renders ranges from. Fields are grouped: the original chat
///     timeout, then the "general" migrated knobs (always shown), then the developer-only advanced knobs (the React lane
///     gates their visibility; the backend always returns them).
/// </summary>
public sealed record NodeSettingsResponse
{
    // ── Chat request timeout (original) ──
    public int MaxMessageRequestTimeoutSeconds { get; init; }

    public int MinMessageRequestTimeoutSeconds { get; init; }

    public int MaxAllowedMessageRequestTimeoutSeconds { get; init; }

    // ── General (always shown) ──
    public string? DefaultModelName { get; init; }

    public bool? EnableTools { get; init; }

    public IReadOnlyList<string>? ToolCapableModels { get; init; }

    public string? OllamaEndpoint { get; init; }

    public string? HuggingFaceDefaultQuant { get; init; }

    public int? LlamaMaxLoadedProcesses { get; init; }

    public int MinLlamaMaxLoadedProcesses { get; init; }

    public int MaxAllowedLlamaMaxLoadedProcesses { get; init; }

    public int? LlamaIdleTimeToLiveSeconds { get; init; }

    public int MinLlamaIdleTimeToLiveSeconds { get; init; }

    public int MaxAllowedLlamaIdleTimeToLiveSeconds { get; init; }

    public int? MaxResponseSizeMb { get; init; }

    public int MinMaxResponseSizeMb { get; init; }

    public int MaxAllowedMaxResponseSizeMb { get; init; }

    public string? RecommendedLlamaCppTag { get; init; }

    // ── Advanced / developer-only ──
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

    public SamplingOptions? SamplingDefaults { get; init; }
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

    // ── General (always shown) ──
    public string? DefaultModelName { get; init; }

    public bool? EnableTools { get; init; }

    public IReadOnlyList<string>? ToolCapableModels { get; init; }

    public string? OllamaEndpoint { get; init; }

    public string? HuggingFaceDefaultQuant { get; init; }

    public int? LlamaMaxLoadedProcesses { get; init; }

    public int? LlamaIdleTimeToLiveSeconds { get; init; }

    public int? MaxResponseSizeMb { get; init; }

    public string? RecommendedLlamaCppTag { get; init; }

    // ── Advanced / developer-only ──
    public long? HuggingFaceDiskMarginBytes { get; init; }

    public int? OrchestrationIdleTimeoutSeconds { get; init; }

    public int? AgentHomePrepareTimeoutSeconds { get; init; }

    public int? AgentHomeCommandTimeoutSeconds { get; init; }

    public long? AgentHomeMaxSelectedFolderBytes { get; init; }

    public long? AgentHomeMaxPatchBytes { get; init; }

    public int? MaxPendingToolCallAgeMinutes { get; init; }

    public SamplingOptions? SamplingDefaults { get; init; }
}

internal static class NodeSettingsEndpointDtoMapper
{
    public static NodeSettingsResponse ToResponse(this StoredNodeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new NodeSettingsResponse
        {
            MaxMessageRequestTimeoutSeconds = settings.MaxMessageRequestTimeoutSeconds,
            MinMessageRequestTimeoutSeconds = StoredNodeSettings.MinMaxMessageRequestTimeoutSeconds,
            MaxAllowedMessageRequestTimeoutSeconds = StoredNodeSettings.MaxMaxMessageRequestTimeoutSeconds,
            DefaultModelName = settings.DefaultModelName,
            EnableTools = settings.EnableTools,
            ToolCapableModels = settings.ToolCapableModels,
            OllamaEndpoint = settings.OllamaEndpoint,
            HuggingFaceDefaultQuant = settings.HuggingFaceDefaultQuant,
            LlamaMaxLoadedProcesses = settings.LlamaMaxLoadedProcesses,
            MinLlamaMaxLoadedProcesses = StoredNodeSettings.MinLlamaMaxLoadedProcesses,
            MaxAllowedLlamaMaxLoadedProcesses = StoredNodeSettings.MaxLlamaMaxLoadedProcesses,
            LlamaIdleTimeToLiveSeconds = settings.LlamaIdleTimeToLiveSeconds,
            MinLlamaIdleTimeToLiveSeconds = StoredNodeSettings.MinLlamaIdleTimeToLiveSeconds,
            MaxAllowedLlamaIdleTimeToLiveSeconds = StoredNodeSettings.MaxLlamaIdleTimeToLiveSeconds,
            MaxResponseSizeMb = settings.MaxResponseSizeMb,
            MinMaxResponseSizeMb = StoredNodeSettings.MinMaxResponseSizeMb,
            MaxAllowedMaxResponseSizeMb = StoredNodeSettings.MaxMaxResponseSizeMb,
            RecommendedLlamaCppTag = settings.RecommendedLlamaCppTag,
            HuggingFaceDiskMarginBytes = settings.HuggingFaceDiskMarginBytes,
            OrchestrationIdleTimeoutSeconds = settings.OrchestrationIdleTimeoutSeconds,
            MinOrchestrationIdleTimeoutSeconds = StoredNodeSettings.MinOrchestrationIdleTimeoutSeconds,
            MaxAllowedOrchestrationIdleTimeoutSeconds = StoredNodeSettings.MaxOrchestrationIdleTimeoutSeconds,
            AgentHomePrepareTimeoutSeconds = settings.AgentHomePrepareTimeoutSeconds,
            AgentHomeCommandTimeoutSeconds = settings.AgentHomeCommandTimeoutSeconds,
            MinAgentHomeTimeoutSeconds = StoredNodeSettings.MinAgentHomeTimeoutSeconds,
            MaxAllowedAgentHomeTimeoutSeconds = StoredNodeSettings.MaxAgentHomeTimeoutSeconds,
            AgentHomeMaxSelectedFolderBytes = settings.AgentHomeMaxSelectedFolderBytes,
            AgentHomeMaxPatchBytes = settings.AgentHomeMaxPatchBytes,
            MaxPendingToolCallAgeMinutes = settings.MaxPendingToolCallAgeMinutes,
            MinMaxPendingToolCallAgeMinutes = StoredNodeSettings.MinMaxPendingToolCallAgeMinutes,
            MaxAllowedMaxPendingToolCallAgeMinutes = StoredNodeSettings.MaxMaxPendingToolCallAgeMinutes,
            SamplingDefaults = settings.SamplingDefaults
        };
    }

    /// <summary>
    ///     Merges the request into the current stored settings: each optional field that is <see langword="null" /> in the
    ///     request keeps its current stored value (mirrors the original <c>DefaultModelName</c> merge). The store's
    ///     <c>Normalize</c> then range-clamps every field on save, so the boundary validator + this merge keep behavior
    ///     additive and backward-compatible.
    /// </summary>
    public static StoredNodeSettings ToStoredSettings(this SaveNodeSettingsRequest request, StoredNodeSettings currentSettings)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(currentSettings);

        return new StoredNodeSettings
        {
            MaxMessageRequestTimeoutSeconds = request.MaxMessageRequestTimeoutSeconds ?? currentSettings.MaxMessageRequestTimeoutSeconds,
            DefaultModelName = request.DefaultModelName is null
                ? currentSettings.DefaultModelName
                : request.DefaultModelName.Trim(),
            EnableTools = request.EnableTools ?? currentSettings.EnableTools,
            ToolCapableModels = request.ToolCapableModels ?? currentSettings.ToolCapableModels,
            OllamaEndpoint = request.OllamaEndpoint is null
                ? currentSettings.OllamaEndpoint
                : request.OllamaEndpoint.Trim(),
            HuggingFaceDefaultQuant = request.HuggingFaceDefaultQuant is null
                ? currentSettings.HuggingFaceDefaultQuant
                : request.HuggingFaceDefaultQuant.Trim(),
            HuggingFaceDiskMarginBytes = request.HuggingFaceDiskMarginBytes ?? currentSettings.HuggingFaceDiskMarginBytes,
            LlamaMaxLoadedProcesses = request.LlamaMaxLoadedProcesses ?? currentSettings.LlamaMaxLoadedProcesses,
            LlamaIdleTimeToLiveSeconds = request.LlamaIdleTimeToLiveSeconds ?? currentSettings.LlamaIdleTimeToLiveSeconds,
            MaxResponseSizeMb = request.MaxResponseSizeMb ?? currentSettings.MaxResponseSizeMb,
            RecommendedLlamaCppTag = request.RecommendedLlamaCppTag is null
                ? currentSettings.RecommendedLlamaCppTag
                : request.RecommendedLlamaCppTag.Trim(),
            OrchestrationIdleTimeoutSeconds = request.OrchestrationIdleTimeoutSeconds ?? currentSettings.OrchestrationIdleTimeoutSeconds,
            AgentHomePrepareTimeoutSeconds = request.AgentHomePrepareTimeoutSeconds ?? currentSettings.AgentHomePrepareTimeoutSeconds,
            AgentHomeCommandTimeoutSeconds = request.AgentHomeCommandTimeoutSeconds ?? currentSettings.AgentHomeCommandTimeoutSeconds,
            AgentHomeMaxSelectedFolderBytes = request.AgentHomeMaxSelectedFolderBytes ?? currentSettings.AgentHomeMaxSelectedFolderBytes,
            AgentHomeMaxPatchBytes = request.AgentHomeMaxPatchBytes ?? currentSettings.AgentHomeMaxPatchBytes,
            MaxPendingToolCallAgeMinutes = request.MaxPendingToolCallAgeMinutes ?? currentSettings.MaxPendingToolCallAgeMinutes,
            SamplingDefaults = request.SamplingDefaults ?? currentSettings.SamplingDefaults
        };
    }
}
