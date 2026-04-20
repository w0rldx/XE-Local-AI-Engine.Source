namespace XE_Local_AI_Engine.Client.Models.Encrypted;

using System.Text.Json.Serialization;

public sealed record MixedEnvelopeAllowedToolDto
{
    [JsonPropertyOrder(1)]
    public required string Name { get; init; }

    [JsonPropertyOrder(2)]
    public string? Description { get; init; }

    [JsonPropertyOrder(3)]
    public string? Schema { get; init; }
}
