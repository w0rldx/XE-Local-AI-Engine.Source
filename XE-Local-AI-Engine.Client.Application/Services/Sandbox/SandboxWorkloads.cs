namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>Every substrate requirements declaration this engine owns, in one file.</summary>
/// <remarks>
///     Gathered here because ADR 0007 Decision 4 makes an architecture test enumerating every constant the replacement for the compile-time
///     guard, and an enumeration only guarantees anything if there is exactly one place a new constant can be added: a declaration added
///     elsewhere is invisible to that test. Adding or widening a member is a reviewed source change — most of all to
///     <see cref="SandboxToolchainSource.EngineApprovedImage" />, the only value that can reach a container backend, which is a decision
///     rather than an implementation detail.
/// </remarks>
public static class SandboxWorkloads
{
    /// <summary>
    ///     AgentHome: host binaries only (<c>dotnet --version</c>, <c>git</c>, its own file tools), nothing outliving the run, and no
    ///     filesystem boundary beyond the jail it has always had.
    /// </summary>
    /// <remarks>
    ///     The network floor is <see cref="SandboxNetworkPolicy.Unrestricted" /> for the reason on
    ///     <see cref="SandboxRequirements.NetworkFloor" />: AgentHome tightens per call to <see cref="SandboxNetworkPolicy.None" />
    ///     wherever the backend can enforce it, and must keep running where it cannot.
    /// </remarks>
    public static readonly SandboxRequirements AgentHome = new()
    {
        Workload = "AgentHome",
        Toolchain = SandboxToolchainSource.HostToolchain,
        IsolationFloor = SandboxIsolationMode.None,
        NetworkFloor = SandboxNetworkPolicy.Unrestricted,
        // Host-toolchain ceilings (LocalContainer:ToolchainLimits) wherever the backend advertises them. AgentHome runs model-directed host
        // commands including a real `dotnet`, so it needs a build-sized ceiling, and one at all: a runaway otherwise costs the machine.
        Ceilings = SandboxCeilingProfile.HostToolchain,
        Persistence = SandboxPersistence.Disposable
    };

    /// <summary>
    ///     Coder declares AgentHome's requirements BY VALUE, never through a constant of its own that could drift from them.
    /// </summary>
    /// <remarks>
    ///     <c>CoderWorkspaceReader</c> creates no sandbox: it reaches AgentHome's LIVE sandbox by attach key through
    ///     <see cref="ISandboxRuntimeProvider.ConnectAsync" />, so a Coder that resolved a different backend — or the same backend as a
    ///     second instance — would answer "no workspace available" to every coder tool. Sharing the declaration is what makes sharing the
    ///     resolution correct.
    /// </remarks>
    public static readonly SandboxRequirements Coder = AgentHome with
    {
        Workload = "Coder"
    };

    /// <summary>Work sessions, which have no v1 consumer: none of the four state tools needs a jail.</summary>
    /// <remarks>
    ///     The declaration exists so the role it serves resolves like the others rather than by a special case, and it is AgentHome's
    ///     because that is the substrate a session tool would execute on the day one needs to.
    /// </remarks>
    public static readonly SandboxRequirements WorkSession = AgentHome with
    {
        Workload = "WorkSession"
    };

    /// <summary>
    ///     <c>run_python</c>, the one workload that declares a filesystem boundary, and not optionally: a script must neither read nor
    ///     write the rest of the machine, so a backend that cannot supply it is refused fail-closed.
    /// </summary>
    /// <remarks>
    ///     NOT resolved through DI. <c>ComputeToolGateway</c> injects <see cref="IAgentSandboxRuntimeProvider" /> and shares AgentHome's
    ///     instance on purpose — they are one backend, and a role marker of its own would be a fourth marker ADR 0007's non-goals rule out.
    ///     This constant states the requirements in the same vocabulary as the others so the architecture test can assert which backends
    ///     may serve it; the runtime refusal stays in <c>ComputeToolGateway.ExecuteAsync</c>, gated on
    ///     <see cref="SandboxProviderCapabilities.SupportsFilesystemIsolation" /> before an interpreter is provisioned.
    /// </remarks>
    public static readonly SandboxRequirements RunPython = new()
    {
        Workload = "run_python",
        Toolchain = SandboxToolchainSource.HostToolchain,
        IsolationFloor = SandboxIsolationMode.Filesystem,
        NetworkFloor = SandboxNetworkPolicy.None,
        // Compute's own, deliberately tight numbers: a script is arbitrary model-supplied code running for a second or two, so a runaway
        // loop must cost very little. The toolchain roles are on a much larger set — SandboxCeilingProfile says why one cannot serve both.
        Ceilings = SandboxCeilingProfile.ComputeTool,
        Persistence = SandboxPersistence.Disposable
    };

    /// <summary>
    ///     An outbound stdio MCP server at the <c>Sandboxed</c> trust tier: an operator-configured third-party executable this node
    ///     launches and speaks JSON-RPC to over its stdin/stdout.
    /// </summary>
    /// <remarks>
    ///     The isolation floor is not optional, and that is the point of the tier: everything else here runs a fixed engine-chosen binary
    ///     or approval-gated model code, while this runs a program the operator installed from a README, whose first tool call could read
    ///     <c>~/.ssh</c> and the node database (threat AB3) — and that path's only control was an environment scrub, which does not touch
    ///     the filesystem. The network floor costs nothing on top: the isolated chain carries its own empty namespace. A
    ///     <c>PrivilegedHost</c> server declares NOTHING here, being a per-server operator grant.
    /// </remarks>
    public static readonly SandboxRequirements McpStdio = new()
    {
        Workload = "McpStdio (sandboxed)",
        Toolchain = SandboxToolchainSource.HostToolchain,
        IsolationFloor = SandboxIsolationMode.Filesystem,
        NetworkFloor = SandboxNetworkPolicy.None,
        // Host-toolchain ceilings, not run_python's: an MCP server is a long-lived child running an operator-installed program, so a
        // runaway one should cost a bounded amount. The script-sized set would strangle a language server on its first index.
        Ceilings = SandboxCeilingProfile.HostToolchain,
        Persistence = SandboxPersistence.Disposable
    };

    /// <summary>
    ///     Development Mode on a node that has NOT been given a container image: the host's toolchain, and the worktree preserved across
    ///     kill and restart.
    /// </summary>
    /// <remarks>
    ///     The network floor stays <see cref="SandboxNetworkPolicy.Unrestricted" /> for AgentHome's reason, not as a leftover: this feature
    ///     creates TWO sandboxes per prepare with opposite postures. The short-lived warm-restore sandbox genuinely needs egress — it fills
    ///     the package cache from the base commit — while the agent-facing one tightens to None per call wherever the backend advertises
    ///     the capability. A floor of None would describe neither, and would refuse the workload wherever networking cannot be confined,
    ///     which is the whole of Windows today. The floor is what the workload ACCEPTS; the status surface reports what was SERVED.
    /// </remarks>
    public static readonly SandboxRequirements DevelopmentModeHostToolchain = new()
    {
        Workload = "DevelopmentMode (host toolchain)",
        Toolchain = SandboxToolchainSource.HostToolchain,
        IsolationFloor = SandboxIsolationMode.None,
        NetworkFloor = SandboxNetworkPolicy.Unrestricted,
        // BOTH sandboxes DevelopmentWorkspaceProvider creates ask for host-toolchain ceilings wherever advertised. This role's measurement
        // produced the two-profile split: under run_python's numbers a real `dotnet build` fails, not merely runs slowly (SandboxToolchainLimits).
        Ceilings = SandboxCeilingProfile.HostToolchain,
        Persistence = SandboxPersistence.PreservedTrustedHostWorkspace
    };

    /// <summary>
    ///     Development Mode on a container-configured node: the ONE declaration naming
    ///     <see cref="SandboxToolchainSource.EngineApprovedImage" />, and so the one workload a container backend can ever serve.
    /// </summary>
    /// <remarks>
    ///     Which of the two Development declarations applies is decided by <c>SandboxProviderSelector.ResolveDevelopment</c> from the
    ///     node's container configuration; the predicate there carries the exact migration of <c>Development:Sandbox:Provider</c>'s
    ///     meaning. Both forms are constants so the architecture test enumerates both.
    /// </remarks>
    public static readonly SandboxRequirements DevelopmentModeImageToolchain = DevelopmentModeHostToolchain with
    {
        Workload = "DevelopmentMode (image toolchain)",
        Toolchain = SandboxToolchainSource.EngineApprovedImage
    };
}
