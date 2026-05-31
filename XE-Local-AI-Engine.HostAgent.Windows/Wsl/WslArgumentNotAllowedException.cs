namespace XE_Local_AI_Engine.HostAgent.Windows.Wsl;

/// <summary>
///     Exception raised for wsl argument not allowed failures.
/// </summary>
public sealed class WslArgumentNotAllowedException : InvalidOperationException
{
    public WslArgumentNotAllowedException(string arguments)
        : base($"The wsl.exe argument form is not allowlisted: {arguments}")
    {
    }
}
