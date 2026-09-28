namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>The host hardware facts a benchmark run was launched on.</summary>
/// <remarks>
///     <paramref name="OsDescription" /> is deliberately NOT named <c>os</c>: the receipt already carries a bounded
///     <c>os</c> token, and two same-named fields of different shapes read as a contradiction in a field-by-field diff.
/// </remarks>
/// <param name="OsDescription">The host OS as the runtime describes it ("Linux 6.18.0-example-WSL2 ...").</param>
/// <param name="DeviceAuditBackend">What the selected runtime actually enumerated: <c>cuda|vulkan|cpu|unknown</c>.</param>
public sealed record BenchmarkHardwareFactsV1(
    string OsDescription,
    string Arch,
    string? CpuModel,
    int LogicalCores,
    long RamBytes,
    IReadOnlyList<BenchmarkGpuFactsV1> Gpus,
    string DeviceAuditBackend);
