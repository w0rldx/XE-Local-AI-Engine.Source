namespace XE_Local_AI_Engine.HostAgent.Abstractions.Manifest;

using System.Text.Json.Serialization;

/// <summary>
///     Value object carrying volume mount manifest data.
/// </summary>
public sealed record VolumeMountManifest
{
    [JsonRequired]
    public required string Source { get; init; }

    [JsonRequired]
    public required string Target { get; init; }

    [JsonRequired]
    public required bool ReadOnly { get; init; }
}
