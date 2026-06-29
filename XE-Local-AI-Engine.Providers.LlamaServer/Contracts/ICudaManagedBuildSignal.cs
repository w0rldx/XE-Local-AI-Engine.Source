namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     A tiny process-wide cached flag: "a managed source-built CUDA runtime is recorded and was present at the last
///     check." It exists so <see cref="IGpuVariantSelector" /> can decide CUDA-vs-Vulkan on a Linux NVIDIA box WITHOUT a
///     per-call <see cref="IInstalledRuntimeStore" /> read on the hot selection path. It is set when a build is adopted,
///     cleared when the build is removed or found invalid at serve time, and seeded once at startup from the store.
/// </summary>
/// <remarks>
///     The flag is intentionally cheap and slightly optimistic: it does NOT prove the binary is on disk + hash-valid right
///     now. Disk-presence/perms/SHA validity is enforced authoritatively by <c>EnsureBinaryAsync</c> at every serve; when
///     that finds the recorded build missing or invalid it clears this flag and falls through, so a stale "true" self-heals
///     on the next serve. Implementations must be thread-safe.
/// </remarks>
public interface ICudaManagedBuildSignal
{
    /// <summary><see langword="true" /> when a managed CUDA source build was recorded and present at the last check.</summary>
    bool IsAvailable { get; }

    /// <summary>Marks a managed CUDA source build as available (called on adopt and at startup seeding).</summary>
    void MarkAvailable();

    /// <summary>Clears the signal (called on remove, and when a serve finds the recorded build missing/invalid).</summary>
    void Clear();
}
