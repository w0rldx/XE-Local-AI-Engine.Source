namespace XE_Local_AI_Engine.Client.Services.Sandbox;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Fake;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;

/// <summary>
///     Picks the backend serving a workload's <see cref="SandboxRequirements" /> — ADR 0007 Decision 2: a consumer declares what it
///     needs, this resolves a backend honouring the WHOLE declaration, and when none can it fails closed. No fallback, no downgrade.
/// </summary>
/// <remarks>
///     Resolution is MINIMAL-SATISFYING, not most-capable-wins: among the backends meeting every declared axis the one with the smallest
///     additional privilege footprint wins (<see cref="ByAscendingPrivilege" /> carries the ranking and its reasoning), which is the first
///     of ADR 0007 Decision 4's three mechanisms replacing the compile-time guard. Each role is a singleton factory, so two roles resolving
///     the same backend share ONE instance — a correctness requirement: <see cref="ProcessSandboxRuntimeProvider" /> allocates its jail
///     root per instance, and a second would answer Coder "no such sandbox".
/// </remarks>
internal static class SandboxProviderSelector
{
    /// <summary>The operator key that constrains the AgentHome, Coder and work-session candidate set.</summary>
    private const string AgentConstraintKey = SandboxOptions.SectionName + ":Provider";

    /// <summary>The operator key that constrains the Development Mode candidate set.</summary>
    private const string DevelopmentConstraintKey = DevelopmentSandboxOptions.SectionName + ":Provider";

    /// <summary>
    ///     Every backend this engine knows, ordered by ASCENDING additional privilege; first match wins, so the order IS the resolution
    ///     rule, code-owned rather than emergent from a <c>switch</c>.
    /// </summary>
    /// <remarks>
    ///     <c>fake</c> executes nothing, so a deterministic no-op is strictly less than any execution. <c>process</c> is a supervised
    ///     child in a working-directory jail with whatever of setsid, systemd-run, unshare and bwrap the host delivers, adding nothing to
    ///     the trusted computing base: the engine's own user, no daemon. <c>docker</c> is last on the DAEMON axis, not isolation strength —
    ///     a socket that is root-equivalent on Linux is added privilege even when the container is the stronger boundary, which is why
    ///     minimal-satisfying and most-capable differ. Insert a fourth with reasoning, never by capability count.
    /// </remarks>
    private static readonly SandboxBackend[] ByAscendingPrivilege =
    [
        new()
        {
            Name = FakeSandboxRuntimeProvider.Name,
            Toolchain = SandboxToolchainSource.HostToolchain,
            Locate = static services => services.GetService<FakeSandboxRuntimeProvider>()
        },
        new()
        {
            Name = ProcessSandboxRuntimeProvider.Name,
            Toolchain = SandboxToolchainSource.HostToolchain,
            Locate = static services => services.GetService<ProcessSandboxRuntimeProvider>()
        },
        new()
        {
            Name = DockerSandboxRuntimeProvider.Name,
            Toolchain = SandboxToolchainSource.EngineApprovedImage,
            Locate = static services => services.GetService<DockerSandboxRuntimeProvider>()
        }
    ];

    /// <summary>
    ///     The ranking, projected for the architecture test: backend name and the toolchain it supplies, in the order resolution walks
    ///     them.
    /// </summary>
    /// <remarks>
    ///     Exposed because the guarantee that used to be an absent <c>implements</c> clause is now an enumeration, and an enumeration the
    ///     test cannot read is not a guarantee.
    /// </remarks>
    internal static IReadOnlyList<(string Name, SandboxToolchainSource Toolchain)> BackendRanking { get; } =
        [.. ByAscendingPrivilege.Select(static backend => (backend.Name, backend.Toolchain))];

    /// <summary>
    ///     Resolves the AgentHome/Coder sandbox for <see cref="SandboxWorkloads.AgentHome" />, constrained by
    ///     <c>AgentHome:Sandbox:Provider</c>.
    /// </summary>
    /// <remarks>
    ///     It cannot return a container backend: the declaration names <see cref="SandboxToolchainSource.HostToolchain" />, and a container
    ///     backend supplies only an image.
    /// </remarks>
    public static IAgentSandboxRuntimeProvider ResolveAgent(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return Resolve<IAgentSandboxRuntimeProvider>(services,
            SandboxWorkloads.AgentHome,
            ReadAgentConstraint(services),
            AgentConstraintKey);
    }

    /// <summary>Resolves the Development Mode sandbox.</summary>
    /// <remarks>
    ///     It is the one workload whose toolchain need is a property of the NODE, so the declaration is
    ///     <see cref="SandboxWorkloads.DevelopmentModeImageToolchain" /> when the node names a container image or
    ///     <c>Development:Sandbox:Provider</c> names an image-backed backend (that key always meant "run Development Mode in a container"),
    ///     and <c>DevelopmentModeHostToolchain</c> otherwise. An explicit Development key always constrains; unset it inherits the
    ///     AgentHome key, but ONLY while the declaration is host-toolchain. A set key is never silently reinterpreted.
    /// </remarks>
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

    /// <summary>Resolves the work-session sandbox for <see cref="SandboxWorkloads.WorkSession" />.</summary>
    /// <remarks>
    ///     There is no <c>WorkSessions:Sandbox:Provider</c> key — nothing in v1 executes inside this jail, and a setting for a role with no
    ///     consumer is one more thing an operator can get wrong for no effect — so the AgentHome key constrains it, which is the backend a
    ///     session tool would land on. Give it its own key when a session tool needs one.
    /// </remarks>
    public static IWorkSessionSandboxRuntimeProvider ResolveWorkSession(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return Resolve<IWorkSessionSandboxRuntimeProvider>(services,
            SandboxWorkloads.WorkSession,
            ReadAgentConstraint(services),
            AgentConstraintKey);
    }

    /// <summary>
    ///     The whole of the axis vocabulary in one pure function: the first requirement a backend supplying
    ///     <paramref name="backendToolchain" /> and advertising <paramref name="capabilities" /> cannot honour, or
    ///     <see langword="null" /> when it honours all of them.
    /// </summary>
    /// <remarks>
    ///     Internal so the architecture test can enumerate declarations against fixed capability sets and fail deterministically offline
    ///     rather than against whatever this host's probe measures. <paramref name="capabilities" /> is a delegate on purpose: reading
    ///     <c>ProcessSandboxRuntimeProvider.Capabilities</c> runs the containment probe, which launches real children, and the axes
    ///     AgentHome and work sessions declare need none of it — a resolution that cost nothing must not start costing a probe.
    /// </remarks>
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

        // The FLOOR is the property, so it is checked against SupportsHostFilesystemBoundary, never SupportsFilesystemIsolation: that one
        // names a mechanism's create-request contract, which run_python asks for per call and gating the floor on would refuse a container.
        if (requirements.IsolationFloor == SandboxIsolationMode.Filesystem
            && !capabilities().HasFlag(SandboxProviderCapabilities.SupportsHostFilesystemBoundary))
        {
            return $"isolation floor ({requirements.IsolationFloor})";
        }

        // An isolated request carries its own empty network namespace (bwrap's --unshare-net, positively controlled by the probe with a
        // loopback connect), so the separate egress mechanism is off that path and gating on it would refuse a host that isolates fine.
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

        // SandboxRequirements.MaxDiskBytes is deliberately absent: it may only TIGHTEN the operator's node-wide ceiling, so every backend
        // satisfies it vacuously and rejecting a candidate over it would refuse a sandbox for asking to be smaller.
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
            throw new InvalidOperationException($"Unknown sandbox provider '{constraint}' configured at '{constraintKey}'. Known backends: "
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
                // Not registered on this node: AddNodeContainerSandbox is a module of its own. Recorded as rejected rather than skipped,
                // because "never registered" and "cannot serve this workload" are different diagnoses the log line has to tell apart.
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

        throw new SandboxCapabilityNotSupportedException($"No registered sandbox backend can serve the '{requirements.Workload}' workload. It declares "
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
        // Once per resolution, and each role resolves once per process. Not decoration: ADR 0007 accepts that a consumer can no longer
        // tell from its own file which backend it got, and that trade is only worth making if the resolution is recorded at Information.
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

    // An unset provider leaves the candidate set unconstrained, which under minimal-satisfying resolution lands on the deterministic fake
    // — the safe non-Production path. In Production, startup validation rejects an unset provider before the selector is ever reached.
    private static string? ReadAgentConstraint(IServiceProvider services)
    {
        return Normalize(services.GetRequiredService<IOptions<SandboxOptions>>().Value.Provider);
    }

    private static string? Normalize(string? provider)
    {
        return string.IsNullOrWhiteSpace(provider) ? null : provider.Trim();
    }

    /// <summary>
    ///     One registered backend as the selector sees it: its stable name, the toolchain it supplies, and how to reach its DI singleton.
    /// </summary>
    /// <remarks>
    ///     The toolchain is stated here rather than read from <see cref="ISandboxRuntimeProvider.Capabilities" /> so that resolving a
    ///     host-toolchain workload never has to probe a backend it is about to reject on that axis;
    ///     <c>SandboxSubstrateSelectionArchitectureTests</c> asserts the two never drift.
    /// </remarks>
    private sealed record SandboxBackend
    {
        public required string Name { get; init; }

        public required SandboxToolchainSource Toolchain { get; init; }

        public required Func<IServiceProvider, ISandboxRuntimeProvider?> Locate { get; init; }

        public bool Matches(string providerName)
        {
            return string.Equals(Name, providerName, StringComparison.OrdinalIgnoreCase);
        }
    }
}
