namespace XE_Local_AI_Engine.Providers.Abstractions.Image;

using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     Ensures an image-model file-set is present (download-if-missing, resume, retry, cancel, offline reuse), resolves
///     a model name to its local part paths, and lists installed image models. Mirrors <see cref="Gguf.IGgufModelStore" />
///     but every operation is over a <b>file-set</b> (diffusion + optional vae/clip/t5), not a single file.
/// </summary>
public interface IImageModelStore
{
    /// <summary>
    ///     Resolves the installed model's parts (each with its verified local path), or <see langword="null" /> when the
    ///     model is not installed or a part file is missing on disk.
    /// </summary>
    Task<IReadOnlyList<ImageModelPart>?> ResolveModelPartsAsync(string modelName, CancellationToken ct);

    /// <summary>Lists every installed image model as a provider-neutral descriptor.</summary>
    Task<IReadOnlyList<LocalModelDescriptor>> ListInstalledModelsAsync(CancellationToken ct);

    /// <summary>
    ///     Ensures every part of <paramref name="request" />'s file-set is present locally, downloading any missing part
    ///     (resumable, retried, cancellable) and registering the completed set. A file-set already present and verified is
    ///     reused without download.
    /// </summary>
    Task<ImageModelHandle> EnsureModelAsync(ImageModelRequest request, IProgress<PullProgress>? progress, CancellationToken ct);

    /// <summary>Removes an installed model: deletes its part files and the registry entry. Idempotent.</summary>
    Task DeleteModelAsync(string modelName, CancellationToken ct);

    /// <summary>True when the model is registered and every part file is present on disk.</summary>
    Task<bool> ExistsAsync(string modelName, CancellationToken ct);
}
