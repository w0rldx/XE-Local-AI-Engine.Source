namespace XE_Local_AI_Engine.HostAgent.Windows.Wsl;

public interface IWindowsProcessRunner
{
    Task<WindowsProcessResult> RunAsync(WindowsProcessRequest request, CancellationToken cancellationToken = default);
}
