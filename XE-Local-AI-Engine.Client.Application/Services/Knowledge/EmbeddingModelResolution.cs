namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>The outcome of one <see cref="IEmbeddingModelResolver.ResolveAsync" /> call.</summary>
/// <remarks>
///     <see cref="Name" /> is always the model name to embed with; ingestion and search consume only that.
///     <see cref="IsConfident" /> marks a REAL resolution — an installed model was matched, the exact configured name or
///     a fallback GGUF — against a degrade-to-configured-name outcome, where nothing matched or the provider was
///     unreachable. A non-confident name must never be treated as a vector identity: comparing stored vectors or
///     staleness against a mere fallback misclassifies every document as stale during a transient provider outage.
/// </remarks>
public sealed class EmbeddingModelResolution
{
    /// <summary>The model name to hand to the embedding generator.</summary>
    public required string Name { get; init; }

    /// <summary>Whether an installed model was actually matched (as opposed to a bare fallback).</summary>
    public required bool IsConfident { get; init; }

    /// <summary>Content-free fingerprint of the matched immutable installed-inventory record.</summary>
    public string RevisionFingerprint { get; init; } = "unresolved";
}
