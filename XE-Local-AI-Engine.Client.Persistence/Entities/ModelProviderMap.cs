namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Persisted mapping of a single local model name to the provider that serves it (for example
///     <c>"llamacpp"</c> for a supervisor-served GGUF or <c>"ollama"</c> for an Ollama-managed model). Keyed by
///     model name (<c>NOCASE</c>). Not encrypted — a model name and a provider key are not secrets. This is the
///     resume-safe routing record the model-routing client and the preview/embeddings resolvers read so a selected
///     model resolves to the right runtime across node restarts.
/// </summary>
internal sealed record class ModelProviderMap
{
    /// <summary>Model name (primary key, <c>NOCASE</c> collation). The stable routing key.</summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>Provider key that serves this model (for example <c>"llamacpp"</c> or <c>"ollama"</c>).</summary>
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>Unix ms of the last row write.</summary>
    public long UpdatedAtUtc { get; set; }
}
