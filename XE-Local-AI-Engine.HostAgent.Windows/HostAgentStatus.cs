namespace XE_Local_AI_Engine.HostAgent.Windows;

public sealed record HostAgentStatus(
    string State,
    string DesiredState,
    string WebUiUrl,
    string Ollama,
    string WebServer,
    DateTimeOffset StartedAt);
