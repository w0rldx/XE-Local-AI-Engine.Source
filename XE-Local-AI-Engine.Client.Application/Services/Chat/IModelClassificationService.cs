namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Single source of a local model's effective <see cref="ModelKind" />, resolving <c>override ?? detected</c> and
///     defaulting to <see cref="ModelKind.Unknown" />.
/// </summary>
/// <remarks>
///     It lazily probes Ollama's <c>/api/show</c> capabilities and caches the result by content digest. The list
///     endpoint and the chat picker both read effective kinds through it, so no kind logic is duplicated in React.
/// </remarks>
public interface IModelClassificationService
{
    /// <summary>
    ///     The effective classification for each supplied model, keyed by model name.
    /// </summary>
    /// <remarks>
    ///     It lazily detects and caches a model that is unclassified, stale — its supplied digest differs from the
    ///     cached one — or still <see cref="ModelKind.Unknown" /> with no cached capabilities. A cache hit issues no
    ///     <c>/api/show</c> call, and a detection failure never propagates: the model falls back to its cached
    ///     classification or <see cref="ModelKind.Unknown" />.
    /// </remarks>
    Task<IReadOnlyDictionary<string, ModelClassificationResult>> ClassifyAsync(IEnumerable<ModelIdentity> models,
        CancellationToken cancellationToken = default);

    /// <summary>Sets the operator override for <paramref name="modelName" /> and returns its resolved classification.</summary>
    Task<ModelClassificationResult> SetOverrideAsync(string modelName, ModelKind kind, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Clears the operator override for <paramref name="modelName" /> so the effective kind falls back to the
    ///     detected one, lazily detecting first when no detection has been cached yet.
    /// </summary>
    Task<ModelClassificationResult> ResetOverrideAsync(string modelName, CancellationToken cancellationToken = default);
}
