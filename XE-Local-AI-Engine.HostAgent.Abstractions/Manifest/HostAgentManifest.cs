namespace XE_Local_AI_Engine.HostAgent.Abstractions.Manifest;

using System.Text.Json.Serialization;

/// <summary>
///     Value object carrying host agent manifest data.
/// </summary>
public sealed record HostAgentManifest
{
    [JsonRequired]
    public required int SchemaVersion { get; init; }

    [JsonRequired]
    public required string RuntimeMode { get; init; }

    [JsonRequired]
    public required ModelManifest Models { get; init; }

    [JsonRequired]
    public required IReadOnlyList<ContainerManifest> Containers { get; init; }

    [JsonRequired]
    public required RuntimeLimitsManifest RuntimeLimits { get; init; }
}
