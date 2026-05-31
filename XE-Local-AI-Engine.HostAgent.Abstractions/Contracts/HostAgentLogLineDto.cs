namespace XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;

using System.Text.Json.Serialization;

/// <summary>
///     Transport DTO for host agent log line data.
/// </summary>
public sealed record HostAgentLogLineDto
{
    [JsonRequired]
    public required string ContainerName { get; init; }

    [JsonRequired]
    public required string Stream { get; init; }

    [JsonRequired]
    public required string Line { get; init; }

    [JsonRequired]
    public required DateTimeOffset ObservedAt { get; init; }
}
