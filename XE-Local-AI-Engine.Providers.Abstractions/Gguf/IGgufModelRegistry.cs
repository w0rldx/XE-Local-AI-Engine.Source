namespace XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Read-mostly query over the GGUF models present locally, backed by the on-disk manifest the store writes. Lane A
///     resolves a <c>ModelName → file</c> mapping; Lane C's advisor checks "already downloaded?". The store is the only
///     writer; this seam never mutates state.
/// </summary>
public interface IGgufModelRegistry
{
    /// <summary>Lists all present GGUF models from the manifest.</summary>
    Task<IReadOnlyList<GgufModelRegistryEntry>> ListAsync(CancellationToken ct);

    /// <summary>Finds the registry entry for <paramref name="modelName" />, or <see langword="null" /> when absent.</summary>
    Task<GgufModelRegistryEntry?> FindAsync(string modelName, CancellationToken ct);
}
