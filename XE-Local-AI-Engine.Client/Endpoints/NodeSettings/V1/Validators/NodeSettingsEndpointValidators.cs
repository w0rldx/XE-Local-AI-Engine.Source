namespace XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1.Validators;

using System.Globalization;
using System.Numerics;
using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Boundary validation for <see cref="SaveNodeSettingsRequest" />.
/// </summary>
/// <remarks>
///     Omitted keeps and null clears, so value rules skip null (range rules bind the nullable property and report the plain
///     field name). Null is the only clear signal: -1 and blank strings fail with a message naming null, and null on a
///     first-run decision is refused. Violations are a 400 before anything is persisted; the store's <c>Normalize</c>
///     remains the defense-in-depth clamp.
/// </remarks>
public sealed class SaveNodeSettingsRequestValidator : Validator<SaveNodeSettingsRequest>
{
    private static readonly string ReasoningBudgetRangeMessage = string.Create(CultureInfo.InvariantCulture,
        $"A reasoning budget must be from {StoredNodeSettings.MinReasoningBudgetTokens} to {StoredNodeSettings.MaxReasoningBudgetTokens} tokens, or null for the default.");

    private static readonly string ChatOutputCapMaxTokensRangeMessage = string.Create(CultureInfo.InvariantCulture,
        $"The chat output cap ceiling must be from {StoredNodeSettings.MinChatOutputCapMaxTokens} to {StoredNodeSettings.MaxChatOutputCapMaxTokens} tokens, or null for the default.");

    private const string PositiveOrNullMessage = "'{PropertyName}' must be greater than '0', or null for the default. You entered {PropertyValue}.";

    public SaveNodeSettingsRequestValidator()
    {
        RuleFor(static request => request.MaxMessageRequestTimeoutSeconds)
            .InclusiveBetween(StoredNodeSettings.MinMaxMessageRequestTimeoutSeconds, StoredNodeSettings.MaxMaxMessageRequestTimeoutSeconds)
            .WithMessage(string.Create(CultureInfo.InvariantCulture,
                $"'{{PropertyName}}' must be between {StoredNodeSettings.MinMaxMessageRequestTimeoutSeconds} and {StoredNodeSettings.MaxMaxMessageRequestTimeoutSeconds}, or null to reset it to the default. You entered {{PropertyValue}}."));

        NotBlank(RuleFor(static request => request.DefaultModelName));
        NotBlank(RuleFor(static request => request.WebSearchSearxngUrl));
        NotBlank(RuleFor(static request => request.OllamaEndpoint));
        NotBlank(RuleFor(static request => request.HuggingFaceDefaultQuant));
        NotBlank(RuleFor(static request => request.KeepModelWarmModelName));
        NotBlank(RuleFor(static request => request.RecommendedLlamaCppTag));
        NotBlank(RuleFor(static request => request.SpeculativeMode));
        NotBlank(RuleFor(static request => request.KvCacheType));
        NotBlank(RuleFor(static request => request.ContainerRuntimeSelection));
        NotBlank(RuleFor(static request => request.SpeculativeDraftModelName));
        NotBlank(RuleFor(static request => request.RerankerModelName));
        NotBlank(RuleFor(static request => request.KnowledgeEmbeddingModelName));
        NotBlank(RuleFor(static request => request.AutoEffortFastModelName));
        NotBlank(RuleFor(static request => request.DefaultVoiceProfile));
        NotBlank(RuleFor(static request => request.IntentRouterDefinitionId));
        NotBlank(RuleFor(static request => request.DefaultReasoningEffort));
        NotBlank(RuleFor(static request => request.ChatOutputCapMode));
        NotBlank(RuleFor(static request => request.PlaybookAnalysisModelName));
        NotBlank(RuleFor(static request => request.PlaybookEvalModelName));
        NotBlank(RuleFor(static request => request.MemoryExtractionModelName));
        NotBlank(RuleFor(static request => request.UpdateChannel));

        // These record first-run decisions; a stored null re-opens the decision, so no client may clear one.
        NotClearable(RuleFor(static request => request.ExternalAccessProfile), nameof(SaveNodeSettingsRequest.ExternalAccessProfile));
        NotClearable(RuleFor(static request => request.UiMode), nameof(SaveNodeSettingsRequest.UiMode));
        NotClearable(RuleFor(static request => request.SandboxSecurityProfile), nameof(SaveNodeSettingsRequest.SandboxSecurityProfile));

        RuleFor(static request => request.OllamaEndpoint!)
            .Must(BeAbsoluteHttpUrl)
            .When(static request => !string.IsNullOrWhiteSpace(request.OllamaEndpoint))
            .WithMessage("Ollama endpoint must be an absolute http or https URL.");

        RuleFor(static request => request.WebSearchSearxngUrl!)
            .Must(BeAbsoluteHttpUrl)
            .When(static request => !string.IsNullOrWhiteSpace(request.WebSearchSearxngUrl))
            .WithMessage("SearXNG URL must be an absolute http or https URL.");

        // Shape only, never "is it installed": the default is legitimately set before its download, and null clears it to the seed.
        RuleFor(static request => request.DefaultModelName!)
            .Must(StoredNodeSettings.IsPlausibleModelName)
            .When(static request => !string.IsNullOrWhiteSpace(request.DefaultModelName))
            .WithMessage(StoredNodeSettings.ImplausibleDefaultModelNameMessage);

        RuleFor(static request => request.KeepModelWarmModelName!)
            .Must(StoredNodeSettings.IsPlausibleModelName)
            .When(static request => !string.IsNullOrWhiteSpace(request.KeepModelWarmModelName))
            .WithMessage(StoredNodeSettings.ImplausibleModelNameMessage);

        RuleFor(static request => request.RerankerModelName!)
            .Must(StoredNodeSettings.IsPlausibleModelName)
            .When(static request => !string.IsNullOrWhiteSpace(request.RerankerModelName))
            .WithMessage(StoredNodeSettings.ImplausibleModelNameMessage);

        // Shape here; "is it an installed embedding model" runs on save in the administration service.
        RuleFor(static request => request.KnowledgeEmbeddingModelName!)
            .Must(StoredNodeSettings.IsPlausibleModelName)
            .When(static request => !string.IsNullOrWhiteSpace(request.KnowledgeEmbeddingModelName))
            .WithMessage(StoredNodeSettings.ImplausibleModelNameMessage);

        RuleFor(static request => request.AutoEffortFastModelName!)
            .Must(StoredNodeSettings.IsPlausibleModelName)
            .When(static request => !string.IsNullOrWhiteSpace(request.AutoEffortFastModelName))
            .WithMessage(StoredNodeSettings.ImplausibleModelNameMessage);

        RuleFor(static request => request.HuggingFaceDefaultQuant!)
            .Must(StoredNodeSettings.IsKnownQuant)
            .When(static request => !string.IsNullOrWhiteSpace(request.HuggingFaceDefaultQuant))
            .WithMessage(StoredNodeSettings.UnknownHuggingFaceDefaultQuantMessage);

        RuleFor(static request => request.ToolCapableModels!)
            .Must(static models => models.Count > 0 && models.All(static model => !string.IsNullOrWhiteSpace(model)))
            .When(static request => request.ToolCapableModels is not null)
            .WithMessage("Tool-capable models must be a non-empty list of non-blank model names.");

        RuleFor(static request => request.RecommendedLlamaCppTag)
            .Must(StoredNodeSettings.IsValidRecommendedLlamaCppTag)
            .When(static request => !string.IsNullOrWhiteSpace(request.RecommendedLlamaCppTag))
            .WithMessage("Recommended llama.cpp tag must be in the form b<number>.");

        // IsExternalAccessPreset, NOT IsValidExternalAccessProfile: "custom" and "pending" are persistable but
        // engine-written, so a client must never be able to claim either as input.
        RuleFor(static request => request.ExternalAccessProfile)
            .Must(StoredNodeSettings.IsExternalAccessPreset)
            .When(static request => request.ExternalAccessProfile is not null)
            .WithMessage("External access profile must be recommended or offline.");

        // Both storable literals are client-sendable here (there is no engine-written third state), so the boundary
        // allow-list IS the full set. A blank string is rejected rather than read as "keep": only an absent member keeps.
        RuleFor(static request => request.UiMode)
            .Must(StoredNodeSettings.IsValidUiMode)
            .When(static request => request.UiMode is not null)
            .WithMessage("Interface mode must be simple or advanced.");

        // The CHOICE allow-list, not the persistable one: "pending" is engine-written by setup, so a client must never claim it as input.
        RuleFor(static request => request.SandboxSecurityProfile)
            .Must(StoredNodeSettings.IsSandboxSecurityProfileChoice)
            .When(static request => request.SandboxSecurityProfile is not null)
            .WithMessage("Sandbox security profile must be low or high.");

        // Same allow-list and same message as the dedicated PUT app-update/channel validator, so the two surfaces
        // that may write the channel cannot drift apart.
        RuleFor(static request => request.UpdateChannel)
            .Must(StoredNodeSettings.IsValidUpdateChannel)
            .When(static request => !string.IsNullOrWhiteSpace(request.UpdateChannel))
            .WithMessage("Update channel must be stable, preview or development.");

        NullOrBetween(RuleFor(static request => request.LlamaMaxLoadedProcesses),
            StoredNodeSettings.MinLlamaMaxLoadedProcesses, StoredNodeSettings.MaxLlamaMaxLoadedProcesses);

        NullOrBetween(RuleFor(static request => request.LlamaIdleTimeToLiveSeconds),
            StoredNodeSettings.MinLlamaIdleTimeToLiveSeconds, StoredNodeSettings.MaxLlamaIdleTimeToLiveSeconds);

        NullOrBetween(RuleFor(static request => request.KeepModelWarmIntervalSeconds),
            StoredNodeSettings.MinKeepModelWarmIntervalSeconds, StoredNodeSettings.MaxKeepModelWarmIntervalSeconds);

        NullOrBetween(RuleFor(static request => request.MaxResponseSizeMb),
            StoredNodeSettings.MinMaxResponseSizeMb, StoredNodeSettings.MaxMaxResponseSizeMb);

        NullOrBetween(RuleFor(static request => request.ChatCacheReuse),
            StoredNodeSettings.MinChatCacheReuse, StoredNodeSettings.MaxChatCacheReuse);

        RuleFor(static request => request.SpeculativeMode)
            .Must(StoredNodeSettings.IsValidSpeculativeMode)
            .When(static request => !string.IsNullOrWhiteSpace(request.SpeculativeMode))
            .WithMessage("Unknown speculative decoding mode.");

        RuleFor(static request => request.KvCacheType)
            .Must(StoredNodeSettings.IsValidKvCacheType)
            .When(static request => !string.IsNullOrWhiteSpace(request.KvCacheType))
            .WithMessage("Unknown KV cache type.");

        // The parser IS the allow-list — one producer, so the accepted set cannot drift from the spelling Format writes — and it is case-insensitive, so a hand-written
        // client's "Docker" is accepted and normalized while "Podman" answers 400. A blank string is refused by NotBlank; null clears it.
        RuleFor(static request => request.ContainerRuntimeSelection)
            .Must(StoredNodeSettings.IsValidContainerRuntimeSelection)
            .When(static request => !string.IsNullOrWhiteSpace(request.ContainerRuntimeSelection))
            .WithMessage("Unknown container runtime selection. Use 'auto' or 'docker'.");

        NullOrBetween(RuleFor(static request => request.SpeculativeDraftMaxTokens),
            StoredNodeSettings.MinSpeculativeDraftMaxTokens, StoredNodeSettings.MaxSpeculativeDraftMaxTokens);

        NullOrBetween(RuleFor(static request => request.SpeculativeDraftGpuLayers),
            StoredNodeSettings.MinSpeculativeDraftGpuLayers, StoredNodeSettings.MaxSpeculativeDraftGpuLayers);

        // Cross-field: a draft-* mode needs a draft model. This boundary rule fires when the request itself sets a draft-* SpeculativeMode, catching "pick draft mode, forget
        // the model" with an immediate 400; it cannot see the CURRENT stored mode, so the endpoint re-checks the merged result — together, no draft-* mode persists modelless.
        RuleFor(static request => request.SpeculativeDraftModelName)
            .Must(static name => !string.IsNullOrWhiteSpace(name))
            .When(static request => StoredNodeSettings.SpeculativeModeRequiresDraftModel(request.SpeculativeMode))
            .WithMessage("Speculative decoding is set to a draft model mode, but no draft model was selected.");

        NullOrBetween(RuleFor(static request => request.HuggingFaceDiskMarginBytes),
            StoredNodeSettings.MinHuggingFaceDiskMarginBytes, StoredNodeSettings.MaxHuggingFaceDiskMarginBytes);

        NullOrBetween(RuleFor(static request => request.OrchestrationIdleTimeoutSeconds),
            StoredNodeSettings.MinOrchestrationIdleTimeoutSeconds, StoredNodeSettings.MaxOrchestrationIdleTimeoutSeconds);

        NullOrBetween(RuleFor(static request => request.AgentHomePrepareTimeoutSeconds),
            StoredNodeSettings.MinAgentHomeTimeoutSeconds, StoredNodeSettings.MaxAgentHomeTimeoutSeconds);

        NullOrBetween(RuleFor(static request => request.AgentHomeCommandTimeoutSeconds),
            StoredNodeSettings.MinAgentHomeTimeoutSeconds, StoredNodeSettings.MaxAgentHomeTimeoutSeconds);

        RuleFor(static request => request.AgentHomeMaxSelectedFolderBytes)
            .Must(static bytes => bytes is null or > 0)
            .WithMessage(PositiveOrNullMessage);

        RuleFor(static request => request.AgentHomeMaxPatchBytes)
            .Must(static bytes => bytes is null or > 0)
            .WithMessage(PositiveOrNullMessage);

        NullOrBetween(RuleFor(static request => request.MaxPendingToolCallAgeMinutes),
            StoredNodeSettings.MinMaxPendingToolCallAgeMinutes, StoredNodeSettings.MaxMaxPendingToolCallAgeMinutes);

        NullOrBetween(RuleFor(static request => request.DetachedGraceSeconds),
            StoredNodeSettings.MinDetachedGraceSeconds, StoredNodeSettings.MaxDetachedGraceSeconds);

        NullOrBetween(RuleFor(static request => request.TranscriptionIdleTimeoutMinutes),
            StoredNodeSettings.MinTranscriptionIdleTimeoutMinutes, StoredNodeSettings.MaxTranscriptionIdleTimeoutMinutes);

        NullOrBetween(RuleFor(static request => request.LlamaReadinessTimeoutCapSeconds),
            StoredNodeSettings.MinLlamaReadinessTimeoutCapSeconds, StoredNodeSettings.MaxLlamaReadinessTimeoutCapSeconds);

        NullOrBetween(RuleFor(static request => request.LlamaChatHttpTimeoutSeconds),
            StoredNodeSettings.MinLlamaChatHttpTimeoutSeconds, StoredNodeSettings.MaxLlamaChatHttpTimeoutSeconds);

        NullOrBetween(RuleFor(static request => request.LlamaEmbeddingHttpTimeoutSeconds),
            StoredNodeSettings.MinLlamaEmbeddingHttpTimeoutSeconds, StoredNodeSettings.MaxLlamaEmbeddingHttpTimeoutSeconds);

        NullOrBetween(RuleFor(static request => request.LlamaChatCacheRamMiB),
            StoredNodeSettings.MinLlamaChatCacheRamMiB, StoredNodeSettings.MaxLlamaChatCacheRamMiB);

        NullOrBetween(RuleFor(static request => request.LlamaCpuThreadReserve),
            StoredNodeSettings.MinLlamaCpuThreadReserve, StoredNodeSettings.MaxLlamaCpuThreadReserve);

        NullOrBetween(RuleFor(static request => request.LlamaGpuReservePercent),
            StoredNodeSettings.MinLlamaGpuReservePercent, StoredNodeSettings.MaxLlamaGpuReservePercent);

        NullOrBetween(RuleFor(static request => request.LlamaRamReservePercent),
            StoredNodeSettings.MinLlamaRamReservePercent, StoredNodeSettings.MaxLlamaRamReservePercent);

        NullOrBetween(RuleFor(static request => request.ImageIdleTimeToLiveSeconds),
            StoredNodeSettings.MinImageIdleTimeToLiveSeconds, StoredNodeSettings.MaxImageIdleTimeToLiveSeconds);

        NullOrBetween(RuleFor(static request => request.ModelFitSafetyMarginPercent),
            StoredNodeSettings.MinModelFitSafetyMarginPercent, StoredNodeSettings.MaxModelFitSafetyMarginPercent);

        NullOrBetween(RuleFor(static request => request.MaxProviderCallsPerInvocation),
            StoredNodeSettings.MinMaxProviderCallsPerInvocation, StoredNodeSettings.MaxMaxProviderCallsPerInvocation);

        NullOrBetween(RuleFor(static request => request.CustomToolMaxTimeoutSeconds),
            StoredNodeSettings.MinCustomToolMaxTimeoutSeconds, StoredNodeSettings.MaxCustomToolMaxTimeoutSeconds);

        NullOrBetween(RuleFor(static request => request.WebFetchTimeoutSeconds),
            StoredNodeSettings.MinWebFetchTimeoutSeconds, StoredNodeSettings.MaxWebFetchTimeoutSeconds);

        RuleFor(static request => request.IntentRouterDefinitionId)
            .Must(static id => Guid.TryParse(id, out var parsed) && parsed != Guid.Empty)
            .When(static request => !string.IsNullOrWhiteSpace(request.IntentRouterDefinitionId))
            .WithMessage("The intent router must be a graph workflow id, or null to route on aliases alone.");

        NullOrBetween(RuleFor(static request => request.IntentMaxUtteranceAgeSeconds),
            StoredNodeSettings.MinIntentMaxUtteranceAgeSeconds, StoredNodeSettings.MaxIntentMaxUtteranceAgeSeconds);

        NullOrBetween(RuleFor(static request => request.WebFetchMaxContentChars),
            StoredNodeSettings.MinWebFetchMaxContentChars, StoredNodeSettings.MaxWebFetchMaxContentChars);

        NullOrBetween(RuleFor(static request => request.KnowledgeSearchDefaultResults),
            StoredNodeSettings.MinKnowledgeSearchResults, StoredNodeSettings.MaxKnowledgeSearchResults);

        NullOrBetween(RuleFor(static request => request.KnowledgeSearchMaxResults),
            StoredNodeSettings.MinKnowledgeSearchResults, StoredNodeSettings.MaxKnowledgeSearchResults);

        // Null is the "back to default" signal; everything else is range-checked. Must, not InclusiveBetween, so the message can say
        // to send null and the published schema is unchanged.
        RuleFor(static request => request.ReasoningBudgetMinimalTokens)
            .Must(static tokens => IsNullOrBetween(tokens, StoredNodeSettings.MinReasoningBudgetTokens, StoredNodeSettings.MaxReasoningBudgetTokens))
            .WithMessage(ReasoningBudgetRangeMessage);

        RuleFor(static request => request.ReasoningBudgetLowTokens)
            .Must(static tokens => IsNullOrBetween(tokens, StoredNodeSettings.MinReasoningBudgetTokens, StoredNodeSettings.MaxReasoningBudgetTokens))
            .WithMessage(ReasoningBudgetRangeMessage);

        RuleFor(static request => request.ReasoningBudgetMediumTokens)
            .Must(static tokens => IsNullOrBetween(tokens, StoredNodeSettings.MinReasoningBudgetTokens, StoredNodeSettings.MaxReasoningBudgetTokens))
            .WithMessage(ReasoningBudgetRangeMessage);

        RuleFor(static request => request.ReasoningBudgetHighTokens)
            .Must(static tokens => IsNullOrBetween(tokens, StoredNodeSettings.MinReasoningBudgetTokens, StoredNodeSettings.MaxReasoningBudgetTokens))
            .WithMessage(ReasoningBudgetRangeMessage);

        RuleFor(static request => request.DefaultReasoningEffort)
            .Must(static effort => StoredNodeSettings.IsValidDefaultReasoningEffort(effort?.Trim()))
            .When(static request => !string.IsNullOrWhiteSpace(request.DefaultReasoningEffort))
            .WithMessage("Default reasoning effort must be minimal, low, medium or high, or null for the default.");

        RuleFor(static request => request.ChatOutputCapMode)
            .Must(static mode => StoredNodeSettings.IsValidChatOutputCapMode(mode?.Trim()))
            .When(static request => !string.IsNullOrWhiteSpace(request.ChatOutputCapMode))
            .WithMessage("Chat output cap mode must be cap, notice or off, or null for the default.");

        RuleFor(static request => request.ChatOutputCapMaxTokens)
            .Must(static tokens => IsNullOrBetween(tokens, StoredNodeSettings.MinChatOutputCapMaxTokens, StoredNodeSettings.MaxChatOutputCapMaxTokens))
            .WithMessage(ChatOutputCapMaxTokensRangeMessage);

        NullOrBetween(RuleFor(static request => request.HuggingFaceDownloadConnections),
            StoredNodeSettings.MinHuggingFaceDownloadConnections, StoredNodeSettings.MaxHuggingFaceDownloadConnections);

        NullOrBetween(RuleFor(static request => request.TranscriptionInferenceTimeoutMinutes),
            StoredNodeSettings.MinTranscriptionInferenceTimeoutMinutes, StoredNodeSettings.MaxTranscriptionInferenceTimeoutMinutes);

        NullOrBetween(RuleFor(static request => request.AgentHomeMaxRunSeconds),
            StoredNodeSettings.MinAgentHomeMaxRunSeconds, StoredNodeSettings.MaxAgentHomeMaxRunSeconds);

        NullOrBetween(RuleFor(static request => request.AgentHomeRunRetentionDays),
            StoredNodeSettings.MinAgentHomeRunRetentionDays, StoredNodeSettings.MaxAgentHomeRunRetentionDays);

        NullOrBetween(RuleFor(static request => request.ToolPipelineMaxIterationsPerRequest),
            StoredNodeSettings.MinToolPipelineMaxIterationsPerRequest, StoredNodeSettings.MaxToolPipelineMaxIterationsPerRequest);

        NullOrBetween(RuleFor(static request => request.ToolPipelineMaxToolResultChars),
            StoredNodeSettings.MinToolPipelineMaxToolResultChars, StoredNodeSettings.MaxToolPipelineMaxToolResultChars);

        NullOrBetween(RuleFor(static request => request.ToolPipelineMaxConsecutiveInvalidToolCalls),
            StoredNodeSettings.MinToolPipelineMaxConsecutiveInvalidToolCalls, StoredNodeSettings.MaxToolPipelineMaxConsecutiveInvalidToolCalls);

        NullOrBetween(RuleFor(static request => request.DefaultContextTokens),
            StoredNodeSettings.MinDefaultContextTokens, StoredNodeSettings.MaxDefaultContextTokens);

        NullOrBetween(RuleFor(static request => request.ProviderBudgetRecentMessagesToKeep),
            StoredNodeSettings.MinProviderBudgetRecentMessagesToKeep, StoredNodeSettings.MaxProviderBudgetRecentMessagesToKeep);

        NullOrBetween(RuleFor(static request => request.ProviderBudgetMaxCumulativeInputTokens),
            StoredNodeSettings.MinProviderBudgetMaxCumulativeInputTokens, StoredNodeSettings.MaxProviderBudgetMaxCumulativeInputTokens);

        NullOrBetween(RuleFor(static request => request.ContextBudgetRecentTurnKeepCount),
            StoredNodeSettings.MinContextBudgetRecentTurnKeepCount, StoredNodeSettings.MaxContextBudgetRecentTurnKeepCount);

        NullOrBetween(RuleFor(static request => request.CompactionAutoCompactPercent),
            StoredNodeSettings.MinCompactionAutoCompactPercent, StoredNodeSettings.MaxCompactionAutoCompactPercent);

        NullOrBetween(RuleFor(static request => request.CompactionRecentMessagesVerbatim),
            StoredNodeSettings.MinCompactionRecentMessagesVerbatim, StoredNodeSettings.MaxCompactionRecentMessagesVerbatim);

        NullOrBetween(RuleFor(static request => request.MaxInlinedAttachmentChars),
            StoredNodeSettings.MinMaxInlinedAttachmentChars, StoredNodeSettings.MaxMaxInlinedAttachmentChars);

        NullOrBetween(RuleFor(static request => request.KnowledgeChatTopK),
            StoredNodeSettings.MinKnowledgeChatTopK, StoredNodeSettings.MaxKnowledgeChatTopK);

        NullOrBetween(RuleFor(static request => request.ProviderMaxRetries),
            StoredNodeSettings.MinProviderMaxRetries, StoredNodeSettings.MaxProviderMaxRetries);

        NullOrBetween(RuleFor(static request => request.SpawnMaxConcurrent),
            StoredNodeSettings.MinSpawnMaxConcurrent, StoredNodeSettings.MaxSpawnMaxConcurrent);

        NullOrBetween(RuleFor(static request => request.SpawnMaxCloud),
            StoredNodeSettings.MinSpawnMaxCloud, StoredNodeSettings.MaxSpawnMaxCloud);

        NullOrBetween(RuleFor(static request => request.SpawnQueueWaitSeconds),
            StoredNodeSettings.MinSpawnQueueWaitSeconds, StoredNodeSettings.MaxSpawnQueueWaitSeconds);

        NullOrBetween(RuleFor(static request => request.KnowledgeRetrievalLatencyBudgetMs),
            StoredNodeSettings.MinKnowledgeRetrievalLatencyBudgetMs, StoredNodeSettings.MaxKnowledgeRetrievalLatencyBudgetMs);

        NullOrBetween(RuleFor(static request => request.KnowledgeScheduledReindexIntervalMinutes),
            StoredNodeSettings.MinKnowledgeScheduledReindexIntervalMinutes, StoredNodeSettings.MaxKnowledgeScheduledReindexIntervalMinutes);

        NullOrBetween(RuleFor(static request => request.ChatRetentionDays),
            StoredNodeSettings.MinRetentionDays, StoredNodeSettings.MaxRetentionDays);

        NullOrBetween(RuleFor(static request => request.AgentExecutionLogRetentionDays),
            StoredNodeSettings.MinRetentionDays, StoredNodeSettings.MaxRetentionDays);

        NullOrBetween(RuleFor(static request => request.NodeDbBackupRetainCount),
            StoredNodeSettings.MinNodeDbBackupRetainCount, StoredNodeSettings.MaxNodeDbBackupRetainCount);

        NullOrBetween(RuleFor(static request => request.BenchmarkKldCacheMaxBytes),
            StoredNodeSettings.MinBenchmarkKldCacheMaxBytes, StoredNodeSettings.MaxBenchmarkKldCacheMaxBytes);

        NullOrBetween(RuleFor(static request => request.SchedulerHistoryRetentionDays),
            StoredNodeSettings.MinRetentionDays, StoredNodeSettings.MaxRetentionDays);

        NullOrBetween(RuleFor(static request => request.ImageMaxLoadedProcesses),
            StoredNodeSettings.MinImageMaxLoadedProcesses, StoredNodeSettings.MaxImageMaxLoadedProcesses);

        NullOrBetween(RuleFor(static request => request.GraphWorkflowMaxConcurrentRuns),
            StoredNodeSettings.MinGraphWorkflowMaxConcurrentRuns, StoredNodeSettings.MaxGraphWorkflowMaxConcurrentRuns);

        NullOrBetween(RuleFor(static request => request.GraphWorkflowDefaultNodeTimeoutSeconds),
            StoredNodeSettings.MinGraphWorkflowDefaultNodeTimeoutSeconds, StoredNodeSettings.MaxGraphWorkflowDefaultNodeTimeoutSeconds);

        NullOrBetween(RuleFor(static request => request.WorkSessionMaxStepsPerRun),
            StoredNodeSettings.MinWorkSessionMaxStepsPerRun, StoredNodeSettings.MaxWorkSessionMaxStepsPerRun);

        NullOrBetween(RuleFor(static request => request.WorkSessionMaxConcurrentSessions),
            StoredNodeSettings.MinWorkSessionMaxConcurrentSessions, StoredNodeSettings.MaxWorkSessionMaxConcurrentSessions);

        NullOrBetween(RuleFor(static request => request.DevelopmentMaxAttemptDurationSeconds),
            StoredNodeSettings.MinDevelopmentMaxAttemptDurationSeconds, StoredNodeSettings.MaxDevelopmentMaxAttemptDurationSeconds);

        NullOrBetween(RuleFor(static request => request.DevelopmentMaxToolCalls),
            StoredNodeSettings.MinDevelopmentMaxToolCalls, StoredNodeSettings.MaxDevelopmentMaxToolCalls);

        NullOrBetween(RuleFor(static request => request.DevelopmentMaxOutputTokens),
            StoredNodeSettings.MinDevelopmentMaxOutputTokens, StoredNodeSettings.MaxDevelopmentMaxOutputTokens);

        NullOrBetween(RuleFor(static request => request.AgentHomeMaxInnerToolCalls),
            StoredNodeSettings.MinAgentHomeMaxInnerToolCalls, StoredNodeSettings.MaxAgentHomeMaxInnerToolCalls);

        NullOrBetween(RuleFor(static request => request.AgentHomePatchApplyTimeoutSeconds),
            StoredNodeSettings.MinAgentHomePatchApplyTimeoutSeconds, StoredNodeSettings.MaxAgentHomePatchApplyTimeoutSeconds);

        NullOrBetween(RuleFor(static request => request.AgentHomeRunRetentionMaxRuns),
            StoredNodeSettings.MinAgentHomeRunRetentionMaxRuns, StoredNodeSettings.MaxAgentHomeRunRetentionMaxRuns);

        NullOrBetween(RuleFor(static request => request.AgentHomeRunRetentionMaxTotalBytes),
            StoredNodeSettings.MinAgentHomeRunRetentionMaxTotalBytes, StoredNodeSettings.MaxAgentHomeRunRetentionMaxTotalBytes);

        // Every override entry must have a non-blank model name and finite, non-negative rates (HasValidRates is the one shared predicate with the store's Normalize). Junk is
        // rejected with an immediate 400; Normalize remains the defense-in-depth second pass that also drops any entry slipping through.
        RuleFor(static request => request.UsageRates!)
            .Must(static rates => rates.All(static entry =>
                !string.IsNullOrWhiteSpace(entry.Key) && entry.Value is not null && entry.Value.HasValidRates))
            .When(static request => request.UsageRates is not null)
            .WithMessage("Usage rates must have a non-blank model name and finite, non-negative input/output rates (USD per 1M tokens).");
    }

    // "" and whitespace used to clear a text setting; null is now the only clear signal, so a blank value is refused.
    private static void NotBlank(IRuleBuilder<SaveNodeSettingsRequest, string?> rule)
    {
        rule.Must(static value => value is null || !string.IsNullOrWhiteSpace(value))
            .WithMessage("'{PropertyName}' cannot be blank; send null to clear it.");
    }

    private static void NotClearable(IRuleBuilder<SaveNodeSettingsRequest, string?> rule, string member)
    {
        rule.Must((request, _) => !request.Clears(member))
            .WithMessage("'{PropertyName}' cannot be cleared.");
    }

    private static bool BeAbsoluteHttpUrl(string value)
    {
        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static bool IsNullOrBetween<T>(T? value, T min, T max)
        where T : struct, INumber<T>
    {
        return value is not { } supplied || (supplied >= min && supplied <= max);
    }

    // A nullable numeric knob: null resets it to the default, anything else must sit in [min, max]. Must, not InclusiveBetween,
    // for the reason given at the reasoning budgets; the message keeps InclusiveBetween's wording and adds the null.
    private static void NullOrBetween<T>(IRuleBuilder<SaveNodeSettingsRequest, T?> rule, T min, T max)
        where T : struct, INumber<T>
    {
        rule.Must(value => IsNullOrBetween(value, min, max))
            .WithMessage(string.Create(CultureInfo.InvariantCulture,
                $"'{{PropertyName}}' must be between {min} and {max}, or null for the default. You entered {{PropertyValue}}."));
    }
}
