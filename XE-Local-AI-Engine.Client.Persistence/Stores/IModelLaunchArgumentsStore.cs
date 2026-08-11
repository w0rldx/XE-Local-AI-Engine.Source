namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Node-scoped persistence for the per-model extra <c>llama-server</c> argument override. Keyed by model name
///     (case-insensitive). Not encrypted — llama.cpp flags are not secrets. The store persists the raw operator string
///     verbatim and performs no tokenizing or flag validation; the caller (the settings endpoint on write, the spawn-path
///     resolver on read) owns parsing and stripping the reserved process-contract flags.
/// </summary>
public interface IModelLaunchArgumentsStore
{
    /// <summary>
    ///     Returns the raw extra-argument string stored for <paramref name="modelName" /> (case-insensitive), or
    ///     <c>null</c> when the model has no override.
    /// </summary>
    Task<string?> GetRawArgumentsAsync(string modelName, CancellationToken cancellationToken = default);

    /// <summary>Returns every stored override, ordered by model name.</summary>
    Task<IReadOnlyList<ModelLaunchArgumentsRecord>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Inserts or updates the raw extra-argument override for <paramref name="modelName" /> and returns the stored
    ///     record.
    /// </summary>
    Task<ModelLaunchArgumentsRecord> UpsertAsync(string modelName, string rawArguments, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Removes the override for <paramref name="modelName" /> if present. Returns <c>true</c> when a row was deleted,
    ///     <c>false</c> when the model had no override (idempotent).
    /// </summary>
    Task<bool> DeleteAsync(string modelName, CancellationToken cancellationToken = default);
}
