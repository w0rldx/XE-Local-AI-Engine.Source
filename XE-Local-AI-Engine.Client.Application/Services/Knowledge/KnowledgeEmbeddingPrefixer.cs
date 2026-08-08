namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Default <see cref="IKnowledgeEmbeddingPrefixer" /> for the <c>search_document:</c> / <c>search_query:</c> intent
///     prefixes used by asymmetric embedding models. Stateless and thread-safe — safe to register as a singleton.
/// </summary>
public sealed class KnowledgeEmbeddingPrefixer : IKnowledgeEmbeddingPrefixer
{
    private const string DocumentPrefix = "search_document: ";
    private const string QueryPrefix = "search_query: ";

    public string ForDocument(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return string.Concat(DocumentPrefix, content);
    }

    public string ForQuery(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return string.Concat(QueryPrefix, query);
    }
}
