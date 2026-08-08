namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;

/// <summary>
///     The containment a single sandbox asks its children to run under, derived once from the
///     <see cref="SandboxCreateRequest" /> and carried for the sandbox's lifetime. It is the request-side input to
///     <see cref="SandboxLaunchPlan" />; what is actually applied is the intersection of this with the host's measured
///     <see cref="SandboxContainment" />.
/// </summary>
public sealed record SandboxLaunchPolicy
{
    /// <summary>No containment requested: the pre-hardening behavior of a plain supervised child.</summary>
    public static SandboxLaunchPolicy Unconstrained { get; } = new();

    /// <summary>
    ///     Requested CPU / memory / PID ceilings, or <see langword="null" /> when the caller asked for none. A sandbox
    ///     is only ever created with a non-null value when the provider advertises
    ///     <see cref="SandboxProviderCapabilities.SupportsResourceLimits" />, so a value here implies an active
    ///     mechanism.
    /// </summary>
    public SandboxResourceLimits? ResourceLimits { get; init; }

    /// <summary>
    ///     <see langword="true" /> when the child must run with no network egress — no route to the host loopback, the
    ///     LAN, or the cloud-metadata endpoint. Set for <see cref="SandboxNetworkPolicy.None" />, which is the default
    ///     posture of <see cref="SandboxCreateRequest" />.
    /// </summary>
    public bool DenyNetworkEgress { get; init; }
}
