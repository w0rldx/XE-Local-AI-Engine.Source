namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Node-scoped persistence for the per-model→provider map. Keyed by model name
///     (case-insensitive). Not encrypted — model names and provider keys are not secrets. The store performs no
///     validation and applies no routing default; the caller (the application-layer provider resolver) owns the
///     "unmapped model → default provider" policy.
/// </summary>
public interface IModelProviderMapStore
{
    /// <summary>
    ///     Returns the provider key mapped to <paramref name="modelName" /> (case-insensitive), or <c>null</c> when the
    ///     model has no explicit mapping (the caller then applies its routing default).
    /// </summary>
    Task<string?> GetProviderForModelAsync(string modelName, CancellationToken cancellationToken = default);

    /// <summary>Returns every stored mapping, ordered by model name.</summary>
    Task<IReadOnlyList<ModelProviderMapRecord>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Inserts or updates the provider mapping for <paramref name="modelName" /> and returns the stored record.
    /// </summary>
    Task<ModelProviderMapRecord> UpsertAsync(string modelName, string providerName, CancellationToken cancellationToken = default);
}
