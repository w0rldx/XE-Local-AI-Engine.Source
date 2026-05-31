namespace XE_Local_AI_Engine.HostAgent.Windows.Wsl;

/// <summary>
///     Value object carrying wsl command result data.
/// </summary>
public sealed record WslCommandResult(
    WslCommand Command,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;
}
