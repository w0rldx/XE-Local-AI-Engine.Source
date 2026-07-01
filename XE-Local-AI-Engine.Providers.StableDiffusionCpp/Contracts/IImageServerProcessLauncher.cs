namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

using XE_Local_AI_Engine.Providers.StableDiffusionCpp;

/// <summary>
///     Thin process-launch seam isolating the OS-specific <c>Process.Start</c> + tree-kill mechanics from the
///     supervisor's lifecycle logic. Faked in unit tests so the supervisor is exercised with no real child processes;
///     the production implementation starts a real <c>sd-server</c> contained by a Windows Job Object or a Linux process
///     group. Mirrors <c>ILlamaServerProcessLauncher</c>.
/// </summary>
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
///     A handle to one launched <c>sd-server</c> child. Disposing the handle tree-kills the process and releases all OS
///     resources (Job Object handle on Windows; the process group is signalled on Linux).
/// </summary>
internal interface IImageServerProcessHandle : IDisposable
{
    /// <summary>The OS process id of the launched server (diagnostics only).</summary>
    int ProcessId { get; }

    /// <summary><see langword="true" /> once the process has exited (crash or clean stop).</summary>
    bool HasExited { get; }

    /// <summary>
    ///     Tree-kills the process and every descendant (Windows: close the Job Object; Linux: <c>kill(-pgid)</c>).
    ///     Idempotent and safe to call after the process has already exited.
    /// </summary>
    void TreeKill();
}
