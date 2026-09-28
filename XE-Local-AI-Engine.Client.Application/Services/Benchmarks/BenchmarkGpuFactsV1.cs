namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>The host facts a benchmark run was launched on. Aggregates only — no hostnames, serials or paths.</summary>
/// <remarks>
///     <paramref name="DriverVersion" /> is always <see langword="null" /> today: the device audit enumerates through
///     the runtime itself, which reports no driver version. Present so a later probe can fill it without a schema
///     change.
/// </remarks>
/// <param name="DriverVersion">The GPU driver version, when a probe can report one.</param>
public sealed record BenchmarkGpuFactsV1(string Name, long? TotalBytes, string? DriverVersion);
