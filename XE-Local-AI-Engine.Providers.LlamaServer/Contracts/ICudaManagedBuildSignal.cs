namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     A tiny process-wide cached flag: a managed source-built CUDA runtime is recorded and was present at the last
///     check.
/// </summary>
/// <remarks>
///     It exists so <see cref="IGpuVariantSelector" /> can decide CUDA versus Vulkan on a Linux NVIDIA box WITHOUT a
///     per-call <see cref="IInstalledRuntimeStore" /> read on the hot selection path, so it is deliberately cheap and
///     slightly optimistic: it does NOT prove the binary is on disk and hash-valid right now. <c>EnsureBinaryAsync</c>
///     enforces presence, permissions and SHA validity authoritatively on every serve and clears this flag when the
///     recorded build is missing or invalid, so a stale "true" self-heals. Implementations must be thread-safe.
/// </remarks>
public interface ICudaManagedBuildSignal : IActiveSourceBuildSignal
{
    /// <summary><see langword="true" /> when a managed CUDA source build was recorded and present at the last check.</summary>
    bool IsAvailable { get; }

    /// <summary>Marks a managed CUDA source build as available; called on adopt and at startup seeding.</summary>
    void MarkAvailable();
}
