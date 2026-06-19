namespace XE_Local_AI_Engine.Providers.Abstractions.Contracts;

using System.Text.Json.Serialization;

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
