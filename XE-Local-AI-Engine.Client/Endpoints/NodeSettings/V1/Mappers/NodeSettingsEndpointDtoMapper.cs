namespace XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1.Mappers;

using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Sandbox;

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
            // `pending` is passed through for the first-run guard; every other state reports the profile in effect.
            SandboxSecurityProfile = string.Equals(settings.SandboxSecurityProfile, StoredNodeSettings.SandboxSecurityProfilePending, StringComparison.Ordinal)
                ? StoredNodeSettings.SandboxSecurityProfilePending
                : SandboxSecurityProfilePolicy.ToLiteral(effective.SandboxSecurityProfile),
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
            KnowledgeEmbeddingModelName = settings.KnowledgeEmbeddingModelName,
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
            IntentRouterDefinitionId = settings.IntentRouterDefinitionId,
            IntentMaxUtteranceAgeSeconds = settings.IntentMaxUtteranceAgeSeconds,
            MinIntentMaxUtteranceAgeSeconds = StoredNodeSettings.MinIntentMaxUtteranceAgeSeconds,
            MaxAllowedIntentMaxUtteranceAgeSeconds = StoredNodeSettings.MaxIntentMaxUtteranceAgeSeconds,
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
    ///     Merges the request into the current stored settings: an omitted member keeps its stored value, an explicit
    ///     JSON null (<see cref="SaveNodeSettingsRequest.Clears" />) clears it back to the effective default, and a value
    ///     sets it.
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

        // The validator has already proven a supplied value parses; the `else` is written rather than banged so a
        // future caller that skips the validator keeps the stored value instead of persisting junk.
        var containerRuntimeSelection = currentSettings.ContainerRuntimeSelection;
        if (request.Clears(nameof(request.ContainerRuntimeSelection)))
        {
            containerRuntimeSelection = null;
        }
        else if (request.ContainerRuntimeSelection is { } selection
                 && ContainerRuntimeSelectionParser.TryParse(selection, out var requestedRuntimeSelection))
        {
            containerRuntimeSelection = ContainerRuntimeSelectionParser.Format(requestedRuntimeSelection);
        }

        return new StoredNodeSettings
        {
            // The one non-nullable stored member: null resets it to the shipped default instead of storing null.
            MaxMessageRequestTimeoutSeconds = request.Clears(nameof(request.MaxMessageRequestTimeoutSeconds))
                ? StoredNodeSettings.DefaultMaxMessageRequestTimeoutSeconds
                : request.MaxMessageRequestTimeoutSeconds ?? currentSettings.MaxMessageRequestTimeoutSeconds,
            DefaultModelName = MergeText(request.DefaultModelName, currentSettings.DefaultModelName, request.Clears(nameof(request.DefaultModelName))),
            EnableTools = Merge(request.EnableTools, currentSettings.EnableTools, request.Clears(nameof(request.EnableTools))),
            CustomToolsEnabled = Merge(request.CustomToolsEnabled, currentSettings.CustomToolsEnabled, request.Clears(nameof(request.CustomToolsEnabled))),
            ToolRelevanceEnabled = Merge(request.ToolRelevanceEnabled, currentSettings.ToolRelevanceEnabled, request.Clears(nameof(request.ToolRelevanceEnabled))),
            WebAccessEnabled = Merge(request.WebAccessEnabled, currentSettings.WebAccessEnabled, request.Clears(nameof(request.WebAccessEnabled))),
            WebSearchSearxngUrl = MergeText(request.WebSearchSearxngUrl, currentSettings.WebSearchSearxngUrl, request.Clears(nameof(request.WebSearchSearxngUrl))),
            ExternalAccessProfile = externalAccessProfile,
            // Plain merge, unlike the external-access block above: the mode couples to no other field, so it needs no joint resolution. The validator has
            // already proven a supplied value is one of the two literals and refused a null; the store's Normalize trims and re-checks it.
            UiMode = MergeText(request.UiMode, currentSettings.UiMode, request.Clears(nameof(request.UiMode))),
            // The UiMode merge: the validator has proven a supplied value is low or high and refused a null, and the store's Normalize re-checks it.
            SandboxSecurityProfile = MergeText(request.SandboxSecurityProfile, currentSettings.SandboxSecurityProfile, request.Clears(nameof(request.SandboxSecurityProfile))),
            UpdateChannel = MergeText(request.UpdateChannel, currentSettings.UpdateChannel, request.Clears(nameof(request.UpdateChannel))),
            AutoCheckApplicationUpdates = autoCheckApplicationUpdates,
            AutoCheckRuntimeUpdates = autoCheckRuntimeUpdates,
            AutoProvisionFirstRunModel = autoProvisionFirstRunModel,
            ToolCapableModels = MergeObject(request.ToolCapableModels, currentSettings.ToolCapableModels, request.Clears(nameof(request.ToolCapableModels))),
            OllamaEndpoint = MergeText(request.OllamaEndpoint, currentSettings.OllamaEndpoint, request.Clears(nameof(request.OllamaEndpoint))),
            HuggingFaceDefaultQuant = MergeText(request.HuggingFaceDefaultQuant, currentSettings.HuggingFaceDefaultQuant, request.Clears(nameof(request.HuggingFaceDefaultQuant))),
            HuggingFaceDiskMarginBytes = Merge(request.HuggingFaceDiskMarginBytes, currentSettings.HuggingFaceDiskMarginBytes, request.Clears(nameof(request.HuggingFaceDiskMarginBytes))),
            LlamaMaxLoadedProcesses = Merge(request.LlamaMaxLoadedProcesses, currentSettings.LlamaMaxLoadedProcesses, request.Clears(nameof(request.LlamaMaxLoadedProcesses))),
            LlamaIdleTimeToLiveSeconds = Merge(request.LlamaIdleTimeToLiveSeconds, currentSettings.LlamaIdleTimeToLiveSeconds, request.Clears(nameof(request.LlamaIdleTimeToLiveSeconds))),
            KeepModelWarmEnabled = Merge(request.KeepModelWarmEnabled, currentSettings.KeepModelWarmEnabled, request.Clears(nameof(request.KeepModelWarmEnabled))),
            KeepModelWarmModelName = MergeText(request.KeepModelWarmModelName, currentSettings.KeepModelWarmModelName, request.Clears(nameof(request.KeepModelWarmModelName))),
            KeepModelWarmIntervalSeconds = Merge(request.KeepModelWarmIntervalSeconds, currentSettings.KeepModelWarmIntervalSeconds, request.Clears(nameof(request.KeepModelWarmIntervalSeconds))),
            MaxResponseSizeMb = Merge(request.MaxResponseSizeMb, currentSettings.MaxResponseSizeMb, request.Clears(nameof(request.MaxResponseSizeMb))),
            RecommendedLlamaCppTag = MergeText(request.RecommendedLlamaCppTag, currentSettings.RecommendedLlamaCppTag, request.Clears(nameof(request.RecommendedLlamaCppTag))),
            ChatCacheReuse = Merge(request.ChatCacheReuse, currentSettings.ChatCacheReuse, request.Clears(nameof(request.ChatCacheReuse))),
            SpeculativeMode = MergeText(request.SpeculativeMode, currentSettings.SpeculativeMode, request.Clears(nameof(request.SpeculativeMode))),
            KvCacheType = MergeText(request.KvCacheType, currentSettings.KvCacheType, request.Clears(nameof(request.KvCacheType))),
            ContainerRuntimeSelection = containerRuntimeSelection,
            SpeculativeDraftModelName = MergeText(request.SpeculativeDraftModelName, currentSettings.SpeculativeDraftModelName, request.Clears(nameof(request.SpeculativeDraftModelName))),
            SpeculativeDraftMaxTokens = Merge(request.SpeculativeDraftMaxTokens, currentSettings.SpeculativeDraftMaxTokens, request.Clears(nameof(request.SpeculativeDraftMaxTokens))),
            SpeculativeDraftGpuLayers = Merge(request.SpeculativeDraftGpuLayers, currentSettings.SpeculativeDraftGpuLayers, request.Clears(nameof(request.SpeculativeDraftGpuLayers))),
            // Null is the "Off" option: stored null, reranking disabled.
            RerankerModelName = MergeText(request.RerankerModelName, currentSettings.RerankerModelName, request.Clears(nameof(request.RerankerModelName))),
            KnowledgeEmbeddingModelName = MergeText(request.KnowledgeEmbeddingModelName, currentSettings.KnowledgeEmbeddingModelName,
                request.Clears(nameof(request.KnowledgeEmbeddingModelName))),
            AutoEffortFastModelName = MergeText(request.AutoEffortFastModelName, currentSettings.AutoEffortFastModelName, request.Clears(nameof(request.AutoEffortFastModelName))),
            OrchestrationIdleTimeoutSeconds = Merge(request.OrchestrationIdleTimeoutSeconds, currentSettings.OrchestrationIdleTimeoutSeconds,
                request.Clears(nameof(request.OrchestrationIdleTimeoutSeconds))),
            AgentHomePrepareTimeoutSeconds = Merge(request.AgentHomePrepareTimeoutSeconds, currentSettings.AgentHomePrepareTimeoutSeconds,
                request.Clears(nameof(request.AgentHomePrepareTimeoutSeconds))),
            AgentHomeCommandTimeoutSeconds = Merge(request.AgentHomeCommandTimeoutSeconds, currentSettings.AgentHomeCommandTimeoutSeconds,
                request.Clears(nameof(request.AgentHomeCommandTimeoutSeconds))),
            AgentHomeMaxSelectedFolderBytes = Merge(request.AgentHomeMaxSelectedFolderBytes, currentSettings.AgentHomeMaxSelectedFolderBytes,
                request.Clears(nameof(request.AgentHomeMaxSelectedFolderBytes))),
            AgentHomeMaxPatchBytes = Merge(request.AgentHomeMaxPatchBytes, currentSettings.AgentHomeMaxPatchBytes, request.Clears(nameof(request.AgentHomeMaxPatchBytes))),
            MaxPendingToolCallAgeMinutes = Merge(request.MaxPendingToolCallAgeMinutes, currentSettings.MaxPendingToolCallAgeMinutes, request.Clears(nameof(request.MaxPendingToolCallAgeMinutes))),
            DetachedGraceSeconds = Merge(request.DetachedGraceSeconds, currentSettings.DetachedGraceSeconds, request.Clears(nameof(request.DetachedGraceSeconds))),
            // The node-default tool-approval policy has no editable field on this request, so the currently stored value is preserved: an unrelated node-settings save must
            // never wipe it.
            ToolApprovalPolicy = currentSettings.ToolApprovalPolicy,
            VoiceFeatureEnabled = Merge(request.VoiceFeatureEnabled, currentSettings.VoiceFeatureEnabled, request.Clears(nameof(request.VoiceFeatureEnabled))),
            DefaultVoiceProfile = MergeText(request.DefaultVoiceProfile, currentSettings.DefaultVoiceProfile, request.Clears(nameof(request.DefaultVoiceProfile))),
            // Omitted keeps the stored override, null clears it, and a supplied map (wrapped back into the stored shape) REPLACES it. The store's
            // Normalize then trims keys and drops negative or non-finite entries, collapsing an empty or all-junk map to null — no override.
            UsageRates = MergeObject(request.UsageRates is { } rates
                    ? new NodeUsageRateSettings
                    {
                        Models = rates
                    }
                    : null,
                currentSettings.UsageRates,
                request.Clears(nameof(request.UsageRates))),
            TranscriptionIdleTimeoutMinutes = Merge(request.TranscriptionIdleTimeoutMinutes, currentSettings.TranscriptionIdleTimeoutMinutes,
                request.Clears(nameof(request.TranscriptionIdleTimeoutMinutes))),
            LlamaReadinessTimeoutCapSeconds = Merge(request.LlamaReadinessTimeoutCapSeconds, currentSettings.LlamaReadinessTimeoutCapSeconds,
                request.Clears(nameof(request.LlamaReadinessTimeoutCapSeconds))),
            LlamaChatHttpTimeoutSeconds = Merge(request.LlamaChatHttpTimeoutSeconds, currentSettings.LlamaChatHttpTimeoutSeconds, request.Clears(nameof(request.LlamaChatHttpTimeoutSeconds))),
            LlamaEmbeddingHttpTimeoutSeconds = Merge(request.LlamaEmbeddingHttpTimeoutSeconds, currentSettings.LlamaEmbeddingHttpTimeoutSeconds,
                request.Clears(nameof(request.LlamaEmbeddingHttpTimeoutSeconds))),
            // Null is automatic.
            LlamaChatCacheRamMiB = Merge(request.LlamaChatCacheRamMiB, currentSettings.LlamaChatCacheRamMiB, request.Clears(nameof(request.LlamaChatCacheRamMiB))),
            LlamaCpuThreadReserve = Merge(request.LlamaCpuThreadReserve, currentSettings.LlamaCpuThreadReserve, request.Clears(nameof(request.LlamaCpuThreadReserve))),
            LlamaGpuReservePercent = Merge(request.LlamaGpuReservePercent, currentSettings.LlamaGpuReservePercent, request.Clears(nameof(request.LlamaGpuReservePercent))),
            LlamaRamReservePercent = Merge(request.LlamaRamReservePercent, currentSettings.LlamaRamReservePercent, request.Clears(nameof(request.LlamaRamReservePercent))),
            ImageIdleTimeToLiveSeconds = Merge(request.ImageIdleTimeToLiveSeconds, currentSettings.ImageIdleTimeToLiveSeconds, request.Clears(nameof(request.ImageIdleTimeToLiveSeconds))),
            ModelFitSafetyMarginPercent = Merge(request.ModelFitSafetyMarginPercent, currentSettings.ModelFitSafetyMarginPercent, request.Clears(nameof(request.ModelFitSafetyMarginPercent))),
            MaxProviderCallsPerInvocation = Merge(request.MaxProviderCallsPerInvocation, currentSettings.MaxProviderCallsPerInvocation, request.Clears(nameof(request.MaxProviderCallsPerInvocation))),
            CustomToolMaxTimeoutSeconds = Merge(request.CustomToolMaxTimeoutSeconds, currentSettings.CustomToolMaxTimeoutSeconds, request.Clears(nameof(request.CustomToolMaxTimeoutSeconds))),
            WebFetchTimeoutSeconds = Merge(request.WebFetchTimeoutSeconds, currentSettings.WebFetchTimeoutSeconds, request.Clears(nameof(request.WebFetchTimeoutSeconds))),
            // The validator has proven a supplied value is a GUID; null clears the router (alias-only routing).
            IntentRouterDefinitionId = Merge(Guid.TryParse(request.IntentRouterDefinitionId, out var parsed) ? parsed : (Guid?)null,
                currentSettings.IntentRouterDefinitionId,
                request.Clears(nameof(request.IntentRouterDefinitionId))),
            IntentMaxUtteranceAgeSeconds = Merge(request.IntentMaxUtteranceAgeSeconds, currentSettings.IntentMaxUtteranceAgeSeconds, request.Clears(nameof(request.IntentMaxUtteranceAgeSeconds))),
            WebFetchMaxContentChars = Merge(request.WebFetchMaxContentChars, currentSettings.WebFetchMaxContentChars, request.Clears(nameof(request.WebFetchMaxContentChars))),
            KnowledgeSearchDefaultResults = Merge(request.KnowledgeSearchDefaultResults, currentSettings.KnowledgeSearchDefaultResults, request.Clears(nameof(request.KnowledgeSearchDefaultResults))),
            KnowledgeSearchMaxResults = Merge(request.KnowledgeSearchMaxResults, currentSettings.KnowledgeSearchMaxResults, request.Clears(nameof(request.KnowledgeSearchMaxResults))),
            ReasoningBudgetMinimalTokens = Merge(request.ReasoningBudgetMinimalTokens, currentSettings.ReasoningBudgetMinimalTokens, request.Clears(nameof(request.ReasoningBudgetMinimalTokens))),
            ReasoningBudgetLowTokens = Merge(request.ReasoningBudgetLowTokens, currentSettings.ReasoningBudgetLowTokens, request.Clears(nameof(request.ReasoningBudgetLowTokens))),
            ReasoningBudgetMediumTokens = Merge(request.ReasoningBudgetMediumTokens, currentSettings.ReasoningBudgetMediumTokens, request.Clears(nameof(request.ReasoningBudgetMediumTokens))),
            ReasoningBudgetHighTokens = Merge(request.ReasoningBudgetHighTokens, currentSettings.ReasoningBudgetHighTokens, request.Clears(nameof(request.ReasoningBudgetHighTokens))),
            DefaultReasoningEffort = MergeText(request.DefaultReasoningEffort, currentSettings.DefaultReasoningEffort, request.Clears(nameof(request.DefaultReasoningEffort))),
            ChatOutputCapMode = MergeText(request.ChatOutputCapMode, currentSettings.ChatOutputCapMode, request.Clears(nameof(request.ChatOutputCapMode))),
            ChatOutputCapMaxTokens = Merge(request.ChatOutputCapMaxTokens, currentSettings.ChatOutputCapMaxTokens, request.Clears(nameof(request.ChatOutputCapMaxTokens))),
            HuggingFaceDownloadConnections = Merge(request.HuggingFaceDownloadConnections, currentSettings.HuggingFaceDownloadConnections,
                request.Clears(nameof(request.HuggingFaceDownloadConnections))),
            TranscriptionInferenceTimeoutMinutes = Merge(request.TranscriptionInferenceTimeoutMinutes, currentSettings.TranscriptionInferenceTimeoutMinutes,
                request.Clears(nameof(request.TranscriptionInferenceTimeoutMinutes))),
            AgentHomeMaxRunSeconds = Merge(request.AgentHomeMaxRunSeconds, currentSettings.AgentHomeMaxRunSeconds, request.Clears(nameof(request.AgentHomeMaxRunSeconds))),
            AgentHomeRunRetentionDays = Merge(request.AgentHomeRunRetentionDays, currentSettings.AgentHomeRunRetentionDays, request.Clears(nameof(request.AgentHomeRunRetentionDays))),
            ToolPipelineMaxIterationsPerRequest = Merge(request.ToolPipelineMaxIterationsPerRequest, currentSettings.ToolPipelineMaxIterationsPerRequest,
                request.Clears(nameof(request.ToolPipelineMaxIterationsPerRequest))),
            ToolPipelineMaxToolResultChars = Merge(request.ToolPipelineMaxToolResultChars, currentSettings.ToolPipelineMaxToolResultChars,
                request.Clears(nameof(request.ToolPipelineMaxToolResultChars))),
            ToolPipelineMaxConsecutiveInvalidToolCalls = Merge(request.ToolPipelineMaxConsecutiveInvalidToolCalls, currentSettings.ToolPipelineMaxConsecutiveInvalidToolCalls,
                request.Clears(nameof(request.ToolPipelineMaxConsecutiveInvalidToolCalls))),
            DefaultContextTokens = Merge(request.DefaultContextTokens, currentSettings.DefaultContextTokens, request.Clears(nameof(request.DefaultContextTokens))),
            ProviderBudgetRecentMessagesToKeep = Merge(request.ProviderBudgetRecentMessagesToKeep, currentSettings.ProviderBudgetRecentMessagesToKeep,
                request.Clears(nameof(request.ProviderBudgetRecentMessagesToKeep))),
            ProviderBudgetMaxCumulativeInputTokens = Merge(request.ProviderBudgetMaxCumulativeInputTokens, currentSettings.ProviderBudgetMaxCumulativeInputTokens,
                request.Clears(nameof(request.ProviderBudgetMaxCumulativeInputTokens))),
            ContextBudgetRecentTurnKeepCount = Merge(request.ContextBudgetRecentTurnKeepCount, currentSettings.ContextBudgetRecentTurnKeepCount,
                request.Clears(nameof(request.ContextBudgetRecentTurnKeepCount))),
            CompactionAutoCompactPercent = Merge(request.CompactionAutoCompactPercent, currentSettings.CompactionAutoCompactPercent, request.Clears(nameof(request.CompactionAutoCompactPercent))),
            CompactionRecentMessagesVerbatim = Merge(request.CompactionRecentMessagesVerbatim, currentSettings.CompactionRecentMessagesVerbatim,
                request.Clears(nameof(request.CompactionRecentMessagesVerbatim))),
            MaxInlinedAttachmentChars = Merge(request.MaxInlinedAttachmentChars, currentSettings.MaxInlinedAttachmentChars, request.Clears(nameof(request.MaxInlinedAttachmentChars))),
            KnowledgeChatTopK = Merge(request.KnowledgeChatTopK, currentSettings.KnowledgeChatTopK, request.Clears(nameof(request.KnowledgeChatTopK))),
            ProviderMaxRetries = Merge(request.ProviderMaxRetries, currentSettings.ProviderMaxRetries, request.Clears(nameof(request.ProviderMaxRetries))),
            SpawnMaxConcurrent = Merge(request.SpawnMaxConcurrent, currentSettings.SpawnMaxConcurrent, request.Clears(nameof(request.SpawnMaxConcurrent))),
            SpawnMaxCloud = Merge(request.SpawnMaxCloud, currentSettings.SpawnMaxCloud, request.Clears(nameof(request.SpawnMaxCloud))),
            SpawnQueueWaitSeconds = Merge(request.SpawnQueueWaitSeconds, currentSettings.SpawnQueueWaitSeconds, request.Clears(nameof(request.SpawnQueueWaitSeconds))),
            CompactionAutoEnabled = Merge(request.CompactionAutoEnabled, currentSettings.CompactionAutoEnabled, request.Clears(nameof(request.CompactionAutoEnabled))),
            CompactionDistillEnabled = Merge(request.CompactionDistillEnabled, currentSettings.CompactionDistillEnabled, request.Clears(nameof(request.CompactionDistillEnabled))),
            ProviderRetryEnabled = Merge(request.ProviderRetryEnabled, currentSettings.ProviderRetryEnabled, request.Clears(nameof(request.ProviderRetryEnabled))),
            KnowledgeAdaptiveRerankingEnabled = Merge(request.KnowledgeAdaptiveRerankingEnabled, currentSettings.KnowledgeAdaptiveRerankingEnabled,
                request.Clears(nameof(request.KnowledgeAdaptiveRerankingEnabled))),
            KnowledgeRetrievalLatencyBudgetMs = Merge(request.KnowledgeRetrievalLatencyBudgetMs, currentSettings.KnowledgeRetrievalLatencyBudgetMs,
                request.Clears(nameof(request.KnowledgeRetrievalLatencyBudgetMs))),
            KnowledgeScheduledReindexEnabled = Merge(request.KnowledgeScheduledReindexEnabled, currentSettings.KnowledgeScheduledReindexEnabled,
                request.Clears(nameof(request.KnowledgeScheduledReindexEnabled))),
            KnowledgeScheduledReindexIntervalMinutes = Merge(request.KnowledgeScheduledReindexIntervalMinutes, currentSettings.KnowledgeScheduledReindexIntervalMinutes,
                request.Clears(nameof(request.KnowledgeScheduledReindexIntervalMinutes))),
            KnowledgeAgentToolsEnabled = Merge(request.KnowledgeAgentToolsEnabled, currentSettings.KnowledgeAgentToolsEnabled, request.Clears(nameof(request.KnowledgeAgentToolsEnabled))),
            AllowCloudModelAccess = Merge(request.AllowCloudModelAccess, currentSettings.AllowCloudModelAccess, request.Clears(nameof(request.AllowCloudModelAccess))),
            AllowCloudModelUnattendedRuns = Merge(request.AllowCloudModelUnattendedRuns, currentSettings.AllowCloudModelUnattendedRuns, request.Clears(nameof(request.AllowCloudModelUnattendedRuns))),
            AllowCloudModelWebTools = Merge(request.AllowCloudModelWebTools, currentSettings.AllowCloudModelWebTools, request.Clears(nameof(request.AllowCloudModelWebTools))),
            AllowCloudModelMcpTools = Merge(request.AllowCloudModelMcpTools, currentSettings.AllowCloudModelMcpTools, request.Clears(nameof(request.AllowCloudModelMcpTools))),
            AllowCloudModelSubAgents = Merge(request.AllowCloudModelSubAgents, currentSettings.AllowCloudModelSubAgents, request.Clears(nameof(request.AllowCloudModelSubAgents))),
            PlaybookAnalysisModelName = MergeText(request.PlaybookAnalysisModelName, currentSettings.PlaybookAnalysisModelName, request.Clears(nameof(request.PlaybookAnalysisModelName))),
            PlaybookEvalModelName = MergeText(request.PlaybookEvalModelName, currentSettings.PlaybookEvalModelName, request.Clears(nameof(request.PlaybookEvalModelName))),
            MemoryExtractionModelName = MergeText(request.MemoryExtractionModelName, currentSettings.MemoryExtractionModelName, request.Clears(nameof(request.MemoryExtractionModelName))),
            ChatRetentionEnabled = Merge(request.ChatRetentionEnabled, currentSettings.ChatRetentionEnabled, request.Clears(nameof(request.ChatRetentionEnabled))),
            ChatRetentionDays = Merge(request.ChatRetentionDays, currentSettings.ChatRetentionDays, request.Clears(nameof(request.ChatRetentionDays))),
            AgentExecutionLogRetentionEnabled = Merge(request.AgentExecutionLogRetentionEnabled, currentSettings.AgentExecutionLogRetentionEnabled,
                request.Clears(nameof(request.AgentExecutionLogRetentionEnabled))),
            AgentExecutionLogRetentionDays = Merge(request.AgentExecutionLogRetentionDays, currentSettings.AgentExecutionLogRetentionDays,
                request.Clears(nameof(request.AgentExecutionLogRetentionDays))),
            NodeDbBackupRetainCount = Merge(request.NodeDbBackupRetainCount, currentSettings.NodeDbBackupRetainCount, request.Clears(nameof(request.NodeDbBackupRetainCount))),
            BenchmarkKldCacheMaxBytes = Merge(request.BenchmarkKldCacheMaxBytes, currentSettings.BenchmarkKldCacheMaxBytes, request.Clears(nameof(request.BenchmarkKldCacheMaxBytes))),
            SchedulerHistoryRetentionDays = Merge(request.SchedulerHistoryRetentionDays, currentSettings.SchedulerHistoryRetentionDays, request.Clears(nameof(request.SchedulerHistoryRetentionDays))),
            ImageMaxLoadedProcesses = Merge(request.ImageMaxLoadedProcesses, currentSettings.ImageMaxLoadedProcesses, request.Clears(nameof(request.ImageMaxLoadedProcesses))),
            ImageTextEncoderOnGpu = Merge(request.ImageTextEncoderOnGpu, currentSettings.ImageTextEncoderOnGpu, request.Clears(nameof(request.ImageTextEncoderOnGpu))),
            GraphWorkflowMaxConcurrentRuns = Merge(request.GraphWorkflowMaxConcurrentRuns, currentSettings.GraphWorkflowMaxConcurrentRuns,
                request.Clears(nameof(request.GraphWorkflowMaxConcurrentRuns))),
            GraphWorkflowDefaultNodeTimeoutSeconds = Merge(request.GraphWorkflowDefaultNodeTimeoutSeconds, currentSettings.GraphWorkflowDefaultNodeTimeoutSeconds,
                request.Clears(nameof(request.GraphWorkflowDefaultNodeTimeoutSeconds))),
            WorkSessionMaxStepsPerRun = Merge(request.WorkSessionMaxStepsPerRun, currentSettings.WorkSessionMaxStepsPerRun, request.Clears(nameof(request.WorkSessionMaxStepsPerRun))),
            WorkSessionMaxConcurrentSessions = Merge(request.WorkSessionMaxConcurrentSessions, currentSettings.WorkSessionMaxConcurrentSessions,
                request.Clears(nameof(request.WorkSessionMaxConcurrentSessions))),
            DevelopmentMaxAttemptDurationSeconds = Merge(request.DevelopmentMaxAttemptDurationSeconds, currentSettings.DevelopmentMaxAttemptDurationSeconds,
                request.Clears(nameof(request.DevelopmentMaxAttemptDurationSeconds))),
            DevelopmentMaxToolCalls = Merge(request.DevelopmentMaxToolCalls, currentSettings.DevelopmentMaxToolCalls, request.Clears(nameof(request.DevelopmentMaxToolCalls))),
            DevelopmentMaxOutputTokens = Merge(request.DevelopmentMaxOutputTokens, currentSettings.DevelopmentMaxOutputTokens, request.Clears(nameof(request.DevelopmentMaxOutputTokens))),
            AgentHomeMaxInnerToolCalls = Merge(request.AgentHomeMaxInnerToolCalls, currentSettings.AgentHomeMaxInnerToolCalls, request.Clears(nameof(request.AgentHomeMaxInnerToolCalls))),
            AgentHomePatchApplyTimeoutSeconds = Merge(request.AgentHomePatchApplyTimeoutSeconds, currentSettings.AgentHomePatchApplyTimeoutSeconds,
                request.Clears(nameof(request.AgentHomePatchApplyTimeoutSeconds))),
            AgentHomeRunRetentionMaxRuns = Merge(request.AgentHomeRunRetentionMaxRuns, currentSettings.AgentHomeRunRetentionMaxRuns, request.Clears(nameof(request.AgentHomeRunRetentionMaxRuns))),
            AgentHomeRunRetentionMaxTotalBytes = Merge(request.AgentHomeRunRetentionMaxTotalBytes, currentSettings.AgentHomeRunRetentionMaxTotalBytes,
                request.Clears(nameof(request.AgentHomeRunRetentionMaxTotalBytes))),
            DevelopmentEnabled = Merge(request.DevelopmentEnabled, currentSettings.DevelopmentEnabled, request.Clears(nameof(request.DevelopmentEnabled))),
            WorkSessionsEnabled = Merge(request.WorkSessionsEnabled, currentSettings.WorkSessionsEnabled, request.Clears(nameof(request.WorkSessionsEnabled))),
            GraphWorkflowsEnabled = Merge(request.GraphWorkflowsEnabled, currentSettings.GraphWorkflowsEnabled, request.Clears(nameof(request.GraphWorkflowsEnabled))),
            TranscriptionEnabled = Merge(request.TranscriptionEnabled, currentSettings.TranscriptionEnabled, request.Clears(nameof(request.TranscriptionEnabled))),
            ExternalAppsEnabled = Merge(request.ExternalAppsEnabled, currentSettings.ExternalAppsEnabled, request.Clears(nameof(request.ExternalAppsEnabled))),
            ComputeEnabled = Merge(request.ComputeEnabled, currentSettings.ComputeEnabled, request.Clears(nameof(request.ComputeEnabled))),
            AgentHomeEnabled = Merge(request.AgentHomeEnabled, currentSettings.AgentHomeEnabled, request.Clears(nameof(request.AgentHomeEnabled))),
            SchedulerEnabled = Merge(request.SchedulerEnabled, currentSettings.SchedulerEnabled, request.Clears(nameof(request.SchedulerEnabled))),
            DevWorkflowsEnabled = Merge(request.DevWorkflowsEnabled, currentSettings.DevWorkflowsEnabled, request.Clears(nameof(request.DevWorkflowsEnabled))),
            ExecutionPreviewsEnabled = Merge(request.ExecutionPreviewsEnabled, currentSettings.ExecutionPreviewsEnabled, request.Clears(nameof(request.ExecutionPreviewsEnabled)))
        };
    }

    /// <summary>
    ///     The ONE owner of the external-access stamp: the four members are returned together because the profile is a
    ///     record of which preset is in force, so it can only be decided alongside the triple it describes.
    /// </summary>
    /// <remarks>
    ///     A request carrying a PRESET (<c>recommended</c> or <c>offline</c>; the validator rejected anything else)
    ///     writes that literal and that preset's triple, ignoring any switch sent with it — one bool drives all three,
    ///     so the presets cannot drift apart. A request carrying at least one switch (an explicit null clears that switch
    ///     to its default and counts as carried) writes those switches and stamps
    ///     the preset the resulting triple matches, else <c>custom</c>. Any other save preserves all four.
    /// </remarks>
    private static ExternalAccessStamp ApplyExternalAccess(SaveNodeSettingsRequest request,
        StoredNodeSettings currentSettings)
    {
        if (StoredNodeSettings.IsExternalAccessPreset(request.ExternalAccessProfile))
        {
            var enabled = string.Equals(request.ExternalAccessProfile, StoredNodeSettings.ExternalAccessProfileRecommended, StringComparison.Ordinal);
            return new ExternalAccessStamp(request.ExternalAccessProfile, enabled, enabled, enabled);
        }

        var clearsApplicationUpdates = request.Clears(nameof(request.AutoCheckApplicationUpdates));
        var clearsRuntimeUpdates = request.Clears(nameof(request.AutoCheckRuntimeUpdates));
        var clearsFirstRunModel = request.Clears(nameof(request.AutoProvisionFirstRunModel));
        if (request.AutoCheckApplicationUpdates is null && !clearsApplicationUpdates
                                                        && request.AutoCheckRuntimeUpdates is null && !clearsRuntimeUpdates
                                                        && request.AutoProvisionFirstRunModel is null && !clearsFirstRunModel)
        {
            return new ExternalAccessStamp(currentSettings.ExternalAccessProfile,
                currentSettings.AutoCheckApplicationUpdates,
                currentSettings.AutoCheckRuntimeUpdates,
                currentSettings.AutoProvisionFirstRunModel);
        }

        var applicationUpdates = Merge(request.AutoCheckApplicationUpdates, currentSettings.AutoCheckApplicationUpdates, clearsApplicationUpdates);
        var runtimeUpdates = Merge(request.AutoCheckRuntimeUpdates, currentSettings.AutoCheckRuntimeUpdates, clearsRuntimeUpdates);
        var firstRunModel = Merge(request.AutoProvisionFirstRunModel, currentSettings.AutoProvisionFirstRunModel, clearsFirstRunModel);
        var allOn = (applicationUpdates ?? StoredNodeSettings.DefaultAutoCheckApplicationUpdates)
                    && (runtimeUpdates ?? StoredNodeSettings.DefaultAutoCheckRuntimeUpdates)
                    && (firstRunModel ?? StoredNodeSettings.DefaultAutoProvisionFirstRunModel);
        var allOff = applicationUpdates == false && runtimeUpdates == false && firstRunModel == false;
        var profile = StoredNodeSettings.ExternalAccessProfileCustom;
        if (allOn)
        {
            profile = StoredNodeSettings.ExternalAccessProfileRecommended;
        }
        else if (allOff)
        {
            profile = StoredNodeSettings.ExternalAccessProfileOffline;
        }

        return new ExternalAccessStamp(profile, applicationUpdates, runtimeUpdates, firstRunModel);
    }

    /// <summary>The external-access profile stamp and the three switches it describes.</summary>
    private readonly record struct ExternalAccessStamp(string? Profile, bool? ApplicationUpdates, bool? RuntimeUpdates, bool? FirstRunModel);

    /// <summary>Omitted keeps the stored value, an explicit null clears it, a value sets it.</summary>
    private static T? Merge<T>(T? requested, T? current, bool clears)
        where T : struct
    {
        return clears ? null : requested ?? current;
    }

    /// <summary>The <see cref="Merge{T}" /> of a reference-typed member.</summary>
    private static T? MergeObject<T>(T? requested, T? current, bool clears)
        where T : class
    {
        return clears ? null : requested ?? current;
    }

    /// <summary>The <see cref="Merge{T}" /> of a text member: a supplied value is trimmed.</summary>
    private static string? MergeText(string? requested, string? current, bool clears)
    {
        return clears ? null : requested?.Trim() ?? current;
    }
}
