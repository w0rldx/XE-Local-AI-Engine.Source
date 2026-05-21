namespace XE_Local_AI_Engine.HostAgent.Abstractions.Manifest;

using System.Text.Json.Serialization;

public sealed record ContainerManifest
{
    [JsonRequired]
    public required string Name { get; init; }

    [JsonRequired]
    public required string Image { get; init; }

    [JsonRequired]
    public required string Network { get; init; }

    [JsonRequired]
    public required IReadOnlyDictionary<string, string> Environment { get; init; }

    [JsonRequired]
    public required IReadOnlyList<VolumeMountManifest> Volumes { get; init; }
}
