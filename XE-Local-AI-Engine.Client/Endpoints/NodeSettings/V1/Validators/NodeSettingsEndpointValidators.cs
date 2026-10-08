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
///     Every migrated field is optional, so a rule fires only when the field is supplied (a <see langword="null" /> keeps
///     the current stored value; range rules bind the nullable property itself, so they skip null and report the plain
///     field name). Range and format violations are rejected with a 400 and
///     a clear message before anything is persisted; the store's <c>Normalize</c> remains the second,
///     defense-in-depth clamp.
/// </remarks>
public sealed class SaveNodeSettingsRequestValidator : Validator<SaveNodeSettingsRequest>
{
    private static readonly string ReasoningBudgetRangeMessage = string.Create(CultureInfo.InvariantCulture,
        $"A reasoning budget must be from {StoredNodeSettings.MinReasoningBudgetTokens} to {StoredNodeSettings.MaxReasoningBudgetTokens} tokens, or {StoredNodeSettings.TokenSettingUnset} for the default.");

    private static readonly string ChatOutputCapMaxTokensRangeMessage = string.Create(CultureInfo.InvariantCulture,
        $"The chat output cap ceiling must be from {StoredNodeSettings.MinChatOutputCapMaxTokens} to {StoredNodeSettings.MaxChatOutputCapMaxTokens} tokens, or {StoredNodeSettings.TokenSettingUnset} for the default.");

    private static readonly string PositiveOrUnsetMessage = string.Create(CultureInfo.InvariantCulture,
        $"'{{PropertyName}}' must be greater than '0', or {StoredNodeSettings.TokenSettingUnset} for the default. You entered {{PropertyValue}}.");

    public SaveNodeSettingsRequestValidator()
    {
        RuleFor(static request => request.MaxMessageRequestTimeoutSeconds)
            .InclusiveBetween(StoredNodeSettings.MinMaxMessageRequestTimeoutSeconds, StoredNodeSettings.MaxMaxMessageRequestTimeoutSeconds);

        RuleFor(static request => request.OllamaEndpoint!)
            .Must(BeAbsoluteHttpUrl)
            .When(static request => !string.IsNullOrWhiteSpace(request.OllamaEndpoint))
            .WithMessage("Ollama endpoint must be an absolute http or https URL.");

        RuleFor(static request => request.WebSearchSearxngUrl!)
            .Must(BeAbsoluteHttpUrl)
            .When(static request => !string.IsNullOrWhiteSpace(request.WebSearchSearxngUrl))
            .WithMessage("SearXNG URL must be an absolute http or https URL.");

        // Shape only, never "is it installed": the default is legitimately set before its download, and a blank value clears it to the seed.
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
            .When(static request => request.RecommendedLlamaCppTag is not null)
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

        // Same allow-list and same message as the dedicated PUT app-update/channel validator, so the two surfaces
        // that may write the channel cannot drift apart.
        RuleFor(static request => request.UpdateChannel)
            .Must(StoredNodeSettings.IsValidUpdateChannel)
            .When(static request => request.UpdateChannel is not null)
            .WithMessage("Update channel must be stable, preview or development.");

        UnsetOrBetween(RuleFor(static request => request.LlamaMaxLoadedProcesses),
            StoredNodeSettings.MinLlamaMaxLoadedProcesses, StoredNodeSettings.MaxLlamaMaxLoadedProcesses);

        UnsetOrBetween(RuleFor(static request => request.LlamaIdleTimeToLiveSeconds),
            StoredNodeSettings.MinLlamaIdleTimeToLiveSeconds, StoredNodeSettings.MaxLlamaIdleTimeToLiveSeconds);

        UnsetOrBetween(RuleFor(static request => request.KeepModelWarmIntervalSeconds),
            StoredNodeSettings.MinKeepModelWarmIntervalSeconds, StoredNodeSettings.MaxKeepModelWarmIntervalSeconds);

        UnsetOrBetween(RuleFor(static request => request.MaxResponseSizeMb),
            StoredNodeSettings.MinMaxResponseSizeMb, StoredNodeSettings.MaxMaxResponseSizeMb);

        UnsetOrBetween(RuleFor(static request => request.ChatCacheReuse),
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
        // client's "Docker" is accepted and normalized while "Podman" answers 400. A blank string is rejected rather than treated as "keep": only an absent member keeps.
        RuleFor(static request => request.ContainerRuntimeSelection)
            .Must(StoredNodeSettings.IsValidContainerRuntimeSelection)
            .When(static request => request.ContainerRuntimeSelection is not null)
            .WithMessage("Unknown container runtime selection. Use 'auto' or 'docker'.");

        UnsetOrBetween(RuleFor(static request => request.SpeculativeDraftMaxTokens),
            StoredNodeSettings.MinSpeculativeDraftMaxTokens, StoredNodeSettings.MaxSpeculativeDraftMaxTokens);

        RuleFor(static request => request.SpeculativeDraftGpuLayers)
            .InclusiveBetween(StoredNodeSettings.MinSpeculativeDraftGpuLayers, StoredNodeSettings.MaxSpeculativeDraftGpuLayers);

        // Cross-field: a draft-* mode needs a draft model. This boundary rule fires when the request itself sets a draft-* SpeculativeMode, catching "pick draft mode, forget
        // the model" with an immediate 400; it cannot see the CURRENT stored mode, so the endpoint re-checks the merged result — together, no draft-* mode persists modelless.
        RuleFor(static request => request.SpeculativeDraftModelName)
            .Must(static name => !string.IsNullOrWhiteSpace(name))
            .When(static request => StoredNodeSettings.SpeculativeModeRequiresDraftModel(request.SpeculativeMode))
            .WithMessage("Speculative decoding is set to a draft model mode, but no draft model was selected.");

        UnsetOrBetween(RuleFor(static request => request.HuggingFaceDiskMarginBytes),
            StoredNodeSettings.MinHuggingFaceDiskMarginBytes, StoredNodeSettings.MaxHuggingFaceDiskMarginBytes);

        UnsetOrBetween(RuleFor(static request => request.OrchestrationIdleTimeoutSeconds),
            StoredNodeSettings.MinOrchestrationIdleTimeoutSeconds, StoredNodeSettings.MaxOrchestrationIdleTimeoutSeconds);

        UnsetOrBetween(RuleFor(static request => request.AgentHomePrepareTimeoutSeconds),
            StoredNodeSettings.MinAgentHomeTimeoutSeconds, StoredNodeSettings.MaxAgentHomeTimeoutSeconds);

        UnsetOrBetween(RuleFor(static request => request.AgentHomeCommandTimeoutSeconds),
            StoredNodeSettings.MinAgentHomeTimeoutSeconds, StoredNodeSettings.MaxAgentHomeTimeoutSeconds);

        RuleFor(static request => request.AgentHomeMaxSelectedFolderBytes)
            .Must(static bytes => bytes is null or StoredNodeSettings.TokenSettingUnset or > 0)
            .WithMessage(PositiveOrUnsetMessage);

        RuleFor(static request => request.AgentHomeMaxPatchBytes)
            .Must(static bytes => bytes is null or StoredNodeSettings.TokenSettingUnset or > 0)
            .WithMessage(PositiveOrUnsetMessage);

        UnsetOrBetween(RuleFor(static request => request.MaxPendingToolCallAgeMinutes),
            StoredNodeSettings.MinMaxPendingToolCallAgeMinutes, StoredNodeSettings.MaxMaxPendingToolCallAgeMinutes);

        UnsetOrBetween(RuleFor(static request => request.DetachedGraceSeconds),
            StoredNodeSettings.MinDetachedGraceSeconds, StoredNodeSettings.MaxDetachedGraceSeconds);

        UnsetOrBetween(RuleFor(static request => request.TranscriptionIdleTimeoutMinutes),
            StoredNodeSettings.MinTranscriptionIdleTimeoutMinutes, StoredNodeSettings.MaxTranscriptionIdleTimeoutMinutes);

        UnsetOrBetween(RuleFor(static request => request.LlamaReadinessTimeoutCapSeconds),
            StoredNodeSettings.MinLlamaReadinessTimeoutCapSeconds, StoredNodeSettings.MaxLlamaReadinessTimeoutCapSeconds);

        UnsetOrBetween(RuleFor(static request => request.LlamaChatHttpTimeoutSeconds),
            StoredNodeSettings.MinLlamaChatHttpTimeoutSeconds, StoredNodeSettings.MaxLlamaChatHttpTimeoutSeconds);

        UnsetOrBetween(RuleFor(static request => request.LlamaEmbeddingHttpTimeoutSeconds),
            StoredNodeSettings.MinLlamaEmbeddingHttpTimeoutSeconds, StoredNodeSettings.MaxLlamaEmbeddingHttpTimeoutSeconds);

        RuleFor(static request => request.LlamaChatCacheRamMiB)
            .InclusiveBetween(StoredNodeSettings.LlamaChatCacheRamMiBAuto, StoredNodeSettings.MaxLlamaChatCacheRamMiB);

        UnsetOrBetween(RuleFor(static request => request.LlamaCpuThreadReserve),
            StoredNodeSettings.MinLlamaCpuThreadReserve, StoredNodeSettings.MaxLlamaCpuThreadReserve);

        UnsetOrBetween(RuleFor(static request => request.LlamaGpuReservePercent),
            StoredNodeSettings.MinLlamaGpuReservePercent, StoredNodeSettings.MaxLlamaGpuReservePercent);

        UnsetOrBetween(RuleFor(static request => request.LlamaRamReservePercent),
            StoredNodeSettings.MinLlamaRamReservePercent, StoredNodeSettings.MaxLlamaRamReservePercent);

        UnsetOrBetween(RuleFor(static request => request.ImageIdleTimeToLiveSeconds),
            StoredNodeSettings.MinImageIdleTimeToLiveSeconds, StoredNodeSettings.MaxImageIdleTimeToLiveSeconds);

        UnsetOrBetween(RuleFor(static request => request.ModelFitSafetyMarginPercent),
            StoredNodeSettings.MinModelFitSafetyMarginPercent, StoredNodeSettings.MaxModelFitSafetyMarginPercent);

        UnsetOrBetween(RuleFor(static request => request.MaxProviderCallsPerInvocation),
            StoredNodeSettings.MinMaxProviderCallsPerInvocation, StoredNodeSettings.MaxMaxProviderCallsPerInvocation);

        UnsetOrBetween(RuleFor(static request => request.CustomToolMaxTimeoutSeconds),
            StoredNodeSettings.MinCustomToolMaxTimeoutSeconds, StoredNodeSettings.MaxCustomToolMaxTimeoutSeconds);

        UnsetOrBetween(RuleFor(static request => request.WebFetchTimeoutSeconds),
            StoredNodeSettings.MinWebFetchTimeoutSeconds, StoredNodeSettings.MaxWebFetchTimeoutSeconds);

        UnsetOrBetween(RuleFor(static request => request.WebFetchMaxContentChars),
            StoredNodeSettings.MinWebFetchMaxContentChars, StoredNodeSettings.MaxWebFetchMaxContentChars);

        UnsetOrBetween(RuleFor(static request => request.KnowledgeSearchDefaultResults),
            StoredNodeSettings.MinKnowledgeSearchResults, StoredNodeSettings.MaxKnowledgeSearchResults);

        UnsetOrBetween(RuleFor(static request => request.KnowledgeSearchMaxResults),
            StoredNodeSettings.MinKnowledgeSearchResults, StoredNodeSettings.MaxKnowledgeSearchResults);

        // TokenSettingUnset (-1) and the empty string are the "back to default" sentinels; everything else is range-checked. Must, not
        // InclusiveBetween: the OpenAPI schema would publish the range as min/max and the generated client would refuse the sentinel.
        RuleFor(static request => request.ReasoningBudgetMinimalTokens)
            .Must(static tokens => IsUnsetOrBetween(tokens, StoredNodeSettings.MinReasoningBudgetTokens, StoredNodeSettings.MaxReasoningBudgetTokens))
            .WithMessage(ReasoningBudgetRangeMessage);

        RuleFor(static request => request.ReasoningBudgetLowTokens)
            .Must(static tokens => IsUnsetOrBetween(tokens, StoredNodeSettings.MinReasoningBudgetTokens, StoredNodeSettings.MaxReasoningBudgetTokens))
            .WithMessage(ReasoningBudgetRangeMessage);

        RuleFor(static request => request.ReasoningBudgetMediumTokens)
            .Must(static tokens => IsUnsetOrBetween(tokens, StoredNodeSettings.MinReasoningBudgetTokens, StoredNodeSettings.MaxReasoningBudgetTokens))
            .WithMessage(ReasoningBudgetRangeMessage);

        RuleFor(static request => request.ReasoningBudgetHighTokens)
            .Must(static tokens => IsUnsetOrBetween(tokens, StoredNodeSettings.MinReasoningBudgetTokens, StoredNodeSettings.MaxReasoningBudgetTokens))
            .WithMessage(ReasoningBudgetRangeMessage);

        RuleFor(static request => request.DefaultReasoningEffort)
            .Must(static effort => StoredNodeSettings.IsValidDefaultReasoningEffort(effort?.Trim()))
            .When(static request => request.DefaultReasoningEffort is not (null or ""))
            .WithMessage("Default reasoning effort must be minimal, low, medium or high, or empty for the default.");

        RuleFor(static request => request.ChatOutputCapMode)
            .Must(static mode => StoredNodeSettings.IsValidChatOutputCapMode(mode?.Trim()))
            .When(static request => request.ChatOutputCapMode is not (null or ""))
            .WithMessage("Chat output cap mode must be cap, notice or off, or empty for the default.");

        RuleFor(static request => request.ChatOutputCapMaxTokens)
            .Must(static tokens => IsUnsetOrBetween(tokens, StoredNodeSettings.MinChatOutputCapMaxTokens, StoredNodeSettings.MaxChatOutputCapMaxTokens))
            .WithMessage(ChatOutputCapMaxTokensRangeMessage);

        UnsetOrBetween(RuleFor(static request => request.HuggingFaceDownloadConnections),
            StoredNodeSettings.MinHuggingFaceDownloadConnections, StoredNodeSettings.MaxHuggingFaceDownloadConnections);

        UnsetOrBetween(RuleFor(static request => request.TranscriptionInferenceTimeoutMinutes),
            StoredNodeSettings.MinTranscriptionInferenceTimeoutMinutes, StoredNodeSettings.MaxTranscriptionInferenceTimeoutMinutes);

        UnsetOrBetween(RuleFor(static request => request.AgentHomeMaxRunSeconds),
            StoredNodeSettings.MinAgentHomeMaxRunSeconds, StoredNodeSettings.MaxAgentHomeMaxRunSeconds);

        UnsetOrBetween(RuleFor(static request => request.AgentHomeRunRetentionDays),
            StoredNodeSettings.MinAgentHomeRunRetentionDays, StoredNodeSettings.MaxAgentHomeRunRetentionDays);

        UnsetOrBetween(RuleFor(static request => request.ToolPipelineMaxIterationsPerRequest),
            StoredNodeSettings.MinToolPipelineMaxIterationsPerRequest, StoredNodeSettings.MaxToolPipelineMaxIterationsPerRequest);

        UnsetOrBetween(RuleFor(static request => request.ToolPipelineMaxToolResultChars),
            StoredNodeSettings.MinToolPipelineMaxToolResultChars, StoredNodeSettings.MaxToolPipelineMaxToolResultChars);

        UnsetOrBetween(RuleFor(static request => request.ToolPipelineMaxConsecutiveInvalidToolCalls),
            StoredNodeSettings.MinToolPipelineMaxConsecutiveInvalidToolCalls, StoredNodeSettings.MaxToolPipelineMaxConsecutiveInvalidToolCalls);

        UnsetOrBetween(RuleFor(static request => request.DefaultContextTokens),
            StoredNodeSettings.MinDefaultContextTokens, StoredNodeSettings.MaxDefaultContextTokens);

        UnsetOrBetween(RuleFor(static request => request.ProviderBudgetRecentMessagesToKeep),
            StoredNodeSettings.MinProviderBudgetRecentMessagesToKeep, StoredNodeSettings.MaxProviderBudgetRecentMessagesToKeep);

        UnsetOrBetween(RuleFor(static request => request.ProviderBudgetMaxCumulativeInputTokens),
            StoredNodeSettings.MinProviderBudgetMaxCumulativeInputTokens, StoredNodeSettings.MaxProviderBudgetMaxCumulativeInputTokens);

        UnsetOrBetween(RuleFor(static request => request.ContextBudgetRecentTurnKeepCount),
            StoredNodeSettings.MinContextBudgetRecentTurnKeepCount, StoredNodeSettings.MaxContextBudgetRecentTurnKeepCount);

        UnsetOrBetween(RuleFor(static request => request.CompactionAutoCompactPercent),
            StoredNodeSettings.MinCompactionAutoCompactPercent, StoredNodeSettings.MaxCompactionAutoCompactPercent);

        UnsetOrBetween(RuleFor(static request => request.CompactionRecentMessagesVerbatim),
            StoredNodeSettings.MinCompactionRecentMessagesVerbatim, StoredNodeSettings.MaxCompactionRecentMessagesVerbatim);

        UnsetOrBetween(RuleFor(static request => request.MaxInlinedAttachmentChars),
            StoredNodeSettings.MinMaxInlinedAttachmentChars, StoredNodeSettings.MaxMaxInlinedAttachmentChars);

        UnsetOrBetween(RuleFor(static request => request.KnowledgeChatTopK),
            StoredNodeSettings.MinKnowledgeChatTopK, StoredNodeSettings.MaxKnowledgeChatTopK);

        UnsetOrBetween(RuleFor(static request => request.ProviderMaxRetries),
            StoredNodeSettings.MinProviderMaxRetries, StoredNodeSettings.MaxProviderMaxRetries);

        UnsetOrBetween(RuleFor(static request => request.SpawnMaxConcurrent),
            StoredNodeSettings.MinSpawnMaxConcurrent, StoredNodeSettings.MaxSpawnMaxConcurrent);

        UnsetOrBetween(RuleFor(static request => request.SpawnMaxCloud),
            StoredNodeSettings.MinSpawnMaxCloud, StoredNodeSettings.MaxSpawnMaxCloud);

        UnsetOrBetween(RuleFor(static request => request.SpawnQueueWaitSeconds),
            StoredNodeSettings.MinSpawnQueueWaitSeconds, StoredNodeSettings.MaxSpawnQueueWaitSeconds);

        UnsetOrBetween(RuleFor(static request => request.KnowledgeRetrievalLatencyBudgetMs),
            StoredNodeSettings.MinKnowledgeRetrievalLatencyBudgetMs, StoredNodeSettings.MaxKnowledgeRetrievalLatencyBudgetMs);

        UnsetOrBetween(RuleFor(static request => request.KnowledgeScheduledReindexIntervalMinutes),
            StoredNodeSettings.MinKnowledgeScheduledReindexIntervalMinutes, StoredNodeSettings.MaxKnowledgeScheduledReindexIntervalMinutes);

        UnsetOrBetween(RuleFor(static request => request.ChatRetentionDays),
            StoredNodeSettings.MinRetentionDays, StoredNodeSettings.MaxRetentionDays);

        UnsetOrBetween(RuleFor(static request => request.AgentExecutionLogRetentionDays),
            StoredNodeSettings.MinRetentionDays, StoredNodeSettings.MaxRetentionDays);

        UnsetOrBetween(RuleFor(static request => request.NodeDbBackupRetainCount),
            StoredNodeSettings.MinNodeDbBackupRetainCount, StoredNodeSettings.MaxNodeDbBackupRetainCount);

        UnsetOrBetween(RuleFor(static request => request.BenchmarkKldCacheMaxBytes),
            StoredNodeSettings.MinBenchmarkKldCacheMaxBytes, StoredNodeSettings.MaxBenchmarkKldCacheMaxBytes);

        UnsetOrBetween(RuleFor(static request => request.SchedulerHistoryRetentionDays),
            StoredNodeSettings.MinRetentionDays, StoredNodeSettings.MaxRetentionDays);

        UnsetOrBetween(RuleFor(static request => request.ImageMaxLoadedProcesses),
            StoredNodeSettings.MinImageMaxLoadedProcesses, StoredNodeSettings.MaxImageMaxLoadedProcesses);

        UnsetOrBetween(RuleFor(static request => request.GraphWorkflowMaxConcurrentRuns),
            StoredNodeSettings.MinGraphWorkflowMaxConcurrentRuns, StoredNodeSettings.MaxGraphWorkflowMaxConcurrentRuns);

        UnsetOrBetween(RuleFor(static request => request.GraphWorkflowDefaultNodeTimeoutSeconds),
            StoredNodeSettings.MinGraphWorkflowDefaultNodeTimeoutSeconds, StoredNodeSettings.MaxGraphWorkflowDefaultNodeTimeoutSeconds);

        UnsetOrBetween(RuleFor(static request => request.WorkSessionMaxStepsPerRun),
            StoredNodeSettings.MinWorkSessionMaxStepsPerRun, StoredNodeSettings.MaxWorkSessionMaxStepsPerRun);

        UnsetOrBetween(RuleFor(static request => request.WorkSessionMaxConcurrentSessions),
            StoredNodeSettings.MinWorkSessionMaxConcurrentSessions, StoredNodeSettings.MaxWorkSessionMaxConcurrentSessions);

        UnsetOrBetween(RuleFor(static request => request.DevelopmentMaxAttemptDurationSeconds),
            StoredNodeSettings.MinDevelopmentMaxAttemptDurationSeconds, StoredNodeSettings.MaxDevelopmentMaxAttemptDurationSeconds);

        UnsetOrBetween(RuleFor(static request => request.DevelopmentMaxToolCalls),
            StoredNodeSettings.MinDevelopmentMaxToolCalls, StoredNodeSettings.MaxDevelopmentMaxToolCalls);

        UnsetOrBetween(RuleFor(static request => request.DevelopmentMaxOutputTokens),
            StoredNodeSettings.MinDevelopmentMaxOutputTokens, StoredNodeSettings.MaxDevelopmentMaxOutputTokens);

        UnsetOrBetween(RuleFor(static request => request.AgentHomeMaxInnerToolCalls),
            StoredNodeSettings.MinAgentHomeMaxInnerToolCalls, StoredNodeSettings.MaxAgentHomeMaxInnerToolCalls);

        UnsetOrBetween(RuleFor(static request => request.AgentHomePatchApplyTimeoutSeconds),
            StoredNodeSettings.MinAgentHomePatchApplyTimeoutSeconds, StoredNodeSettings.MaxAgentHomePatchApplyTimeoutSeconds);

        UnsetOrBetween(RuleFor(static request => request.AgentHomeRunRetentionMaxRuns),
            StoredNodeSettings.MinAgentHomeRunRetentionMaxRuns, StoredNodeSettings.MaxAgentHomeRunRetentionMaxRuns);

        UnsetOrBetween(RuleFor(static request => request.AgentHomeRunRetentionMaxTotalBytes),
            StoredNodeSettings.MinAgentHomeRunRetentionMaxTotalBytes, StoredNodeSettings.MaxAgentHomeRunRetentionMaxTotalBytes);

        // Every override entry must have a non-blank model name and finite, non-negative rates (HasValidRates is the one shared predicate with the store's Normalize). Junk is
        // rejected with an immediate 400; Normalize remains the defense-in-depth second pass that also drops any entry slipping through.
        RuleFor(static request => request.UsageRates!)
            .Must(static rates => rates.All(static entry =>
                !string.IsNullOrWhiteSpace(entry.Key) && entry.Value is not null && entry.Value.HasValidRates))
            .When(static request => request.UsageRates is not null)
            .WithMessage("Usage rates must have a non-blank model name and finite, non-negative input/output rates (USD per 1M tokens).");
    }

    private static bool BeAbsoluteHttpUrl(string value)
    {
        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static bool IsUnsetOrBetween<T>(T? value, T min, T max)
        where T : struct, INumber<T>
    {
        return value is not { } supplied || supplied == T.CreateChecked(StoredNodeSettings.TokenSettingUnset) || (supplied >= min && supplied <= max);
    }

    // A nullable numeric knob: TokenSettingUnset (-1) resets it to the default, anything else must sit in [min, max]. Must, not
    // InclusiveBetween, for the reason given at the reasoning budgets; the message keeps InclusiveBetween's wording.
    private static void UnsetOrBetween<T>(IRuleBuilder<SaveNodeSettingsRequest, T?> rule, T min, T max)
        where T : struct, INumber<T>
    {
        rule.Must(value => IsUnsetOrBetween(value, min, max))
            .WithMessage(string.Create(CultureInfo.InvariantCulture,
                $"'{{PropertyName}}' must be between {min} and {max}, or {StoredNodeSettings.TokenSettingUnset} for the default. You entered {{PropertyValue}}."));
    }
}
