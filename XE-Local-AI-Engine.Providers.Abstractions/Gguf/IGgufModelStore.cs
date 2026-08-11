namespace XE_Local_AI_Engine.Providers.Abstractions.Gguf;

using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     App-controlled lifecycle of GGUF model files on local disk and the shared storage seam. The llama-server
///     provider consumes the resolve + list pair (to launch <c>llama-server -m &lt;path&gt;</c> and to enumerate
///     installed models for <see cref="ILocalModelProvider.ListModelsAsync" />); its pull/delete operations and the
///     model-fit advisor consume the acquire/delete pair.
/// </summary>
/// <remarks>
///     Downloads are atomic-on-complete: a file is reported present only after the full byte stream is received and its
///     hash verifies (when an LFS OID is available). Partial downloads live under a <c>.part</c> name and are never
///     returned as complete. Progress is reported as the same <see cref="PullProgress" /> DTO the Ollama provider uses,
///     so the provider layer maps it 1:1.
/// </remarks>
public interface IGgufModelStore
{
    /// <summary>
    ///     Resolves the absolute path to the local GGUF file backing <paramref name="modelName" />, or
    ///     <see langword="null" /> when the model is not installed. Consumed by the llama-server supervisor to launch
    ///     the process.
    /// </summary>
    Task<string?> ResolveModelFilePathAsync(string modelName, CancellationToken ct);

    /// <summary>
    ///     Resolves the absolute path to the local multimodal projector (<c>mmproj</c>) file paired with
    ///     <paramref name="modelName" />, or <see langword="null" /> when the model has no projector companion (a
    ///     text-only model) or the model is not installed. Consumed by the llama-server supervisor to add
    ///     <c>--mmproj &lt;path&gt;</c> so a vision model can accept image input.
    /// </summary>
    Task<string?> ResolveProjectorFilePathAsync(string modelName, CancellationToken ct);

    /// <summary>Enumerates the installed GGUF models as normalized host-agent descriptors.</summary>
    Task<IReadOnlyList<LocalModelDescriptor>> ListInstalledModelsAsync(CancellationToken ct);

    /// <summary>
    ///     Resolves the canonical <c>{repoId}:{quant}</c> model name a request would be stored under — the SAME identity
    ///     <see cref="EnsureModelAsync" /> registers — WITHOUT downloading. Lets a caller (e.g. the download coordinator)
    ///     key its tracking/cancellation by the identity the model will actually be installed as, even when a base-quant
    ///     request resolves to a different file (such as an Unsloth Dynamic variant). May perform a lightweight repo
    ///     inspection to resolve the file; throws the same discovery/transport exceptions as a download's resolve step.
    /// </summary>
    Task<string> ResolveModelNameAsync(GgufModelRequest request, CancellationToken ct);

    /// <summary>
    ///     Ensures the selected GGUF is present locally, downloading (resume-capable, retryable, cancellable) if
    ///     missing, and returns its local path + metadata. Reports byte/status progress via <paramref name="progress" />.
    /// </summary>
    Task<GgufModelHandle> EnsureModelAsync(GgufModelRequest request, IProgress<PullProgress>? progress, CancellationToken ct);

    /// <summary>Deletes a locally installed GGUF model (file + registry entry). Idempotent.</summary>
    Task DeleteModelAsync(string modelName, CancellationToken ct);

    /// <summary>Returns whether a verified GGUF file for <paramref name="modelName" /> is present locally.</summary>
    Task<bool> ExistsAsync(string modelName, CancellationToken ct);

    /// <summary>
    ///     Resolves the memory-footprint inputs for the installed model <paramref name="modelName" /> — the registry
    ///     quant label + on-disk file size + a single tolerant GGUF header read (param/block/head/embedding/context).
    ///     Returns <see langword="null" /> when the model is not installed (no registry entry or its file is gone). Used
    ///     by the capacity footprint provider so it never re-parses GGUF headers or re-reads the quant from the file
    ///     name. The header read is cached per <c>(path, size, downloaded-at)</c>; a re-download invalidates the entry.
    /// </summary>
    Task<GgufModelFootprintFacts?> ResolveModelFootprintFactsAsync(string modelName, CancellationToken ct);
}
