namespace XE_Local_AI_Engine.HostAgent.Windows.Wsl;

/// <summary>
///     Request DTO for windows process operations.
/// </summary>
public sealed record WindowsProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? StandardInput,
    TimeSpan Timeout);
