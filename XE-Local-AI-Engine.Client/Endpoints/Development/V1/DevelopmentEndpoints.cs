namespace XE_Local_AI_Engine.Client.Endpoints.Development.V1;

using FastEndpoints;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Development.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
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
    IDevelopmentSandboxRuntimeProvider sandboxRuntimeProvider,
    IAgentSandboxRuntimeProvider agentSandboxRuntimeProvider,
    IWorkSessionSandboxRuntimeProvider workSessionSandboxRuntimeProvider,
    ISandboxContainmentProbe containmentProbe,
    IDockerDaemonPreflightService dockerDaemonPreflight) : EndpointWithoutRequest<DevelopmentCapabilityResponse>
{
    private readonly IOptions<DevelopmentOptions> _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly IDevelopmentSandboxRuntimeProvider _sandboxRuntimeProvider = sandboxRuntimeProvider ?? throw new ArgumentNullException(nameof(sandboxRuntimeProvider));
    private readonly IAgentSandboxRuntimeProvider _agentSandboxRuntimeProvider = agentSandboxRuntimeProvider ?? throw new ArgumentNullException(nameof(agentSandboxRuntimeProvider));
    private readonly IWorkSessionSandboxRuntimeProvider _workSessionSandboxRuntimeProvider = workSessionSandboxRuntimeProvider ?? throw new ArgumentNullException(nameof(workSessionSandboxRuntimeProvider));
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
    private IReadOnlyList<SandboxIsolationSummaryResponse> BuildIsolationSummary()
    {
        var containment = _containmentProbe.Containment;

        return
        [
            DevelopmentContractMapper.ToIsolationSummary("agent-home", SandboxWorkloads.AgentHome, _agentSandboxRuntimeProvider, containment),
            // run_python shares AgentHome's provider instance ON PURPOSE (ComputeToolGateway injects
            // IAgentSandboxRuntimeProvider), and is still a row of its own: it is the ONE workload in this engine that
            // declares SandboxIsolationMode.Filesystem, so on a host with a working bubblewrap chain its posture is
            // materially stronger than AgentHome's on the very same backend. Folding the two together would report the
            // stronger role's boundary for the weaker one or the weaker one's for the stronger. Reported whether or not
            // Compute:Enabled is set: this table answers "what would this role be served here", which is exactly the
            // question an operator asks before turning the tool on.
            DevelopmentContractMapper.ToIsolationSummary("run_python", SandboxWorkloads.RunPython, _agentSandboxRuntimeProvider, containment),
            // Either Development declaration is correct here: DevelopmentModeImageToolchain is
            // DevelopmentModeHostToolchain `with` a different workload name and toolchain source, and this projection
            // reads neither — it reads the isolation floor, which is None on both. Passing the host-toolchain constant
            // avoids re-deriving SandboxProviderSelector.ResolveDevelopment's node predicate for an answer that cannot
            // differ; the resolved PROVIDER, which does differ, is already reported from the instance itself.
            DevelopmentContractMapper.ToIsolationSummary("development", SandboxWorkloads.DevelopmentModeHostToolchain, _sandboxRuntimeProvider, containment),
            DevelopmentContractMapper.ToIsolationSummary("work-session", SandboxWorkloads.WorkSession, _workSessionSandboxRuntimeProvider, containment)
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

public sealed class ListDevelopmentRepositoriesEndpoint(IDevelopmentManagementService service)
    : EndpointWithoutRequest<ListDevelopmentRepositoriesResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Get(LocalApiRoutes.Development.Repositories);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var repositories = await _service.ListRepositoriesAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(new ListDevelopmentRepositoriesResponse(repositories.Select(DevelopmentContractMapper.ToResponse).ToArray()), ct)
                  .ConfigureAwait(false);
    }
}

public sealed class RegisterDevelopmentRepositoryEndpoint(IDevelopmentManagementService service)
    : Endpoint<RegisterDevelopmentRepositoryRequest, DevelopmentRepositoryResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Post(LocalApiRoutes.Development.Repositories);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesProblemDetails(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(RegisterDevelopmentRepositoryRequest req, CancellationToken ct)
    {
        try
        {
            var repository = await _service.RegisterRepositoryAsync(req.Alias, req.HostPath, ct).ConfigureAwait(false);
            await Send.OkAsync(repository.ToResponse(), ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (SelectedFolderEndpointSupport.IsHandled(exception))
        {
            await SelectedFolderEndpointSupport.SendAsync(this, Send, exception, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or DevelopmentWorkspaceSecurityException)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}

public sealed class ListDevelopmentTemplatesEndpoint(IDevelopmentTemplateService service)
    : EndpointWithoutRequest<ListDevelopmentTemplatesResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentTemplateService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Get(LocalApiRoutes.Development.Templates);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var templates = await _service.ListTemplatesAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(new ListDevelopmentTemplatesResponse(templates.Select(template => template.ToResponse()).ToArray()), ct)
                  .ConfigureAwait(false);
    }
}

public sealed class RegisterDevelopmentTemplateEndpoint(IDevelopmentTemplateService service)
    : Endpoint<RegisterDevelopmentTemplateRequest, DevelopmentTemplateResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentTemplateService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Post(LocalApiRoutes.Development.Templates);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(RegisterDevelopmentTemplateRequest req, CancellationToken ct)
    {
        try
        {
            var template = await _service.AddTemplateAsync(req.Alias, req.HostPath, ct).ConfigureAwait(false);
            await Send.OkAsync(template.ToResponse(), ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or DevelopmentWorkspaceSecurityException
                                              or DevelopmentTemplateAliasInUseException
                                              or DirectoryNotFoundException)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}

public sealed class RemoveDevelopmentTemplateEndpoint(IDevelopmentTemplateService service)
    : Endpoint<DevelopmentTemplateRequest>, IDevelopmentEndpoint
{
    private readonly IDevelopmentTemplateService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Delete(LocalApiRoutes.Development.TemplateById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DevelopmentTemplateRequest req, CancellationToken ct)
    {
        if (!await _service.RemoveTemplateAsync(req.TemplateId, ct).ConfigureAwait(false))
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.NoContentAsync(ct).ConfigureAwait(false);
    }
}

public sealed class CreateDevelopmentRepositoryFromTemplateEndpoint(IDevelopmentTemplateService service)
    : Endpoint<CreateDevelopmentRepositoryFromTemplateRequest, DevelopmentRepositoryFromTemplateResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentTemplateService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Post(LocalApiRoutes.Development.RepositoriesFromTemplate);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesProblemDetails(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(CreateDevelopmentRepositoryFromTemplateRequest req, CancellationToken ct)
    {
        try
        {
            var result = await _service.CreateFromTemplateAsync(req.TemplateId, req.DestinationPath, req.Alias, req.BaseBranch, ct)
                                       .ConfigureAwait(false);
            await Send.OkAsync(new DevelopmentRepositoryFromTemplateResponse(result.Repository.ToResponse(),
                    result.TemplateAlias,
                    result.TemplateCommit),
                ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (SelectedFolderEndpointSupport.IsHandled(exception))
        {
            await SelectedFolderEndpointSupport.SendAsync(this, Send, exception, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or DevelopmentWorkspaceSecurityException
                                              or DevelopmentTemplateMaterializationException
                                              or DirectoryNotFoundException
                                              or IOException
                                              or UnauthorizedAccessException)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}

public sealed class DetectDevelopmentRepositoryProfileEndpoint(IDevelopmentManagementService service)
    : Endpoint<DevelopmentProfileDetectionRequest, DevelopmentProfileDetectionResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Get(LocalApiRoutes.Development.RepositoryProfileDetection);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesProblemDetails(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(DevelopmentProfileDetectionRequest req, CancellationToken ct)
    {
        try
        {
            var detection = await _service.DetectRepositoryProfileAsync(req.SelectedFolderId, ct).ConfigureAwait(false);
            await Send.OkAsync(new DevelopmentProfileDetectionResponse(detection.ProfileId, detection.BuildTarget, detection.Candidates), ct)
                      .ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (SelectedFolderEndpointSupport.IsHandled(exception))
        {
            await SelectedFolderEndpointSupport.SendAsync(this, Send, exception, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DevelopmentWorkspaceSecurityException or DirectoryNotFoundException)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}

public sealed class ListDevelopmentProjectsEndpoint(IDevelopmentManagementService service)
    : EndpointWithoutRequest<ListDevelopmentProjectsResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Get(LocalApiRoutes.Development.Projects);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var projects = await _service.ListProjectsAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(new ListDevelopmentProjectsResponse(projects.Select(DevelopmentContractMapper.ToResponse).ToArray()), ct).ConfigureAwait(false);
    }
}

public sealed class CreateDevelopmentProjectEndpoint(IDevelopmentManagementService service)
    : Endpoint<CreateDevelopmentProjectRequest, DevelopmentProjectDetailResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Post(LocalApiRoutes.Development.Projects);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesProblemDetails(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(CreateDevelopmentProjectRequest req, CancellationToken ct)
    {
        if (!Enum.TryParse<DevelopmentEgressPolicy>(req.EgressPolicy, ignoreCase: true, out var egressPolicy))
        {
            AddError("The Development egress policy is invalid.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        try
        {
            var result = await _service.CreateProjectAsync(new DevelopmentCreateProjectInput(req.OperationId,
                                               req.SelectedFolderId,
                                               req.Objective,
                                               req.BaseBranch,
                                               req.TaskTitle,
                                               req.Requirements,
                                               req.AcceptanceCriteriaJson,
                                               egressPolicy,
                                               req.CoderModelId,
                                               req.ReviewerModelId,
                                               req.TrustedRepositoryAcknowledged,
                                               req.MaxTokens,
                                               req.MaxDurationSeconds,
                                               req.CommandProfileId,
                                               req.BuildTarget),
                                           ct)
                                       .ConfigureAwait(false);
            await Send.OkAsync(result.ToResponse(), ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (SelectedFolderEndpointSupport.IsHandled(exception))
        {
            await SelectedFolderEndpointSupport.SendAsync(this, Send, exception, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or DevelopmentWorkspaceSecurityException)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}

public sealed class GetDevelopmentProjectEndpoint(IDevelopmentManagementService service)
    : Endpoint<DevelopmentProjectRequest, DevelopmentProjectDetailResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Get(LocalApiRoutes.Development.ProjectById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DevelopmentProjectRequest req, CancellationToken ct)
    {
        try
        {
            await Send.OkAsync((await _service.GetProjectAsync(req.ProjectId, ct).ConfigureAwait(false)).ToResponse(), ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
    }
}

public sealed class GetDevelopmentTaskEndpoint(IDevelopmentManagementService service)
    : Endpoint<DevelopmentTaskRequest, DevelopmentTaskDetailResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Get(LocalApiRoutes.Development.TaskById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DevelopmentTaskRequest req, CancellationToken ct)
    {
        try
        {
            await Send.OkAsync((await _service.GetTaskAsync(req.ProjectId, req.TaskId, ct).ConfigureAwait(false)).ToResponse(), ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
    }
}

public sealed class StartDevelopmentNextActionEndpoint(IDevelopmentManagementService service)
    : Endpoint<DevelopmentActionRequest, DevelopmentNextActionResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Post(LocalApiRoutes.Development.NextAction);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesProblemDetails(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(DevelopmentActionRequest req, CancellationToken ct)
    {
        try
        {
            var result = await _service.StartNextActionAsync(req.ProjectId, req.TaskId, req.OperationId, ct).ConfigureAwait(false);
            await Send.OkAsync(new DevelopmentNextActionResponse(result.Action,
                    result.ProjectId,
                    result.TaskId,
                    result.AttemptId,
                    result.TaskStatus.ToString(),
                    result.Role?.ToString()),
                ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (SelectedFolderEndpointSupport.IsHandled(exception))
        {
            await SelectedFolderEndpointSupport.SendAsync(this, Send, exception, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DevelopmentInvalidTransitionException
                                              or DevelopmentConcurrencyException
                                              or DevelopmentWorkspaceSecurityException)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(statusCode: StatusCodes.Status409Conflict, cancellation: ct).ConfigureAwait(false);
        }
    }
}

public sealed class CancelDevelopmentAttemptEndpoint(IDevelopmentManagementService service)
    : Endpoint<DevelopmentAttemptRequest>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Post(LocalApiRoutes.Development.CancelAttempt);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(description => description.Accepts<DevelopmentAttemptRequest>());
    }

    public override async Task HandleAsync(DevelopmentAttemptRequest req, CancellationToken ct)
    {
        try
        {
            if (!await _service.CancelAttemptAsync(req.ProjectId, req.TaskId, req.AttemptId, ct).ConfigureAwait(false))
            {
                await Send.NoContentAsync(ct).ConfigureAwait(false);
                return;
            }

            await Send.NoContentAsync(ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
    }
}

public sealed class ListDevelopmentEventsEndpoint(IDevelopmentManagementService service)
    : Endpoint<DevelopmentProjectRequest, ListDevelopmentEventsResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Get(LocalApiRoutes.Development.Events);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DevelopmentProjectRequest req, CancellationToken ct)
    {
        try
        {
            var events = await _service.ListEventsAsync(req.ProjectId, ct).ConfigureAwait(false);
            await Send.OkAsync(new ListDevelopmentEventsResponse(events.Select(DevelopmentContractMapper.ToResponse).ToArray()), ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
    }
}

public sealed class ListDevelopmentArtifactsEndpoint(IDevelopmentManagementService service)
    : Endpoint<DevelopmentTaskRequest, ListDevelopmentArtifactsResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Get(LocalApiRoutes.Development.TaskArtifacts);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DevelopmentTaskRequest req, CancellationToken ct)
    {
        try
        {
            var artifacts = await _service.ListArtifactsAsync(req.ProjectId, req.TaskId, ct).ConfigureAwait(false);
            await Send.OkAsync(new ListDevelopmentArtifactsResponse(artifacts.Select(DevelopmentContractMapper.ToResponse).ToArray()), ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
    }
}

public sealed class GetDevelopmentArtifactEndpoint(IDevelopmentManagementService service)
    : Endpoint<DevelopmentArtifactRequest, DevelopmentArtifactContentResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Get(LocalApiRoutes.Development.ArtifactById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DevelopmentArtifactRequest req, CancellationToken ct)
    {
        try
        {
            var artifact = await _service.ReadArtifactAsync(req.ProjectId, req.TaskId, req.ArtifactId, ct).ConfigureAwait(false);
            await Send.OkAsync(new DevelopmentArtifactContentResponse(artifact.Artifact.ToResponse(), artifact.Content), ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
        catch (DevelopmentInvalidTransitionException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(statusCode: StatusCodes.Status409Conflict, cancellation: ct).ConfigureAwait(false);
        }
    }
}

public sealed class PreviewDevelopmentPatchEndpoint(IDevelopmentManagementService service)
    : Endpoint<DevelopmentActionRequest, DevelopmentPatchPreviewResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Post(LocalApiRoutes.Development.PatchPreview);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesProblemDetails(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(DevelopmentActionRequest req, CancellationToken ct)
    {
        try
        {
            var preview = await _service.PreviewAsync(req.ProjectId, req.TaskId, ct).ConfigureAwait(false);
            await Send.OkAsync(new DevelopmentPatchPreviewResponse(preview.SubjectHash,
                    preview.PatchHash,
                    preview.ManifestHash,
                    preview.ExpectedResultHash,
                    preview.Patch,
                    preview.ChangedFiles),
                ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (SelectedFolderEndpointSupport.IsHandled(exception))
        {
            await SelectedFolderEndpointSupport.SendAsync(this, Send, exception, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DevelopmentInvalidTransitionException or DevelopmentWorkspaceSecurityException)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(statusCode: StatusCodes.Status409Conflict, cancellation: ct).ConfigureAwait(false);
        }
    }
}

public sealed class ApplyDevelopmentPatchEndpoint(IDevelopmentManagementService service)
    : Endpoint<DevelopmentActionRequest, DevelopmentApplyResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Post(LocalApiRoutes.Development.Apply);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesProblemDetails(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(DevelopmentActionRequest req, CancellationToken ct)
    {
        try
        {
            var result = await _service.ApplyAsync(req.ProjectId, req.TaskId, req.OperationId, ct).ConfigureAwait(false);
            await Send.OkAsync(new DevelopmentApplyResponse(result.OperationId,
                    result.Phase,
                    result.Outcome,
                    result.Status,
                    result.Version,
                    result.Sequence),
                ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (SelectedFolderEndpointSupport.IsHandled(exception))
        {
            await SelectedFolderEndpointSupport.SendAsync(this, Send, exception, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DevelopmentInvalidTransitionException
                                              or DevelopmentConcurrencyException
                                              or DevelopmentWorkspaceSecurityException)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(statusCode: StatusCodes.Status409Conflict, cancellation: ct).ConfigureAwait(false);
        }
    }
}

public sealed class ReconnectDevelopmentRepositoryEndpoint(IDevelopmentManagementService service)
    : Endpoint<ReconnectDevelopmentRepositoryRequest, DevelopmentProjectDetailResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Post(LocalApiRoutes.Development.RepositoryConnection);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesProblemDetails(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(ReconnectDevelopmentRepositoryRequest req, CancellationToken ct)
    {
        try
        {
            var project = await _service.ReconnectRepositoryAsync(req.ProjectId, req.SelectedFolderId, req.ExpectedVersion, ct)
                                        .ConfigureAwait(false);
            await Send.OkAsync(project.ToResponse(), ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (SelectedFolderEndpointSupport.IsHandled(exception))
        {
            await SelectedFolderEndpointSupport.SendAsync(this, Send, exception, ct).ConfigureAwait(false);
        }
        // Reconnect is the one Development endpoint whose request BOTH carries a folder to validate and acts on the
        // project's persisted binding, so it is the only one that has to split the workspace-security family by type:
        // the persisted binding blocking the reconnect is a 409, while the folder the caller just picked being
        // unusable (not a Git root, read-only, network path) is the same 400 it is on register/create.
        catch (Exception exception) when (exception is DevelopmentConcurrencyException
                                              or DevelopmentRepositoryStateConflictException)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(statusCode: StatusCodes.Status409Conflict, cancellation: ct).ConfigureAwait(false);
        }
        catch (DevelopmentWorkspaceSecurityException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}
