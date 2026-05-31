namespace XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;

using System.Text.Json.Serialization;

/// <summary>
///     Transport DTO for container action request data.
/// </summary>
public sealed record ContainerActionRequestDto
{
    [JsonRequired]
    public required string ContainerName { get; init; }

    [JsonRequired]
    public required TimeSpan DrainTimeout { get; init; }
}
