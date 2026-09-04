namespace XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1.Mappers;

using XE_Local_AI_Engine.Client.Services.NodeSettings;

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
            CustomToolsEnabled = settings.CustomToolsEnabled,
            ToolCapableModels = settings.ToolCapableModels,
            OllamaEndpoint = settings.OllamaEndpoint,
            HuggingFaceDefaultQuant = settings.HuggingFaceDefaultQuant,
            LlamaMaxLoadedProcesses = settings.LlamaMaxLoadedProcesses,
            MinLlamaMaxLoadedProcesses = StoredNodeSettings.MinLlamaMaxLoadedProcesses,
            MaxAllowedLlamaMaxLoadedProcesses = StoredNodeSettings.MaxLlamaMaxLoadedProcesses,
            LlamaIdleTimeToLiveSeconds = settings.LlamaIdleTimeToLiveSeconds,
            MinLlamaIdleTimeToLiveSeconds = StoredNodeSettings.MinLlamaIdleTimeToLiveSeconds,
            MaxAllowedLlamaIdleTimeToLiveSeconds = StoredNodeSettings.MaxLlamaIdleTimeToLiveSeconds,
            KeepModelWarmEnabled = settings.KeepModelWarmEnabled,
            KeepModelWarmModelName = settings.KeepModelWarmModelName,
            KeepModelWarmIntervalSeconds = settings.KeepModelWarmIntervalSeconds,
            MinKeepModelWarmIntervalSeconds = StoredNodeSettings.MinKeepModelWarmIntervalSeconds,
            MaxAllowedKeepModelWarmIntervalSeconds = StoredNodeSettings.MaxKeepModelWarmIntervalSeconds,
            MaxResponseSizeMb = settings.MaxResponseSizeMb,
            MinMaxResponseSizeMb = StoredNodeSettings.MinMaxResponseSizeMb,
            MaxAllowedMaxResponseSizeMb = StoredNodeSettings.MaxMaxResponseSizeMb,
            RecommendedLlamaCppTag = settings.RecommendedLlamaCppTag,
            ChatCacheReuse = settings.ChatCacheReuse,
            MinChatCacheReuse = StoredNodeSettings.MinChatCacheReuse,
            MaxAllowedChatCacheReuse = StoredNodeSettings.MaxChatCacheReuse,
            SpeculativeMode = settings.SpeculativeMode,
            KvCacheType = settings.KvCacheType,
            SpeculativeDraftModelName = settings.SpeculativeDraftModelName,
            SpeculativeDraftMaxTokens = settings.SpeculativeDraftMaxTokens,
            MinSpeculativeDraftMaxTokens = StoredNodeSettings.MinSpeculativeDraftMaxTokens,
            MaxAllowedSpeculativeDraftMaxTokens = StoredNodeSettings.MaxSpeculativeDraftMaxTokens,
            SpeculativeDraftGpuLayers = settings.SpeculativeDraftGpuLayers,
            MinSpeculativeDraftGpuLayers = StoredNodeSettings.MinSpeculativeDraftGpuLayers,
            MaxAllowedSpeculativeDraftGpuLayers = StoredNodeSettings.MaxSpeculativeDraftGpuLayers,
            RerankerModelName = settings.RerankerModelName,
            AutoEffortFastModelName = settings.AutoEffortFastModelName,
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
            DetachedGraceSeconds = settings.DetachedGraceSeconds,
            MinDetachedGraceSeconds = StoredNodeSettings.MinDetachedGraceSeconds,
            MaxAllowedDetachedGraceSeconds = StoredNodeSettings.MaxDetachedGraceSeconds,
            VoiceFeatureEnabled = settings.VoiceFeatureEnabled,
            DefaultVoiceProfile = settings.DefaultVoiceProfile,
            // Flatten the stored wrapper to the map the React rate editor renders (null wrapper → null map).
            UsageRates = settings.UsageRates?.Models
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
            CustomToolsEnabled = request.CustomToolsEnabled ?? currentSettings.CustomToolsEnabled,
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
            KeepModelWarmEnabled = request.KeepModelWarmEnabled ?? currentSettings.KeepModelWarmEnabled,
            KeepModelWarmModelName = request.KeepModelWarmModelName is null
                ? currentSettings.KeepModelWarmModelName
                : request.KeepModelWarmModelName.Trim(),
            KeepModelWarmIntervalSeconds = request.KeepModelWarmIntervalSeconds ?? currentSettings.KeepModelWarmIntervalSeconds,
            MaxResponseSizeMb = request.MaxResponseSizeMb ?? currentSettings.MaxResponseSizeMb,
            RecommendedLlamaCppTag = request.RecommendedLlamaCppTag is null
                ? currentSettings.RecommendedLlamaCppTag
                : request.RecommendedLlamaCppTag.Trim(),
            ChatCacheReuse = request.ChatCacheReuse ?? currentSettings.ChatCacheReuse,
            SpeculativeMode = request.SpeculativeMode is null
                ? currentSettings.SpeculativeMode
                : request.SpeculativeMode.Trim(),
            KvCacheType = request.KvCacheType is null
                ? currentSettings.KvCacheType
                : request.KvCacheType.Trim(),
            SpeculativeDraftModelName = request.SpeculativeDraftModelName is null
                ? currentSettings.SpeculativeDraftModelName
                : request.SpeculativeDraftModelName.Trim(),
            SpeculativeDraftMaxTokens = request.SpeculativeDraftMaxTokens ?? currentSettings.SpeculativeDraftMaxTokens,
            SpeculativeDraftGpuLayers = request.SpeculativeDraftGpuLayers ?? currentSettings.SpeculativeDraftGpuLayers,
            // Optional string, mirroring OllamaEndpoint/DefaultModelName: a null request field keeps the current value; a
            // supplied value (including an empty string from the "Off" option) is trimmed, and the store's Normalize maps
            // blank to null (reranking disabled).
            RerankerModelName = request.RerankerModelName is null
                ? currentSettings.RerankerModelName
                : request.RerankerModelName.Trim(),
            AutoEffortFastModelName = request.AutoEffortFastModelName is null
                ? currentSettings.AutoEffortFastModelName
                : request.AutoEffortFastModelName.Trim(),
            OrchestrationIdleTimeoutSeconds = request.OrchestrationIdleTimeoutSeconds ?? currentSettings.OrchestrationIdleTimeoutSeconds,
            AgentHomePrepareTimeoutSeconds = request.AgentHomePrepareTimeoutSeconds ?? currentSettings.AgentHomePrepareTimeoutSeconds,
            AgentHomeCommandTimeoutSeconds = request.AgentHomeCommandTimeoutSeconds ?? currentSettings.AgentHomeCommandTimeoutSeconds,
            AgentHomeMaxSelectedFolderBytes = request.AgentHomeMaxSelectedFolderBytes ?? currentSettings.AgentHomeMaxSelectedFolderBytes,
            AgentHomeMaxPatchBytes = request.AgentHomeMaxPatchBytes ?? currentSettings.AgentHomeMaxPatchBytes,
            MaxPendingToolCallAgeMinutes = request.MaxPendingToolCallAgeMinutes ?? currentSettings.MaxPendingToolCallAgeMinutes,
            DetachedGraceSeconds = request.DetachedGraceSeconds ?? currentSettings.DetachedGraceSeconds,
            // The node-default tool-approval policy has no editable field on this request yet (the operator
            // surface is planned but not yet built); preserve the currently stored value so an unrelated node-settings
            // save never wipes it.
            ToolApprovalPolicy = currentSettings.ToolApprovalPolicy,
            VoiceFeatureEnabled = request.VoiceFeatureEnabled ?? currentSettings.VoiceFeatureEnabled,
            DefaultVoiceProfile = request.DefaultVoiceProfile is null
                ? currentSettings.DefaultVoiceProfile
                : request.DefaultVoiceProfile.Trim(),
            // Null-preserving: a null request map keeps the currently stored override; a supplied map (wrapped back into
            // the stored shape) REPLACES it. The store's Normalize then trims keys and drops negative/non-finite entries,
            // collapsing an empty/all-junk map to null (no override).
            UsageRates = request.UsageRates is null
                ? currentSettings.UsageRates
                : new NodeUsageRateSettings
                {
                    Models = request.UsageRates
                }
        };
    }
}
