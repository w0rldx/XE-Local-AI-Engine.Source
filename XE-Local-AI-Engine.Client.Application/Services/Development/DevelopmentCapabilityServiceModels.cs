namespace XE_Local_AI_Engine.Client.Services.Development;

using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;

/// <summary>Development Mode's availability and the state of the runtime it will actually execute on.</summary>
public sealed class DevelopmentCapability
{
    public required bool Enabled { get; init; }

    public required string SandboxProvider { get; init; }

    /// <summary>The container-runtime preflight, present only when the container provider is in force.</summary>
    public required DockerDaemonPreflight? ContainerRuntime { get; init; }

    public required IReadOnlyList<DevelopmentIsolationRole> Isolation { get; init; }
}

/// <summary>One sandbox role's inputs to the served-isolation projection, in the order the panel lists them.</summary>
public sealed class DevelopmentIsolationRole
{
    public required string Role { get; init; }

    /// <summary>The role's ADR 0007 declaration from <see cref="SandboxWorkloads" />.</summary>
    public required SandboxRequirements Requirements { get; init; }

    public required ISandboxRuntimeProvider Provider { get; init; }

    public required SandboxContainment Containment { get; init; }

    /// <summary>The <c>RequireEgressDenial</c> switch of the options section that constrains this role.</summary>
    public required bool NodeRequiresEgressDenial { get; init; }

    /// <summary>
    ///     Set when a host fact outside the sandbox mechanism takes this role's filesystem boundary away; the projection
    ///     then reports no boundary, with this reason.
    /// </summary>
    public string? FilesystemIsolationWithheldReason { get; init; }
}
