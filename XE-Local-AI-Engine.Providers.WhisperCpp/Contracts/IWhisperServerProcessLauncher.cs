namespace XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

using XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;

/// <summary>
///     Thin process-launch seam isolating the OS-specific <c>Process.Start</c> and tree-kill mechanics from the
///     supervisor's lifecycle logic.
/// </summary>
/// <remarks>
///     Faked in unit tests so the supervisor is exercised with no real child process; the production implementation starts a real
///     <c>whisper-server</c> contained by a Windows Job Object or a Linux process group. Internal on purpose, together with
///     <c>WhisperServerLaunchSpec</c>: making either public would move it under <c>PlacementConventionTests</c>' public-interface
///     rule, and they reach the test project through <c>InternalsVisibleTo</c>. Do not widen them for consistency with the public
///     contracts beside them. The handle it returns is the shared <see cref="IProcessTreeHandle" />.
/// </remarks>
internal interface IWhisperServerProcessLauncher
{
    /// <summary>
    ///     Starts the process described by <paramref name="spec" />, contained so a later
    ///     <see cref="IProcessTreeHandle.TreeKill" /> (or handle dispose) leaves no orphan behind.
    /// </summary>
    /// <exception cref="WhisperRuntimeException">The process could not be started; the message is sanitized.</exception>
    IProcessTreeHandle Launch(WhisperServerLaunchSpec spec);
}
