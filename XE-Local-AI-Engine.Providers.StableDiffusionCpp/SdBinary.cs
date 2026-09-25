namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp;

/// <summary>
///     A resolved, hash-verified stable-diffusion.cpp <c>sd-server</c> prebuilt binary on disk.
/// </summary>
public sealed class SdBinary
{
    /// <summary>Absolute path to the resolved <c>sd-server</c> executable.</summary>
    public required string ServerExecutablePath { get; init; }

    /// <summary>The stable-diffusion.cpp rolling release tag the binary was built from (for example <c>master-913-b167b94</c>).</summary>
    public required string Version { get; init; }

    /// <summary>The acceleration backend of the resolved binary.</summary>
    public required SdGpuBackend Backend { get; init; }

    /// <summary>
    ///     <see langword="true" /> when this is the recommended-pinned binary; <see langword="false" /> when it is an
    ///     operator bring-your-own override.
    /// </summary>
    public required bool IsPinnedFallback { get; init; }
}
