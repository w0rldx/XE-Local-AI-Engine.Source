namespace XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     A resolved, hash-verified llama.cpp prebuilt binary on disk.
/// </summary>
/// <param name="ServerExecutablePath">Absolute path to the resolved <c>llama-server</c> executable.</param>
/// <param name="Version">The llama.cpp release tag the binary was built from (for example <c>b9692</c>).</param>
/// <param name="Variant">The acceleration variant of the resolved binary.</param>
/// <param name="IsPinnedFallback">
///     <see langword="true" /> when this is the recommended-pinned binary; <see langword="false" /> when it is a
///     user-selected upgrade. The pinned fallback is never deleted by an upgrade.
/// </param>
public sealed record LlamaBinary(
    string ServerExecutablePath,
    string Version,
    GpuVariant Variant,
    bool IsPinnedFallback);
