namespace XE_Local_AI_Engine.Tray;

using System.Text.Json.Serialization;

internal sealed record HostAgentRuntimeMetadata(
    [property: JsonPropertyName("pid")]
    int Pid,
    [property: JsonPropertyName("adminPort")]
    int AdminPort,
    [property: JsonPropertyName("exePath")]
    string? ExePath,
    [property: JsonPropertyName("exeSha256")]
    string? ExeSha256,
    [property: JsonPropertyName("sessionId")]
    string? SessionId);
