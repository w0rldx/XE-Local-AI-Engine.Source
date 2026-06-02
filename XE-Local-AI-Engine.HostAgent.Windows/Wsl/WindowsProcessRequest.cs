namespace XE_Local_AI_Engine.HostAgent.Windows.Wsl;

public sealed record WindowsProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? StandardInput,
    TimeSpan Timeout);
