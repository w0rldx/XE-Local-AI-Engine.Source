namespace XE_Local_AI_Engine.HostAgent.Windows.Wsl;

/// <summary>
///     Value object carrying windows process result data.
/// </summary>
public sealed record WindowsProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;
}
