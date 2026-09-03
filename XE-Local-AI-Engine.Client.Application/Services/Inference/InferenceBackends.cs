namespace XE_Local_AI_Engine.Client.Services.Inference;

using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     The canonical lowercase backend tokens persisted in an inference profile's <c>backend</c> column and the
///     <see cref="GpuVariant" />-to-token mapping the resolver uses to key a profile. Kept in one place so the resolver
///     (which writes the key) and the invalidation evaluator (which reads <c>profile.Backend</c>) agree byte-for-byte.
/// </summary>
internal static class InferenceBackends
{
    /// <summary>NVIDIA CUDA backend token (Windows prebuilt).</summary>
    public const string Cuda = "cuda";

    /// <summary>Vulkan backend token (AMD/Intel, and the Linux NVIDIA fallback).</summary>
    public const string Vulkan = "vulkan";

    /// <summary>CPU-only backend token.</summary>
    public const string Cpu = "cpu";

    /// <summary>Maps the acceleration <paramref name="variant" /> to its persisted lowercase backend token.</summary>
    public static string FromVariant(GpuVariant variant)
    {
        return variant switch
        {
            GpuVariant.Cuda => Cuda,
            GpuVariant.Vulkan => Vulkan,
            _ => Cpu
        };
    }

    /// <summary>
    ///     The inverse of <see cref="FromVariant" />, for the accelerated tokens only. Returns <see langword="false" /> for
    ///     <see cref="Cpu" /> and for any unrecognized token: neither carries a GPU placement decision, so a caller that
    ///     needs the variant in order to re-derive one has nothing to re-derive.
    /// </summary>
    public static bool TryGetGpuVariant(string? backend, out GpuVariant variant)
    {
        if (string.Equals(backend, Cuda, StringComparison.OrdinalIgnoreCase))
        {
            variant = GpuVariant.Cuda;
            return true;
        }

        if (string.Equals(backend, Vulkan, StringComparison.OrdinalIgnoreCase))
        {
            variant = GpuVariant.Vulkan;
            return true;
        }

        variant = GpuVariant.Cpu;
        return false;
    }
}
