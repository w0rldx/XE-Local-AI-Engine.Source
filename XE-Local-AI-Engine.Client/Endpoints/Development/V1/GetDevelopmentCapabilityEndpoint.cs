namespace XE_Local_AI_Engine.Client.Endpoints.Development.V1;

using FastEndpoints;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Development.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;

// Constructor injection is safe only because the IDevelopmentEndpoint marker keeps these endpoints out of FastEndpoints discovery (EndpointDiscoveryOptions.Filter) when Development:Enabled is false:
// discovery activates every endpoint at startup, AddNodeDevelopment only when the feature is on. GetDevelopmentCapabilityEndpoint must stay reachable with it off, so it carries no marker.

/// <summary>
///     Reports Development Mode's availability, and the state of the runtime it will actually execute on.
/// </summary>
/// <remarks>
///     The container-runtime block is reported ONLY when the resolved <see cref="IDevelopmentSandboxRuntimeProvider" />
///     really is the container provider: reporting it unconditionally tells an operator that a node without a Docker
///     daemon cannot run Development Mode while it runs perfectly well on the supervised process sandbox, and an
///     over-reported dependency is a false blocker on a working feature. The one endpoint in this file without the
///     <c>IDevelopmentEndpoint</c> marker, so it stays registered with Development Mode switched off.
/// </remarks>
public sealed class GetDevelopmentCapabilityEndpoint : EndpointWithoutRequest<DevelopmentCapabilityResponse>
{
    private readonly IOptions<DevelopmentOptions> _options;
    private readonly IOptions<SandboxOptions> _agentSandboxOptions;
    private readonly IOptions<DevelopmentSandboxOptions> _developmentSandboxOptions;
    private readonly IDevelopmentSandboxRuntimeProvider _sandboxRuntimeProvider;
    private readonly IAgentSandboxRuntimeProvider _agentSandboxRuntimeProvider;
    private readonly IWorkSessionSandboxRuntimeProvider _workSessionSandboxRuntimeProvider;
    private readonly ISandboxContainmentProbe _containmentProbe;
    private readonly IDockerDaemonPreflightService _dockerDaemonPreflight;

    public GetDevelopmentCapabilityEndpoint(IOptions<DevelopmentOptions> options,
        IOptions<SandboxOptions> agentSandboxOptions,
        IOptions<DevelopmentSandboxOptions> developmentSandboxOptions,
        IDevelopmentSandboxRuntimeProvider sandboxRuntimeProvider,
        IAgentSandboxRuntimeProvider agentSandboxRuntimeProvider,
        IWorkSessionSandboxRuntimeProvider workSessionSandboxRuntimeProvider,
        ISandboxContainmentProbe containmentProbe,
        IDockerDaemonPreflightService dockerDaemonPreflight)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(agentSandboxOptions);
        ArgumentNullException.ThrowIfNull(developmentSandboxOptions);
        ArgumentNullException.ThrowIfNull(sandboxRuntimeProvider);
        ArgumentNullException.ThrowIfNull(agentSandboxRuntimeProvider);
        ArgumentNullException.ThrowIfNull(workSessionSandboxRuntimeProvider);
        ArgumentNullException.ThrowIfNull(containmentProbe);
        ArgumentNullException.ThrowIfNull(dockerDaemonPreflight);
        _options = options;
        _agentSandboxOptions = agentSandboxOptions;
        _developmentSandboxOptions = developmentSandboxOptions;
        _sandboxRuntimeProvider = sandboxRuntimeProvider;
        _agentSandboxRuntimeProvider = agentSandboxRuntimeProvider;
        _workSessionSandboxRuntimeProvider = workSessionSandboxRuntimeProvider;
        _containmentProbe = containmentProbe;
        _dockerDaemonPreflight = dockerDaemonPreflight;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Development.Capability);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var enabled = _options.Value.Enabled;
        var providerName = _sandboxRuntimeProvider.ProviderName;
        var isolation = BuildIsolationSummary();
        if (!string.Equals(providerName, DockerSandboxRuntimeProvider.Name, StringComparison.Ordinal))
        {
            await Send.OkAsync(new DevelopmentCapabilityResponse
            {
                Enabled = enabled,
                SandboxProvider = providerName,
                ContainerRuntime = null,
                Isolation = isolation
            }, ct);
            return;
        }

        var preflight = await _dockerDaemonPreflight.InspectAsync(ct);

        await Send.OkAsync(new DevelopmentCapabilityResponse
        {
            Enabled = enabled,
            SandboxProvider = providerName,
            ContainerRuntime = preflight.ToResponse(),
            Isolation = isolation
        }, ct);
    }

    // Reaches no daemon: the role providers are DI singletons and the container preflight above is the one call that talks to anything (it caches its attestation).
    // The containment measurement is a process-lifetime Lazy, so the first caller pays once; the probe is bounded and best-effort by contract, never a failure.
    /// <summary>
    ///     The one fact about the <c>mcp-stdio</c> row that is not a property of the sandbox mechanism.
    /// </summary>
    /// <remarks>
    ///     The tier's sensitive-host-root denylist is derived from the account's home directory, so a host that cannot
    ///     name one refuses every Sandboxed connection. The mapper reports what the backend serves and knows nothing
    ///     about that, so it is stated here rather than threaded through a projection it does not belong to.
    /// </remarks>
    private static SandboxIsolationSummaryResponse WithHomeDirectoryCaveat(SandboxIsolationSummaryResponse summary)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))
        {
            return summary;
        }

        return summary with
        {
            FilesystemIsolation = false,
            FilesystemIsolationUnavailableReason =
            "this account has no home directory (HOME is unset), so the Sandboxed trust tier cannot tell a server's package tree from the operator's credential stores and refuses every connection. "
            + "Run the engine as an account with a home directory, or move each server to the Privileged host tier deliberately."
        };
    }

    private IReadOnlyList<SandboxIsolationSummaryResponse> BuildIsolationSummary()
    {
        var containment = _containmentProbe.Containment;
        // Each role reads the switch of the section that CONSTRAINS it, which is the same split the provider keys
        // already have: Development Mode has its own, and AgentHome / run_python / work sessions share AgentHome's.
        var agentRequiresDenial = _agentSandboxOptions.Value.RequireEgressDenial;
        var developmentRequiresDenial = _developmentSandboxOptions.Value.RequireEgressDenial;

        return
        [
            DevelopmentContractMapper.ToIsolationSummary("agent-home", SandboxWorkloads.AgentHome, _agentSandboxRuntimeProvider, containment, agentRequiresDenial),
            // run_python shares AgentHome's provider instance (ComputeToolGateway injects IAgentSandboxRuntimeProvider) and is still its own row: the ONE workload declaring
            // SandboxIsolationMode.Filesystem, so folding them reports one role's boundary for the other. Reported whether or not Compute:Enabled is set: it answers what a role WOULD get here.
            DevelopmentContractMapper.ToIsolationSummary("run_python", SandboxWorkloads.RunPython, _agentSandboxRuntimeProvider, containment, agentRequiresDenial),
            // A Sandboxed stdio MCP server: served by the agent-role provider instance, its own row for run_python's reason, and the row an operator reads BEFORE registering a server — on a host that
            // cannot isolate, every Sandboxed registration refuses to connect. A PrivilegedHost server has no row: it declares nothing, an explicit per-server host grant.
            WithHomeDirectoryCaveat(DevelopmentContractMapper.ToIsolationSummary("mcp-stdio", SandboxWorkloads.McpStdio, _agentSandboxRuntimeProvider, containment, agentRequiresDenial)),
            // Either Development declaration works: DevelopmentModeImageToolchain is DevelopmentModeHostToolchain `with` a different name and toolchain source, and this projection reads only the
            // isolation floor, None on both. The host-toolchain constant avoids re-deriving SandboxProviderSelector.ResolveDevelopment's predicate; the resolved PROVIDER comes from the instance.
            DevelopmentContractMapper.ToIsolationSummary("development", SandboxWorkloads.DevelopmentModeHostToolchain, _sandboxRuntimeProvider, containment, developmentRequiresDenial),
            DevelopmentContractMapper.ToIsolationSummary("work-session", SandboxWorkloads.WorkSession, _workSessionSandboxRuntimeProvider, containment, agentRequiresDenial)
        ];
    }
}
