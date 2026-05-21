namespace XE_Local_AI_Engine.HostAgent.Windows.Wsl;

public sealed class WslArgumentNotAllowedException : InvalidOperationException
{
    public WslArgumentNotAllowedException(string arguments)
        : base($"The wsl.exe argument form is not allowlisted: {arguments}")
    {
    }
}
