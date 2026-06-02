namespace XE_Local_AI_Engine.HostAgent.Windows.Wsl;

public sealed record WslCommand(
    IReadOnlyList<string> Arguments,
    string? StandardInput = null,
    TimeSpan? Timeout = null);
