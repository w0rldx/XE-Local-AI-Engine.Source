namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Content-addressed reuse for document-chunk embeddings. Keys contain only hashes and version/model identities;
///     plaintext chunk content is never retained by this surface.
/// </summary>
public interface IKnowledgeChunkEmbeddingCache
{
    /// <summary>
    ///     Resolves one vector per key, preserving input order. Committed vectors are reused first and the factory is
    ///     invoked once for the remaining keys. A failed or incomplete factory result is never cached.
    /// </summary>
    Task<IReadOnlyList<byte[]>> GetOrCreateManyAsync(IReadOnlyList<KnowledgeChunkEmbeddingCacheKey> keys,
        Func<IReadOnlyList<KnowledgeChunkEmbeddingCacheKey>, CancellationToken, Task<IReadOnlyList<byte[]>>> factory,
        CancellationToken cancellationToken);
}

/// <summary>Durable lookup seam over already-committed knowledge vectors.</summary>
public interface IKnowledgeChunkEmbeddingReuseStore
{
    /// <summary>Returns exact-key matches which were committed no earlier than <paramref name="notBeforeUtc" />.</summary>
    Task<IReadOnlyDictionary<KnowledgeChunkEmbeddingCacheKey, byte[]>> FindManyAsync(
        IReadOnlyList<KnowledgeChunkEmbeddingCacheKey> keys,
        DateTimeOffset notBeforeUtc,
        CancellationToken cancellationToken);
}

/// <summary>
///     Exact cache identity for the contextual chunk text, preprocessing versions, and canonical vector projection.
///     The embedding-input SHA-256 is normalized to lowercase; no raw chunk text is accepted or retained.
/// </summary>
public sealed record KnowledgeChunkEmbeddingCacheKey
{
    public KnowledgeChunkEmbeddingCacheKey(string embeddingInputHash,
        string parserVersion,
        string chunkerVersion,
        string vectorIdentity,
        int dimension)
    {
        if (!IsSha256Hex(embeddingInputHash))
        {
            throw new ArgumentException("The embedding input identity must be a SHA-256 hexadecimal hash.", nameof(embeddingInputHash));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(parserVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkerVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(vectorIdentity);
        if (dimension <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dimension), dimension, "The vector dimension must be positive.");
        }

        EmbeddingInputHash = embeddingInputHash.ToUpperInvariant();
        ParserVersion = parserVersion;
        ChunkerVersion = chunkerVersion;
        VectorIdentity = vectorIdentity;
        Dimension = dimension;
    }

    public string EmbeddingInputHash { get; }
    public string ParserVersion { get; }
    public string ChunkerVersion { get; }
    public string VectorIdentity { get; }
    public int Dimension { get; }

    private static bool IsSha256Hex(string value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!((character >= '0' && character <= '9')
                  || (character >= 'a' && character <= 'f')
                  || (character >= 'A' && character <= 'F')))
            {
                return false;
            }
        }

        return true;
    }
}
