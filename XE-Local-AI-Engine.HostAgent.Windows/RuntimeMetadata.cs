namespace XE_Local_AI_Engine.HostAgent.Windows;

/// <summary>
///     Value object carrying runtime metadata data.
/// </summary>
public sealed record RuntimeMetadata(
    int Pid,
    int AdminPort,
    string ExePath,
    string ExeSha256,
    DateTimeOffset StartedAt,
    string TokenGenerationId,
    string SessionId);
