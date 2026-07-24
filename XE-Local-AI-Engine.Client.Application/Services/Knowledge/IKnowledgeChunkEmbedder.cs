namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Embeds a document's chunk texts through the node-local embedding provider and returns one <c>float32</c> BLOB per
///     chunk, laid out in the platform's native byte order (aligned by index to the input), together with the RESOLVED
///     embedding model name that produced them and the vector dimension they were produced at. Applies the document
///     embedding prefix and batches the work; a transport/model failure or a within-run dimension inconsistency throws a
///     content-free <see cref="KnowledgeIngestionException" />.
/// </summary>
public interface IKnowledgeChunkEmbedder
{
    /// <summary>
    ///     Embeds <paramref name="chunkContents" /> in batches and returns the per-chunk embedding blobs in the same
    ///     order plus the resolved model name that built them. Returns an empty vector list for empty input.
    /// </summary>
    Task<KnowledgeEmbeddingResult> EmbedAsync(IReadOnlyList<string> chunkContents, CancellationToken cancellationToken);

    /// <summary>
    ///     Best-effort resolution of the CONFIDENTLY-resolved embedding model's advertised context window (in tokens), for
    ///     token-aware chunk sizing. Returns <see langword="null" /> when the window is unknown — the provider is
    ///     unreachable, the resolution is not confident, or the resolved model advertises no context length — so the caller
    ///     falls back to the configured chunk-token budget. This never throws for a provider/transport failure (chunking
    ///     must proceed regardless); a genuine caller cancellation still propagates.
    /// </summary>
    Task<int?> ResolveEmbeddingContextWindowAsync(CancellationToken cancellationToken);
}

/// <summary>
///     The embedding blobs for a set of chunks plus the RESOLVED embedding model name that produced them and the vector
///     dimension observed for this run. The resolved name (not the configured name) is the single identity the ingestion
///     lane stamps on the document row and every chunk-vector scope key, so the model that built the vectors always equals
///     the name they are keyed under; the dimension is stamped on each vector row alongside it. Dimension is derived from
///     the vectors themselves — no static config constant — so any model's native width is honored.
/// </summary>
/// <param name="Vectors">One little-endian <c>float32</c> embedding blob per input chunk, aligned by index.</param>
/// <param name="ResolvedModel">The model name the resolver selected on the embedding provider for this operation.</param>
/// <param name="Dimension">The <c>float32</c> vector width every blob in <see cref="Vectors" /> was produced at; <c>0</c> for empty input.</param>
public sealed record KnowledgeEmbeddingResult(IReadOnlyList<byte[]> Vectors, string ResolvedModel, int Dimension);
