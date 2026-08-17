namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Thin process-launch seam isolating the OS-specific <c>Process.Start</c> + tree-kill mechanics from the
///     supervisor's lifecycle/eviction/single-flight logic. Faked in unit tests so the supervisor's logic is exercised
///     with no real child processes; the production implementation (<see cref="LlamaServerProcessLauncher" />) starts a
///     real <c>llama-server</c> contained by a Windows Job Object or a Linux process group.
/// </summary>
internal interface ILlamaServerProcessLauncher
{
    /// <summary>
    ///     Starts the process described by <paramref name="spec" />, contained so that a later
    ///     <see cref="ILlamaServerProcessHandle.TreeKill" /> (or handle dispose) leaves no orphaned child.
    /// </summary>
    /// <exception cref="LlamaRuntimeException">The process could not be started — message is sanitized.</exception>
    ILlamaServerProcessHandle Launch(LlamaServerLaunchSpec spec);
}

/// <summary>
///     A handle to one launched <c>llama-server</c> child. Disposing the handle tree-kills the process and releases all
///     OS resources (Job Object handle on Windows; the process group is signalled on Linux).
/// </summary>
internal interface ILlamaServerProcessHandle : IDisposable
{
    /// <summary>The OS process id of the launched server (diagnostics only).</summary>
    int ProcessId { get; }

    /// <summary><see langword="true" /> once the process has exited (crash or clean stop).</summary>
    bool HasExited { get; }

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
