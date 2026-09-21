namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     A free-VRAM observation captured after an existing process for the profiled model and role has been evicted,
///     but before the transient profiling server is spawned.
/// </summary>
/// <remarks>
///     The global-free and process-budget values intentionally remain separate, because they have different semantics
///     under WDDM.
/// </remarks>
public sealed record LlamaServerProfilingVramSnapshot
{
    /// <summary>Authoritative machine-global free VRAM, or <see langword="null" /> when unavailable.</summary>
    public required long? GlobalFreeBytes { get; init; }

    /// <summary>
    ///     The llama.cpp process residency budget reported by the selected backend, or <see langword="null" /> when
    ///     unavailable.
    /// </summary>
    public required long? ProcessBudgetBytes { get; init; }
}
