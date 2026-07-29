namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container;

/// <summary>
///     The distinguishable outcomes of the container-runtime preflight. They are distinct because the operator action
///     is distinct in each case; an outcome that did not change what someone should do next would not earn a value
///     here.
/// </summary>
public enum DockerDaemonPreflightStatus
{
    /// <summary>A daemon is reachable, new enough, and matches this node's approved daemon.</summary>
    Ready = 0,

    /// <summary>Nothing answered at the resolved endpoint. Action: start a daemon, or point the engine at one.</summary>
    DaemonUnreachable = 1,

    /// <summary>Something answered but refused this process. Action: grant socket access — knowing what that grants.</summary>
    PermissionDenied = 2,

    /// <summary>A daemon answered but serves an API older than the hardening contract can be verified through. Action: upgrade.</summary>
    ApiVersionTooOld = 3,

    /// <summary>A daemon answered and it is not the one this node approved. Action: confirm it, or restore the previous one.</summary>
    DaemonIdentityChanged = 4,

    /// <summary>The engine's own container configuration is unusable, independent of any daemon. Action: fix the configuration.</summary>
    NotConfigured = 5,

    /// <summary>The probe failed in a way none of the above describes. Action: read the reason.</summary>
    ProbeFailed = 6
}

/// <summary>
///     The result of one container-runtime preflight: a machine-readable status, an operator-actionable message, and
///     the evidence behind both.
///     <para>
///         Per ADR 0004 there is deliberately no unisolated fallback — an operator without a working daemon does not
///         get a degraded Development Mode, they get none — so this message is the entire user experience of that
///         failure and is written as a feature rather than as an error string.
///     </para>
/// </summary>
public sealed record DockerDaemonPreflight
{
    /// <summary>The distinguishable outcome.</summary>
    public required DockerDaemonPreflightStatus Status { get; init; }

    /// <summary>Whether a Development Mode container could be created right now.</summary>
    public bool Ready => Status == DockerDaemonPreflightStatus.Ready;

    /// <summary>
    ///     Whether clearing this needs an explicit operator decision rather than a fix to the machine. True only for
    ///     <see cref="DockerDaemonPreflightStatus.DaemonIdentityChanged" />, which is the one state a confirmation
    ///     resolves and the one state a confirmation must never be offered for otherwise.
    /// </summary>
    public bool RequiresOperatorConfirmation => Status == DockerDaemonPreflightStatus.DaemonIdentityChanged;

    /// <summary>The prose an operator reads. Never null, never a bare exception message.</summary>
    public required string Message { get; init; }

    /// <summary>The endpoint the probe used, when one could be resolved at all.</summary>
    public DockerDaemonEndpoint? Endpoint { get; init; }

    /// <summary>The daemon actually reached, when one answered.</summary>
    public DockerDaemonIdentity? ObservedDaemon { get; init; }

    /// <summary>This node's approved daemon, when it has one.</summary>
    public DockerDaemonAttestation? PinnedDaemon { get; init; }
}
