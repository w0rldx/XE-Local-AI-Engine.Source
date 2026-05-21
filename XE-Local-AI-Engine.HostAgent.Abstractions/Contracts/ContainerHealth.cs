namespace XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;

using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter<ContainerHealth>))]
public enum ContainerHealth
{
    [JsonStringEnumMemberName("unknown")]
    Unknown,

    [JsonStringEnumMemberName("starting")]
    Starting,

    [JsonStringEnumMemberName("healthy")]
    Healthy,

    [JsonStringEnumMemberName("unhealthy")]
    Unhealthy,

    [JsonStringEnumMemberName("stopped")]
    Stopped
}
