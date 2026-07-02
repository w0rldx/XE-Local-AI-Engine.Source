namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp;

/// <summary>
///     A resolved, hash-verified stable-diffusion.cpp <c>sd-server</c> prebuilt binary on disk.
/// </summary>
/// <param name="ServerExecutablePath">Absolute path to the resolved <c>sd-server</c> executable.</param>
/// <param name="Version">The stable-diffusion.cpp rolling release tag the binary was built from (for example <c>master-742-1a13107</c>).</param>
/// <param name="Backend">The acceleration backend of the resolved binary.</param>
/// <param name="IsPinnedFallback">
///     <see langword="true" /> when this is the recommended-pinned binary; <see langword="false" /> when it is an
///     operator bring-your-own override.
/// </param>
public sealed record SdBinary(
    string ServerExecutablePath,
    string Version,
    SdGpuBackend Backend,
    bool IsPinnedFallback);
