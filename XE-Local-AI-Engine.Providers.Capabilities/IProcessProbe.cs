namespace XE_Local_AI_Engine.Providers.Capabilities;

/// <summary>
///     Minimal process-shell seam owned by this project (deliberately NOT the now-removed
///     <c>HostAgent.Linux.Capabilities.IProcessRunner</c>). Runs a command and returns its exit code plus stdout so
///     <see cref="HardwareProfiler" /> is unit-testable with canned probe output and no real GPU.
/// </summary>
internal interface IProcessProbe
{
    /// <summary>
    ///     Runs <paramref name="fileName" /> with <paramref name="arguments" /> and returns the result, or
    ///     <see langword="null" /> when the tool is missing / not on PATH / failed to start (never throws for those).
    /// </summary>
    Task<ProcessProbeResult?> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct);
}

/// <summary>Exit code + captured stdout from a probe command.</summary>
internal sealed record ProcessProbeResult(int ExitCode, string StandardOutput);
