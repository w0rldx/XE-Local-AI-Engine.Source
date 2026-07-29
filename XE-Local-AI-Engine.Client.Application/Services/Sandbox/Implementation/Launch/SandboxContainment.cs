namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;

/// <summary>
///     What containment the CURRENT host can actually deliver for a sandboxed child, as measured once at startup by
///     <see cref="ISandboxContainmentProbe" />. This record is the single source of truth for both halves of the
///     capability-honesty invariant: <c>ProcessSandboxRuntimeProvider.Capabilities</c> advertises a flag only when the
///     matching mechanism here is active, and the launch path applies only the mechanisms named here. Because both read
///     the same probe, advertisement and enforcement cannot drift apart.
///     <para>
///         Every mechanism is independently optional. A host with <c>setsid</c> but no user systemd still gets
///         process-group launch; a host with neither degrades to a plain child process and the provider advertises
///         neither <see cref="SandboxProviderCapabilities.SupportsResourceLimits" /> nor
///         <see cref="SandboxProviderCapabilities.SupportsNetworkPolicy" />. The <c>…UnavailableReason</c> members carry
///         the measured reason so a degraded host logs WHY, and so a live-gated test can skip with a reason instead of
///         silently passing.
///     </para>
/// </summary>
public sealed record SandboxContainment
{
    /// <summary>A host that can contain nothing: the plain-child fallback. Used off-Linux and when every probe fails.</summary>
    public static SandboxContainment None { get; } = new();

    /// <summary>
    ///     <see langword="true" /> when the child can be launched under <c>setsid</c> so its pid is also its
    ///     process-group id. This is what makes group-kill and the orphan reaper's <c>kill(-pgid)</c> possible; it is
    ///     not itself an advertised capability.
    /// </summary>
    public bool SupportsProcessGroup { get; init; }

    /// <summary>
    ///     <see langword="true" /> when memory / PID / CPU ceilings can actually be imposed on the child (measured by
    ///     really starting a constrained transient scope, not by probing for the binary). Gates
    ///     <see cref="SandboxProviderCapabilities.SupportsResourceLimits" />.
    /// </summary>
    public bool SupportsResourceLimits { get; init; }

    /// <summary>
    ///     <see langword="true" /> when the child can be placed in a fresh empty network namespace with no route to the
    ///     host loopback, the LAN, or the cloud-metadata endpoint (measured by really creating one). Gates
    ///     <see cref="SandboxProviderCapabilities.SupportsNetworkPolicy" />.
    /// </summary>
    public bool SupportsNetworkIsolation { get; init; }

    /// <summary>Absolute path to <c>setsid</c>, or <see langword="null" /> when <see cref="SupportsProcessGroup" /> is false.</summary>
    public string? SetsidPath { get; init; }

    /// <summary>Absolute path to <c>systemd-run</c>, or <see langword="null" /> when <see cref="SupportsResourceLimits" /> is false.</summary>
    public string? SystemdRunPath { get; init; }

    /// <summary>Absolute path to <c>unshare</c>, or <see langword="null" /> when <see cref="SupportsNetworkIsolation" /> is false.</summary>
    public string? UnsharePath { get; init; }

    /// <summary>
    ///     Absolute path to <c>env</c>. Required by the resource-limit path: <c>systemd-run --user</c> needs the user
    ///     bus address in its environment, and <c>env -u</c> is what strips that address back out before the sandboxed
    ///     executable runs. See <see cref="UserBusEnvironment" />.
    /// </summary>
    public string? EnvPath { get; init; }

    /// <summary>
    ///     The environment variables <c>systemd-run --user</c> needs in order to reach the per-user systemd bus
    ///     (<c>XDG_RUNTIME_DIR</c>). Empty when the resource-limit mechanism is inactive.
    ///     <para>
    ///         SECURITY: these variables address a UNIX socket, and a network namespace does NOT confine UNIX sockets —
    ///         a sandboxed child that inherited them could call <c>systemd-run</c> itself and start a unit OUTSIDE its
    ///         own scope and namespace, escaping both the ceiling and the egress denial. They are therefore injected for
    ///         the WRAPPER only and stripped by an <c>env -u</c> layer immediately before the sandboxed executable is
    ///         exec'd. This was verified live: without the strip a child inside the namespace successfully started a
    ///         unit outside it.
    ///     </para>
    /// </summary>
    public IReadOnlyDictionary<string, string> UserBusEnvironment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Measured reason resource-limit enforcement is unavailable, for logging and skip-with-reason tests.</summary>
    public string? ResourceLimitsUnavailableReason { get; init; }

    /// <summary>Measured reason network isolation is unavailable, for logging and skip-with-reason tests.</summary>
    public string? NetworkIsolationUnavailableReason { get; init; }
}
