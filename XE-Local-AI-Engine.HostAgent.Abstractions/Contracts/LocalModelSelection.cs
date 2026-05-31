namespace XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;

using System.Text.Json.Serialization;

/// <summary>
///     Value object carrying local model selection data.
/// </summary>
public sealed record LocalModelSelection
{
    [JsonRequired]
    public required string ModelName { get; init; }

    [JsonRequired]
    public required string ProviderName { get; init; }
}
