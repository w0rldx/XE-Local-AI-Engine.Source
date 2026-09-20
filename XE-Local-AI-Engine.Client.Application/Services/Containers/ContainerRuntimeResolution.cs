namespace XE_Local_AI_Engine.Client.Services.Containers;

using XE_Local_AI_Engine.Client.Services.Sandbox.Container;

/// <summary>The distinguishable outcomes of resolving a container runtime for application containers.</summary>
/// <remarks>
///     The members mirror <see cref="DockerDaemonPreflightStatus" /> value for value rather than aliasing it, so
///     this layer's public surface exports no type named for Development Mode's preflight. The mapping is one switch
///     with no default arm, which makes a new preflight status a compile error here rather than a fall-through.
/// </remarks>
public enum ContainerRuntimeStatus
{
    /// <summary>A daemon is reachable, new enough, and is the one this node approved.</summary>
    Ready = 0,

    /// <summary>Nothing answered at the resolved endpoint.</summary>
    DaemonUnreachable = 1,

    /// <summary>Something answered but refused this process.</summary>
    PermissionDenied = 2,

    /// <summary>A daemon answered but serves an API too old for this layer.</summary>
    ApiVersionTooOld = 3,

    /// <summary>A daemon answered and it is not the one this node approved.</summary>
    DaemonIdentityChanged = 4,

    /// <summary>The engine's own configuration is unusable, independent of any daemon.</summary>
    /// <remarks>
    ///     Unreachable in V1: its producers are Development Mode's own gates, and faulty options here fail at
    ///     <c>ValidateOnStart</c> instead. Kept for parity with the preflight enum so the mapping stays total — said
    ///     here so a later reader does not delete it as dead.
    /// </remarks>
    NotConfigured = 5,

    /// <summary>The probe failed in a way none of the above describes; the message says how.</summary>
    ProbeFailed = 6
}

/// <summary>What is known about the daemon behind a resolution: what was observed now, and what this node approved earlier.</summary>
/// <remarks>
///     The four observed members are nullable because an unreachable daemon reports none of them, and filling them
///     with empty strings would report a daemon that was never reached; the two pinned members come from the
///     attestation and are null when this node has never pinned one. The attestation also records the engine version
///     seen at approval and this record deliberately does not re-export it: one <c>ServerVersion</c> that sometimes
///     means "now" and sometimes "at approval" is a field two readers read two ways.
/// </remarks>
public sealed record ContainerDaemonSummary
{
    /// <summary>The endpoint the resolution used.</summary>
    public required string Endpoint { get; init; }

    /// <summary>How that endpoint was arrived at — configuration, environment, or a conventional socket.</summary>
    public required DockerDaemonEndpointSource EndpointSource { get; init; }

    /// <summary>The daemon's own installation id, as observed now, or null when nothing answered.</summary>
    public string? DaemonId { get; init; }

    /// <summary>The engine version observed now, or null when nothing answered.</summary>
    public string? ServerVersion { get; init; }

    /// <summary>The API version observed now, or null when nothing answered.</summary>
    public string? ApiVersion { get; init; }

    /// <summary>Whether the observed daemon reported itself rootless. False when nothing answered.</summary>
    public bool IsRootless { get; init; }

    /// <summary>The daemon id this node approved, or null when it has never pinned one.</summary>
    public string? PinnedDaemonId { get; init; }

    /// <summary>When that approval happened, or null when this node has never pinned a daemon.</summary>
    public DateTimeOffset? PinnedDaemonConfirmedAtUtc { get; init; }
}

/// <summary>
///     The answer to "which container runtime, and may we use it" — the shape behind the operator-facing runtime card
///     and the gate every application-container operation passes through.
/// </summary>
public sealed record ContainerRuntimeResolution
{
    /// <summary>The provider that answered. <c>docker</c> is the only value in V1.</summary>
    public required string Provider { get; init; }

    /// <summary>The distinguishable outcome.</summary>
    public required ContainerRuntimeStatus Status { get; init; }

    /// <summary>What this runtime can do; <see cref="ContainerRuntimeCapabilities.None" /> when it is not ready.</summary>
    public required ContainerRuntimeCapabilities Capabilities { get; init; }

    /// <summary>The prose an operator reads. Never null, never a bare exception message.</summary>
    public required string Message { get; init; }

    /// <summary>What is known about the daemon behind this resolution.</summary>
    public required ContainerDaemonSummary Daemon { get; init; }

    /// <summary>Whether an application container may be created right now.</summary>
    public bool Ready => Status == ContainerRuntimeStatus.Ready;

    /// <summary>
    ///     Whether clearing this needs an explicit operator decision rather than a fix to the machine. True only for
    ///     <see cref="ContainerRuntimeStatus.DaemonIdentityChanged" />, which is the one state a confirmation resolves
    ///     and the one state a confirmation must never be offered for otherwise.
    /// </summary>
    public bool RequiresOperatorConfirmation => Status == ContainerRuntimeStatus.DaemonIdentityChanged;

    /// <summary>Whether a daemon answered at all, which is not the same question as <see cref="Ready" />.</summary>
    /// <remarks>
    ///     False for exactly the three statuses where nothing was reached: <c>DaemonUnreachable</c>,
    ///     <c>NotConfigured</c> and <c>ProbeFailed</c>. <c>PermissionDenied</c>, <c>ApiVersionTooOld</c> and
    ///     <c>DaemonIdentityChanged</c> are therefore available but not ready — something is there and the operator
    ///     has an action to take, which a caller must not report as "there is no container runtime on this machine".
    /// </remarks>
    public bool Available =>
        Status is not (ContainerRuntimeStatus.DaemonUnreachable
            or ContainerRuntimeStatus.NotConfigured
            or ContainerRuntimeStatus.ProbeFailed);
}
