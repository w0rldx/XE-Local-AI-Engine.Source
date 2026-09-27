namespace XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1.Validators;

using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Boundary validation for <see cref="SaveNodeSettingsRequest" />.
/// </summary>
/// <remarks>
///     Every migrated field is optional, so a rule fires only <c>When</c> the field is supplied (a
///     <see langword="null" /> keeps the current stored value). Range and format violations are rejected with a 400 and
///     a clear message before anything is persisted; the store's <c>Normalize</c> remains the second,
///     defense-in-depth clamp.
/// </remarks>
public sealed class SaveNodeSettingsRequestValidator : Validator<SaveNodeSettingsRequest>
{
    public SaveNodeSettingsRequestValidator()
    {
        RuleFor(static request => request.MaxMessageRequestTimeoutSeconds!.Value)
            .InclusiveBetween(StoredNodeSettings.MinMaxMessageRequestTimeoutSeconds, StoredNodeSettings.MaxMaxMessageRequestTimeoutSeconds)
            .When(static request => request.MaxMessageRequestTimeoutSeconds is not null);

        RuleFor(static request => request.OllamaEndpoint!)
            .Must(BeAbsoluteHttpUrl)
            .When(static request => !string.IsNullOrWhiteSpace(request.OllamaEndpoint))
            .WithMessage("Ollama endpoint must be an absolute http or https URL.");

        RuleFor(static request => request.WebSearchSearxngUrl!)
            .Must(BeAbsoluteHttpUrl)
            .When(static request => !string.IsNullOrWhiteSpace(request.WebSearchSearxngUrl))
            .WithMessage("SearXNG URL must be an absolute http or https URL.");

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

        RuleFor(static request => request.LlamaMaxLoadedProcesses!.Value)
            .InclusiveBetween(StoredNodeSettings.MinLlamaMaxLoadedProcesses, StoredNodeSettings.MaxLlamaMaxLoadedProcesses)
            .When(static request => request.LlamaMaxLoadedProcesses is not null);

        RuleFor(static request => request.LlamaIdleTimeToLiveSeconds!.Value)
            .InclusiveBetween(StoredNodeSettings.MinLlamaIdleTimeToLiveSeconds, StoredNodeSettings.MaxLlamaIdleTimeToLiveSeconds)
            .When(static request => request.LlamaIdleTimeToLiveSeconds is not null);

        RuleFor(static request => request.KeepModelWarmIntervalSeconds!.Value)
            .InclusiveBetween(StoredNodeSettings.MinKeepModelWarmIntervalSeconds, StoredNodeSettings.MaxKeepModelWarmIntervalSeconds)
            .When(static request => request.KeepModelWarmIntervalSeconds is not null);

        RuleFor(static request => request.MaxResponseSizeMb!.Value)
            .InclusiveBetween(StoredNodeSettings.MinMaxResponseSizeMb, StoredNodeSettings.MaxMaxResponseSizeMb)
            .When(static request => request.MaxResponseSizeMb is not null);

        RuleFor(static request => request.ChatCacheReuse!.Value)
            .InclusiveBetween(StoredNodeSettings.MinChatCacheReuse, StoredNodeSettings.MaxChatCacheReuse)
            .When(static request => request.ChatCacheReuse is not null);

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

        RuleFor(static request => request.SpeculativeDraftMaxTokens!.Value)
            .InclusiveBetween(StoredNodeSettings.MinSpeculativeDraftMaxTokens, StoredNodeSettings.MaxSpeculativeDraftMaxTokens)
            .When(static request => request.SpeculativeDraftMaxTokens is not null);

        RuleFor(static request => request.SpeculativeDraftGpuLayers!.Value)
            .InclusiveBetween(StoredNodeSettings.MinSpeculativeDraftGpuLayers, StoredNodeSettings.MaxSpeculativeDraftGpuLayers)
            .When(static request => request.SpeculativeDraftGpuLayers is not null);

        // Cross-field: a draft-* mode needs a draft model. This boundary rule fires when the request itself sets a draft-* SpeculativeMode, catching "pick draft mode, forget
        // the model" with an immediate 400; it cannot see the CURRENT stored mode, so the endpoint re-checks the merged result — together, no draft-* mode persists modelless.
        RuleFor(static request => request.SpeculativeDraftModelName)
            .Must(static name => !string.IsNullOrWhiteSpace(name))
            .When(static request => StoredNodeSettings.SpeculativeModeRequiresDraftModel(request.SpeculativeMode))
            .WithMessage("Speculative decoding is set to a draft model mode, but no draft model was selected.");

        RuleFor(static request => request.HuggingFaceDiskMarginBytes!.Value)
            .InclusiveBetween(StoredNodeSettings.MinHuggingFaceDiskMarginBytes, StoredNodeSettings.MaxHuggingFaceDiskMarginBytes)
            .When(static request => request.HuggingFaceDiskMarginBytes is not null);

        RuleFor(static request => request.OrchestrationIdleTimeoutSeconds!.Value)
            .InclusiveBetween(StoredNodeSettings.MinOrchestrationIdleTimeoutSeconds, StoredNodeSettings.MaxOrchestrationIdleTimeoutSeconds)
            .When(static request => request.OrchestrationIdleTimeoutSeconds is not null);

        RuleFor(static request => request.AgentHomePrepareTimeoutSeconds!.Value)
            .InclusiveBetween(StoredNodeSettings.MinAgentHomeTimeoutSeconds, StoredNodeSettings.MaxAgentHomeTimeoutSeconds)
            .When(static request => request.AgentHomePrepareTimeoutSeconds is not null);

        RuleFor(static request => request.AgentHomeCommandTimeoutSeconds!.Value)
            .InclusiveBetween(StoredNodeSettings.MinAgentHomeTimeoutSeconds, StoredNodeSettings.MaxAgentHomeTimeoutSeconds)
            .When(static request => request.AgentHomeCommandTimeoutSeconds is not null);

        RuleFor(static request => request.AgentHomeMaxSelectedFolderBytes!.Value)
            .GreaterThan(0)
            .When(static request => request.AgentHomeMaxSelectedFolderBytes is not null);

        RuleFor(static request => request.AgentHomeMaxPatchBytes!.Value)
            .GreaterThan(0)
            .When(static request => request.AgentHomeMaxPatchBytes is not null);

        RuleFor(static request => request.MaxPendingToolCallAgeMinutes!.Value)
            .InclusiveBetween(StoredNodeSettings.MinMaxPendingToolCallAgeMinutes, StoredNodeSettings.MaxMaxPendingToolCallAgeMinutes)
            .When(static request => request.MaxPendingToolCallAgeMinutes is not null);

        RuleFor(static request => request.DetachedGraceSeconds!.Value)
            .InclusiveBetween(StoredNodeSettings.MinDetachedGraceSeconds, StoredNodeSettings.MaxDetachedGraceSeconds)
            .When(static request => request.DetachedGraceSeconds is not null);

        RuleFor(static request => request.TranscriptionIdleTimeoutMinutes!.Value)
            .InclusiveBetween(StoredNodeSettings.MinTranscriptionIdleTimeoutMinutes, StoredNodeSettings.MaxTranscriptionIdleTimeoutMinutes)
            .When(static request => request.TranscriptionIdleTimeoutMinutes is not null);

        RuleFor(static request => request.LlamaReadinessTimeoutCapSeconds!.Value)
            .InclusiveBetween(StoredNodeSettings.MinLlamaReadinessTimeoutCapSeconds, StoredNodeSettings.MaxLlamaReadinessTimeoutCapSeconds)
            .When(static request => request.LlamaReadinessTimeoutCapSeconds is not null);

        RuleFor(static request => request.LlamaChatHttpTimeoutSeconds!.Value)
            .InclusiveBetween(StoredNodeSettings.MinLlamaChatHttpTimeoutSeconds, StoredNodeSettings.MaxLlamaChatHttpTimeoutSeconds)
            .When(static request => request.LlamaChatHttpTimeoutSeconds is not null);

        RuleFor(static request => request.LlamaEmbeddingHttpTimeoutSeconds!.Value)
            .InclusiveBetween(StoredNodeSettings.MinLlamaEmbeddingHttpTimeoutSeconds, StoredNodeSettings.MaxLlamaEmbeddingHttpTimeoutSeconds)
            .When(static request => request.LlamaEmbeddingHttpTimeoutSeconds is not null);

        RuleFor(static request => request.LlamaChatCacheRamMiB!.Value)
            .InclusiveBetween(StoredNodeSettings.LlamaChatCacheRamMiBAuto, StoredNodeSettings.MaxLlamaChatCacheRamMiB)
            .When(static request => request.LlamaChatCacheRamMiB is not null);

        RuleFor(static request => request.LlamaCpuThreadReserve!.Value)
            .InclusiveBetween(StoredNodeSettings.MinLlamaCpuThreadReserve, StoredNodeSettings.MaxLlamaCpuThreadReserve)
            .When(static request => request.LlamaCpuThreadReserve is not null);

        RuleFor(static request => request.LlamaGpuReservePercent!.Value)
            .InclusiveBetween(StoredNodeSettings.MinLlamaGpuReservePercent, StoredNodeSettings.MaxLlamaGpuReservePercent)
            .When(static request => request.LlamaGpuReservePercent is not null);

        RuleFor(static request => request.LlamaRamReservePercent!.Value)
            .InclusiveBetween(StoredNodeSettings.MinLlamaRamReservePercent, StoredNodeSettings.MaxLlamaRamReservePercent)
            .When(static request => request.LlamaRamReservePercent is not null);

        RuleFor(static request => request.ImageIdleTimeToLiveSeconds!.Value)
            .InclusiveBetween(StoredNodeSettings.MinImageIdleTimeToLiveSeconds, StoredNodeSettings.MaxImageIdleTimeToLiveSeconds)
            .When(static request => request.ImageIdleTimeToLiveSeconds is not null);

        RuleFor(static request => request.ModelFitSafetyMarginPercent!.Value)
            .InclusiveBetween(StoredNodeSettings.MinModelFitSafetyMarginPercent, StoredNodeSettings.MaxModelFitSafetyMarginPercent)
            .When(static request => request.ModelFitSafetyMarginPercent is not null);

        RuleFor(static request => request.MaxProviderCallsPerInvocation!.Value)
            .InclusiveBetween(StoredNodeSettings.MinMaxProviderCallsPerInvocation, StoredNodeSettings.MaxMaxProviderCallsPerInvocation)
            .When(static request => request.MaxProviderCallsPerInvocation is not null);

        RuleFor(static request => request.CustomToolMaxTimeoutSeconds!.Value)
            .InclusiveBetween(StoredNodeSettings.MinCustomToolMaxTimeoutSeconds, StoredNodeSettings.MaxCustomToolMaxTimeoutSeconds)
            .When(static request => request.CustomToolMaxTimeoutSeconds is not null);

        RuleFor(static request => request.WebFetchTimeoutSeconds!.Value)
            .InclusiveBetween(StoredNodeSettings.MinWebFetchTimeoutSeconds, StoredNodeSettings.MaxWebFetchTimeoutSeconds)
            .When(static request => request.WebFetchTimeoutSeconds is not null);

        RuleFor(static request => request.WebFetchMaxContentChars!.Value)
            .InclusiveBetween(StoredNodeSettings.MinWebFetchMaxContentChars, StoredNodeSettings.MaxWebFetchMaxContentChars)
            .When(static request => request.WebFetchMaxContentChars is not null);

        RuleFor(static request => request.KnowledgeSearchDefaultResults!.Value)
            .InclusiveBetween(StoredNodeSettings.MinKnowledgeSearchResults, StoredNodeSettings.MaxKnowledgeSearchResults)
            .When(static request => request.KnowledgeSearchDefaultResults is not null);

        RuleFor(static request => request.KnowledgeSearchMaxResults!.Value)
            .InclusiveBetween(StoredNodeSettings.MinKnowledgeSearchResults, StoredNodeSettings.MaxKnowledgeSearchResults)
            .When(static request => request.KnowledgeSearchMaxResults is not null);

        RuleFor(static request => request.HuggingFaceDownloadConnections!.Value)
            .InclusiveBetween(StoredNodeSettings.MinHuggingFaceDownloadConnections, StoredNodeSettings.MaxHuggingFaceDownloadConnections)
            .When(static request => request.HuggingFaceDownloadConnections is not null);

        RuleFor(static request => request.TranscriptionInferenceTimeoutMinutes!.Value)
            .InclusiveBetween(StoredNodeSettings.MinTranscriptionInferenceTimeoutMinutes, StoredNodeSettings.MaxTranscriptionInferenceTimeoutMinutes)
            .When(static request => request.TranscriptionInferenceTimeoutMinutes is not null);

        RuleFor(static request => request.AgentHomeMaxRunSeconds!.Value)
            .InclusiveBetween(StoredNodeSettings.MinAgentHomeMaxRunSeconds, StoredNodeSettings.MaxAgentHomeMaxRunSeconds)
            .When(static request => request.AgentHomeMaxRunSeconds is not null);

        RuleFor(static request => request.AgentHomeRunRetentionDays!.Value)
            .InclusiveBetween(StoredNodeSettings.MinAgentHomeRunRetentionDays, StoredNodeSettings.MaxAgentHomeRunRetentionDays)
            .When(static request => request.AgentHomeRunRetentionDays is not null);

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
}
