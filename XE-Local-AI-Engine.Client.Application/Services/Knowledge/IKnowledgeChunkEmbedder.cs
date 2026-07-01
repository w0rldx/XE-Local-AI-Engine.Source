namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Embeds a document's chunk texts through the node-local embedding provider and returns one <c>float32</c> BLOB per
///     chunk, laid out in the platform's native byte order (aligned by index to the input), together with the RESOLVED
///     embedding model name that produced them. Applies the document embedding prefix and batches the work; a
///     transport/model failure or a dimension/count mismatch throws a content-free <see cref="KnowledgeIngestionException" />.
/// </summary>
public interface IKnowledgeChunkEmbedder
{
    /// <summary>
    ///     Embeds <paramref name="chunkContents" /> in batches and returns the per-chunk embedding blobs in the same
    ///     order plus the resolved model name that built them. Returns an empty vector list for empty input.
    /// </summary>
    Task<KnowledgeEmbeddingResult> EmbedAsync(IReadOnlyList<string> chunkContents, CancellationToken cancellationToken);
}

/// <summary>
///     The embedding blobs for a set of chunks plus the RESOLVED embedding model name that produced them. The resolved
///     name (not the configured name) is the single identity the ingestion lane stamps on the document row and every
///     chunk-vector scope key, so the model that built the vectors always equals the name they are keyed under.
/// </summary>
/// <param name="Vectors">One little-endian <c>float32</c> embedding blob per input chunk, aligned by index.</param>
/// <param name="ResolvedModel">The model name the resolver selected on the embedding provider for this operation.</param>
public sealed record KnowledgeEmbeddingResult(IReadOnlyList<byte[]> Vectors, string ResolvedModel);
