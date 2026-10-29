namespace XE_Local_AI_Engine.Client.Services.Drafting.Implementation;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.Ollama.Contracts;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;

/// <summary>Which node-local models an operator-triggered generation may run on: drafting's rule, shared with the workflow designer.</summary>
internal static class DraftModelEligibility
{
    /// <summary>
    ///     Fail-closed eligibility: the allowlisted provider that serves <paramref name="modelName" />, or
    ///     <see langword="null" /> when the model may not be drafted with.
    /// </summary>
    /// <remarks>
    ///     It is read-only — the persisted classification is read directly — so no detection probe runs and no cache
    ///     row is written.
    /// </remarks>
    public static async Task<string?> ResolveEligibleProviderAsync(string modelName,
        IModelClassificationStore modelClassificationStore,
        IGgufModelStore ggufModelStore,
        IOllamaModelService ollamaModelService,
        CancellationToken cancellationToken)
    {
        var classification = await modelClassificationStore.GetByNameAsync(modelName, cancellationToken);

        // A fresh GGUF has no row, so an installed one uses the shared GGUF chat rule (override, row, then name).
        var installedGguf = await ggufModelStore.ListInstalledModelsAsync(cancellationToken);
        if (installedGguf.FirstOrDefault(descriptor => string.Equals(descriptor.ModelName, modelName, StringComparison.OrdinalIgnoreCase)) is { } installed)
        {
            return LocalGgufModelKindClassifier.IsChatModel(modelName, classification, installed.DecisionType) ? LlamaServerProviderConstants.ProviderName : null;
        }

        // An Ollama model needs a persisted Chat row: drafting must not be the thing that discovers its kind.
        if (classification is null || (classification.OverrideKind ?? classification.DetectedKind) != ModelKind.Chat)
        {
            return null;
        }

        // The two installed-model universes are disjoint and there is no unified inventory facade, so the Ollama side is
        // composed separately — and only when its endpoint is loopback.
        return await ollamaModelService.IsLoopbackModelInstalledAsync(modelName, cancellationToken)
            ? OllamaLocalModelProvider.OllamaProviderName
            : null;
    }
}
