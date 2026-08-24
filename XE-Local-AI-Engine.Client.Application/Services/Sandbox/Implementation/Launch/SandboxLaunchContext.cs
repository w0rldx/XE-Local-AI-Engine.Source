namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;

/// <summary>
///     The per-COMMAND facts the isolated launch needs and the policy cannot carry, because they change from one
///     command to the next: which jail directory becomes the sandbox's writable root, and how long the transient scope
///     may live.
///     <para>
///         It is optional everywhere. A caller that supplies nothing gets the non-isolated behaviour unchanged, which
///         is what keeps AgentHome, Coder and Development Mode byte-identical to what they ran before this layer
///         existed.
///     </para>
/// </summary>
public sealed record SandboxLaunchContext
{
    /// <summary>The sandbox's jail directory. Required for an isolated launch; ignored otherwise.</summary>
    public string? JailRoot { get; init; }

    /// <summary>
    ///     The command's own timeout, from which the scope's <c>RuntimeMaxSec</c> is derived. That ceiling is enforced
    ///     by the USER MANAGER rather than by the engine, so it is what bounds a jail whose supervising engine was
    ///     hard-killed: the scope stops on its own instead of running until the box reboots.
    /// </summary>
    public TimeSpan? CommandTimeout { get; init; }
}
