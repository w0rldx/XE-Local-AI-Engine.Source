namespace XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;

/// <summary>
///     A handle to one launched runtime child (<c>llama-server</c>, <c>sd-server</c>, <c>whisper-server</c>). Disposing
///     the handle tree-kills the process and releases all OS resources (Job Object handle on Windows; the process group
///     is signalled on Linux).
/// </summary>
/// <remarks>
///     The seam each provider's launcher returns and its supervisor holds; unit tests fake it so the supervisors run
///     with no real child process.
/// </remarks>
public interface IProcessTreeHandle : IDisposable
{
    /// <summary>The OS process id of the launched server (diagnostics only).</summary>
    int ProcessId { get; }

    /// <summary><see langword="true" /> once the process has exited (crash or clean stop).</summary>
    bool HasExited { get; }

    /// <summary>The process exit code once it has exited; <see langword="null" /> while running or when the OS cannot report it.</summary>
    int? ExitCode { get; }

    /// <summary>The last few sanitized lines the child wrote to <c>stderr</c>, or <see langword="null" /> when it wrote none.</summary>
    /// <remarks>
    ///     Only launchers that attach a <see cref="ProcessStderrTail" /> fill it; absolute paths are already reduced to file
    ///     names.
    /// </remarks>
    string? StderrTail { get; }

    /// <summary>
    ///     Waits up to <paramref name="timeout" /> for the contained process to exit. Returns <see langword="false" />
    ///     only when the bound elapses; caller cancellation is propagated.
    /// </summary>
    Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct);

    /// <summary>
    ///     Tree-kills the process and every descendant (Windows: close the Job Object; Linux: <c>kill(-pgid)</c>).
    ///     Idempotent and safe to call after the process has already exited.
    /// </summary>
    void TreeKill();
}
