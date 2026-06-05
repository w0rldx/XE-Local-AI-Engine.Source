namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Node-scoped persistence for local model classifications. Stores the digest-keyed detection cache and the
///     operator override, keyed by model name (case-insensitive). Not encrypted — model names, digests, capabilities
///     and kinds are not secrets. This store performs no validation — that is the application-layer service's
///     responsibility; the store only maps rows to records.
/// </summary>
public interface IModelClassificationStore
{
    /// <summary>Returns the classification for <paramref name="modelName" /> (case-insensitive), or <c>null</c> when none exists.</summary>
    Task<ModelClassificationRecord?> GetByNameAsync(string modelName, CancellationToken cancellationToken = default);

    /// <summary>Returns every stored classification, ordered by model name.</summary>
    Task<IReadOnlyList<ModelClassificationRecord>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Inserts or updates the detected fields (digest, kind, capabilities, detected-at) for <paramref name="modelName" />,
    ///     preserving any existing operator override, and returns the stored record.
    /// </summary>
    Task<ModelClassificationRecord> UpsertDetectedAsync(string modelName,
        string? digest,
        ModelKind detectedKind,
        string? capabilitiesJson,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Sets (or, when <paramref name="overrideKind" /> is <c>null</c>, clears) the operator override for
    ///     <paramref name="modelName" />, inserting a row when none exists, and returns the stored record.
    /// </summary>
    Task<ModelClassificationRecord> SetOverrideAsync(string modelName,
        ModelKind? overrideKind,
        CancellationToken cancellationToken = default);
}
