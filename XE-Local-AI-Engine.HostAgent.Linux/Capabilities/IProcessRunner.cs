namespace XE_Local_AI_Engine.HostAgent.Linux.Capabilities;

/// <summary>
///     Abstraction for process runner behavior.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}
