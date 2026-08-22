namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Client.Persistence;
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
}
