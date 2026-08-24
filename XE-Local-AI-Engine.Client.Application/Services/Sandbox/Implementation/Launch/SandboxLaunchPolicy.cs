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

    /// <summary>
    ///     The filesystem posture this sandbox's commands run under. A policy only ever carries
    ///     <see cref="SandboxIsolationMode.Filesystem" /> when the host was measured able to deliver it — the registry
    ///     rejects the create request otherwise — so a value here implies an active boundary, exactly as a non-null
    ///     <see cref="ResourceLimits" /> implies an active ceiling.
    /// </summary>
    public SandboxIsolationMode Isolation { get; init; } = SandboxIsolationMode.None;

    /// <summary>Host trees the isolated chain binds read-only. Empty under <see cref="SandboxIsolationMode.None" />.</summary>
    public IReadOnlyList<string> ReadOnlyTrees { get; init; } = [];

    /// <summary>The value the isolated chain pins every numeric-library thread-count variable to.</summary>
    public int ThreadLimit { get; init; } = 1;

    /// <summary>
    ///     The role segment of the transient scope's unit name, so <c>systemctl</c> output and the startup sweep's
    ///     logs say WHAT was running rather than only that something was.
    /// </summary>
    public string? Role { get; init; }
}
