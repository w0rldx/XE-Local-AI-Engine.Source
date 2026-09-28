namespace XE_Local_AI_Engine.Client.Services.Capacity;

/// <summary>
///     The node's single admission point for GPU work (ADR 0005 §2).
/// </summary>
/// <remarks>
///     Exclusive work — a training run, an evaluation run, an export — owns the whole node and admits only while nothing else
///     holds the gate at all. Shared work — benchmarks, dataset generation, image jobs — admits only while no exclusive holder
///     exists, and coexists with other shared holders, which is what those three have always done through the ordinary
///     inference path. Both decisions are taken under ONE lock, and that is the point: acquiring the gate IS the check, so
///     nothing may consult it and act on the answer afterwards — that would be a check-then-act race over the whole GPU.
/// </remarks>
public interface IGpuWorkGate
{
    /// <summary>The exclusive holder's kind, or <see langword="null" />. For UX refusals only — never gate work on it.</summary>
    GpuWorkKind? ExclusiveKind { get; }

    /// <summary>
    ///     Takes the whole node, or returns <see langword="null" /> when any holder — exclusive or shared — exists.
    ///     Disposing the handle releases it; disposing twice is a no-op.
    /// </summary>
    IDisposable? TryBeginExclusive(GpuWorkKind kind);

    /// <summary>
    ///     Takes a shared hold, or returns <see langword="null" /> while an exclusive holder owns the node. Shared
    ///     holders coexist. Disposing the handle releases it; disposing twice is a no-op.
    /// </summary>
    IDisposable? TryBeginShared(GpuWorkKind kind);
}
