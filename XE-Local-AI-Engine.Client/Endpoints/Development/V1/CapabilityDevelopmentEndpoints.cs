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

// Constructor injection here is only safe because the IDevelopmentEndpoint marker keeps these endpoints out of
// FastEndpoints discovery when Development:Enabled is false (see the EndpointDiscoveryOptions.Filter in
// ConfigureServices): FastEndpoints activates every discovered endpoint once at startup, while AddNodeDevelopment
// registers their services only when the feature is on. GetDevelopmentCapabilityEndpoint must stay reachable with the
// feature off and therefore must NOT carry the marker — its dependencies are all registered unconditionally.

/// <summary>
///     Reports Development Mode's availability, and the state of the runtime it will actually execute on.
///     <para>
///         The container-runtime block is reported only when the resolved
///         <see cref="IDevelopmentSandboxRuntimeProvider" /> really is the container provider. Reporting it
///         unconditionally — which is what this endpoint did before per-feature selection (D2) landed — told operators
///         that a node without a Docker daemon could not run Development Mode, while Development Mode was in fact
///         running perfectly well on the supervised process sandbox. An over-reported dependency is not a harmless
///         extra field: it is a false blocker on a working feature.
///     </para>
///     <para>
///         The one endpoint in this file without the <c>IDevelopmentEndpoint</c> marker: it stays registered with
///         Development Mode switched off, which is exactly when an operator needs to be told the feature is off.
///     </para>
/// </summary>
public sealed class GetDevelopmentCapabilityEndpoint(
    IOptions<DevelopmentOptions> options,
    IOptions<SandboxOptions> agentSandboxOptions,
    IOptions<DevelopmentSandboxOptions> developmentSandboxOptions,
    IDevelopmentSandboxRuntimeProvider sandboxRuntimeProvider,
    IAgentSandboxRuntimeProvider agentSandboxRuntimeProvider,
    IWorkSessionSandboxRuntimeProvider workSessionSandboxRuntimeProvider,
    ISandboxContainmentProbe containmentProbe,
    IDockerDaemonPreflightService dockerDaemonPreflight) : EndpointWithoutRequest<DevelopmentCapabilityResponse>
{
    private readonly IOptions<DevelopmentOptions> _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly IOptions<SandboxOptions> _agentSandboxOptions = agentSandboxOptions ?? throw new ArgumentNullException(nameof(agentSandboxOptions));
    private readonly IOptions<DevelopmentSandboxOptions> _developmentSandboxOptions = developmentSandboxOptions ?? throw new ArgumentNullException(nameof(developmentSandboxOptions));
    private readonly IDevelopmentSandboxRuntimeProvider _sandboxRuntimeProvider = sandboxRuntimeProvider ?? throw new ArgumentNullException(nameof(sandboxRuntimeProvider));
    private readonly IAgentSandboxRuntimeProvider _agentSandboxRuntimeProvider = agentSandboxRuntimeProvider ?? throw new ArgumentNullException(nameof(agentSandboxRuntimeProvider));

    private readonly IWorkSessionSandboxRuntimeProvider _workSessionSandboxRuntimeProvider =
        workSessionSandboxRuntimeProvider ?? throw new ArgumentNullException(nameof(workSessionSandboxRuntimeProvider));

    private readonly ISandboxContainmentProbe _containmentProbe = containmentProbe ?? throw new ArgumentNullException(nameof(containmentProbe));
    private readonly IDockerDaemonPreflightService _dockerDaemonPreflight = dockerDaemonPreflight ?? throw new ArgumentNullException(nameof(dockerDaemonPreflight));

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
            await Send.OkAsync(new DevelopmentCapabilityResponse(enabled, providerName, ContainerRuntime: null, isolation), ct).ConfigureAwait(false);
            return;
        }

        var preflight = await _dockerDaemonPreflight.InspectAsync(ct).ConfigureAwait(false);

        await Send.OkAsync(new DevelopmentCapabilityResponse(enabled, providerName, preflight.ToResponse(), isolation), ct).ConfigureAwait(false);
    }

    // Reaches no daemon: the role providers are DI singletons resolved by the selector, and the container preflight
    // above is the one call that talks to anything (it keeps its own cached attestation). The containment measurement
    // is a process-lifetime cache behind a Lazy, so whichever caller touches it first pays for it once and every
    // capability GET after that is free. This endpoint can be that first caller on a node where nothing has read a
    // provider's Capabilities yet; the probe is bounded and best-effort by contract, so the cost is one bounded
    // measurement, never a failure.
    /// <summary>
    ///     The one fact about the <c>mcp-stdio</c> row that is not a property of the sandbox mechanism: the tier's
    ///     sensitive-host-root denylist is derived from the account's home directory, so a host that cannot name one
    ///     refuses every Sandboxed connection. The mapper reports what the backend serves and knows nothing about
    ///     that, so it is stated here rather than threaded through a projection it does not belong to.
    /// </summary>
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
            // run_python shares AgentHome's provider instance ON PURPOSE (ComputeToolGateway injects
            // IAgentSandboxRuntimeProvider), and is still a row of its own: it is the ONE workload in this engine that
            // declares SandboxIsolationMode.Filesystem, so on a host with a working bubblewrap chain its posture is
            // materially stronger than AgentHome's on the very same backend. Folding the two together would report the
            // stronger role's boundary for the weaker one or the weaker one's for the stronger. Reported whether or not
            // Compute:Enabled is set: this table answers "what would this role be served here", which is exactly the
            // question an operator asks before turning the tool on.
            DevelopmentContractMapper.ToIsolationSummary("run_python", SandboxWorkloads.RunPython, _agentSandboxRuntimeProvider, containment, agentRequiresDenial),
            // A Sandboxed stdio MCP server. Also served by the agent-role provider instance, and also a row of its own
            // for run_python's reason: it declares SandboxIsolationMode.Filesystem, so its served posture differs from
            // AgentHome's on the very same backend. It is the row that answers the question an operator has BEFORE
            // registering a server — on a host that cannot isolate, this row says so, and every Sandboxed registration
            // will refuse to connect rather than launching on the host. A PrivilegedHost server has no row here
            // because it declares nothing: it is not a substrate consumer, it is an explicit per-server host grant.
            WithHomeDirectoryCaveat(DevelopmentContractMapper.ToIsolationSummary("mcp-stdio", SandboxWorkloads.McpStdio, _agentSandboxRuntimeProvider, containment, agentRequiresDenial)),
            // Either Development declaration is correct here: DevelopmentModeImageToolchain is
            // DevelopmentModeHostToolchain `with` a different workload name and toolchain source, and this projection
            // reads neither — it reads the isolation floor, which is None on both. Passing the host-toolchain constant
            // avoids re-deriving SandboxProviderSelector.ResolveDevelopment's node predicate for an answer that cannot
            // differ; the resolved PROVIDER, which does differ, is already reported from the instance itself.
            DevelopmentContractMapper.ToIsolationSummary("development", SandboxWorkloads.DevelopmentModeHostToolchain, _sandboxRuntimeProvider, containment, developmentRequiresDenial),
            DevelopmentContractMapper.ToIsolationSummary("work-session", SandboxWorkloads.WorkSession, _workSessionSandboxRuntimeProvider, containment, agentRequiresDenial)
        ];
    }
}

/// <summary>
///     Records the operator's explicit approval of the container runtime currently reachable.
///     <para>
///         Its own endpoint rather than a flag on the capability GET, because pinning a daemon is a decision and a GET
///         must not make decisions: a page refresh, a prefetch or a health check would otherwise silently approve
///         whatever daemon happened to be answering.
///     </para>
/// </summary>
public sealed class ConfirmDevelopmentContainerRuntimeEndpoint(IDockerDaemonPreflightService dockerDaemonPreflight)
    : Endpoint<ConfirmDevelopmentContainerRuntimeRequest, DevelopmentContainerRuntimeResponse>, IDevelopmentEndpoint
{
    private readonly IDockerDaemonPreflightService _dockerDaemonPreflight = dockerDaemonPreflight ?? throw new ArgumentNullException(nameof(dockerDaemonPreflight));

    public override void Configure()
    {
        Post(LocalApiRoutes.Development.ContainerRuntimeConfirmation);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ConfirmDevelopmentContainerRuntimeRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.DaemonId))
        {
            AddError("A container runtime id is required so the confirmation approves the runtime you were shown.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        var preflight = await _dockerDaemonPreflight.ConfirmAsync(req.DaemonId, ct).ConfigureAwait(false);

        await Send.OkAsync(preflight.ToResponse(), ct).ConfigureAwait(false);
    }
}
