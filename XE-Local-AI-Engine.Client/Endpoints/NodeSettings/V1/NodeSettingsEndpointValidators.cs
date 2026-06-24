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
