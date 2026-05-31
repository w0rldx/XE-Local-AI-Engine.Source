namespace XE_Local_AI_Engine.HostAgent.Windows.Wsl;

/// <summary>
///     Value object carrying wsl command data.
/// </summary>
public sealed record WslCommand(
    IReadOnlyList<string> Arguments,
    string? StandardInput = null,
    TimeSpan? Timeout = null);
