namespace XE_Local_AI_Engine.Providers.Capabilities.Contracts;

/// <summary>
///     Minimal process-shell seam owned by this project. Runs a command and returns its exit code plus stdout so
///     <see cref="HardwareProfiler" /> is unit-testable with canned probe output and no real GPU.
/// </summary>
internal interface IProcessProbe
{
    /// <summary>
    ///     Runs <paramref name="fileName" /> with <paramref name="arguments" /> under a wall-clock
    ///     <paramref name="timeout" />, returning the result or <see langword="null" /> for a tool that is missing, not
    ///     on PATH or failed to start.
    /// </summary>
    /// <remarks>
    ///     Never throws for those cases. On overrun the process tree is killed and a
    ///     <see cref="ProcessProbeResult.TimedOut" /> result is returned, never a fault; genuine caller cancellation
    ///     also tree-kills and surfaces as <see cref="OperationCanceledException" />. A non-positive timeout means no
    ///     internal deadline, the caller's token still bounding the call.
    /// </remarks>
    Task<ProcessProbeResult?> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
///     Exit code + captured stdout from a probe command. <see cref="TimedOut" /> is <see langword="true" /> only when the
///     probe was killed for exceeding its wall-clock deadline (the caller degrades rather than trusting a partial read);
///     the exit code is then non-zero and the stdout empty.
/// </summary>
internal sealed class ProcessProbeResult
{
    public required int ExitCode { get; init; }

    public required string StandardOutput { get; init; }

    public bool TimedOut { get; init; }
}
