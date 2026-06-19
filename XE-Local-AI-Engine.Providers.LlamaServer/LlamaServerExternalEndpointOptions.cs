namespace XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     Optional hybrid attach-to-external-endpoint configuration. When a model name maps to an external
///     base URL here, the supervisor attaches to that endpoint instead of spawning + supervising a local child for it.
///     Empty by default (pure spawn-and-supervise). Bound from node config at DI time.
/// </summary>
public sealed class LlamaServerExternalEndpointOptions
{
    /// <summary>
    ///     Map of <c>(modelName, role)</c> → external OpenAI-compatible base URL (ending with <c>/v1</c>). A match
    ///     short-circuits spawning: the supervisor returns the configured endpoint and never owns a process for it.
    /// </summary>
    public IReadOnlyDictionary<string, Uri> ChatEndpointsByModel { get; init; } =
        new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);

    /// <summary>External embedding endpoints by model name (same semantics as <see cref="ChatEndpointsByModel" />).</summary>
    public IReadOnlyDictionary<string, Uri> EmbeddingEndpointsByModel { get; init; } =
        new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolves a configured external endpoint for <paramref name="modelName" />/<paramref name="role" />, or null.</summary>
    public Uri? Resolve(string modelName, ModelRole role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        var map = role == ModelRole.Embedding ? EmbeddingEndpointsByModel : ChatEndpointsByModel;
        return map.TryGetValue(modelName, out var uri) ? uri : null;
    }
}
