namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Which containment axes are PRECONDITIONS for one workload on this node, as <see cref="SandboxSecurityProfilePolicy.Preconditions" />
///     computes them: a required axis the host cannot serve refuses the sandbox instead of degrading it.
/// </summary>
/// <remarks>
///     Floors are not restated here: a <c>Filesystem</c> isolation floor or a <c>None</c> network floor already refuses at selection or at
///     the create site under either profile, so these three flags describe only what the profile and the configuration switch ADD.
/// </remarks>
public sealed class SandboxProfilePreconditions
{
    /// <summary>Egress must be denied: the configuration's <c>RequireEgressDenial</c> key, or <c>high</c> on a workload that tightens where advertised.</summary>
    public required bool RequireEgressDenial { get; init; }

    /// <summary>A Stable host-filesystem boundary must be served, because the declaration prefers one and the profile is <c>high</c>.</summary>
    public required bool RequireFilesystemBoundary { get; init; }

    /// <summary>CPU, memory and process-count ceilings must be imposed, because the declaration asks for them and the profile is <c>high</c>.</summary>
    public required bool RequireResourceCeilings { get; init; }
}
