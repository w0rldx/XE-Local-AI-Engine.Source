namespace XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;

using System.Text.Json.Serialization;

public sealed record ContainerActionRequestDto
{
    [JsonRequired]
    public required string ContainerName { get; init; }

    [JsonRequired]
    public required TimeSpan DrainTimeout { get; init; }
}
