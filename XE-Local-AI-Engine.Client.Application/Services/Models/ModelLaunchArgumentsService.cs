namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Owns the per-model extra <c>llama-server</c> launch-argument override behind the LocalModels Operator
///     endpoints: read it, store it, clear it. The HTTP edge keeps the decoding, validation and reserved-flag
///     rejection it already owns; this type is the only path from those endpoints to the persisted override.
/// </summary>
public sealed class ModelLaunchArgumentsService(IModelLaunchArgumentsStore store)
{
    private readonly IModelLaunchArgumentsStore _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>
    ///     Returns the raw override stored for <paramref name="modelName" />, or <see langword="null" /> when the
    ///     model has none.
    /// </summary>
    public Task<string?> GetRawArgumentsAsync(string modelName, CancellationToken cancellationToken = default)
    {
        return _store.GetRawArgumentsAsync(modelName, cancellationToken);
    }

    /// <summary>
    ///     Stores <paramref name="rawArguments" /> as the override for <paramref name="modelName" /> and returns the
    ///     stored record, whose model name is the persisted casing rather than the caller's.
    /// </summary>
    public Task<ModelLaunchArgumentsRecord> SaveAsync(string modelName, string rawArguments, CancellationToken cancellationToken = default)
    {
        return _store.UpsertAsync(modelName, rawArguments, cancellationToken);
    }

    /// <summary>Removes the override for <paramref name="modelName" />. Idempotent: no override is not an error.</summary>
    public async Task ClearAsync(string modelName, CancellationToken cancellationToken = default)
    {
        _ = await _store.DeleteAsync(modelName, cancellationToken).ConfigureAwait(false);
    }
}
