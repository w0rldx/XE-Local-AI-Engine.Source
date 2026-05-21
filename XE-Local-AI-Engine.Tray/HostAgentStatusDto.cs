namespace XE_Local_AI_Engine.Tray;

using System.Text.Json.Serialization;

internal sealed record HostAgentStatusDto(
    [property: JsonPropertyName("state")]
    string? State,
    [property: JsonPropertyName("desiredState")]
    string? DesiredState,
    [property: JsonPropertyName("webUiUrl")]
    string? WebUiUrl,
    [property: JsonPropertyName("ollama")]
    string? Ollama,
    [property: JsonPropertyName("webServer")]
    string? WebServer);
