namespace XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;

using System.Text.Json.Serialization;

/// <summary>
///     Transport DTO for runtime component status data.
/// </summary>
public sealed record RuntimeComponentStatusDto
{
    [JsonRequired]
    public required string Name { get; init; }

    [JsonRequired]
    public required ContainerDesiredState DesiredState { get; init; }

    [JsonRequired]
    public required ContainerHealth Health { get; init; }

    [JsonRequired]
    public required string ImageReference { get; init; }

    [JsonRequired]
    public required bool DigestVerified { get; init; }

    [JsonRequired]
    public required DateTimeOffset ObservedAt { get; init; }

    [JsonRequired]
    public required IReadOnlyList<string> Diagnostics { get; init; }
}
