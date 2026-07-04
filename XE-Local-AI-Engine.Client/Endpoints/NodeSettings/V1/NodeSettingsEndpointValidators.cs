namespace XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1;

using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Boundary validation for <see cref="SaveNodeSettingsRequest" />. Every migrated field is optional; a rule fires only
///     <c>When</c> the field is supplied (a <see langword="null" /> keeps the current stored value). Range/format
///     violations are rejected with a 400 and a clear message before anything is persisted; the store's <c>Normalize</c>
///     remains the second, defense-in-depth clamp.
/// </summary>
public sealed class SaveNodeSettingsRequestValidator : Validator<SaveNodeSettingsRequest>
{
    public SaveNodeSettingsRequestValidator()
    {
        RuleFor(static request => request.MaxMessageRequestTimeoutSeconds!.Value)
            .InclusiveBetween(StoredNodeSettings.MinMaxMessageRequestTimeoutSeconds, StoredNodeSettings.MaxMaxMessageRequestTimeoutSeconds)
            .When(static request => request.MaxMessageRequestTimeoutSeconds is not null);

        // ── General ──
        RuleFor(static request => request.OllamaEndpoint!)
            .Must(BeAbsoluteHttpUrl)
            .When(static request => !string.IsNullOrWhiteSpace(request.OllamaEndpoint))
            .WithMessage("Ollama endpoint must be an absolute http or https URL.");

        RuleFor(static request => request.ToolCapableModels!)
            .Must(static models => models.Count > 0 && models.All(static model => !string.IsNullOrWhiteSpace(model)))
            .When(static request => request.ToolCapableModels is not null)
            .WithMessage("Tool-capable models must be a non-empty list of non-blank model names.");

        RuleFor(static request => request.RecommendedLlamaCppTag)
            .Must(StoredNodeSettings.IsValidRecommendedLlamaCppTag)
            .When(static request => request.RecommendedLlamaCppTag is not null)
            .WithMessage("Recommended llama.cpp tag must be in the form b<number>.");

        RuleFor(static request => request.LlamaMaxLoadedProcesses!.Value)
            .InclusiveBetween(StoredNodeSettings.MinLlamaMaxLoadedProcesses, StoredNodeSettings.MaxLlamaMaxLoadedProcesses)
            .When(static request => request.LlamaMaxLoadedProcesses is not null);

        RuleFor(static request => request.LlamaIdleTimeToLiveSeconds!.Value)
            .InclusiveBetween(StoredNodeSettings.MinLlamaIdleTimeToLiveSeconds, StoredNodeSettings.MaxLlamaIdleTimeToLiveSeconds)
            .When(static request => request.LlamaIdleTimeToLiveSeconds is not null);

        RuleFor(static request => request.MaxResponseSizeMb!.Value)
            .InclusiveBetween(StoredNodeSettings.MinMaxResponseSizeMb, StoredNodeSettings.MaxMaxResponseSizeMb)
            .When(static request => request.MaxResponseSizeMb is not null);

        // ── Chat launch tuning (speculative decoding + prompt-cache reuse) ──
        RuleFor(static request => request.ChatCacheReuse!.Value)
            .InclusiveBetween(StoredNodeSettings.MinChatCacheReuse, StoredNodeSettings.MaxChatCacheReuse)
            .When(static request => request.ChatCacheReuse is not null);

        RuleFor(static request => request.SpeculativeMode)
            .Must(StoredNodeSettings.IsValidSpeculativeMode)
            .When(static request => !string.IsNullOrWhiteSpace(request.SpeculativeMode))
            .WithMessage("Unknown speculative decoding mode.");

        RuleFor(static request => request.SpeculativeDraftMaxTokens!.Value)
            .InclusiveBetween(StoredNodeSettings.MinSpeculativeDraftMaxTokens, StoredNodeSettings.MaxSpeculativeDraftMaxTokens)
            .When(static request => request.SpeculativeDraftMaxTokens is not null);

        RuleFor(static request => request.SpeculativeDraftGpuLayers!.Value)
            .InclusiveBetween(StoredNodeSettings.MinSpeculativeDraftGpuLayers, StoredNodeSettings.MaxSpeculativeDraftGpuLayers)
            .When(static request => request.SpeculativeDraftGpuLayers is not null);

        // Cross-field: a draft-* mode needs a draft model. This boundary rule fires when the request itself sets a draft-*
        // SpeculativeMode; it catches the common "pick draft mode, forget the model" case with an immediate 400. It cannot
        // see the CURRENT stored mode (partial-update merge), so the endpoint additionally re-checks the merged result —
        // together they guarantee a draft-* mode never persists without a draft model (which would fail chat-server start).
        RuleFor(static request => request.SpeculativeDraftModelName)
            .Must(static name => !string.IsNullOrWhiteSpace(name))
            .When(static request => StoredNodeSettings.SpeculativeModeRequiresDraftModel(request.SpeculativeMode))
            .WithMessage("Speculative decoding is set to a draft model mode, but no draft model was selected.");

        // ── Advanced / developer-only ──
        RuleFor(static request => request.HuggingFaceDiskMarginBytes!.Value)
            .GreaterThan(0)
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
    }

    private static bool BeAbsoluteHttpUrl(string value)
    {
        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}
