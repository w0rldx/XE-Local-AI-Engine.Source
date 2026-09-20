namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>Provider-neutral network posture requested for a sandbox.</summary>
/// <remarks>
///     <see cref="None" /> and <see cref="Restricted" /> both ask the provider to CONSTRAIN egress; one that does not advertise
///     <see cref="SandboxProviderCapabilities.SupportsNetworkPolicy" /> can honour neither and, under the fail-closed contract, rejects the
///     request with <see cref="SandboxCapabilityNotSupportedException" /> rather than handing back an un-isolated sandbox.
///     <see cref="Unrestricted" /> is the honest posture for a supervised-but-not-isolated provider: the caller acknowledges that the child
///     shares the host's network.
/// </remarks>
public enum SandboxNetworkPolicy
{
    /// <summary>No network access. Requires a provider that advertises network isolation; otherwise rejected fail-closed.</summary>
    None = 0,

    /// <summary>A provider-defined restricted policy (e.g. an egress allow-list). Honored only when the provider advertises support; otherwise rejected fail-closed.</summary>
    Restricted = 1,

    /// <summary>No network isolation is requested: the sandbox child shares the host's network.</summary>
    /// <remarks>
    ///     The only posture a supervised-but-not-isolated provider such as <c>ProcessSandboxRuntimeProvider</c> can honestly serve;
    ///     choosing it is an explicit acknowledgement that the provider does not confine egress.
    /// </remarks>
    Unrestricted = 2
}
