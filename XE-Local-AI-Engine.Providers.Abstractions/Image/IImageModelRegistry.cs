namespace XE_Local_AI_Engine.Providers.Abstractions.Image;

/// <summary>
///     Read-mostly query over the image models present locally, backed by the on-disk manifest the store writes. The
///     runtime resolves a <c>ModelName → file-set</c> mapping; the store is the only writer, so this seam never mutates
///     state. Mirrors <see cref="Gguf.IGgufModelRegistry" /> for the diffusion-model file-set.
/// </summary>
public interface IImageModelRegistry
{
    /// <summary>Lists all present image models from the manifest.</summary>
    Task<IReadOnlyList<ImageModelRegistryEntry>> ListAsync(CancellationToken ct);

    /// <summary>Finds the registry entry for <paramref name="modelName" />, or <see langword="null" /> when absent.</summary>
    Task<ImageModelRegistryEntry?> FindAsync(string modelName, CancellationToken ct);
}
