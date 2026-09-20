namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;

/// <summary>
///     Resolves once per host, then caches, which containment mechanisms a sandboxed child can actually be launched under.
/// </summary>
/// <remarks>
///     Both <c>ProcessSandboxRuntimeProvider.Capabilities</c> (advertisement) and the launch path (enforcement) read this same seam, which
///     is what keeps the capability-honesty invariant mechanically true rather than maintained by hand.
/// </remarks>
public interface ISandboxContainmentProbe
{
    /// <summary>The measured containment for this host.</summary>
    /// <remarks>
    ///     Implementations must be idempotent and cheap after the first call, the real probe starting short-lived child processes, and
    ///     must never throw: an unprobeable mechanism is reported as unavailable with a reason, never as an exception.
    /// </remarks>
    SandboxContainment Containment { get; }
}
