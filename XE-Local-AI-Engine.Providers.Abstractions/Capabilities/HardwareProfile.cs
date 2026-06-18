namespace XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>
///     A provider-neutral snapshot of the host's inference-relevant hardware: RAM, GPU VRAM, GPU vendor, CPU cores and
///     free disk on the models volume. Produced by <see cref="IHardwareProfiler" /> and consumed by the model advisor
///     to choose a memory-fit budget (VRAM when <see cref="GpuAccelAvailable" />, otherwise available RAM).
/// </summary>
/// <remarks>
///     Carries aggregates only — no machine identifiers (hostnames/serials) — so it is safe to surface to the operator
///     UI (plan §10). <see cref="VramKnown" /> gates GPU-mode: when VRAM could not be measured the advisor must degrade
///     to a CPU/RAM-only recommendation (plan §7.1 degrade rule).
/// </remarks>
public sealed record HardwareProfile
{
    /// <summary>Total physical RAM in bytes.</summary>
    public required long TotalRamBytes { get; init; }

    /// <summary>RAM available for allocation in bytes (free + reclaimable), used as the CPU-mode fit budget.</summary>
    public required long AvailableRamBytes { get; init; }

    /// <summary>Total dedicated GPU VRAM in bytes, or <see langword="null" /> when it could not be measured.</summary>
    public long? VramBytes { get; init; }

    /// <summary>
    ///     <see langword="true" /> only when <see cref="VramBytes" /> was actually measured. <see langword="false" />
    ///     forces the CPU-mode degrade rule (<see cref="GpuAccelAvailable" /> is then always <see langword="false" />).
    /// </summary>
    public required bool VramKnown { get; init; }

    /// <summary>Detected GPU vendor (NVIDIA/AMD/Intel/None/Unknown).</summary>
    public required GpuVendor GpuVendor { get; init; }

    /// <summary>
    ///     <see langword="true" /> when a usable GPU acceleration budget exists — i.e. a vendor GPU is present AND its
    ///     VRAM is known. Always <see langword="false" /> when <see cref="VramKnown" /> is <see langword="false" />.
    /// </summary>
    public required bool GpuAccelAvailable { get; init; }

    /// <summary>Logical CPU core count (<see cref="System.Environment.ProcessorCount" />).</summary>
    public required int CpuCores { get; init; }

    /// <summary>Free disk space in bytes on the volume that hosts the models/content root.</summary>
    public required long FreeDiskBytes { get; init; }
}
