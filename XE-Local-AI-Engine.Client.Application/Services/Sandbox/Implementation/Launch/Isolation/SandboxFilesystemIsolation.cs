namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;

/// <summary>
///     What the containment probe MEASURED about this host's ability to run a command behind a filesystem boundary:
///     the four helper binaries, each resolved through <see cref="TrustedBinaryResolver" /> rather than through
///     <c>PATH</c>, the host's legacy-root layout, and the uid/gid the jail maps.
///     <para>
///         Its presence is the capability. <c>SandboxContainment.SupportsFilesystemIsolation</c> is defined as "this
///         is not null", so there is exactly one thing to be true, and the launch path cannot render a chain out of
///         values the probe never validated.
///     </para>
/// </summary>
internal sealed record SandboxFilesystemIsolation
{
    public required string SetsidPath { get; init; }

    public required string SystemdRunPath { get; init; }

    public required string SystemctlPath { get; init; }

    public required string BwrapPath { get; init; }

    /// <summary>How the host's legacy top-level roots are reproduced inside the jail; see <see cref="SandboxUsrMergeLayout" />.</summary>
    public required IReadOnlyList<SandboxUsrMergeEntry> UsrMergeEntries { get; init; }

    public required uint UserId { get; init; }

    public required uint GroupId { get; init; }

    /// <summary>
    ///     The variables <c>systemd-run --user</c> needs to reach the per-user bus. Carried for the WRAPPER only; the
    ///     chain's <c>--clearenv</c> removes them (and everything else) before the workload runs, which is why the
    ///     isolated chain needs no <c>env -u</c> layer.
    /// </summary>
    public required IReadOnlyDictionary<string, string> UserBusEnvironment { get; init; }
}
