namespace XE_Local_AI_Engine.Providers.Abstractions.Contracts;

using System.Text.Json.Serialization;

public sealed record LocalModelSelection
{
    [JsonRequired]
    public required string ModelName { get; init; }

    [JsonRequired]
    public required string ProviderName { get; init; }
}
