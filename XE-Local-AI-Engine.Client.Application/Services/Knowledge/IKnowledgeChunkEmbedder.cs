namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Embeds a document's chunk texts through the node-local embedding provider and returns one <c>float32</c> BLOB per
///     chunk, aligned by index to the input.
/// </summary>
/// <remarks>
///     Blobs are laid out in the platform's native byte order and carry the RESOLVED embedding model name that produced
///     them plus the vector dimension they were produced at. Applies the document embedding prefix and batches the work;
///     a transport/model failure, or a within-run dimension inconsistency, throws a content-free
///     <see cref="KnowledgeIngestionException" />.
/// </remarks>
public interface IKnowledgeChunkEmbedder
{
    /// <summary>
    ///     Embeds <paramref name="chunkContents" /> in batches and returns the per-chunk embedding blobs in the same
    ///     order plus the resolved model name that built them. Returns an empty vector list for empty input.
    /// </summary>
    Task<KnowledgeEmbeddingResult> EmbedAsync(IReadOnlyList<string> chunkContents, CancellationToken cancellationToken);

    /// <summary>
    ///     Best-effort resolution of the CONFIDENTLY-resolved embedding model's advertised context window in tokens, for
    ///     token-aware chunk sizing.
    /// </summary>
    /// <remarks>
    ///     Returns <see langword="null" /> when the window is unknown — provider unreachable, resolution not confident, or
    ///     the resolved model advertises no context length — so the caller falls back to the configured chunk-token
    ///     budget. Never throws for a provider/transport failure, because chunking must proceed regardless; a genuine
    ///     caller cancellation still propagates.
    /// </remarks>
    Task<int?> ResolveEmbeddingContextWindowAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Resolves an exact pre-generation identity only when the active vector policy fixes its width. Native-width
    ///     models return null because their exact cache identity is unknown until the provider produces a vector.
    /// </summary>
    Task<KnowledgeEmbeddingDescriptor?> ResolveExpectedVectorAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<KnowledgeEmbeddingDescriptor?>(null);
    }
}

public sealed class KnowledgeEmbeddingDescriptor
{
    public required string ResolvedModel { get; init; }

    public required string VectorIdentity { get; init; }

    public required int Dimension { get; init; }
}

/// <summary>
///     The embedding blobs for a set of chunks plus the RESOLVED embedding model name that produced them and the vector
///     dimension observed for this run.
/// </summary>
/// <remarks>
///     The resolved name — not the configured name — is the single identity the ingestion lane stamps on the document
///     row and every chunk-vector scope key, so the model that built the vectors always equals the name they are keyed
///     under. The dimension is stamped on each vector row alongside it and is derived from the vectors themselves, with
///     no static config constant, so any model's native width is honored.
/// </remarks>
public sealed class KnowledgeEmbeddingResult
{
    /// <summary>One little-endian <c>float32</c> embedding blob per input chunk, aligned by index.</summary>
    public required IReadOnlyList<byte[]> Vectors { get; init; }

    /// <summary>The model name the resolver selected on the embedding provider for this operation.</summary>
    public required string ResolvedModel { get; init; }

    /// <summary>Canonical resolved-model + transform algorithm/version + width identity.</summary>
    public required string VectorIdentity { get; init; }

    /// <summary>The <c>float32</c> vector width every blob in <see cref="Vectors" /> was produced at; <c>0</c> for empty input.</summary>
    public required int Dimension { get; init; }
}
