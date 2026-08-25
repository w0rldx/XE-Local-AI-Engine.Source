namespace XE_Local_AI_Engine.Client.Services.Sandbox;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Fake;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;

/// <summary>
///     Picks the backend that serves a workload's <see cref="SandboxRequirements" />. ADR 0007 Decision 2: a consumer
///     declares what it needs, this resolves a backend that can honour the WHOLE declaration, and when none can it
///     fails closed. There is no fallback, no downgrade and no best-effort resolution.
///     <para>
///         <b>Resolution is minimal-satisfying, not most-capable-wins.</b> Among the backends that meet every declared
///         axis, the one with the smallest additional privilege footprint wins — see
///         <see cref="ByAscendingPrivilege" /> for the ranking and the reasoning behind its order. That ordering is
///         load-bearing: it is the first of the three mechanisms ADR 0007 Decision 4 uses to replace the compile-time
///         guard that used to keep a container out of AgentHome.
///     </para>
///     <para>
///         Each role is registered once as a singleton factory, so a provider change still requires a restart, and two
///         roles that resolve the same backend still share ONE instance — every backend is reached through
///         <c>GetService&lt;TConcrete&gt;()</c> rather than constructed. That sharing is a correctness requirement, not
///         a saving: <see cref="ProcessSandboxRuntimeProvider" /> allocates its jail root once per instance, and Coder
///         reaches AgentHome's live sandbox by attach key through <see cref="ISandboxRuntimeProvider.ConnectAsync" />,
///         so a second instance would answer "no such sandbox" to every coder tool.
///     </para>
/// </summary>
internal static class SandboxProviderSelector
{
    /// <summary>The operator key that constrains the AgentHome, Coder and work-session candidate set.</summary>
    private const string AgentConstraintKey = SandboxOptions.SectionName + ":Provider";

    /// <summary>The operator key that constrains the Development Mode candidate set.</summary>
    private const string DevelopmentConstraintKey = DevelopmentSandboxOptions.SectionName + ":Provider";

    /// <summary>
    ///     Every backend this engine knows, ordered by ASCENDING additional privilege. First match wins, so the order
    ///     IS the resolution rule and it is code-owned rather than emergent from a <c>switch</c>.
    ///     <list type="number">
    ///         <item>
    ///             <description>
    ///                 <c>fake</c> — executes nothing at all. It cannot start a process, so there is no privilege to
    ///                 compare; it is first because a deterministic no-op is strictly less than any execution.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <c>process</c> — a supervised child of the engine, in a working-directory jail, with whatever
    ///                 of setsid / systemd-run / unshare / bwrap the host actually delivers. It adds no component to
    ///                 the trusted computing base: it runs as the engine's own user and talks to no daemon.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <c>docker</c> — last, and the axis that puts it there is not isolation strength but the daemon.
    ///                 A container backend needs a live daemon whose socket is root-equivalent on Linux; ADR 0004
    ///                 documents that rather than mitigating it. Reaching a root-equivalent socket is additional
    ///                 privilege even when the resulting container is a stronger boundary than the jail, which is
    ///                 precisely why "minimal-satisfying" and "most-capable" are different orderings.
    ///             </description>
    ///         </item>
    ///     </list>
    ///     <para>
    ///         The day a third execution backend exists this comparison gets harder — ADR 0007 records that as a cost.
    ///         Insert it here with its reasoning written down, not by capability count.
    ///     </para>
    /// </summary>
    private static readonly SandboxBackend[] ByAscendingPrivilege =
    [
        new(FakeSandboxRuntimeProvider.Name,
            SandboxToolchainSource.HostToolchain,
            static services => services.GetService<FakeSandboxRuntimeProvider>()),
        new(ProcessSandboxRuntimeProvider.Name,
            SandboxToolchainSource.HostToolchain,
            static services => services.GetService<ProcessSandboxRuntimeProvider>()),
        new(DockerSandboxRuntimeProvider.Name,
            SandboxToolchainSource.EngineApprovedImage,
            static services => services.GetService<DockerSandboxRuntimeProvider>())
    ];

    /// <summary>
    ///     The ranking, projected for the architecture test: backend name and the toolchain it supplies, in the order
    ///     resolution walks them. Exposed because the guarantee that used to be an absent <c>implements</c> clause is
    ///     now an enumeration, and an enumeration the test cannot read is not a guarantee.
    /// </summary>
    internal static IReadOnlyList<(string Name, SandboxToolchainSource Toolchain)> BackendRanking { get; } =
        [.. ByAscendingPrivilege.Select(static backend => (backend.Name, backend.Toolchain))];

    /// <summary>
    ///     Resolves the AgentHome/Coder sandbox for <see cref="SandboxWorkloads.AgentHome" />, constrained by
    ///     <c>AgentHome:Sandbox:Provider</c>. It cannot return a container backend: the declaration names
    ///     <see cref="SandboxToolchainSource.HostToolchain" />, and a container backend supplies only an image.
    /// </summary>
    public static IAgentSandboxRuntimeProvider ResolveAgent(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return Resolve<IAgentSandboxRuntimeProvider>(services,
            SandboxWorkloads.AgentHome,
            ReadAgentConstraint(services),
            AgentConstraintKey);
    }

    /// <summary>
    ///     Resolves the Development Mode sandbox.
    ///     <para>
    ///         <b>Which declaration applies.</b> Development Mode is the one workload whose toolchain need is a
    ///         property of the node rather than of the code: a node with an operator-approved image has one, a node
    ///         without has only the host's. So the declaration is
    ///         <see cref="SandboxWorkloads.DevelopmentModeImageToolchain" /> when the node names a container image, or
    ///         when <c>Development:Sandbox:Provider</c> names an image-backed backend — that key meant "run Development
    ///         Mode in a container", and reading it as a declared image-toolchain need is the migration of its meaning
    ///         rather than a reinterpretation of it. Otherwise it is
    ///         <see cref="SandboxWorkloads.DevelopmentModeHostToolchain" />, which is what every node ships as today.
    ///     </para>
    ///     <para>
    ///         <b>Which constraint applies.</b> An explicit <c>Development:Sandbox:Provider</c> always constrains. When
    ///         it is unset the AgentHome key constrains instead — the fallback that has always made this seam a runtime
    ///         no-op on a node that never set the new key — but ONLY while the declaration is host-toolchain. A node
    ///         that configured an image is asking for a container, and inheriting a key that names the process backend
    ///         would answer that request with a refusal it never asked for. A node that names an image AND pins
    ///         <c>Development:Sandbox:Provider</c> to a backend that cannot supply one still fails closed, loudly: a
    ///         set key is never silently reinterpreted, because that is how a hardened node becomes an unhardened one.
    ///     </para>
    /// </summary>
    public static IDevelopmentSandboxRuntimeProvider ResolveDevelopment(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var configured = Normalize(services.GetService<IOptions<DevelopmentSandboxOptions>>()?.Value.Provider);
        var imageConfigured = !string.IsNullOrWhiteSpace(services.GetService<IOptions<ContainerSandboxOptions>>()?.Value.Image);
        var namesImageBackend = configured is not null
                                && Array.Exists(ByAscendingPrivilege,
                                    backend => backend.Matches(configured)
                                               && backend.Toolchain == SandboxToolchainSource.EngineApprovedImage);

        var requirements = imageConfigured || namesImageBackend
            ? SandboxWorkloads.DevelopmentModeImageToolchain
            : SandboxWorkloads.DevelopmentModeHostToolchain;

        if (configured is not null)
        {
            return Resolve<IDevelopmentSandboxRuntimeProvider>(services, requirements, configured, DevelopmentConstraintKey);
        }

        var inherited = requirements.Toolchain == SandboxToolchainSource.HostToolchain ? ReadAgentConstraint(services) : null;
        return Resolve<IDevelopmentSandboxRuntimeProvider>(services, requirements, inherited, AgentConstraintKey);
    }

    /// <summary>
    ///     Resolves the work-session sandbox for <see cref="SandboxWorkloads.WorkSession" />. There is still no
    ///     <c>WorkSessions:Sandbox:Provider</c> key — nothing in v1 executes inside this jail, and inventing a setting
    ///     for a role with no consumer would be one more thing an operator can get wrong for no effect — so it is
    ///     constrained by the AgentHome key, which is the backend a session tool would land on. Give it its own key
    ///     when a session tool needs one.
    /// </summary>
    public static IWorkSessionSandboxRuntimeProvider ResolveWorkSession(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return Resolve<IWorkSessionSandboxRuntimeProvider>(services,
            SandboxWorkloads.WorkSession,
            ReadAgentConstraint(services),
            AgentConstraintKey);
    }

    /// <summary>
    ///     The whole of the axis vocabulary, in one pure function: the first requirement
    ///     <paramref name="requirements" /> states that a backend supplying <paramref name="backendToolchain" /> and
    ///     advertising <paramref name="capabilities" /> cannot honour, or <see langword="null" /> when it can honour
    ///     all of them. Internal so the architecture test can enumerate declarations against fixed capability sets and
    ///     fail deterministically offline, rather than against whatever this host's containment probe happens to
    ///     measure.
    ///     <para>
    ///         <paramref name="capabilities" /> is a delegate on purpose. Reading
    ///         <c>ProcessSandboxRuntimeProvider.Capabilities</c> runs the host containment probe, which launches real
    ///         children; the axes AgentHome and work sessions declare need none of it, so a resolution that used to
    ///         cost nothing must not start costing a probe.
    ///     </para>
    /// </summary>
    internal static string? FindUnmetAxis(SandboxRequirements requirements,
        SandboxToolchainSource backendToolchain,
        Func<SandboxProviderCapabilities> capabilities)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(capabilities);

        if (requirements.Toolchain != backendToolchain)
        {
            return $"toolchain source ({requirements.Toolchain})";
        }

        // The FLOOR is the property — the host filesystem absent from the sandbox's view — so it is checked against
        // SupportsHostFilesystemBoundary, not against SupportsFilesystemIsolation. The latter names one mechanism's
        // create-request contract, and gating the floor on it would refuse the container backend an isolation level it
        // genuinely provides. Which mechanism a workload also needs is a per-call matter for SandboxCreateRequest,
        // where run_python asks for the bwrap contract by name and is refused fail-closed without it.
        if (requirements.IsolationFloor == SandboxIsolationMode.Filesystem
            && !capabilities().HasFlag(SandboxProviderCapabilities.SupportsHostFilesystemBoundary))
        {
            return $"isolation floor ({requirements.IsolationFloor})";
        }

        // An isolated request carries its own empty network namespace — bwrap's --unshare-net, positively controlled
        // by the containment probe with a loopback connect — so the separate egress mechanism is not on that path and
        // gating on it would refuse a host that isolates perfectly well. This mirrors the check
        // ComputeToolGateway.ExecuteAsync already makes, and for the same reason.
        if (requirements.NetworkFloor != SandboxNetworkPolicy.Unrestricted
            && requirements.IsolationFloor != SandboxIsolationMode.Filesystem
            && !capabilities().HasFlag(SandboxProviderCapabilities.SupportsNetworkPolicy))
        {
            return $"network posture ({requirements.NetworkFloor})";
        }

        if (requirements.Persistence == SandboxPersistence.PreservedTrustedHostWorkspace
            && !capabilities().HasFlag(SandboxProviderCapabilities.SupportsTrustedHostWorkspace))
        {
            return $"persistence ({requirements.Persistence})";
        }

        // SandboxRequirements.MaxDiskBytes is deliberately absent from this function: it may only TIGHTEN the
        // operator's node-wide ceiling, so a backend that ignores it is no worse off than one that honours it, and
        // every backend satisfies it vacuously. Rejecting a candidate over it would refuse a sandbox for asking to be
        // smaller.
        return null;
    }

    private static TRole Resolve<TRole>(IServiceProvider services,
        SandboxRequirements requirements,
        string? constraint,
        string constraintKey)
        where TRole : class, ISandboxRuntimeProvider
    {
        if (constraint is not null && !Array.Exists(ByAscendingPrivilege, backend => backend.Matches(constraint)))
        {
            throw new InvalidOperationException(
                $"Unknown sandbox provider '{constraint}' configured at '{constraintKey}'. Known backends: "
                + string.Join(", ", ByAscendingPrivilege.Select(static backend => backend.Name)) + ".");
        }

        var rejected = new List<string>(ByAscendingPrivilege.Length);
        var candidates = new List<string>(ByAscendingPrivilege.Length);

        foreach (var backend in ByAscendingPrivilege)
        {
            if (constraint is not null && !backend.Matches(constraint))
            {
                continue;
            }

            var provider = backend.Locate(services);
            if (provider is null)
            {
                // Not registered on this node — AddNodeContainerSandbox is a module of its own, so the container
                // backend simply is not there when it was never added. Recorded as rejected rather than skipped
                // silently, because "docker was never registered" and "docker cannot serve this workload" are
                // different diagnoses, and the log line is now how a reader tells them apart.
                rejected.Add($"{backend.Name}: not registered");
                continue;
            }

            candidates.Add(backend.Name);
            var unmet = FindUnmetAxis(requirements, backend.Toolchain, () => provider.Capabilities);
            if (unmet is not null)
            {
                rejected.Add($"{backend.Name}: cannot honour {unmet}");
                continue;
            }

            if (provider is not TRole role)
            {
                rejected.Add($"{backend.Name}: does not serve the {typeof(TRole).Name} role");
                continue;
            }

            LogResolution(services, requirements, constraint, constraintKey, candidates, rejected, backend.Name);
            return role;
        }

        throw new SandboxCapabilityNotSupportedException(
            $"No registered sandbox backend can serve the '{requirements.Workload}' workload. It declares "
            + $"toolchain={requirements.Toolchain}, isolation floor={requirements.IsolationFloor}, "
            + $"network floor={requirements.NetworkFloor}, persistence={requirements.Persistence}. "
            + (constraint is null
                ? $"Rejected: {FormatRejections(rejected)}."
                : $"'{constraintKey}' constrains the candidate set to '{constraint}', and {FormatRejections(rejected)}. "
                  + "Clear that key, or set it to a backend that can serve this workload."));
    }

    private static string FormatRejections(List<string> rejected)
    {
        return rejected.Count == 0 ? "no backend is registered" : string.Join("; ", rejected);
    }

    private static void LogResolution(IServiceProvider services,
        SandboxRequirements requirements,
        string? constraint,
        string constraintKey,
        List<string> candidates,
        List<string> rejected,
        string winner)
    {
        // Once per resolution, and each role resolves once per process because the factories are singletons. This log
        // line is not decoration: ADR 0007 accepts that a consumer can no longer tell from its own file which backend
        // it got, and that trade is only worth making if the resolution is recorded. Information, not Debug, for the
        // same reason.
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger(typeof(SandboxProviderSelector).FullName!);
        logger?.LogInformation(
            "Sandbox substrate resolved for '{Workload}': backend '{Winner}' (toolchain={Toolchain}, isolation floor={IsolationFloor}, network floor={NetworkFloor}, persistence={Persistence}). Constraint: {Constraint}. Candidates considered: {Candidates}. Rejected: {Rejected}.",
            requirements.Workload,
            winner,
            requirements.Toolchain,
            requirements.IsolationFloor,
            requirements.NetworkFloor,
            requirements.Persistence,
            constraint is null ? "none" : $"{constraintKey}={constraint}",
            candidates.Count == 0 ? "none" : string.Join(", ", candidates),
            rejected.Count == 0 ? "none" : string.Join("; ", rejected));
    }

    // An unset provider leaves the candidate set unconstrained, which under minimal-satisfying resolution lands on the
    // deterministic fake — exactly where the old "unset means fake" special case landed, now as a consequence of the
    // ranking rather than as a rule of its own. This is the safe non-Production path; in Production the SandboxOptions
    // startup validation rejects an unset provider before anything resolves the selector, so a stripped config can
    // never reach here and silently grant an execution-capable backend.
    private static string? ReadAgentConstraint(IServiceProvider services)
    {
        return Normalize(services.GetRequiredService<IOptions<SandboxOptions>>().Value.Provider);
    }

    private static string? Normalize(string? provider)
    {
        return string.IsNullOrWhiteSpace(provider) ? null : provider.Trim();
    }

    /// <summary>
    ///     One registered backend as the selector sees it: its stable name, the toolchain it supplies, and how to reach
    ///     its DI singleton. The toolchain is stated here rather than read from
    ///     <see cref="ISandboxRuntimeProvider.Capabilities" /> so that resolving a host-toolchain workload never has to
    ///     probe a backend it is about to reject on that axis; <c>SandboxSubstrateSelectionArchitectureTests</c>
    ///     asserts the two never drift.
    /// </summary>
    private sealed record SandboxBackend(string Name,
        SandboxToolchainSource Toolchain,
        Func<IServiceProvider, ISandboxRuntimeProvider?> Locate)
    {
        public bool Matches(string providerName)
        {
            return string.Equals(Name, providerName, StringComparison.OrdinalIgnoreCase);
        }
    }
}
