namespace XE_Local_AI_Engine.Providers.Capabilities.Contracts;

/// <summary>
///     Minimal process-shell seam owned by this project. Runs a command and returns its exit code plus stdout so
///     <see cref="HardwareProfiler" /> is unit-testable with canned probe output and no real GPU.
/// </summary>
internal interface IProcessProbe
{
    /// <summary>
    ///     Runs <paramref name="fileName" /> with <paramref name="arguments" /> under a wall-clock <paramref name="timeout" />
    ///     and returns the result, or <see langword="null" /> when the tool is missing / not on PATH / failed to start
    ///     (never throws for those). On overrun the process tree is killed and a <see cref="ProcessProbeResult.TimedOut" />
    ///     result is returned (never fatal); genuine caller cancellation via <paramref name="ct" /> also tree-kills and is
    ///     surfaced as <see cref="OperationCanceledException" />. A non-positive <paramref name="timeout" /> means no
    ///     internal deadline (the caller's token still bounds the call).
    /// </summary>
    Task<ProcessProbeResult?> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
///     Exit code + captured stdout from a probe command. <see cref="TimedOut" /> is <see langword="true" /> only when the
///     probe was killed for exceeding its wall-clock deadline (the caller degrades rather than trusting a partial read);
///     the exit code is then non-zero and the stdout empty.
/// </summary>
internal sealed record ProcessProbeResult(int ExitCode, string StandardOutput, bool TimedOut = false);
