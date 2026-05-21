namespace XE_Local_AI_Engine.HostAgent.Abstractions.Manifest;

using System.Text.Json.Serialization;

public sealed record RuntimeLimitsManifest
{
    [JsonRequired]
    public required int MaxRuntimeDiskGb { get; init; }

    [JsonRequired]
    public required int StopDrainTimeoutSeconds { get; init; }
}
