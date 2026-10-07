namespace XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1.Mappers;

using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

internal static class NodeSettingsEndpointDtoMapper
{
    /// <summary>Maps the stored record; the migrated switches and retention windows come from <paramref name="effective" />.</summary>
    public static NodeSettingsResponse ToResponse(this StoredNodeSettings settings, NodeSettingsEffectiveValues effective)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(effective);

        return new NodeSettingsResponse
        {
            MaxMessageRequestTimeoutSeconds = settings.MaxMessageRequestTimeoutSeconds,
            MinMessageRequestTimeoutSeconds = StoredNodeSettings.MinMaxMessageRequestTimeoutSeconds,
            MaxAllowedMessageRequestTimeoutSeconds = StoredNodeSettings.MaxMaxMessageRequestTimeoutSeconds,
            DefaultModelName = settings.DefaultModelName,
            EnableTools = settings.EnableTools,
            CustomToolsEnabled = settings.CustomToolsEnabled,
            ToolRelevanceEnabled = settings.ToolRelevanceEnabled,
            WebAccessEnabled = settings.WebAccessEnabled,
            WebSearchSearxngUrl = settings.WebSearchSearxngUrl,
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
            MinHuggingFaceDiskMarginBytes = StoredNodeSettings.MinHuggingFaceDiskMarginBytes,
            MaxAllowedHuggingFaceDiskMarginBytes = StoredNodeSettings.MaxHuggingFaceDiskMarginBytes,
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
            UsageRates = settings.UsageRates?.Models,
            TranscriptionIdleTimeoutMinutes = settings.TranscriptionIdleTimeoutMinutes,
            MinTranscriptionIdleTimeoutMinutes = StoredNodeSettings.MinTranscriptionIdleTimeoutMinutes,
            MaxAllowedTranscriptionIdleTimeoutMinutes = StoredNodeSettings.MaxTranscriptionIdleTimeoutMinutes,
            LlamaReadinessTimeoutCapSeconds = settings.LlamaReadinessTimeoutCapSeconds,
            MinLlamaReadinessTimeoutCapSeconds = StoredNodeSettings.MinLlamaReadinessTimeoutCapSeconds,
            MaxAllowedLlamaReadinessTimeoutCapSeconds = StoredNodeSettings.MaxLlamaReadinessTimeoutCapSeconds,
            LlamaChatHttpTimeoutSeconds = settings.LlamaChatHttpTimeoutSeconds,
            MinLlamaChatHttpTimeoutSeconds = StoredNodeSettings.MinLlamaChatHttpTimeoutSeconds,
            MaxAllowedLlamaChatHttpTimeoutSeconds = StoredNodeSettings.MaxLlamaChatHttpTimeoutSeconds,
            LlamaEmbeddingHttpTimeoutSeconds = settings.LlamaEmbeddingHttpTimeoutSeconds,
            MinLlamaEmbeddingHttpTimeoutSeconds = StoredNodeSettings.MinLlamaEmbeddingHttpTimeoutSeconds,
            MaxAllowedLlamaEmbeddingHttpTimeoutSeconds = StoredNodeSettings.MaxLlamaEmbeddingHttpTimeoutSeconds,
            LlamaChatCacheRamMiB = settings.LlamaChatCacheRamMiB,
            MinLlamaChatCacheRamMiB = StoredNodeSettings.MinLlamaChatCacheRamMiB,
            MaxAllowedLlamaChatCacheRamMiB = StoredNodeSettings.MaxLlamaChatCacheRamMiB,
            LlamaCpuThreadReserve = settings.LlamaCpuThreadReserve,
            MinLlamaCpuThreadReserve = StoredNodeSettings.MinLlamaCpuThreadReserve,
            MaxAllowedLlamaCpuThreadReserve = StoredNodeSettings.MaxLlamaCpuThreadReserve,
            LlamaGpuReservePercent = settings.LlamaGpuReservePercent,
            MinLlamaGpuReservePercent = StoredNodeSettings.MinLlamaGpuReservePercent,
            MaxAllowedLlamaGpuReservePercent = StoredNodeSettings.MaxLlamaGpuReservePercent,
            LlamaRamReservePercent = settings.LlamaRamReservePercent,
            MinLlamaRamReservePercent = StoredNodeSettings.MinLlamaRamReservePercent,
            MaxAllowedLlamaRamReservePercent = StoredNodeSettings.MaxLlamaRamReservePercent,
            ImageIdleTimeToLiveSeconds = settings.ImageIdleTimeToLiveSeconds,
            MinImageIdleTimeToLiveSeconds = StoredNodeSettings.MinImageIdleTimeToLiveSeconds,
            MaxAllowedImageIdleTimeToLiveSeconds = StoredNodeSettings.MaxImageIdleTimeToLiveSeconds,
            ModelFitSafetyMarginPercent = settings.ModelFitSafetyMarginPercent,
            MinModelFitSafetyMarginPercent = StoredNodeSettings.MinModelFitSafetyMarginPercent,
            MaxAllowedModelFitSafetyMarginPercent = StoredNodeSettings.MaxModelFitSafetyMarginPercent,
            MaxProviderCallsPerInvocation = settings.MaxProviderCallsPerInvocation,
            MinMaxProviderCallsPerInvocation = StoredNodeSettings.MinMaxProviderCallsPerInvocation,
            MaxAllowedMaxProviderCallsPerInvocation = StoredNodeSettings.MaxMaxProviderCallsPerInvocation,
            CustomToolMaxTimeoutSeconds = settings.CustomToolMaxTimeoutSeconds,
            MinCustomToolMaxTimeoutSeconds = StoredNodeSettings.MinCustomToolMaxTimeoutSeconds,
            MaxAllowedCustomToolMaxTimeoutSeconds = StoredNodeSettings.MaxCustomToolMaxTimeoutSeconds,
            WebFetchTimeoutSeconds = settings.WebFetchTimeoutSeconds,
            MinWebFetchTimeoutSeconds = StoredNodeSettings.MinWebFetchTimeoutSeconds,
            MaxAllowedWebFetchTimeoutSeconds = StoredNodeSettings.MaxWebFetchTimeoutSeconds,
            WebFetchMaxContentChars = settings.WebFetchMaxContentChars,
            MinWebFetchMaxContentChars = StoredNodeSettings.MinWebFetchMaxContentChars,
            MaxAllowedWebFetchMaxContentChars = StoredNodeSettings.MaxWebFetchMaxContentChars,
            KnowledgeSearchDefaultResults = settings.KnowledgeSearchDefaultResults,
            MinKnowledgeSearchResults = StoredNodeSettings.MinKnowledgeSearchResults,
            MaxAllowedKnowledgeSearchResults = StoredNodeSettings.MaxKnowledgeSearchResults,
            KnowledgeSearchMaxResults = settings.KnowledgeSearchMaxResults,
            ReasoningBudgetMinimalTokens = settings.ReasoningBudgetMinimalTokens,
            ReasoningBudgetLowTokens = settings.ReasoningBudgetLowTokens,
            ReasoningBudgetMediumTokens = settings.ReasoningBudgetMediumTokens,
            ReasoningBudgetHighTokens = settings.ReasoningBudgetHighTokens,
            MinReasoningBudgetTokens = StoredNodeSettings.MinReasoningBudgetTokens,
            MaxAllowedReasoningBudgetTokens = StoredNodeSettings.MaxReasoningBudgetTokens,
            DefaultReasoningEffort = settings.DefaultReasoningEffort,
            ChatOutputCapMode = settings.ChatOutputCapMode,
            ChatOutputCapMaxTokens = settings.ChatOutputCapMaxTokens,
            MinChatOutputCapMaxTokens = StoredNodeSettings.MinChatOutputCapMaxTokens,
            MaxAllowedChatOutputCapMaxTokens = StoredNodeSettings.MaxChatOutputCapMaxTokens,
            HuggingFaceDownloadConnections = settings.HuggingFaceDownloadConnections,
            MinHuggingFaceDownloadConnections = StoredNodeSettings.MinHuggingFaceDownloadConnections,
            MaxAllowedHuggingFaceDownloadConnections = StoredNodeSettings.MaxHuggingFaceDownloadConnections,
            TranscriptionInferenceTimeoutMinutes = settings.TranscriptionInferenceTimeoutMinutes,
            MinTranscriptionInferenceTimeoutMinutes = StoredNodeSettings.MinTranscriptionInferenceTimeoutMinutes,
            MaxAllowedTranscriptionInferenceTimeoutMinutes = StoredNodeSettings.MaxTranscriptionInferenceTimeoutMinutes,
            AgentHomeMaxRunSeconds = settings.AgentHomeMaxRunSeconds,
            MinAgentHomeMaxRunSeconds = StoredNodeSettings.MinAgentHomeMaxRunSeconds,
            MaxAllowedAgentHomeMaxRunSeconds = StoredNodeSettings.MaxAgentHomeMaxRunSeconds,
            AgentHomeRunRetentionDays = effective.AgentHomeRunRetentionDays,
            MinAgentHomeRunRetentionDays = StoredNodeSettings.MinAgentHomeRunRetentionDays,
            MaxAllowedAgentHomeRunRetentionDays = StoredNodeSettings.MaxAgentHomeRunRetentionDays,
            ToolPipelineMaxIterationsPerRequest = settings.ToolPipelineMaxIterationsPerRequest,
            MinToolPipelineMaxIterationsPerRequest = StoredNodeSettings.MinToolPipelineMaxIterationsPerRequest,
            MaxAllowedToolPipelineMaxIterationsPerRequest = StoredNodeSettings.MaxToolPipelineMaxIterationsPerRequest,
            ToolPipelineMaxToolResultChars = settings.ToolPipelineMaxToolResultChars,
            MinToolPipelineMaxToolResultChars = StoredNodeSettings.MinToolPipelineMaxToolResultChars,
            MaxAllowedToolPipelineMaxToolResultChars = StoredNodeSettings.MaxToolPipelineMaxToolResultChars,
            ToolPipelineMaxConsecutiveInvalidToolCalls = settings.ToolPipelineMaxConsecutiveInvalidToolCalls,
            MinToolPipelineMaxConsecutiveInvalidToolCalls = StoredNodeSettings.MinToolPipelineMaxConsecutiveInvalidToolCalls,
            MaxAllowedToolPipelineMaxConsecutiveInvalidToolCalls = StoredNodeSettings.MaxToolPipelineMaxConsecutiveInvalidToolCalls,
            DefaultContextTokens = settings.DefaultContextTokens,
            MinDefaultContextTokens = StoredNodeSettings.MinDefaultContextTokens,
            MaxAllowedDefaultContextTokens = StoredNodeSettings.MaxDefaultContextTokens,
            ProviderBudgetRecentMessagesToKeep = settings.ProviderBudgetRecentMessagesToKeep,
            MinProviderBudgetRecentMessagesToKeep = StoredNodeSettings.MinProviderBudgetRecentMessagesToKeep,
            MaxAllowedProviderBudgetRecentMessagesToKeep = StoredNodeSettings.MaxProviderBudgetRecentMessagesToKeep,
            ProviderBudgetMaxCumulativeInputTokens = settings.ProviderBudgetMaxCumulativeInputTokens,
            MinProviderBudgetMaxCumulativeInputTokens = StoredNodeSettings.MinProviderBudgetMaxCumulativeInputTokens,
            MaxAllowedProviderBudgetMaxCumulativeInputTokens = StoredNodeSettings.MaxProviderBudgetMaxCumulativeInputTokens,
            ContextBudgetRecentTurnKeepCount = settings.ContextBudgetRecentTurnKeepCount,
            MinContextBudgetRecentTurnKeepCount = StoredNodeSettings.MinContextBudgetRecentTurnKeepCount,
            MaxAllowedContextBudgetRecentTurnKeepCount = StoredNodeSettings.MaxContextBudgetRecentTurnKeepCount,
            CompactionAutoCompactPercent = settings.CompactionAutoCompactPercent,
            MinCompactionAutoCompactPercent = StoredNodeSettings.MinCompactionAutoCompactPercent,
            MaxAllowedCompactionAutoCompactPercent = StoredNodeSettings.MaxCompactionAutoCompactPercent,
            CompactionRecentMessagesVerbatim = settings.CompactionRecentMessagesVerbatim,
            MinCompactionRecentMessagesVerbatim = StoredNodeSettings.MinCompactionRecentMessagesVerbatim,
            MaxAllowedCompactionRecentMessagesVerbatim = StoredNodeSettings.MaxCompactionRecentMessagesVerbatim,
            MaxInlinedAttachmentChars = settings.MaxInlinedAttachmentChars,
            MinMaxInlinedAttachmentChars = StoredNodeSettings.MinMaxInlinedAttachmentChars,
            MaxAllowedMaxInlinedAttachmentChars = StoredNodeSettings.MaxMaxInlinedAttachmentChars,
            KnowledgeChatTopK = settings.KnowledgeChatTopK,
            MinKnowledgeChatTopK = StoredNodeSettings.MinKnowledgeChatTopK,
            MaxAllowedKnowledgeChatTopK = StoredNodeSettings.MaxKnowledgeChatTopK,
            ProviderMaxRetries = settings.ProviderMaxRetries,
            MinProviderMaxRetries = StoredNodeSettings.MinProviderMaxRetries,
            MaxAllowedProviderMaxRetries = StoredNodeSettings.MaxProviderMaxRetries,
            SpawnMaxConcurrent = settings.SpawnMaxConcurrent,
            MinSpawnMaxConcurrent = StoredNodeSettings.MinSpawnMaxConcurrent,
            MaxAllowedSpawnMaxConcurrent = StoredNodeSettings.MaxSpawnMaxConcurrent,
            SpawnMaxCloud = settings.SpawnMaxCloud,
            MinSpawnMaxCloud = StoredNodeSettings.MinSpawnMaxCloud,
            MaxAllowedSpawnMaxCloud = StoredNodeSettings.MaxSpawnMaxCloud,
            SpawnQueueWaitSeconds = settings.SpawnQueueWaitSeconds,
            MinSpawnQueueWaitSeconds = StoredNodeSettings.MinSpawnQueueWaitSeconds,
            MaxAllowedSpawnQueueWaitSeconds = StoredNodeSettings.MaxSpawnQueueWaitSeconds,
            CompactionAutoEnabled = effective.CompactionAutoEnabled,
            CompactionDistillEnabled = effective.CompactionDistillEnabled,
            ProviderRetryEnabled = effective.ProviderRetryEnabled,
            KnowledgeAdaptiveRerankingEnabled = effective.KnowledgeAdaptiveRerankingEnabled,
            KnowledgeRetrievalLatencyBudgetMs = settings.KnowledgeRetrievalLatencyBudgetMs,
            MinKnowledgeRetrievalLatencyBudgetMs = StoredNodeSettings.MinKnowledgeRetrievalLatencyBudgetMs,
            MaxAllowedKnowledgeRetrievalLatencyBudgetMs = StoredNodeSettings.MaxKnowledgeRetrievalLatencyBudgetMs,
            KnowledgeScheduledReindexEnabled = effective.KnowledgeScheduledReindexEnabled,
            KnowledgeScheduledReindexIntervalMinutes = settings.KnowledgeScheduledReindexIntervalMinutes,
            MinKnowledgeScheduledReindexIntervalMinutes = StoredNodeSettings.MinKnowledgeScheduledReindexIntervalMinutes,
            MaxAllowedKnowledgeScheduledReindexIntervalMinutes = StoredNodeSettings.MaxKnowledgeScheduledReindexIntervalMinutes,
            KnowledgeAgentToolsEnabled = effective.KnowledgeAgentToolsEnabled,
            AllowCloudModelAccess = effective.AllowCloudModelAccess,
            AllowCloudModelUnattendedRuns = effective.AllowCloudModelUnattendedRuns,
            AllowCloudModelWebTools = effective.AllowCloudModelWebTools,
            AllowCloudModelMcpTools = effective.AllowCloudModelMcpTools,
            AllowCloudModelSubAgents = effective.AllowCloudModelSubAgents,
            PlaybookAnalysisModelName = settings.PlaybookAnalysisModelName,
            PlaybookEvalModelName = settings.PlaybookEvalModelName,
            MemoryExtractionModelName = settings.MemoryExtractionModelName,
            ChatRetentionEnabled = effective.ChatRetentionEnabled,
            ChatRetentionDays = effective.ChatRetentionDays,
            MinRetentionDays = StoredNodeSettings.MinRetentionDays,
            MaxAllowedRetentionDays = StoredNodeSettings.MaxRetentionDays,
            AgentExecutionLogRetentionEnabled = effective.AgentExecutionLogRetentionEnabled,
            AgentExecutionLogRetentionDays = effective.AgentExecutionLogRetentionDays,
            NodeDbBackupRetainCount = settings.NodeDbBackupRetainCount,
            MinNodeDbBackupRetainCount = StoredNodeSettings.MinNodeDbBackupRetainCount,
            MaxAllowedNodeDbBackupRetainCount = StoredNodeSettings.MaxNodeDbBackupRetainCount,
            BenchmarkKldCacheMaxBytes = settings.BenchmarkKldCacheMaxBytes,
            MinBenchmarkKldCacheMaxBytes = StoredNodeSettings.MinBenchmarkKldCacheMaxBytes,
            MaxAllowedBenchmarkKldCacheMaxBytes = StoredNodeSettings.MaxBenchmarkKldCacheMaxBytes,
            SchedulerHistoryRetentionDays = effective.SchedulerHistoryRetentionDays,
            ImageMaxLoadedProcesses = settings.ImageMaxLoadedProcesses,
            MinImageMaxLoadedProcesses = StoredNodeSettings.MinImageMaxLoadedProcesses,
            MaxAllowedImageMaxLoadedProcesses = StoredNodeSettings.MaxImageMaxLoadedProcesses,
            ImageTextEncoderOnGpu = effective.ImageTextEncoderOnGpu,
            GraphWorkflowMaxConcurrentRuns = settings.GraphWorkflowMaxConcurrentRuns,
            MinGraphWorkflowMaxConcurrentRuns = StoredNodeSettings.MinGraphWorkflowMaxConcurrentRuns,
            MaxAllowedGraphWorkflowMaxConcurrentRuns = StoredNodeSettings.MaxGraphWorkflowMaxConcurrentRuns,
            GraphWorkflowDefaultNodeTimeoutSeconds = settings.GraphWorkflowDefaultNodeTimeoutSeconds,
            MinGraphWorkflowDefaultNodeTimeoutSeconds = StoredNodeSettings.MinGraphWorkflowDefaultNodeTimeoutSeconds,
            MaxAllowedGraphWorkflowDefaultNodeTimeoutSeconds = StoredNodeSettings.MaxGraphWorkflowDefaultNodeTimeoutSeconds,
            WorkSessionMaxStepsPerRun = settings.WorkSessionMaxStepsPerRun,
            MinWorkSessionMaxStepsPerRun = StoredNodeSettings.MinWorkSessionMaxStepsPerRun,
            MaxAllowedWorkSessionMaxStepsPerRun = StoredNodeSettings.MaxWorkSessionMaxStepsPerRun,
            WorkSessionMaxConcurrentSessions = settings.WorkSessionMaxConcurrentSessions,
            MinWorkSessionMaxConcurrentSessions = StoredNodeSettings.MinWorkSessionMaxConcurrentSessions,
            MaxAllowedWorkSessionMaxConcurrentSessions = StoredNodeSettings.MaxWorkSessionMaxConcurrentSessions,
            DevelopmentMaxAttemptDurationSeconds = settings.DevelopmentMaxAttemptDurationSeconds,
            MinDevelopmentMaxAttemptDurationSeconds = StoredNodeSettings.MinDevelopmentMaxAttemptDurationSeconds,
            MaxAllowedDevelopmentMaxAttemptDurationSeconds = StoredNodeSettings.MaxDevelopmentMaxAttemptDurationSeconds,
            DevelopmentMaxToolCalls = settings.DevelopmentMaxToolCalls,
            MinDevelopmentMaxToolCalls = StoredNodeSettings.MinDevelopmentMaxToolCalls,
            MaxAllowedDevelopmentMaxToolCalls = StoredNodeSettings.MaxDevelopmentMaxToolCalls,
            DevelopmentMaxOutputTokens = settings.DevelopmentMaxOutputTokens,
            MinDevelopmentMaxOutputTokens = StoredNodeSettings.MinDevelopmentMaxOutputTokens,
            MaxAllowedDevelopmentMaxOutputTokens = StoredNodeSettings.MaxDevelopmentMaxOutputTokens,
            AgentHomeMaxInnerToolCalls = settings.AgentHomeMaxInnerToolCalls,
            MinAgentHomeMaxInnerToolCalls = StoredNodeSettings.MinAgentHomeMaxInnerToolCalls,
            MaxAllowedAgentHomeMaxInnerToolCalls = StoredNodeSettings.MaxAgentHomeMaxInnerToolCalls,
            AgentHomePatchApplyTimeoutSeconds = settings.AgentHomePatchApplyTimeoutSeconds,
            MinAgentHomePatchApplyTimeoutSeconds = StoredNodeSettings.MinAgentHomePatchApplyTimeoutSeconds,
            MaxAllowedAgentHomePatchApplyTimeoutSeconds = StoredNodeSettings.MaxAgentHomePatchApplyTimeoutSeconds,
            AgentHomeRunRetentionMaxRuns = settings.AgentHomeRunRetentionMaxRuns,
            MinAgentHomeRunRetentionMaxRuns = StoredNodeSettings.MinAgentHomeRunRetentionMaxRuns,
            MaxAllowedAgentHomeRunRetentionMaxRuns = StoredNodeSettings.MaxAgentHomeRunRetentionMaxRuns,
            AgentHomeRunRetentionMaxTotalBytes = settings.AgentHomeRunRetentionMaxTotalBytes,
            MinAgentHomeRunRetentionMaxTotalBytes = StoredNodeSettings.MinAgentHomeRunRetentionMaxTotalBytes,
            MaxAllowedAgentHomeRunRetentionMaxTotalBytes = StoredNodeSettings.MaxAgentHomeRunRetentionMaxTotalBytes,
            DevelopmentEnabled = effective.DevelopmentEnabled,
            WorkSessionsEnabled = effective.WorkSessionsEnabled,
            GraphWorkflowsEnabled = effective.GraphWorkflowsEnabled,
            TranscriptionEnabled = effective.TranscriptionEnabled,
            ExternalAppsEnabled = effective.ExternalAppsEnabled,
            ComputeEnabled = effective.ComputeEnabled,
            AgentHomeEnabled = effective.AgentHomeEnabled,
            SchedulerEnabled = effective.SchedulerEnabled,
            DevWorkflowsEnabled = effective.DevWorkflowsEnabled,
            ExecutionPreviewsEnabled = effective.ExecutionPreviewsEnabled
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
            WebAccessEnabled = request.WebAccessEnabled ?? currentSettings.WebAccessEnabled,
            // Blank clears it: the store's Normalize maps an empty string to null, like OllamaEndpoint.
            WebSearchSearxngUrl = request.WebSearchSearxngUrl is null
                ? currentSettings.WebSearchSearxngUrl
                : request.WebSearchSearxngUrl.Trim(),
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
                },
            TranscriptionIdleTimeoutMinutes = request.TranscriptionIdleTimeoutMinutes ?? currentSettings.TranscriptionIdleTimeoutMinutes,
            LlamaReadinessTimeoutCapSeconds = request.LlamaReadinessTimeoutCapSeconds ?? currentSettings.LlamaReadinessTimeoutCapSeconds,
            LlamaChatHttpTimeoutSeconds = request.LlamaChatHttpTimeoutSeconds ?? currentSettings.LlamaChatHttpTimeoutSeconds,
            LlamaEmbeddingHttpTimeoutSeconds = request.LlamaEmbeddingHttpTimeoutSeconds ?? currentSettings.LlamaEmbeddingHttpTimeoutSeconds,
            // -1 is the request's "back to automatic", the one way a null-keeps member can clear a stored count.
            LlamaChatCacheRamMiB = request.LlamaChatCacheRamMiB switch
            {
                null => currentSettings.LlamaChatCacheRamMiB,
                StoredNodeSettings.LlamaChatCacheRamMiBAuto => null,
                { } cacheRamMiB => cacheRamMiB
            },
            LlamaCpuThreadReserve = request.LlamaCpuThreadReserve ?? currentSettings.LlamaCpuThreadReserve,
            LlamaGpuReservePercent = request.LlamaGpuReservePercent ?? currentSettings.LlamaGpuReservePercent,
            LlamaRamReservePercent = request.LlamaRamReservePercent ?? currentSettings.LlamaRamReservePercent,
            ImageIdleTimeToLiveSeconds = request.ImageIdleTimeToLiveSeconds ?? currentSettings.ImageIdleTimeToLiveSeconds,
            ModelFitSafetyMarginPercent = request.ModelFitSafetyMarginPercent ?? currentSettings.ModelFitSafetyMarginPercent,
            MaxProviderCallsPerInvocation = request.MaxProviderCallsPerInvocation ?? currentSettings.MaxProviderCallsPerInvocation,
            CustomToolMaxTimeoutSeconds = request.CustomToolMaxTimeoutSeconds ?? currentSettings.CustomToolMaxTimeoutSeconds,
            WebFetchTimeoutSeconds = request.WebFetchTimeoutSeconds ?? currentSettings.WebFetchTimeoutSeconds,
            WebFetchMaxContentChars = request.WebFetchMaxContentChars ?? currentSettings.WebFetchMaxContentChars,
            KnowledgeSearchDefaultResults = request.KnowledgeSearchDefaultResults ?? currentSettings.KnowledgeSearchDefaultResults,
            KnowledgeSearchMaxResults = request.KnowledgeSearchMaxResults ?? currentSettings.KnowledgeSearchMaxResults,
            ReasoningBudgetMinimalTokens = KeepOrUnsetTokens(request.ReasoningBudgetMinimalTokens, currentSettings.ReasoningBudgetMinimalTokens),
            ReasoningBudgetLowTokens = KeepOrUnsetTokens(request.ReasoningBudgetLowTokens, currentSettings.ReasoningBudgetLowTokens),
            ReasoningBudgetMediumTokens = KeepOrUnsetTokens(request.ReasoningBudgetMediumTokens, currentSettings.ReasoningBudgetMediumTokens),
            ReasoningBudgetHighTokens = KeepOrUnsetTokens(request.ReasoningBudgetHighTokens, currentSettings.ReasoningBudgetHighTokens),
            // The empty string clears these two back to the shipped default, like WebSearchSearxngUrl.
            DefaultReasoningEffort = request.DefaultReasoningEffort switch
            {
                null => currentSettings.DefaultReasoningEffort,
                "" => null,
                { } effort => effort.Trim()
            },
            ChatOutputCapMode = request.ChatOutputCapMode switch
            {
                null => currentSettings.ChatOutputCapMode,
                "" => null,
                { } mode => mode.Trim()
            },
            ChatOutputCapMaxTokens = KeepOrUnsetTokens(request.ChatOutputCapMaxTokens, currentSettings.ChatOutputCapMaxTokens),
            HuggingFaceDownloadConnections = request.HuggingFaceDownloadConnections ?? currentSettings.HuggingFaceDownloadConnections,
            TranscriptionInferenceTimeoutMinutes = request.TranscriptionInferenceTimeoutMinutes ?? currentSettings.TranscriptionInferenceTimeoutMinutes,
            AgentHomeMaxRunSeconds = request.AgentHomeMaxRunSeconds ?? currentSettings.AgentHomeMaxRunSeconds,
            AgentHomeRunRetentionDays = request.AgentHomeRunRetentionDays ?? currentSettings.AgentHomeRunRetentionDays,
            ToolPipelineMaxIterationsPerRequest = request.ToolPipelineMaxIterationsPerRequest ?? currentSettings.ToolPipelineMaxIterationsPerRequest,
            ToolPipelineMaxToolResultChars = request.ToolPipelineMaxToolResultChars ?? currentSettings.ToolPipelineMaxToolResultChars,
            ToolPipelineMaxConsecutiveInvalidToolCalls = request.ToolPipelineMaxConsecutiveInvalidToolCalls ?? currentSettings.ToolPipelineMaxConsecutiveInvalidToolCalls,
            DefaultContextTokens = request.DefaultContextTokens ?? currentSettings.DefaultContextTokens,
            ProviderBudgetRecentMessagesToKeep = request.ProviderBudgetRecentMessagesToKeep ?? currentSettings.ProviderBudgetRecentMessagesToKeep,
            ProviderBudgetMaxCumulativeInputTokens = request.ProviderBudgetMaxCumulativeInputTokens ?? currentSettings.ProviderBudgetMaxCumulativeInputTokens,
            ContextBudgetRecentTurnKeepCount = request.ContextBudgetRecentTurnKeepCount ?? currentSettings.ContextBudgetRecentTurnKeepCount,
            CompactionAutoCompactPercent = request.CompactionAutoCompactPercent ?? currentSettings.CompactionAutoCompactPercent,
            CompactionRecentMessagesVerbatim = request.CompactionRecentMessagesVerbatim ?? currentSettings.CompactionRecentMessagesVerbatim,
            MaxInlinedAttachmentChars = request.MaxInlinedAttachmentChars ?? currentSettings.MaxInlinedAttachmentChars,
            KnowledgeChatTopK = request.KnowledgeChatTopK ?? currentSettings.KnowledgeChatTopK,
            ProviderMaxRetries = request.ProviderMaxRetries ?? currentSettings.ProviderMaxRetries,
            SpawnMaxConcurrent = request.SpawnMaxConcurrent ?? currentSettings.SpawnMaxConcurrent,
            SpawnMaxCloud = request.SpawnMaxCloud ?? currentSettings.SpawnMaxCloud,
            SpawnQueueWaitSeconds = request.SpawnQueueWaitSeconds ?? currentSettings.SpawnQueueWaitSeconds,
            CompactionAutoEnabled = request.CompactionAutoEnabled ?? currentSettings.CompactionAutoEnabled,
            CompactionDistillEnabled = request.CompactionDistillEnabled ?? currentSettings.CompactionDistillEnabled,
            ProviderRetryEnabled = request.ProviderRetryEnabled ?? currentSettings.ProviderRetryEnabled,
            KnowledgeAdaptiveRerankingEnabled = request.KnowledgeAdaptiveRerankingEnabled ?? currentSettings.KnowledgeAdaptiveRerankingEnabled,
            KnowledgeRetrievalLatencyBudgetMs = request.KnowledgeRetrievalLatencyBudgetMs ?? currentSettings.KnowledgeRetrievalLatencyBudgetMs,
            KnowledgeScheduledReindexEnabled = request.KnowledgeScheduledReindexEnabled ?? currentSettings.KnowledgeScheduledReindexEnabled,
            KnowledgeScheduledReindexIntervalMinutes = request.KnowledgeScheduledReindexIntervalMinutes ?? currentSettings.KnowledgeScheduledReindexIntervalMinutes,
            KnowledgeAgentToolsEnabled = request.KnowledgeAgentToolsEnabled ?? currentSettings.KnowledgeAgentToolsEnabled,
            AllowCloudModelAccess = request.AllowCloudModelAccess ?? currentSettings.AllowCloudModelAccess,
            AllowCloudModelUnattendedRuns = request.AllowCloudModelUnattendedRuns ?? currentSettings.AllowCloudModelUnattendedRuns,
            AllowCloudModelWebTools = request.AllowCloudModelWebTools ?? currentSettings.AllowCloudModelWebTools,
            AllowCloudModelMcpTools = request.AllowCloudModelMcpTools ?? currentSettings.AllowCloudModelMcpTools,
            AllowCloudModelSubAgents = request.AllowCloudModelSubAgents ?? currentSettings.AllowCloudModelSubAgents,
            PlaybookAnalysisModelName = request.PlaybookAnalysisModelName is null ? currentSettings.PlaybookAnalysisModelName : request.PlaybookAnalysisModelName.Trim(),
            PlaybookEvalModelName = request.PlaybookEvalModelName is null ? currentSettings.PlaybookEvalModelName : request.PlaybookEvalModelName.Trim(),
            MemoryExtractionModelName = request.MemoryExtractionModelName is null ? currentSettings.MemoryExtractionModelName : request.MemoryExtractionModelName.Trim(),
            ChatRetentionEnabled = request.ChatRetentionEnabled ?? currentSettings.ChatRetentionEnabled,
            ChatRetentionDays = request.ChatRetentionDays ?? currentSettings.ChatRetentionDays,
            AgentExecutionLogRetentionEnabled = request.AgentExecutionLogRetentionEnabled ?? currentSettings.AgentExecutionLogRetentionEnabled,
            AgentExecutionLogRetentionDays = request.AgentExecutionLogRetentionDays ?? currentSettings.AgentExecutionLogRetentionDays,
            NodeDbBackupRetainCount = request.NodeDbBackupRetainCount ?? currentSettings.NodeDbBackupRetainCount,
            BenchmarkKldCacheMaxBytes = request.BenchmarkKldCacheMaxBytes ?? currentSettings.BenchmarkKldCacheMaxBytes,
            SchedulerHistoryRetentionDays = request.SchedulerHistoryRetentionDays ?? currentSettings.SchedulerHistoryRetentionDays,
            ImageMaxLoadedProcesses = request.ImageMaxLoadedProcesses ?? currentSettings.ImageMaxLoadedProcesses,
            ImageTextEncoderOnGpu = request.ImageTextEncoderOnGpu ?? currentSettings.ImageTextEncoderOnGpu,
            GraphWorkflowMaxConcurrentRuns = request.GraphWorkflowMaxConcurrentRuns ?? currentSettings.GraphWorkflowMaxConcurrentRuns,
            GraphWorkflowDefaultNodeTimeoutSeconds = request.GraphWorkflowDefaultNodeTimeoutSeconds ?? currentSettings.GraphWorkflowDefaultNodeTimeoutSeconds,
            WorkSessionMaxStepsPerRun = request.WorkSessionMaxStepsPerRun ?? currentSettings.WorkSessionMaxStepsPerRun,
            WorkSessionMaxConcurrentSessions = request.WorkSessionMaxConcurrentSessions ?? currentSettings.WorkSessionMaxConcurrentSessions,
            DevelopmentMaxAttemptDurationSeconds = request.DevelopmentMaxAttemptDurationSeconds ?? currentSettings.DevelopmentMaxAttemptDurationSeconds,
            DevelopmentMaxToolCalls = request.DevelopmentMaxToolCalls ?? currentSettings.DevelopmentMaxToolCalls,
            DevelopmentMaxOutputTokens = request.DevelopmentMaxOutputTokens ?? currentSettings.DevelopmentMaxOutputTokens,
            AgentHomeMaxInnerToolCalls = request.AgentHomeMaxInnerToolCalls ?? currentSettings.AgentHomeMaxInnerToolCalls,
            AgentHomePatchApplyTimeoutSeconds = request.AgentHomePatchApplyTimeoutSeconds ?? currentSettings.AgentHomePatchApplyTimeoutSeconds,
            AgentHomeRunRetentionMaxRuns = request.AgentHomeRunRetentionMaxRuns ?? currentSettings.AgentHomeRunRetentionMaxRuns,
            AgentHomeRunRetentionMaxTotalBytes = request.AgentHomeRunRetentionMaxTotalBytes ?? currentSettings.AgentHomeRunRetentionMaxTotalBytes,
            DevelopmentEnabled = request.DevelopmentEnabled ?? currentSettings.DevelopmentEnabled,
            WorkSessionsEnabled = request.WorkSessionsEnabled ?? currentSettings.WorkSessionsEnabled,
            GraphWorkflowsEnabled = request.GraphWorkflowsEnabled ?? currentSettings.GraphWorkflowsEnabled,
            TranscriptionEnabled = request.TranscriptionEnabled ?? currentSettings.TranscriptionEnabled,
            ExternalAppsEnabled = request.ExternalAppsEnabled ?? currentSettings.ExternalAppsEnabled,
            ComputeEnabled = request.ComputeEnabled ?? currentSettings.ComputeEnabled,
            AgentHomeEnabled = request.AgentHomeEnabled ?? currentSettings.AgentHomeEnabled,
            SchedulerEnabled = request.SchedulerEnabled ?? currentSettings.SchedulerEnabled,
            DevWorkflowsEnabled = request.DevWorkflowsEnabled ?? currentSettings.DevWorkflowsEnabled,
            ExecutionPreviewsEnabled = request.ExecutionPreviewsEnabled ?? currentSettings.ExecutionPreviewsEnabled
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
    private static ExternalAccessStamp ApplyExternalAccess(SaveNodeSettingsRequest request,
        StoredNodeSettings currentSettings)
    {
        if (StoredNodeSettings.IsExternalAccessPreset(request.ExternalAccessProfile))
        {
            var enabled = string.Equals(request.ExternalAccessProfile, StoredNodeSettings.ExternalAccessProfileRecommended, StringComparison.Ordinal);
            return new ExternalAccessStamp(request.ExternalAccessProfile, enabled, enabled, enabled);
        }

        if (request.AutoCheckApplicationUpdates is null
            && request.AutoCheckRuntimeUpdates is null
            && request.AutoProvisionFirstRunModel is null)
        {
            return new ExternalAccessStamp(currentSettings.ExternalAccessProfile,
                currentSettings.AutoCheckApplicationUpdates,
                currentSettings.AutoCheckRuntimeUpdates,
                currentSettings.AutoProvisionFirstRunModel);
        }

        return new ExternalAccessStamp(StoredNodeSettings.ExternalAccessProfileCustom,
            request.AutoCheckApplicationUpdates ?? currentSettings.AutoCheckApplicationUpdates,
            request.AutoCheckRuntimeUpdates ?? currentSettings.AutoCheckRuntimeUpdates,
            request.AutoProvisionFirstRunModel ?? currentSettings.AutoProvisionFirstRunModel);
    }

    /// <summary>The external-access profile stamp and the three switches it describes.</summary>
    private readonly record struct ExternalAccessStamp(string? Profile, bool? ApplicationUpdates, bool? RuntimeUpdates, bool? FirstRunModel);

    /// <summary>Null keeps the stored value; <see cref="StoredNodeSettings.TokenSettingUnset" /> clears it back to the shipped default.</summary>
    private static int? KeepOrUnsetTokens(int? requested, int? current)
    {
        return requested switch
        {
            null => current,
            StoredNodeSettings.TokenSettingUnset => null,
            { } tokens => tokens
        };
    }
}
