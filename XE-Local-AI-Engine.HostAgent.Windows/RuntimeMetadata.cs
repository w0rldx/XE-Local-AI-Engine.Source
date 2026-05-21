namespace XE_Local_AI_Engine.HostAgent.Windows;

public sealed record RuntimeMetadata(
    int Pid,
    int AdminPort,
    string ExePath,
    string ExeSha256,
    DateTimeOffset StartedAt,
    string TokenGenerationId,
    string SessionId);
