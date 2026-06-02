namespace XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;

using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter<HostAgentState>))]
public enum HostAgentState
{
    [JsonStringEnumMemberName("unknown")]
    Unknown,

    [JsonStringEnumMemberName("starting")]
    Starting,

    [JsonStringEnumMemberName("running")]
    Running,

    [JsonStringEnumMemberName("degraded")]
    Degraded,

    [JsonStringEnumMemberName("stopping")]
    Stopping,

    [JsonStringEnumMemberName("stopped")]
    Stopped,

    [JsonStringEnumMemberName("failed")]
    Failed
}
