namespace XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;

using System.Text.Json.Serialization;

/// <summary>
///     Transport DTO for host capabilities data.
/// </summary>
public sealed record HostCapabilitiesDto
{
    [JsonRequired]
    public required bool CpuAvailable { get; init; }

    [JsonRequired]
    public required bool NvidiaGpuInference { get; init; }

    [JsonRequired]
    public required bool GpuRuntimeConfigured { get; init; }

    [JsonRequired]
    public required string AmdGpuStatus { get; init; }

    [JsonRequired]
    public required long RuntimeDiskBytes { get; init; }

    [JsonRequired]
    public required DateTimeOffset ObservedAt { get; init; }

    [JsonRequired]
    public required IReadOnlyList<string> Diagnostics { get; init; }
}
