namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

using XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;

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
    ///     <see cref="IProcessTreeHandle.TreeKill" /> (or handle dispose) leaves no orphaned child.
    /// </summary>
    /// <exception cref="StableDiffusionRuntimeException">The process could not be started — the message is sanitized.</exception>
    IProcessTreeHandle Launch(ImageServerLaunchSpec spec);
}
