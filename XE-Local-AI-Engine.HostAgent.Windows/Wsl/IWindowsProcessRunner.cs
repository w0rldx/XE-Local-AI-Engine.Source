namespace XE_Local_AI_Engine.HostAgent.Windows.Wsl;

/// <summary>
///     Abstraction for windows process runner behavior.
/// </summary>
public interface IWindowsProcessRunner
{
    Task<WindowsProcessResult> RunAsync(WindowsProcessRequest request, CancellationToken cancellationToken = default);
}
