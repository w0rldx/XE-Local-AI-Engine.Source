namespace XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;

using System.Text.Json.Serialization;

public sealed record HostAgentStatusDto
{
    [JsonRequired]
    public required HostAgentState State { get; init; }

    [JsonRequired]
    public required HostAgentDesiredState DesiredState { get; init; }

    [JsonRequired]
    public required RuntimeLifecycle RuntimeLifecycle { get; init; }

    [JsonRequired]
    public required bool BootstrapModelReady { get; init; }

    [JsonRequired]
    public required string WebUiUrl { get; init; }

    [JsonRequired]
    public required DateTimeOffset ObservedAt { get; init; }

    [JsonRequired]
    public required IReadOnlyList<RuntimeComponentStatusDto> Components { get; init; }

    [JsonRequired]
    public required IReadOnlyList<string> Diagnostics { get; init; }
}
