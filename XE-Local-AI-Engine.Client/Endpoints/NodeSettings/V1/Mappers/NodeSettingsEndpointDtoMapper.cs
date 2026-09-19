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
    ///     Merges the request into the current stored settings: each optional field that is <see langword="null" /> in the
    ///     request keeps its current stored value (mirrors the original <c>DefaultModelName</c> merge). The store's
    ///     <c>Normalize</c> then range-clamps every field on save, so the boundary validator + this merge keep behavior
    ///     additive and backward-compatible.
    /// </summary>
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
            // Plain null-preserving merge, unlike the external-access block above: the mode couples to no other field,
            // so it needs no joint resolution. The validator has already proven a supplied value is one of the two
            // literals; the store's Normalize trims and re-checks it.
            UiMode = request.UiMode is null
                ? currentSettings.UiMode
                : request.UiMode.Trim(),
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

    /// <summary>
    ///     The ONE owner of the external-access stamp. The four members are returned together because assigning them
    ///     independently is exactly the two-owner bug this replaces: the profile is a record of which preset is in force,
    ///     so it can only be decided alongside the triple it describes.
    ///     <list type="number">
    ///         <item>
    ///             The request carries a PRESET (<c>recommended</c> or <c>offline</c> — the validator has already
    ///             rejected anything else): write that literal and that preset's triple, ignoring any switch sent in the
    ///             same request. One bool drives all three, so the two presets cannot drift apart.
    ///         </item>
    ///         <item>
    ///             Otherwise, the request carries at least one switch: write the supplied switches (each falling back to
    ///             the stored value) and stamp <c>custom</c> UNCONDITIONALLY. No re-derivation — a node at <c>custom</c>
    ///             with (true, true, false) whose owner flips the third back on stays <c>custom</c>, because the stamp
    ///             records that the operator edited switches, not that the values happen to match a preset today.
    ///         </item>
    ///         <item>
    ///             Otherwise the save touches no external-access member (every other setting's save): preserve all four,
    ///             so an unrelated save never disturbs a decided node.
    ///         </item>
    ///     </list>
    /// </summary>
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
