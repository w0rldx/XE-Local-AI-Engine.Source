namespace XE_Local_AI_Engine.Client.Services.Development;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;

public interface IDevelopmentCapabilityService
{
    Task<DevelopmentCapability> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>
///     Reports Development Mode's availability, and the state of the runtime it will actually execute on.
/// </summary>
/// <remarks>
///     The container-runtime preflight is reported ONLY when the resolved <see cref="IDevelopmentSandboxRuntimeProvider" />
///     really is the container provider: reporting it unconditionally tells an operator that a node without a Docker
///     daemon cannot run Development Mode while it runs perfectly well on the supervised process sandbox, and an
///     over-reported dependency is a false blocker on a working feature. Registered whether or not Development Mode is
///     on, because the capability endpoint answers the disabled state.
/// </remarks>
internal sealed class DevelopmentCapabilityService : IDevelopmentCapabilityService
{
    internal const string NoHomeDirectoryReason =
        "this account has no home directory (HOME is unset), so the Sandboxed trust tier cannot tell a server's package tree from the operator's credential stores and refuses every connection. "
        + "Run the engine as an account with a home directory, or move each server to the Privileged host tier deliberately.";

    private readonly IOptions<DevelopmentOptions> _options;
    private readonly INodeRuntimeSettings _runtimeSettings;
    private readonly IOptions<SandboxOptions> _agentSandboxOptions;
    private readonly IOptions<DevelopmentSandboxOptions> _developmentSandboxOptions;
    private readonly IDevelopmentSandboxRuntimeProvider _sandboxRuntimeProvider;
    private readonly IAgentSandboxRuntimeProvider _agentSandboxRuntimeProvider;
    private readonly IWorkSessionSandboxRuntimeProvider _workSessionSandboxRuntimeProvider;
    private readonly ISandboxContainmentProbe _containmentProbe;
    private readonly IDockerDaemonPreflightService _dockerDaemonPreflight;
    private readonly Func<string> _homeDirectory;

    public DevelopmentCapabilityService(IOptions<DevelopmentOptions> options,
        IOptions<SandboxOptions> agentSandboxOptions,
        IOptions<DevelopmentSandboxOptions> developmentSandboxOptions,
        IDevelopmentSandboxRuntimeProvider sandboxRuntimeProvider,
        IAgentSandboxRuntimeProvider agentSandboxRuntimeProvider,
        IWorkSessionSandboxRuntimeProvider workSessionSandboxRuntimeProvider,
        ISandboxContainmentProbe containmentProbe,
        IDockerDaemonPreflightService dockerDaemonPreflight,
        INodeRuntimeSettings runtimeSettings)
        : this(options,
            agentSandboxOptions,
            developmentSandboxOptions,
            sandboxRuntimeProvider,
            agentSandboxRuntimeProvider,
            workSessionSandboxRuntimeProvider,
            containmentProbe,
            dockerDaemonPreflight,
            runtimeSettings,
            static () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
    {
    }

    /// <summary>Test seam: <paramref name="homeDirectory" /> stands in for the account's profile directory.</summary>
    internal DevelopmentCapabilityService(IOptions<DevelopmentOptions> options,
        IOptions<SandboxOptions> agentSandboxOptions,
        IOptions<DevelopmentSandboxOptions> developmentSandboxOptions,
        IDevelopmentSandboxRuntimeProvider sandboxRuntimeProvider,
        IAgentSandboxRuntimeProvider agentSandboxRuntimeProvider,
        IWorkSessionSandboxRuntimeProvider workSessionSandboxRuntimeProvider,
        ISandboxContainmentProbe containmentProbe,
        IDockerDaemonPreflightService dockerDaemonPreflight,
        INodeRuntimeSettings runtimeSettings,
        Func<string> homeDirectory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(agentSandboxOptions);
        ArgumentNullException.ThrowIfNull(developmentSandboxOptions);
        ArgumentNullException.ThrowIfNull(sandboxRuntimeProvider);
        ArgumentNullException.ThrowIfNull(agentSandboxRuntimeProvider);
        ArgumentNullException.ThrowIfNull(workSessionSandboxRuntimeProvider);
        ArgumentNullException.ThrowIfNull(containmentProbe);
        ArgumentNullException.ThrowIfNull(dockerDaemonPreflight);
        ArgumentNullException.ThrowIfNull(homeDirectory);
        _options = options;
        _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
        _agentSandboxOptions = agentSandboxOptions;
        _developmentSandboxOptions = developmentSandboxOptions;
        _sandboxRuntimeProvider = sandboxRuntimeProvider;
        _agentSandboxRuntimeProvider = agentSandboxRuntimeProvider;
        _workSessionSandboxRuntimeProvider = workSessionSandboxRuntimeProvider;
        _containmentProbe = containmentProbe;
        _dockerDaemonPreflight = dockerDaemonPreflight;
        _homeDirectory = homeDirectory;
    }

    public async Task<DevelopmentCapability> GetAsync(CancellationToken cancellationToken = default)
    {
        var providerName = _sandboxRuntimeProvider.ProviderName;
        var profile = await _runtimeSettings.GetSandboxSecurityProfileAsync(cancellationToken);
        var isolation = BuildIsolation(profile);
        var preflight = string.Equals(providerName, DockerSandboxRuntimeProvider.Name, StringComparison.Ordinal)
            ? await _dockerDaemonPreflight.InspectAsync(cancellationToken)
            : null;

        return new DevelopmentCapability
        {
            // The options carry the startup value, which decided registration; the live switch can only close what that opened.
            Enabled = _options.Value.Enabled && await _runtimeSettings.GetDevelopmentEnabledAsync(cancellationToken),
            SandboxProvider = providerName,
            ContainerRuntime = preflight,
            Isolation = isolation,
            SandboxSecurityProfile = profile,
            // What `high` ADDS, whatever is in effect: runs under low and not under high, so a role a config key, its own isolation floor or a
            // withheld boundary refuses today is not blamed on the profile and the chooser does not recommend `low` for nothing.
            HighProfileRefusals =
            [
                .. isolation.Where(static row => RunsUnder(row, SandboxSecurityProfile.Low) && !RunsUnder(row, SandboxSecurityProfile.High))
                            .Select(static row => row.Role)
            ]
        };
    }

    // Whether a create for the row's role would get a sandbox here under `profile`: the floor and a withheld boundary refuse under every
    // profile (the selector's rule and the mcp-stdio caveat), and the profile rule itself is the one the create sites enforce.
    private static bool RunsUnder(DevelopmentIsolationRole row, SandboxSecurityProfile profile)
    {
        return row.FilesystemIsolationWithheldReason is null
               && SandboxProviderSelector.ServesIsolationFloor(row.Requirements, row.Provider.Capabilities)
               && !SandboxSecurityProfilePolicy.Refuses(row.Requirements, row.Provider.Capabilities, profile, row.NodeRequiresEgressDenial);
    }

    // Reaches no daemon: the role providers are DI singletons and the container preflight above is the one call that talks to anything (it caches its attestation).
    // The containment measurement is a process-lifetime Lazy, so the first caller pays once; the probe is bounded and best-effort by contract, never a failure.
    private List<DevelopmentIsolationRole> BuildIsolation(SandboxSecurityProfile profile)
    {
        var containment = _containmentProbe.Containment;
        // Each role reads the switch of the section that CONSTRAINS it, which is the same split the provider keys
        // already have: Development Mode has its own, and AgentHome / run_python / work sessions share AgentHome's.
        var agentRequiresDenial = _agentSandboxOptions.Value.RequireEgressDenial;
        var developmentRequiresDenial = _developmentSandboxOptions.Value.RequireEgressDenial;

        return
        [
            Row("agent-home", SandboxWorkloads.AgentHome, _agentSandboxRuntimeProvider, containment, agentRequiresDenial, profile),
            // run_python shares AgentHome's provider instance (ComputeToolGateway injects IAgentSandboxRuntimeProvider) and is still its own row: the ONE workload declaring
            // SandboxIsolationMode.Filesystem, so folding them reports one role's boundary for the other. Reported whether or not Compute:Enabled is set: it answers what a role WOULD get here.
            Row("run_python", SandboxWorkloads.RunPython, _agentSandboxRuntimeProvider, containment, agentRequiresDenial, profile),
            // A Sandboxed stdio MCP server: served by the agent-role provider instance, its own row for run_python's reason, and the row an operator reads BEFORE registering a server — on a host that
            // cannot isolate, every Sandboxed registration refuses to connect. A PrivilegedHost server has no row: it declares nothing, an explicit per-server host grant.
            Row("mcp-stdio", SandboxWorkloads.McpStdio, _agentSandboxRuntimeProvider, containment, agentRequiresDenial, profile, HomeDirectoryCaveat()),
            // Either Development declaration works: DevelopmentModeImageToolchain is DevelopmentModeHostToolchain `with` a different name and toolchain source, and this projection reads only the
            // isolation floor, None on both. The host-toolchain constant avoids re-deriving SandboxProviderSelector.ResolveDevelopment's predicate; the resolved PROVIDER comes from the instance.
            Row("development", SandboxWorkloads.DevelopmentModeHostToolchain, _sandboxRuntimeProvider, containment, developmentRequiresDenial, profile),
            Row("work-session", SandboxWorkloads.WorkSession, _workSessionSandboxRuntimeProvider, containment, agentRequiresDenial, profile)
        ];
    }

    /// <summary>
    ///     The one fact about the <c>mcp-stdio</c> row that is not a property of the sandbox mechanism.
    /// </summary>
    /// <remarks>
    ///     The tier's sensitive-host-root denylist is derived from the account's home directory, so a host that cannot
    ///     name one refuses every Sandboxed connection. The isolation projection reports what the backend serves and
    ///     knows nothing about that, so it is stated here rather than threaded through a projection it does not belong to.
    /// </remarks>
    private string? HomeDirectoryCaveat()
    {
        return string.IsNullOrWhiteSpace(_homeDirectory()) ? NoHomeDirectoryReason : null;
    }

    private static DevelopmentIsolationRole Row(string role,
        SandboxRequirements requirements,
        ISandboxRuntimeProvider provider,
        SandboxContainment containment,
        bool nodeRequiresEgressDenial,
        SandboxSecurityProfile profile,
        string? filesystemIsolationWithheldReason = null)
    {
        return new DevelopmentIsolationRole
        {
            Role = role,
            Requirements = requirements,
            Provider = provider,
            Containment = containment,
            NodeRequiresEgressDenial = nodeRequiresEgressDenial,
            SandboxSecurityProfile = profile,
            FilesystemIsolationWithheldReason = filesystemIsolationWithheldReason
        };
    }
}
