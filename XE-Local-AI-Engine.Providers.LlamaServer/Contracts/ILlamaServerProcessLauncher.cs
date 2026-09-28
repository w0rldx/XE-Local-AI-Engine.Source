namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

using XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;

/// <summary>
///     Thin process-launch seam isolating the OS-specific <c>Process.Start</c> and tree-kill mechanics from the
///     supervisor's lifecycle, eviction and single-flight logic.
/// </summary>
/// <remarks>
///     Faked in unit tests so the supervisor's logic is exercised with no real child processes;
///     <see cref="LlamaServerProcessLauncher" /> starts a real <c>llama-server</c> contained by a Windows Job Object or
///     a Linux process group.
/// </remarks>
internal interface ILlamaServerProcessLauncher
{
    /// <summary>
    ///     Starts the process described by <paramref name="spec" />, contained so that a later
    ///     <see cref="IProcessTreeHandle.TreeKill" /> (or handle dispose) leaves no orphaned child.
    /// </summary>
    /// <exception cref="LlamaRuntimeException">The process could not be started — message is sanitized.</exception>
    IProcessTreeHandle Launch(LlamaServerLaunchSpec spec);
}
