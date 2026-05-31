namespace XE_Local_AI_Engine.HostAgent.Abstractions.Manifest;

using System.Text.Json.Serialization;

/// <summary>
///     Value object carrying model manifest data.
/// </summary>
public sealed record ModelManifest
{
    [JsonRequired]
    public required string BootstrapModel { get; init; }

    [JsonRequired]
    public required string DefaultChatModel { get; init; }
}
