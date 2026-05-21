namespace XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;

using System.Text.Json.Serialization;

public sealed record ModelProviderHealth
{
    [JsonRequired]
    public required string ProviderName { get; init; }

    [JsonRequired]
    public required bool IsHealthy { get; init; }

    [JsonRequired]
    public required DateTimeOffset ObservedAt { get; init; }

    [JsonRequired]
    public required IReadOnlyList<string> Diagnostics { get; init; }
}
