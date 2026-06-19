namespace XE_Local_AI_Engine.Providers.Abstractions;

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

    /// <summary>Enumerates the installed GGUF models as normalized host-agent descriptors.</summary>
    Task<IReadOnlyList<LocalModelDescriptor>> ListInstalledModelsAsync(CancellationToken ct);

    /// <summary>
    ///     Ensures the selected GGUF is present locally, downloading (resume-capable, retryable, cancellable) if
    ///     missing, and returns its local path + metadata. Reports byte/status progress via <paramref name="progress" />.
    /// </summary>
    Task<GgufModelHandle> EnsureModelAsync(GgufModelRequest request, IProgress<PullProgress>? progress, CancellationToken ct);

    /// <summary>Deletes a locally installed GGUF model (file + registry entry). Idempotent.</summary>
    Task DeleteModelAsync(string modelName, CancellationToken ct);

    /// <summary>Returns whether a verified GGUF file for <paramref name="modelName" /> is present locally.</summary>
    Task<bool> ExistsAsync(string modelName, CancellationToken ct);
}
