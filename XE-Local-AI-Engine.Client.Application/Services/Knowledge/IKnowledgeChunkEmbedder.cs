namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Embeds a document's chunk texts through the node-local embedding provider and returns one <c>float32</c> BLOB per
///     chunk, laid out in the platform's native byte order (aligned by index to the input). Applies the document
///     embedding prefix and batches the work; a transport/model failure or a dimension/count mismatch throws a
///     content-free <see cref="KnowledgeIngestionException" />.
/// </summary>
public interface IKnowledgeChunkEmbedder
{
    /// <summary>
    ///     Embeds <paramref name="chunkContents" /> in batches and returns the per-chunk embedding blobs in the same
    ///     order. Returns an empty list for empty input.
    /// </summary>
    Task<IReadOnlyList<byte[]>> EmbedAsync(IReadOnlyList<string> chunkContents, CancellationToken cancellationToken);
}
