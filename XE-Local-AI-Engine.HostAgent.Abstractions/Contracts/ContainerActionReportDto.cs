namespace XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;

using System.Text.Json.Serialization;

/// <summary>
///     Transport DTO for container action report data.
/// </summary>
public sealed record ContainerActionReportDto
{
    [JsonRequired]
    public required string Action { get; init; }

    [JsonRequired]
    public required bool Succeeded { get; init; }

    [JsonRequired]
    public required DateTimeOffset StartedAt { get; init; }

    [JsonRequired]
    public required DateTimeOffset CompletedAt { get; init; }

    [JsonRequired]
    public required IReadOnlyList<RuntimeComponentStatusDto> Components { get; init; }

    [JsonRequired]
    public required IReadOnlyList<string> Diagnostics { get; init; }
}
