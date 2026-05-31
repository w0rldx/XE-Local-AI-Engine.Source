namespace XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;

using System.Text.Json.Serialization;

/// <summary>
///     Enumerates supported host agent desired state values.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<HostAgentDesiredState>))]
public enum HostAgentDesiredState
{
    [JsonStringEnumMemberName("running")]
    Running,

    [JsonStringEnumMemberName("stopped")]
    Stopped
}
