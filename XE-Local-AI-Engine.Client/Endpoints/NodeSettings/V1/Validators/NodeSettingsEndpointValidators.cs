namespace XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1.Validators;

using System.Globalization;
using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

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
    /// <summary>The widest model-name column the node persists (<c>base_model_name</c>, 256).</summary>
    private const int MaxModelNameLength = 256;

    private static readonly string ReasoningBudgetRangeMessage = string.Create(CultureInfo.InvariantCulture,
        $"A reasoning budget must be from {StoredNodeSettings.MinReasoningBudgetTokens} to {StoredNodeSettings.MaxReasoningBudgetTokens} tokens, or {StoredNodeSettings.TokenSettingUnset} for the default.");

    private static readonly string ChatOutputCapMaxTokensRangeMessage = string.Create(CultureInfo.InvariantCulture,
        $"The chat output cap ceiling must be from {StoredNodeSettings.MinChatOutputCapMaxTokens} to {StoredNodeSettings.MaxChatOutputCapMaxTokens} tokens, or {StoredNodeSettings.TokenSettingUnset} for the default.");

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
            .Must(BePlausibleModelName)
            .When(static request => !string.IsNullOrWhiteSpace(request.DefaultModelName))
            .WithMessage($"Default model name must be at most {MaxModelNameLength} characters and contain no control characters.");

        RuleFor(static request => request.HuggingFaceDefaultQuant!)
            .Must(BeKnownQuant)
            .When(static request => !string.IsNullOrWhiteSpace(request.HuggingFaceDefaultQuant))
            .WithMessage("Unknown Hugging Face default quant. Use a GGUF quant label such as Q4_K_M or UD-Q4_K_XL.");

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

        RuleFor(static request => request.LlamaMaxLoadedProcesses)
            .InclusiveBetween(StoredNodeSettings.MinLlamaMaxLoadedProcesses, StoredNodeSettings.MaxLlamaMaxLoadedProcesses);

        RuleFor(static request => request.LlamaIdleTimeToLiveSeconds)
            .InclusiveBetween(StoredNodeSettings.MinLlamaIdleTimeToLiveSeconds, StoredNodeSettings.MaxLlamaIdleTimeToLiveSeconds);

        RuleFor(static request => request.KeepModelWarmIntervalSeconds)
            .InclusiveBetween(StoredNodeSettings.MinKeepModelWarmIntervalSeconds, StoredNodeSettings.MaxKeepModelWarmIntervalSeconds);

        RuleFor(static request => request.MaxResponseSizeMb)
            .InclusiveBetween(StoredNodeSettings.MinMaxResponseSizeMb, StoredNodeSettings.MaxMaxResponseSizeMb);

        RuleFor(static request => request.ChatCacheReuse)
            .InclusiveBetween(StoredNodeSettings.MinChatCacheReuse, StoredNodeSettings.MaxChatCacheReuse);

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

        RuleFor(static request => request.SpeculativeDraftMaxTokens)
            .InclusiveBetween(StoredNodeSettings.MinSpeculativeDraftMaxTokens, StoredNodeSettings.MaxSpeculativeDraftMaxTokens);

        RuleFor(static request => request.SpeculativeDraftGpuLayers)
            .InclusiveBetween(StoredNodeSettings.MinSpeculativeDraftGpuLayers, StoredNodeSettings.MaxSpeculativeDraftGpuLayers);

        // Cross-field: a draft-* mode needs a draft model. This boundary rule fires when the request itself sets a draft-* SpeculativeMode, catching "pick draft mode, forget
        // the model" with an immediate 400; it cannot see the CURRENT stored mode, so the endpoint re-checks the merged result — together, no draft-* mode persists modelless.
        RuleFor(static request => request.SpeculativeDraftModelName)
            .Must(static name => !string.IsNullOrWhiteSpace(name))
            .When(static request => StoredNodeSettings.SpeculativeModeRequiresDraftModel(request.SpeculativeMode))
            .WithMessage("Speculative decoding is set to a draft model mode, but no draft model was selected.");

        RuleFor(static request => request.HuggingFaceDiskMarginBytes)
            .InclusiveBetween(StoredNodeSettings.MinHuggingFaceDiskMarginBytes, StoredNodeSettings.MaxHuggingFaceDiskMarginBytes);

        RuleFor(static request => request.OrchestrationIdleTimeoutSeconds)
            .InclusiveBetween(StoredNodeSettings.MinOrchestrationIdleTimeoutSeconds, StoredNodeSettings.MaxOrchestrationIdleTimeoutSeconds);

        RuleFor(static request => request.AgentHomePrepareTimeoutSeconds)
            .InclusiveBetween(StoredNodeSettings.MinAgentHomeTimeoutSeconds, StoredNodeSettings.MaxAgentHomeTimeoutSeconds);

        RuleFor(static request => request.AgentHomeCommandTimeoutSeconds)
            .InclusiveBetween(StoredNodeSettings.MinAgentHomeTimeoutSeconds, StoredNodeSettings.MaxAgentHomeTimeoutSeconds);

        RuleFor(static request => request.AgentHomeMaxSelectedFolderBytes)
            .GreaterThan(0);

        RuleFor(static request => request.AgentHomeMaxPatchBytes)
            .GreaterThan(0);

        RuleFor(static request => request.MaxPendingToolCallAgeMinutes)
            .InclusiveBetween(StoredNodeSettings.MinMaxPendingToolCallAgeMinutes, StoredNodeSettings.MaxMaxPendingToolCallAgeMinutes);

        RuleFor(static request => request.DetachedGraceSeconds)
            .InclusiveBetween(StoredNodeSettings.MinDetachedGraceSeconds, StoredNodeSettings.MaxDetachedGraceSeconds);

        RuleFor(static request => request.TranscriptionIdleTimeoutMinutes)
            .InclusiveBetween(StoredNodeSettings.MinTranscriptionIdleTimeoutMinutes, StoredNodeSettings.MaxTranscriptionIdleTimeoutMinutes);

        RuleFor(static request => request.LlamaReadinessTimeoutCapSeconds)
            .InclusiveBetween(StoredNodeSettings.MinLlamaReadinessTimeoutCapSeconds, StoredNodeSettings.MaxLlamaReadinessTimeoutCapSeconds);

        RuleFor(static request => request.LlamaChatHttpTimeoutSeconds)
            .InclusiveBetween(StoredNodeSettings.MinLlamaChatHttpTimeoutSeconds, StoredNodeSettings.MaxLlamaChatHttpTimeoutSeconds);

        RuleFor(static request => request.LlamaEmbeddingHttpTimeoutSeconds)
            .InclusiveBetween(StoredNodeSettings.MinLlamaEmbeddingHttpTimeoutSeconds, StoredNodeSettings.MaxLlamaEmbeddingHttpTimeoutSeconds);

        RuleFor(static request => request.LlamaChatCacheRamMiB)
            .InclusiveBetween(StoredNodeSettings.LlamaChatCacheRamMiBAuto, StoredNodeSettings.MaxLlamaChatCacheRamMiB);

        RuleFor(static request => request.LlamaCpuThreadReserve)
            .InclusiveBetween(StoredNodeSettings.MinLlamaCpuThreadReserve, StoredNodeSettings.MaxLlamaCpuThreadReserve);

        RuleFor(static request => request.LlamaGpuReservePercent)
            .InclusiveBetween(StoredNodeSettings.MinLlamaGpuReservePercent, StoredNodeSettings.MaxLlamaGpuReservePercent);

        RuleFor(static request => request.LlamaRamReservePercent)
            .InclusiveBetween(StoredNodeSettings.MinLlamaRamReservePercent, StoredNodeSettings.MaxLlamaRamReservePercent);

        RuleFor(static request => request.ImageIdleTimeToLiveSeconds)
            .InclusiveBetween(StoredNodeSettings.MinImageIdleTimeToLiveSeconds, StoredNodeSettings.MaxImageIdleTimeToLiveSeconds);

        RuleFor(static request => request.ModelFitSafetyMarginPercent)
            .InclusiveBetween(StoredNodeSettings.MinModelFitSafetyMarginPercent, StoredNodeSettings.MaxModelFitSafetyMarginPercent);

        RuleFor(static request => request.MaxProviderCallsPerInvocation)
            .InclusiveBetween(StoredNodeSettings.MinMaxProviderCallsPerInvocation, StoredNodeSettings.MaxMaxProviderCallsPerInvocation);

        RuleFor(static request => request.CustomToolMaxTimeoutSeconds)
            .InclusiveBetween(StoredNodeSettings.MinCustomToolMaxTimeoutSeconds, StoredNodeSettings.MaxCustomToolMaxTimeoutSeconds);

        RuleFor(static request => request.WebFetchTimeoutSeconds)
            .InclusiveBetween(StoredNodeSettings.MinWebFetchTimeoutSeconds, StoredNodeSettings.MaxWebFetchTimeoutSeconds);

        RuleFor(static request => request.WebFetchMaxContentChars)
            .InclusiveBetween(StoredNodeSettings.MinWebFetchMaxContentChars, StoredNodeSettings.MaxWebFetchMaxContentChars);

        RuleFor(static request => request.KnowledgeSearchDefaultResults)
            .InclusiveBetween(StoredNodeSettings.MinKnowledgeSearchResults, StoredNodeSettings.MaxKnowledgeSearchResults);

        RuleFor(static request => request.KnowledgeSearchMaxResults)
            .InclusiveBetween(StoredNodeSettings.MinKnowledgeSearchResults, StoredNodeSettings.MaxKnowledgeSearchResults);

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

        RuleFor(static request => request.HuggingFaceDownloadConnections)
            .InclusiveBetween(StoredNodeSettings.MinHuggingFaceDownloadConnections, StoredNodeSettings.MaxHuggingFaceDownloadConnections);

        RuleFor(static request => request.TranscriptionInferenceTimeoutMinutes)
            .InclusiveBetween(StoredNodeSettings.MinTranscriptionInferenceTimeoutMinutes, StoredNodeSettings.MaxTranscriptionInferenceTimeoutMinutes);

        RuleFor(static request => request.AgentHomeMaxRunSeconds)
            .InclusiveBetween(StoredNodeSettings.MinAgentHomeMaxRunSeconds, StoredNodeSettings.MaxAgentHomeMaxRunSeconds);

        RuleFor(static request => request.AgentHomeRunRetentionDays)
            .InclusiveBetween(StoredNodeSettings.MinAgentHomeRunRetentionDays, StoredNodeSettings.MaxAgentHomeRunRetentionDays);

        RuleFor(static request => request.ToolPipelineMaxIterationsPerRequest)
            .InclusiveBetween(StoredNodeSettings.MinToolPipelineMaxIterationsPerRequest, StoredNodeSettings.MaxToolPipelineMaxIterationsPerRequest);

        RuleFor(static request => request.ToolPipelineMaxToolResultChars)
            .InclusiveBetween(StoredNodeSettings.MinToolPipelineMaxToolResultChars, StoredNodeSettings.MaxToolPipelineMaxToolResultChars);

        RuleFor(static request => request.ToolPipelineMaxConsecutiveInvalidToolCalls)
            .InclusiveBetween(StoredNodeSettings.MinToolPipelineMaxConsecutiveInvalidToolCalls, StoredNodeSettings.MaxToolPipelineMaxConsecutiveInvalidToolCalls);

        RuleFor(static request => request.DefaultContextTokens)
            .InclusiveBetween(StoredNodeSettings.MinDefaultContextTokens, StoredNodeSettings.MaxDefaultContextTokens);

        RuleFor(static request => request.ProviderBudgetRecentMessagesToKeep)
            .InclusiveBetween(StoredNodeSettings.MinProviderBudgetRecentMessagesToKeep, StoredNodeSettings.MaxProviderBudgetRecentMessagesToKeep);

        RuleFor(static request => request.ProviderBudgetMaxCumulativeInputTokens)
            .InclusiveBetween(StoredNodeSettings.MinProviderBudgetMaxCumulativeInputTokens, StoredNodeSettings.MaxProviderBudgetMaxCumulativeInputTokens);

        RuleFor(static request => request.ContextBudgetRecentTurnKeepCount)
            .InclusiveBetween(StoredNodeSettings.MinContextBudgetRecentTurnKeepCount, StoredNodeSettings.MaxContextBudgetRecentTurnKeepCount);

        RuleFor(static request => request.CompactionAutoCompactPercent)
            .InclusiveBetween(StoredNodeSettings.MinCompactionAutoCompactPercent, StoredNodeSettings.MaxCompactionAutoCompactPercent);

        RuleFor(static request => request.CompactionRecentMessagesVerbatim)
            .InclusiveBetween(StoredNodeSettings.MinCompactionRecentMessagesVerbatim, StoredNodeSettings.MaxCompactionRecentMessagesVerbatim);

        RuleFor(static request => request.MaxInlinedAttachmentChars)
            .InclusiveBetween(StoredNodeSettings.MinMaxInlinedAttachmentChars, StoredNodeSettings.MaxMaxInlinedAttachmentChars);

        RuleFor(static request => request.KnowledgeChatTopK)
            .InclusiveBetween(StoredNodeSettings.MinKnowledgeChatTopK, StoredNodeSettings.MaxKnowledgeChatTopK);

        RuleFor(static request => request.ProviderMaxRetries)
            .InclusiveBetween(StoredNodeSettings.MinProviderMaxRetries, StoredNodeSettings.MaxProviderMaxRetries);

        RuleFor(static request => request.SpawnMaxConcurrent)
            .InclusiveBetween(StoredNodeSettings.MinSpawnMaxConcurrent, StoredNodeSettings.MaxSpawnMaxConcurrent);

        RuleFor(static request => request.SpawnMaxCloud)
            .InclusiveBetween(StoredNodeSettings.MinSpawnMaxCloud, StoredNodeSettings.MaxSpawnMaxCloud);

        RuleFor(static request => request.SpawnQueueWaitSeconds)
            .InclusiveBetween(StoredNodeSettings.MinSpawnQueueWaitSeconds, StoredNodeSettings.MaxSpawnQueueWaitSeconds);

        RuleFor(static request => request.KnowledgeRetrievalLatencyBudgetMs)
            .InclusiveBetween(StoredNodeSettings.MinKnowledgeRetrievalLatencyBudgetMs, StoredNodeSettings.MaxKnowledgeRetrievalLatencyBudgetMs);

        RuleFor(static request => request.KnowledgeScheduledReindexIntervalMinutes)
            .InclusiveBetween(StoredNodeSettings.MinKnowledgeScheduledReindexIntervalMinutes, StoredNodeSettings.MaxKnowledgeScheduledReindexIntervalMinutes);

        RuleFor(static request => request.ChatRetentionDays)
            .InclusiveBetween(StoredNodeSettings.MinRetentionDays, StoredNodeSettings.MaxRetentionDays);

        RuleFor(static request => request.AgentExecutionLogRetentionDays)
            .InclusiveBetween(StoredNodeSettings.MinRetentionDays, StoredNodeSettings.MaxRetentionDays);

        RuleFor(static request => request.NodeDbBackupRetainCount)
            .InclusiveBetween(StoredNodeSettings.MinNodeDbBackupRetainCount, StoredNodeSettings.MaxNodeDbBackupRetainCount);

        RuleFor(static request => request.BenchmarkKldCacheMaxBytes)
            .InclusiveBetween(StoredNodeSettings.MinBenchmarkKldCacheMaxBytes, StoredNodeSettings.MaxBenchmarkKldCacheMaxBytes);

        RuleFor(static request => request.SchedulerHistoryRetentionDays)
            .InclusiveBetween(StoredNodeSettings.MinRetentionDays, StoredNodeSettings.MaxRetentionDays);

        RuleFor(static request => request.ImageMaxLoadedProcesses)
            .InclusiveBetween(StoredNodeSettings.MinImageMaxLoadedProcesses, StoredNodeSettings.MaxImageMaxLoadedProcesses);

        RuleFor(static request => request.GraphWorkflowMaxConcurrentRuns)
            .InclusiveBetween(StoredNodeSettings.MinGraphWorkflowMaxConcurrentRuns, StoredNodeSettings.MaxGraphWorkflowMaxConcurrentRuns);

        RuleFor(static request => request.GraphWorkflowDefaultNodeTimeoutSeconds)
            .InclusiveBetween(StoredNodeSettings.MinGraphWorkflowDefaultNodeTimeoutSeconds, StoredNodeSettings.MaxGraphWorkflowDefaultNodeTimeoutSeconds);

        RuleFor(static request => request.WorkSessionMaxStepsPerRun)
            .InclusiveBetween(StoredNodeSettings.MinWorkSessionMaxStepsPerRun, StoredNodeSettings.MaxWorkSessionMaxStepsPerRun);

        RuleFor(static request => request.WorkSessionMaxConcurrentSessions)
            .InclusiveBetween(StoredNodeSettings.MinWorkSessionMaxConcurrentSessions, StoredNodeSettings.MaxWorkSessionMaxConcurrentSessions);

        RuleFor(static request => request.DevelopmentMaxAttemptDurationSeconds)
            .InclusiveBetween(StoredNodeSettings.MinDevelopmentMaxAttemptDurationSeconds, StoredNodeSettings.MaxDevelopmentMaxAttemptDurationSeconds);

        RuleFor(static request => request.DevelopmentMaxToolCalls)
            .InclusiveBetween(StoredNodeSettings.MinDevelopmentMaxToolCalls, StoredNodeSettings.MaxDevelopmentMaxToolCalls);

        RuleFor(static request => request.DevelopmentMaxOutputTokens)
            .InclusiveBetween(StoredNodeSettings.MinDevelopmentMaxOutputTokens, StoredNodeSettings.MaxDevelopmentMaxOutputTokens);

        RuleFor(static request => request.AgentHomeMaxInnerToolCalls)
            .InclusiveBetween(StoredNodeSettings.MinAgentHomeMaxInnerToolCalls, StoredNodeSettings.MaxAgentHomeMaxInnerToolCalls);

        RuleFor(static request => request.AgentHomePatchApplyTimeoutSeconds)
            .InclusiveBetween(StoredNodeSettings.MinAgentHomePatchApplyTimeoutSeconds, StoredNodeSettings.MaxAgentHomePatchApplyTimeoutSeconds);

        RuleFor(static request => request.AgentHomeRunRetentionMaxRuns)
            .InclusiveBetween(StoredNodeSettings.MinAgentHomeRunRetentionMaxRuns, StoredNodeSettings.MaxAgentHomeRunRetentionMaxRuns);

        RuleFor(static request => request.AgentHomeRunRetentionMaxTotalBytes)
            .InclusiveBetween(StoredNodeSettings.MinAgentHomeRunRetentionMaxTotalBytes, StoredNodeSettings.MaxAgentHomeRunRetentionMaxTotalBytes);

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

    private static bool BePlausibleModelName(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= MaxModelNameLength && !trimmed.Any(char.IsControl);
    }

    // The quant parser IS the repo's quant vocabulary; the whole value must be one token in its canonical spelling, case aside.
    private static bool BeKnownQuant(string value)
    {
        var trimmed = value.Trim();
        return string.Equals(GgufQuantParser.TryParse(trimmed), trimmed, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnsetOrBetween(int? tokens, int min, int max)
    {
        return tokens is null or StoredNodeSettings.TokenSettingUnset || (tokens >= min && tokens <= max);
    }
}
