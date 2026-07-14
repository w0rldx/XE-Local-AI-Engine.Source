namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Provider-neutral network posture requested for a sandbox. <see cref="None" /> and <see cref="Restricted" /> both
///     ask the provider to CONSTRAIN egress; a provider that does not advertise
///     <see cref="SandboxProviderCapabilities.SupportsNetworkPolicy" /> cannot honor either and — under the fail-closed
///     contract — rejects such a request with <see cref="SandboxCapabilityNotSupportedException" /> rather than silently
///     handing back an un-isolated sandbox. <see cref="Unrestricted" /> is the honest posture for a supervised-but-not-
///     isolated provider: the caller acknowledges the child shares the host's network.
/// </summary>
public enum SandboxNetworkPolicy
{
    /// <summary>No network access. Requires a provider that advertises network isolation; otherwise rejected fail-closed.</summary>
    None = 0,

    /// <summary>A provider-defined restricted policy (e.g. an egress allow-list). Honored only when the provider advertises support; otherwise rejected fail-closed.</summary>
    Restricted = 1,

    /// <summary>
    ///     No network isolation is requested: the sandbox child shares the host's network. This is the only posture a
    ///     supervised-but-not-isolated provider (e.g. <c>ProcessSandboxRuntimeProvider</c>) can honestly serve. Choosing
    ///     it is an explicit acknowledgement that the provider does not confine egress.
    /// </summary>
    Unrestricted = 2
}
