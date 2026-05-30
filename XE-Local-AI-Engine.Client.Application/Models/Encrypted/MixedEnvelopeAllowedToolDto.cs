namespace XE_Local_AI_Engine.Client.Models.Encrypted;

using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Models.Enums;

public sealed record MixedEnvelopeAllowedToolDto
{
    [JsonPropertyOrder(1)]
    public required string Name { get; init; }

    [JsonPropertyOrder(2)]
    public string? Description { get; init; }

    [JsonPropertyOrder(3)]
    public string? Schema { get; init; }

    /// <summary>
    ///     Execution location of the offered tool. Carried on the wire so a <see cref="ToolLocation.ClientLocal" /> tool
    ///     routes to its local registry placeholder instead of an API-side bridge. Serialized as the underlying int (no
    ///     string-enum converter) and covered by the config hash, so it stays byte-identical to the server payload.
    /// </summary>
    [JsonPropertyOrder(4)]
    public ToolLocation Location { get; init; }

    /// <summary>
    ///     Whether the tool must be gated behind an approval round-trip before it executes. Carried so the envelope is
    ///     self-describing; the ClientLocal registry still re-derives approval from the handler.
    /// </summary>
    [JsonPropertyOrder(5)]
    public bool RequiresApproval { get; init; }
}
