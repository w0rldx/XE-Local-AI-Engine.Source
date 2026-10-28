namespace XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;

/// <summary>
///     Runs one hardened child process for a managed source build. The seam exists so every step of the build — git, cmake,
///     the smoke test and the post-build gates — is drivable by a fake in tests without compiling
///     anything native.
/// </summary>
public interface ISourceCommandRunner
{
    Task<SourceCommandResult> RunAsync(string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Action<string> onOutput,
        TimeSpan timeout,
        bool captureOutput,
        CancellationToken ct);
}
