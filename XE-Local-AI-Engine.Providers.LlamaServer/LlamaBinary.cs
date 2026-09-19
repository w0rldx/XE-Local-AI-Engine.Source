namespace XE_Local_AI_Engine.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     A resolved, hash-verified llama.cpp prebuilt binary on disk.
/// </summary>
public sealed class LlamaBinary
{
    /// <summary>Absolute path to the resolved <c>llama-server</c> executable.</summary>
    public required string ServerExecutablePath { get; init; }

    /// <summary>The llama.cpp release tag the binary was built from (for example <c>b10201</c>).</summary>
    public required string Version { get; init; }

    /// <summary>The acceleration variant of the resolved binary.</summary>
    public required GpuVariant Variant { get; init; }

    /// <summary>
    ///     <see langword="true" /> when this is the recommended-pinned binary; <see langword="false" /> when it is a
    ///     user-selected upgrade. The pinned fallback is never deleted by an upgrade.
    /// </summary>
    public required bool IsPinnedFallback { get; init; }

    /// <summary>
    ///     The <c>llama-quantize</c> helper beside this binary, or <see langword="null" /> when this runtime shipped
    ///     none. Upstream prebuilt archives carry no quantizer today, so only a source build resolves one — that is the
    ///     recorded presence check, evaluated on read rather than stored so it can never go stale against the tree.
    ///     A null here means "training exports cannot quantize with this runtime", never that the runtime is unusable.
    /// </summary>
    public string? QuantizerExecutablePath => LlamaCppToolBinaries.TryResolveQuantizerBesideServer(ServerExecutablePath);

    /// <summary>
    ///     The <c>llama-perplexity</c> helper beside this binary, or <see langword="null" /> when this runtime shipped
    ///     none. Prebuilt archives carry it; a source build only does so from the commit that widened the cmake target
    ///     lists onward. Evaluated on read rather than stored, exactly like <see cref="QuantizerExecutablePath" />.
    ///     A null here means "benchmark fidelity cannot be measured with this runtime", never that the runtime is
    ///     unusable.
    /// </summary>
    public string? PerplexityExecutablePath => LlamaCppToolBinaries.TryResolvePerplexityBesideServer(ServerExecutablePath);
}
