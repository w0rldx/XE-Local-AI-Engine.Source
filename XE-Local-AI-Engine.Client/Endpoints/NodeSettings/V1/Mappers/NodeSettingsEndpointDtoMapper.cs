namespace XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1.Mappers;

using XE_Local_AI_Engine.Client.Services.Containers;
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
            ToolRelevanceEnabled = settings.ToolRelevanceEnabled,
            ExternalAccessProfile = settings.ExternalAccessProfile,
            UiMode = settings.UiMode,
            UpdateChannel = settings.UpdateChannel,
            AutoCheckApplicationUpdates = settings.AutoCheckApplicationUpdates,
            AutoCheckRuntimeUpdates = settings.AutoCheckRuntimeUpdates,
            AutoProvisionFirstRunModel = settings.AutoProvisionFirstRunModel,
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
            // The meeting point: the store holds a lower-case string, the engine holds the enum, and this pair is the
            // only conversion. An unparseable stored value reads back as the default rather than crossing the wire.
            ContainerRuntimeSelection = ContainerRuntimeSelectionParser.Format(ContainerRuntimeSelectionParser.TryParse(settings.ContainerRuntimeSelection, out var runtimeSelection)
                ? runtimeSelection
                : ContainerRuntimeSelection.Auto),
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
    ///     Merges the request into the current stored settings: an optional field that is <see langword="null" /> in
    ///     the request keeps its current stored value, mirroring the <c>DefaultModelName</c> merge.
    /// </summary>
    /// <remarks>
    ///     The store's <c>Normalize</c> then range-clamps every field on save, so the boundary validator and this merge
    ///     together keep a settings change additive.
    /// </remarks>
    public static StoredNodeSettings ToStoredSettings(this SaveNodeSettingsRequest request, StoredNodeSettings currentSettings)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(currentSettings);

        // The four external-access members are resolved TOGETHER; see ApplyExternalAccess for why they cannot be
        // assigned independently.
        var (externalAccessProfile, autoCheckApplicationUpdates, autoCheckRuntimeUpdates, autoProvisionFirstRunModel) =
            ApplyExternalAccess(request, currentSettings);

        return new StoredNodeSettings
        {
            MaxMessageRequestTimeoutSeconds = request.MaxMessageRequestTimeoutSeconds ?? currentSettings.MaxMessageRequestTimeoutSeconds,
            DefaultModelName = request.DefaultModelName is null
                ? currentSettings.DefaultModelName
                : request.DefaultModelName.Trim(),
            EnableTools = request.EnableTools ?? currentSettings.EnableTools,
            CustomToolsEnabled = request.CustomToolsEnabled ?? currentSettings.CustomToolsEnabled,
            ToolRelevanceEnabled = request.ToolRelevanceEnabled ?? currentSettings.ToolRelevanceEnabled,
            ExternalAccessProfile = externalAccessProfile,
            // Plain null-preserving merge, unlike the external-access block above: the mode couples to no other field, so it needs no joint resolution. The validator has
            // already proven a supplied value is one of the two literals; the store's Normalize trims and re-checks it.
            UiMode = request.UiMode is null
                ? currentSettings.UiMode
                : request.UiMode.Trim(),
            UpdateChannel = request.UpdateChannel is null
                ? currentSettings.UpdateChannel
                : request.UpdateChannel.Trim(),
            AutoCheckApplicationUpdates = autoCheckApplicationUpdates,
            AutoCheckRuntimeUpdates = autoCheckRuntimeUpdates,
            AutoProvisionFirstRunModel = autoProvisionFirstRunModel,
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
            // The validator has already proven a supplied value parses; the `else` is written rather than banged so a
            // future caller that skips the validator keeps the stored value instead of persisting junk.
            ContainerRuntimeSelection =
                request.ContainerRuntimeSelection is null
                || !ContainerRuntimeSelectionParser.TryParse(request.ContainerRuntimeSelection, out var requestedRuntimeSelection)
                    ? currentSettings.ContainerRuntimeSelection
                    : ContainerRuntimeSelectionParser.Format(requestedRuntimeSelection),
            SpeculativeDraftModelName = request.SpeculativeDraftModelName is null
                ? currentSettings.SpeculativeDraftModelName
                : request.SpeculativeDraftModelName.Trim(),
            SpeculativeDraftMaxTokens = request.SpeculativeDraftMaxTokens ?? currentSettings.SpeculativeDraftMaxTokens,
            SpeculativeDraftGpuLayers = request.SpeculativeDraftGpuLayers ?? currentSettings.SpeculativeDraftGpuLayers,
            // Optional string, mirroring OllamaEndpoint/DefaultModelName: a null request field keeps the current value, and a supplied value (including the empty string the
            // "Off" option sends) is trimmed, with the store's Normalize mapping blank to null — reranking disabled.
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
            // The node-default tool-approval policy has no editable field on this request, so the currently stored value is preserved: an unrelated node-settings save must
            // never wipe it.
            ToolApprovalPolicy = currentSettings.ToolApprovalPolicy,
            VoiceFeatureEnabled = request.VoiceFeatureEnabled ?? currentSettings.VoiceFeatureEnabled,
            DefaultVoiceProfile = request.DefaultVoiceProfile is null
                ? currentSettings.DefaultVoiceProfile
                : request.DefaultVoiceProfile.Trim(),
            // Null-preserving: a null request map keeps the currently stored override, and a supplied map (wrapped back into the stored shape) REPLACES it. The store's
            // Normalize then trims keys and drops negative or non-finite entries, collapsing an empty or all-junk map to null — no override.
            UsageRates = request.UsageRates is null
                ? currentSettings.UsageRates
                : new NodeUsageRateSettings
                {
                    Models = request.UsageRates
                }
        };
    }

    /// <summary>
    ///     The ONE owner of the external-access stamp: the four members are returned together because the profile is a
    ///     record of which preset is in force, so it can only be decided alongside the triple it describes.
    /// </summary>
    /// <remarks>
    ///     A request carrying a PRESET (<c>recommended</c> or <c>offline</c>; the validator rejected anything else)
    ///     writes that literal and that preset's triple, ignoring any switch sent with it — one bool drives all three,
    ///     so the presets cannot drift apart. A request carrying at least one switch writes those switches, each
    ///     falling back to the stored value, and stamps <c>custom</c> UNCONDITIONALLY, never re-derived: the stamp
    ///     records that the operator edited switches. Any other save preserves all four, disturbing no decided node.
    /// </remarks>
    private static (string? Profile, bool? ApplicationUpdates, bool? RuntimeUpdates, bool? FirstRunModel) ApplyExternalAccess(SaveNodeSettingsRequest request,
        StoredNodeSettings currentSettings)
    {
        if (StoredNodeSettings.IsExternalAccessPreset(request.ExternalAccessProfile))
        {
            var enabled = string.Equals(request.ExternalAccessProfile, StoredNodeSettings.ExternalAccessProfileRecommended, StringComparison.Ordinal);
            return (request.ExternalAccessProfile, enabled, enabled, enabled);
        }

        if (request.AutoCheckApplicationUpdates is null
            && request.AutoCheckRuntimeUpdates is null
            && request.AutoProvisionFirstRunModel is null)
        {
            return (currentSettings.ExternalAccessProfile,
                currentSettings.AutoCheckApplicationUpdates,
                currentSettings.AutoCheckRuntimeUpdates,
                currentSettings.AutoProvisionFirstRunModel);
        }

        return (StoredNodeSettings.ExternalAccessProfileCustom,
            request.AutoCheckApplicationUpdates ?? currentSettings.AutoCheckApplicationUpdates,
            request.AutoCheckRuntimeUpdates ?? currentSettings.AutoCheckRuntimeUpdates,
            request.AutoProvisionFirstRunModel ?? currentSettings.AutoProvisionFirstRunModel);
    }
}
