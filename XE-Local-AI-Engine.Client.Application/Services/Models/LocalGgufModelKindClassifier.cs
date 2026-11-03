namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>Transport-neutral, offline classification for installed GGUF models.</summary>
/// <remarks>
///     A GGUF's <c>&lt;arch&gt;.decision.type</c> header key (surfaced as <c>LocalModelDescriptor.DecisionType</c>) is
///     structural, like the draft marker: it outranks every name heuristic and the Chat default, because Kev-4B is a
///     <c>qwen35</c> with a chat template and still cannot chat.
/// </remarks>
public static class LocalGgufModelKindClassifier
{
    public static ModelKind Classify(string modelName, string? decisionType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        if (!string.IsNullOrWhiteSpace(decisionType))
        {
            return ModelKind.Decision;
        }

        if (ModelKindDetector.IsDraftName(modelName))
        {
            return ModelKind.Draft;
        }

        if (ModelKindDetector.IsRerankerName(modelName))
        {
            return ModelKind.Reranker;
        }

        return ModelKindDetector.IsEmbeddingName(modelName) ? ModelKind.Embedding : ModelKind.Chat;
    }

    /// <summary>
    ///     The effective kind: a draft quant or a decision model is structural and ignores any override; otherwise an
    ///     operator override wins over the offline classification.
    /// </summary>
    public static ResolvedModelKind ResolveKind(string modelName, ModelClassificationRecord? classification, string? decisionType = null)
    {
        var detected = Classify(modelName, decisionType);
        var overrideKind = detected is ModelKind.Draft or ModelKind.Decision ? null : classification?.OverrideKind;

        return new ResolvedModelKind
        {
            Kind = overrideKind ?? detected,
            DetectedKind = detected,
            IsOverridden = overrideKind is not null
        };
    }

    /// <summary>Whether an installed GGUF may serve chat, by its effective kind.</summary>
    public static bool IsChatModel(string modelName, ModelClassificationRecord? classification, string? decisionType = null)
    {
        return ResolveKind(modelName, classification, decisionType).Kind == ModelKind.Chat;
    }

    /// <summary>The installed GGUF's <c>decision.type</c> family, or null when the model is not installed or is no decision model.</summary>
    /// <remarks>Reads the store's cached header facts, so a caller holding only a name pays one list, not a file read.</remarks>
    public static async Task<string?> ReadDecisionTypeAsync(IGgufModelStore store, string modelName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);

        var installed = await store.ListInstalledModelsAsync(cancellationToken);
        return installed.FirstOrDefault(descriptor => string.Equals(descriptor.ModelName, modelName, StringComparison.OrdinalIgnoreCase))?.DecisionType;
    }
}
