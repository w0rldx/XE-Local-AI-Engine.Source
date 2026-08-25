namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Every substrate requirements declaration this engine owns, in one file.
///     <para>
///         ADR 0007 Decision 1 says a declaration is composed "by engine code at the consumer's single creation site,
///         from constants". They are gathered here rather than spread across
///         <c>AgentHomeService</c> / <c>ComputeToolGateway</c> / <c>DevelopmentWorkspaceProvider</c> for two reasons.
///         First, the selector runs at DI resolution and would otherwise have to reach up from the sandbox namespace
///         into three feature namespaces to read them. Second — and this is the one that matters — ADR 0007 Decision 4
///         makes an architecture test that "enumerates every consumer's requirements constant" the replacement for the
///         compile-time guard, and an enumeration is only a guarantee if there is exactly one place a new constant can
///         be added. A declaration added anywhere else is invisible to that test.
///     </para>
///     <para>
///         Adding or widening a member here is a source change in a reviewed file, which is the same visibility the
///         absent <c>implements</c> clause on <c>DockerSandboxRuntimeProvider</c> used to give. Widening one — most of
///         all to <see cref="SandboxToolchainSource.EngineApprovedImage" />, the only value that can reach a container
///         backend — is a decision, not an implementation detail.
///     </para>
/// </summary>
public static class SandboxWorkloads
{
    /// <summary>
    ///     AgentHome runs <c>dotnet --version</c>, <c>git</c> under <c>/dev/null</c> hooks, and its own file tools —
    ///     all host binaries, nothing that outlives the run, and no filesystem boundary beyond the jail it has always
    ///     had. The network floor is <see cref="SandboxNetworkPolicy.Unrestricted" /> and the reason is on
    ///     <see cref="SandboxRequirements.NetworkFloor" />: AgentHome tightens per call to
    ///     <see cref="SandboxNetworkPolicy.None" /> wherever the backend can enforce it, and must keep running where it
    ///     cannot.
    /// </summary>
    public static readonly SandboxRequirements AgentHome = new()
    {
        Workload = "AgentHome",
        Toolchain = SandboxToolchainSource.HostToolchain,
        IsolationFloor = SandboxIsolationMode.None,
        NetworkFloor = SandboxNetworkPolicy.Unrestricted,
        // The host-toolchain ceilings (LocalContainer:ToolchainLimits), wherever the backend advertises
        // SupportsResourceLimits. AgentHome runs model-directed host commands including a real `dotnet`, so it needs a
        // build-sized ceiling, not run_python's script-sized one — and it needs one at all, because otherwise a runaway
        // command costs a timeout's worth of the whole machine.
        Ceilings = SandboxCeilingProfile.HostToolchain,
        Persistence = SandboxPersistence.Disposable
    };

    /// <summary>
    ///     Coder declares AgentHome's requirements, deliberately by value rather than by a constant of its own that
    ///     could drift from it. <c>CoderWorkspaceReader</c> creates no sandbox: it reaches AgentHome's LIVE sandbox by
    ///     attach key through <see cref="ISandboxRuntimeProvider.ConnectAsync" />, so a Coder that resolved a different
    ///     backend — or the same backend as a second instance — would answer "no workspace available" to every coder
    ///     tool. Sharing the declaration is what makes sharing the resolution correct.
    /// </summary>
    public static readonly SandboxRequirements Coder = AgentHome with { Workload = "Coder" };

    /// <summary>
    ///     Work sessions have no v1 consumer — none of the four state tools needs a jail. The declaration exists so the
    ///     role it serves resolves like the others rather than by a special case, and it is AgentHome's because that is
    ///     the substrate a session tool would execute on the day one needs to.
    /// </summary>
    public static readonly SandboxRequirements WorkSession = AgentHome with { Workload = "WorkSession" };

    /// <summary>
    ///     <c>run_python</c> is the one workload that declares a filesystem boundary, and it is not optional: the whole
    ///     point of the tool is that a script can neither read nor write the rest of the machine. A backend that cannot
    ///     supply it is refused fail-closed rather than serving a weaker sandbox.
    ///     <para>
    ///         <b>Not resolved through DI.</b> <c>ComputeToolGateway</c> injects
    ///         <see cref="IAgentSandboxRuntimeProvider" /> and shares AgentHome's instance ON PURPOSE — the two are one
    ///         backend, and giving Compute a role marker of its own would be a fourth marker for a consumer that does
    ///         not need a different backend, which ADR 0007's non-goals rule out. What this constant does is state the
    ///         workload's requirements in the same vocabulary as the others, so the architecture test can assert the
    ///         backends allowed to serve it; the runtime refusal stays where it already is and already fires before an
    ///         interpreter is provisioned (<c>ComputeToolGateway.ExecuteAsync</c>, gated on
    ///         <see cref="SandboxProviderCapabilities.SupportsFilesystemIsolation" />).
    ///     </para>
    /// </summary>
    public static readonly SandboxRequirements RunPython = new()
    {
        Workload = "run_python",
        Toolchain = SandboxToolchainSource.HostToolchain,
        IsolationFloor = SandboxIsolationMode.Filesystem,
        NetworkFloor = SandboxNetworkPolicy.None,
        // The one workload on Compute's own, deliberately tight numbers: a script is arbitrary model-supplied code
        // that runs for a second or two, so a runaway loop must cost very little. The toolchain roles are on a separate
        // and much larger set — see SandboxCeilingProfile for why one set could not serve both.
        Ceilings = SandboxCeilingProfile.ComputeTool,
        Persistence = SandboxPersistence.Disposable
    };

    /// <summary>
    ///     Development Mode on a node that has NOT been given a container image: the host's toolchain and the worktree
    ///     preserved across kill/restart.
    ///     <para>
    ///         The network FLOOR stays <see cref="SandboxNetworkPolicy.Unrestricted" /> after G1, and for AgentHome's
    ///         reason rather than as a leftover: this feature now creates TWO sandboxes per prepare, and they want
    ///         opposite postures. The short-lived warm-restore sandbox genuinely needs egress — it is what fills the
    ///         package cache from the base commit — and the agent-facing one tightens to
    ///         <see cref="SandboxNetworkPolicy.None" /> per call wherever the backend advertises
    ///         <see cref="SandboxProviderCapabilities.SupportsNetworkPolicy" />
    ///         (<c>DevelopmentWorkspaceProvider.ResolveAgentFacingNetworkPolicy</c>). A floor of <c>None</c> would
    ///         describe neither, and would refuse the workload outright on a node whose backend cannot confine
    ///         networking — which is the whole of Windows today.
    ///     </para>
    ///     <para>
    ///         The floor is therefore what the workload will ACCEPT, and the served posture is what
    ///         the Development status surface reports. Those are different numbers on a node without network
    ///         confinement, and G6 exists so the difference is visible rather than assumed.
    ///     </para>
    /// </summary>
    public static readonly SandboxRequirements DevelopmentModeHostToolchain = new()
    {
        Workload = "DevelopmentMode (host toolchain)",
        Toolchain = SandboxToolchainSource.HostToolchain,
        IsolationFloor = SandboxIsolationMode.None,
        NetworkFloor = SandboxNetworkPolicy.Unrestricted,
        // BOTH sandboxes DevelopmentWorkspaceProvider creates ask for the host-toolchain ceilings wherever the backend
        // advertises them. This is the role whose measurement produced the two-profile split: under run_python's
        // numbers a real `dotnet build` does not merely run slowly, it fails — see SandboxToolchainLimits.
        Ceilings = SandboxCeilingProfile.HostToolchain,
        Persistence = SandboxPersistence.PreservedTrustedHostWorkspace
    };

    /// <summary>
    ///     Development Mode on a container-configured node. The ONE declaration in this engine that names
    ///     <see cref="SandboxToolchainSource.EngineApprovedImage" />, and therefore the one workload a container
    ///     backend can ever serve.
    ///     <para>
    ///         Which of the two Development declarations applies is decided by
    ///         <c>SandboxProviderSelector.ResolveDevelopment</c> from the node's container configuration; see the
    ///         predicate there for the exact migration of <c>Development:Sandbox:Provider</c>'s meaning. Both forms are
    ///         constants so the architecture test enumerates both.
    ///     </para>
    /// </summary>
    public static readonly SandboxRequirements DevelopmentModeImageToolchain = DevelopmentModeHostToolchain with
    {
        Workload = "DevelopmentMode (image toolchain)",
        Toolchain = SandboxToolchainSource.EngineApprovedImage
    };
}
