namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;

/// <summary>
///     Resolves — ONCE per host, then caches — which containment mechanisms a sandboxed child can actually be launched
///     under. Both <c>ProcessSandboxRuntimeProvider.Capabilities</c> (advertisement) and the launch path (enforcement)
///     read this same seam, which is what keeps the capability-honesty invariant mechanically true rather than
///     maintained by hand.
/// </summary>
public interface ISandboxContainmentProbe
{
    /// <summary>
    ///     The measured containment for this host. Implementations must be idempotent and cheap after the first call
    ///     (the real probe starts short-lived child processes), and must never throw — an unprobeable mechanism is
    ///     reported as unavailable with a reason, never as an exception.
    /// </summary>
    SandboxContainment Containment { get; }
}
