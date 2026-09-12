namespace XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>
///     Thin process-launch seam isolating the OS-specific <c>Process.Start</c> and tree-kill mechanics from the
///     supervisor's lifecycle logic. Faked in unit tests so the supervisor is exercised with no real child process; the
///     production implementation starts a real <c>whisper-server</c> contained by a Windows Job Object or a Linux
///     process group.
/// </summary>
/// <remarks>
///     Internal on purpose, together with <see cref="IWhisperServerProcessHandle" /> and
///     <c>WhisperServerLaunchSpec</c>: making any of them public would move it under
///     <c>PlacementConventionTests</c>' public-interface rule. They are visible to the test project through
///     <c>InternalsVisibleTo</c>. Do not widen them for consistency with the public contracts beside them.
/// </remarks>
internal interface IWhisperServerProcessLauncher
{
    /// <summary>
    ///     Starts the process described by <paramref name="spec" />, contained so a later
    ///     <see cref="IWhisperServerProcessHandle.TreeKill" /> (or handle dispose) leaves no orphan behind.
    /// </summary>
    /// <exception cref="WhisperRuntimeException">The process could not be started; the message is sanitized.</exception>
    IWhisperServerProcessHandle Launch(WhisperServerLaunchSpec spec);
}

/// <summary>
///     A handle to one launched <c>whisper-server</c> child. Disposing it tree-kills the process and releases the OS
///     resources holding the containment (the Job Object on Windows; the process group is signalled on Linux).
/// </summary>
internal interface IWhisperServerProcessHandle : IDisposable
{
    /// <summary>The OS process id of the launched server (diagnostics only).</summary>
    int ProcessId { get; }

    /// <summary><see langword="true" /> once the process has exited, cleanly or otherwise.</summary>
    bool HasExited { get; }

    /// <summary>
    ///     Tree-kills the process and every descendant (Windows: close the Job Object; Linux: <c>kill(-pgid)</c>).
    ///     Idempotent and safe to call after the process has already exited.
    /// </summary>
    void TreeKill();
}
