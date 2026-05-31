namespace XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;

using System.Text.Json.Serialization;

/// <summary>
///     Enumerates supported container desired state values.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ContainerDesiredState>))]
public enum ContainerDesiredState
{
    [JsonStringEnumMemberName("running")]
    Running,

    [JsonStringEnumMemberName("stopped")]
    Stopped
}
