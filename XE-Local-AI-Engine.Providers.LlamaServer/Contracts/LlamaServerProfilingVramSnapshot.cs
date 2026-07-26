namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     A free-VRAM observation captured after an existing process for the profiled model/role has been evicted but before
///     the transient profiling server is spawned. The global-free and process-budget values intentionally remain
///     separate because they have different semantics under WDDM.
/// </summary>
/// <param name="GlobalFreeBytes">Authoritative machine-global free VRAM, or <see langword="null" /> when unavailable.</param>
/// <param name="ProcessBudgetBytes">
///     The llama.cpp process residency budget reported by the selected backend, or <see langword="null" /> when
///     unavailable.
/// </param>
public sealed record LlamaServerProfilingVramSnapshot(long? GlobalFreeBytes, long? ProcessBudgetBytes);
