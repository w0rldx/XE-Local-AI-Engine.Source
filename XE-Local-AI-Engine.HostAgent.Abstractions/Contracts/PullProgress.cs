namespace XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;

using System.Text.Json.Serialization;

/// <summary>
///     Value object carrying pull progress data.
/// </summary>
public sealed record PullProgress
{
    [JsonRequired]
    public required string ModelName { get; init; }

    [JsonRequired]
    public required string Status { get; init; }

    [JsonRequired]
    public required long? TotalBytes { get; init; }

    [JsonRequired]
    public required long? CompletedBytes { get; init; }
}
