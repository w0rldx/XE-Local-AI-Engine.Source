namespace XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;

using System.Text.Json.Serialization;

/// <summary>
///     Enumerates supported runtime lifecycle values.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RuntimeLifecycle>))]
public enum RuntimeLifecycle
{
    [JsonStringEnumMemberName("managed")]
    Managed,

    [JsonStringEnumMemberName("native")]
    Native,

    [JsonStringEnumMemberName("external")]
    External
}
