namespace XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>
///     Runs one hardened child process for the source build. The seam exists so every step of the build — git, cmake,
///     the smoke test and the <c>readelf</c> relocation gate — is drivable by a fake in tests without compiling
///     anything native.
/// </summary>
internal interface IWhisperSourceCommandRunner
{
    Task<WhisperSourceCommandResult> RunAsync(string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Action<string> onOutput,
        TimeSpan timeout,
        bool captureOutput,
        CancellationToken ct);
}

/// <summary>Exit code and, when the caller asked to capture it, the bounded output of one build command.</summary>
internal sealed record WhisperSourceCommandResult(int ExitCode, string StandardOutput, string StandardError);
