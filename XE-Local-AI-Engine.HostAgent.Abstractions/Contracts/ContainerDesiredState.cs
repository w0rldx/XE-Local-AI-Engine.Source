namespace XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;

using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter<ContainerDesiredState>))]
public enum ContainerDesiredState
{
    [JsonStringEnumMemberName("running")]
    Running,

    [JsonStringEnumMemberName("stopped")]
    Stopped
}
