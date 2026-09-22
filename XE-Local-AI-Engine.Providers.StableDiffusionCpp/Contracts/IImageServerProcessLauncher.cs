namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>
///     Thin process-launch seam isolating the OS-specific <c>Process.Start</c> + tree-kill mechanics from the
///     supervisor's lifecycle logic. Mirrors <c>ILlamaServerProcessLauncher</c>.
/// </summary>
/// <remarks>
///     Faked in unit tests so the supervisor is exercised with no real child processes; the production implementation
///     starts a real <c>sd-server</c> contained by a Windows Job Object or a Linux process group.
/// </remarks>
internal interface IImageServerProcessLauncher
{
    /// <summary>
    ///     Starts the process described by <paramref name="spec" />, contained so that a later
    ///     <see cref="IImageServerProcessHandle.TreeKill" /> (or handle dispose) leaves no orphaned child.
    /// </summary>
    /// <exception cref="StableDiffusionRuntimeException">The process could not be started — the message is sanitized.</exception>
    IImageServerProcessHandle Launch(ImageServerLaunchSpec spec);
}

/// <summary>
///     A handle to one launched <c>sd-server</c> child.
/// </summary>
/// <remarks>
///     Disposing the handle tree-kills the process and releases all OS resources (Job Object handle on Windows; the
///     process group is signalled on Linux).
/// </remarks>
internal interface IImageServerProcessHandle : IDisposable
{
    /// <summary>The OS process id of the launched server (diagnostics only).</summary>
    int ProcessId { get; }

    /// <summary><see langword="true" /> once the process has exited (crash or clean stop).</summary>
    bool HasExited { get; }

    /// <summary>The exit code once the process has exited, or <see langword="null" /> while it runs or when the code is unavailable.</summary>
    /// <remarks>
    ///     Diagnostics only. An <c>sd-server</c> that dies during model load exits within a second and its code is the
    ///     one thing that distinguishes a missing GPU device from an out-of-memory kill.
    /// </remarks>
    int? ExitCode { get; }

    /// <summary>The last few sanitized lines the child wrote to <c>stderr</c>, or <see langword="null" /> when it wrote none.</summary>
    /// <remarks>Bounded by the launcher; absolute paths are already reduced to file names. See <c>ImageServerStderrTail</c>.</remarks>
    string? StderrTail { get; }

    /// <summary>
    ///     Tree-kills the process and every descendant (Windows: close the Job Object; Linux: <c>kill(-pgid)</c>).
    ///     Idempotent and safe to call after the process has already exited.
    /// </summary>
    void TreeKill();
}
