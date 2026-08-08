namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Single source of a local model's effective <see cref="ModelKind" />. Resolves <c>override ?? detected</c>
///     (defaulting to <see cref="ModelKind.Unknown" />), lazily probing Ollama's <c>/api/show</c> capabilities and
///     caching the result by content digest. The list endpoint and the chat picker both read effective kinds through
///     this service so no kind logic is duplicated in React.
/// </summary>
public interface IModelClassificationService
{
    /// <summary>
    ///     Resolves the effective classification for each supplied model, keyed by model name. Lazily detects (and
    ///     caches) models that are unclassified, stale (the supplied digest differs from the cached one) or whose
    ///     detected kind is still <see cref="ModelKind.Unknown" /> with no cached capabilities. A cache hit (record
    ///     present, digest matches) issues no <c>/api/show</c> call. Detection failures never propagate — the model
    ///     falls back to its cached classification or <see cref="ModelKind.Unknown" />.
    /// </summary>
    Task<IReadOnlyDictionary<string, ModelClassificationResult>> ClassifyAsync(IEnumerable<(string ModelName, string? Digest)> models,
        CancellationToken cancellationToken = default);

    /// <summary>Sets the operator override for <paramref name="modelName" /> and returns its resolved classification.</summary>
    Task<ModelClassificationResult> SetOverrideAsync(string modelName, ModelKind kind, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Clears the operator override for <paramref name="modelName" /> so the effective kind falls back to the
    ///     detected one, lazily detecting first when no detection has been cached yet.
    /// </summary>
    Task<ModelClassificationResult> ResetOverrideAsync(string modelName, CancellationToken cancellationToken = default);
}
