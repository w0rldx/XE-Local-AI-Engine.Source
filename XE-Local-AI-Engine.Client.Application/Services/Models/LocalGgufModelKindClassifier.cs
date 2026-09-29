namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>Transport-neutral, offline classification for installed GGUF model names.</summary>
public static class LocalGgufModelKindClassifier
{
    public static ModelKind Classify(string modelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

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
    ///     Whether an installed GGUF may serve chat: an operator override wins, then a persisted non-Unknown detection,
    ///     else the offline name classification (a fresh GGUF carries no row). A draft quant is never chat.
    /// </summary>
    public static bool IsChatModel(string modelName, ModelClassificationRecord? classification)
    {
        if (Classify(modelName) == ModelKind.Draft)
        {
            return false;
        }

        if (classification?.OverrideKind is { } overrideKind)
        {
            return overrideKind == ModelKind.Chat;
        }

        return classification?.DetectedKind switch
        {
            ModelKind.Embedding or ModelKind.Reranker or ModelKind.Draft => false,
            ModelKind.Chat => true,
            _ => Classify(modelName) == ModelKind.Chat
        };
    }
}
