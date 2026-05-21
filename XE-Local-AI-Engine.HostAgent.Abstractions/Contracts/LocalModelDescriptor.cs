namespace XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;

using System.Text.Json.Serialization;

public sealed record LocalModelDescriptor
{
    [JsonRequired]
    public required string ModelName { get; init; }

    [JsonRequired]
    public required string ProviderName { get; init; }

    [JsonRequired]
    public required bool IsAvailable { get; init; }

    [JsonRequired]
    public required long? SizeBytes { get; init; }

    [JsonRequired]
    public required DateTimeOffset? ModifiedAt { get; init; }

    [JsonRequired]
    public required int? MaxContextTokens { get; init; }
}
