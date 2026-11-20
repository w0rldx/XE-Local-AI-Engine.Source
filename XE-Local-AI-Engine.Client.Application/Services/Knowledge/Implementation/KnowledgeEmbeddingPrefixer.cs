namespace XE_Local_AI_Engine.Client.Services.Knowledge.Implementation;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Default <see cref="IKnowledgeEmbeddingPrefixer" />: formats through the <see cref="EmbeddingModelProfile" /> of the
///     resolved model name. Stateless and thread-safe — safe to register as a singleton.
/// </summary>
public sealed class KnowledgeEmbeddingPrefixer : IKnowledgeEmbeddingPrefixer
{
    public string ForDocument(EmbeddingModelResolution resolution, string content)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(content);
        return string.Concat(EmbeddingModelProfile.For(resolution.Name).DocumentPrefix, content);
    }

    public string ForQuery(EmbeddingModelResolution resolution, string query)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(query);
        return string.Concat(EmbeddingModelProfile.For(resolution.Name).QueryPrefix, query);
    }
}
