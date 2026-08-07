namespace XE_Local_AI_Engine.Client.Endpoints.Development.V1;

using FastEndpoints;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;
using XE_Local_AI_Engine.Client.Services.Workspace;

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
/// </summary>
public sealed class GetDevelopmentCapabilityEndpoint
    : EndpointWithoutRequest<DevelopmentCapabilityResponse>, IDevelopmentEndpoint
{
    public override void Configure()
    {
        Get(LocalApiRoutes.Development.Capability);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var enabled = HttpContext.RequestServices.GetRequiredService<IOptions<DevelopmentOptions>>().Value.Enabled;
        var providerName = HttpContext.RequestServices.GetRequiredService<IDevelopmentSandboxRuntimeProvider>().ProviderName;
        if (!string.Equals(providerName, DockerSandboxRuntimeProvider.Name, StringComparison.Ordinal))
        {
            await Send.OkAsync(new DevelopmentCapabilityResponse(enabled, providerName, ContainerRuntime: null), ct).ConfigureAwait(false);
            return;
        }

        var preflight = await HttpContext.RequestServices.GetRequiredService<IDockerDaemonPreflightService>()
                                         .InspectAsync(ct)
                                         .ConfigureAwait(false);

        await Send.OkAsync(new DevelopmentCapabilityResponse(enabled, providerName, preflight.ToResponse()), ct).ConfigureAwait(false);
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
public sealed class ConfirmDevelopmentContainerRuntimeEndpoint
    : Endpoint<ConfirmDevelopmentContainerRuntimeRequest, DevelopmentContainerRuntimeResponse>, IDevelopmentEndpoint
{
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

        var preflight = await HttpContext.RequestServices.GetRequiredService<IDockerDaemonPreflightService>()
                                         .ConfirmAsync(req.DaemonId, ct)
                                         .ConfigureAwait(false);

        await Send.OkAsync(preflight.ToResponse(), ct).ConfigureAwait(false);
    }
}

public sealed class ListDevelopmentRepositoriesEndpoint
    : EndpointWithoutRequest<ListDevelopmentRepositoriesResponse>, IDevelopmentEndpoint
{
    public override void Configure()
    {
        Get(LocalApiRoutes.Development.Repositories);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var service = HttpContext.RequestServices.GetRequiredService<IDevelopmentManagementService>();
        var repositories = await service.ListRepositoriesAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(new ListDevelopmentRepositoriesResponse(repositories.Select(DevelopmentContractMapper.ToResponse).ToArray()), ct)
                  .ConfigureAwait(false);
    }
}

public sealed class RegisterDevelopmentRepositoryEndpoint
    : Endpoint<RegisterDevelopmentRepositoryRequest, DevelopmentRepositoryResponse>, IDevelopmentEndpoint
{
    public override void Configure()
    {
        Post(LocalApiRoutes.Development.Repositories);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(RegisterDevelopmentRepositoryRequest req, CancellationToken ct)
    {
        try
        {
            var service = HttpContext.RequestServices.GetRequiredService<IDevelopmentManagementService>();
            var repository = await service.RegisterRepositoryAsync(req.Alias, req.HostPath, ct).ConfigureAwait(false);
            await Send.OkAsync(repository.ToResponse(), ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or DevelopmentWorkspaceSecurityException
                                              or SelectedFolderValidationException)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}

public sealed class ListDevelopmentTemplatesEndpoint
    : EndpointWithoutRequest<ListDevelopmentTemplatesResponse>, IDevelopmentEndpoint
{
    public override void Configure()
    {
        Get(LocalApiRoutes.Development.Templates);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var service = HttpContext.RequestServices.GetRequiredService<IDevelopmentTemplateService>();
        var templates = await service.ListTemplatesAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(new ListDevelopmentTemplatesResponse(templates.Select(template => template.ToResponse()).ToArray()), ct)
                  .ConfigureAwait(false);
    }
}

public sealed class RegisterDevelopmentTemplateEndpoint
    : Endpoint<RegisterDevelopmentTemplateRequest, DevelopmentTemplateResponse>, IDevelopmentEndpoint
{
    public override void Configure()
    {
        Post(LocalApiRoutes.Development.Templates);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(RegisterDevelopmentTemplateRequest req, CancellationToken ct)
    {
        try
        {
            var service = HttpContext.RequestServices.GetRequiredService<IDevelopmentTemplateService>();
            var template = await service.AddTemplateAsync(req.Alias, req.HostPath, ct).ConfigureAwait(false);
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

public sealed class RemoveDevelopmentTemplateEndpoint
    : Endpoint<DevelopmentTemplateRequest>, IDevelopmentEndpoint
{
    public override void Configure()
    {
        Delete(LocalApiRoutes.Development.TemplateById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DevelopmentTemplateRequest req, CancellationToken ct)
    {
        var service = HttpContext.RequestServices.GetRequiredService<IDevelopmentTemplateService>();
        if (!await service.RemoveTemplateAsync(req.TemplateId, ct).ConfigureAwait(false))
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.NoContentAsync(ct).ConfigureAwait(false);
    }
}

public sealed class CreateDevelopmentRepositoryFromTemplateEndpoint
    : Endpoint<CreateDevelopmentRepositoryFromTemplateRequest, DevelopmentRepositoryFromTemplateResponse>, IDevelopmentEndpoint
{
    public override void Configure()
    {
        Post(LocalApiRoutes.Development.RepositoriesFromTemplate);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CreateDevelopmentRepositoryFromTemplateRequest req, CancellationToken ct)
    {
        var service = HttpContext.RequestServices.GetRequiredService<IDevelopmentTemplateService>();
        try
        {
            var result = await service.CreateFromTemplateAsync(req.TemplateId, req.DestinationPath, req.Alias, req.BaseBranch, ct)
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
        catch (Exception exception) when (exception is ArgumentException
                                              or DevelopmentWorkspaceSecurityException
                                              or DevelopmentTemplateMaterializationException
                                              or SelectedFolderValidationException
                                              or DirectoryNotFoundException
                                              or IOException
                                              or UnauthorizedAccessException)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}

public sealed class DetectDevelopmentRepositoryProfileEndpoint
    : Endpoint<DevelopmentProfileDetectionRequest, DevelopmentProfileDetectionResponse>, IDevelopmentEndpoint
{
    public override void Configure()
    {
        Get(LocalApiRoutes.Development.RepositoryProfileDetection);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DevelopmentProfileDetectionRequest req, CancellationToken ct)
    {
        var service = HttpContext.RequestServices.GetRequiredService<IDevelopmentManagementService>();
        try
        {
            var detection = await service.DetectRepositoryProfileAsync(req.SelectedFolderId, ct).ConfigureAwait(false);
            await Send.OkAsync(new DevelopmentProfileDetectionResponse(detection.ProfileId, detection.BuildTarget, detection.Candidates), ct)
                      .ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DevelopmentWorkspaceSecurityException
                                              or SelectedFolderValidationException
                                              or DirectoryNotFoundException)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}

public sealed class ListDevelopmentProjectsEndpoint
    : EndpointWithoutRequest<ListDevelopmentProjectsResponse>, IDevelopmentEndpoint
{
    public override void Configure()
    {
        Get(LocalApiRoutes.Development.Projects);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var service = HttpContext.RequestServices.GetRequiredService<IDevelopmentManagementService>();
        var projects = await service.ListProjectsAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(new ListDevelopmentProjectsResponse(projects.Select(DevelopmentContractMapper.ToResponse).ToArray()), ct).ConfigureAwait(false);
    }
}

public sealed class CreateDevelopmentProjectEndpoint
    : Endpoint<CreateDevelopmentProjectRequest, DevelopmentProjectDetailResponse>, IDevelopmentEndpoint
{
    public override void Configure()
    {
        Post(LocalApiRoutes.Development.Projects);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CreateDevelopmentProjectRequest req, CancellationToken ct)
    {
        var service = HttpContext.RequestServices.GetRequiredService<IDevelopmentManagementService>();
        if (!Enum.TryParse<DevelopmentEgressPolicy>(req.EgressPolicy, ignoreCase: true, out var egressPolicy))
        {
            AddError("The Development egress policy is invalid.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        try
        {
            var result = await service.CreateProjectAsync(new DevelopmentCreateProjectInput(req.OperationId,
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
        catch (Exception exception) when (exception is ArgumentException
                                              or DevelopmentWorkspaceSecurityException
                                              or SelectedFolderValidationException)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}

public sealed class GetDevelopmentProjectEndpoint
    : Endpoint<DevelopmentProjectRequest, DevelopmentProjectDetailResponse>, IDevelopmentEndpoint
{
    public override void Configure()
    {
        Get(LocalApiRoutes.Development.ProjectById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DevelopmentProjectRequest req, CancellationToken ct)
    {
        var service = HttpContext.RequestServices.GetRequiredService<IDevelopmentManagementService>();
        try
        {
            await Send.OkAsync((await service.GetProjectAsync(req.ProjectId, ct).ConfigureAwait(false)).ToResponse(), ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
    }
}

public sealed class GetDevelopmentTaskEndpoint
    : Endpoint<DevelopmentTaskRequest, DevelopmentTaskDetailResponse>, IDevelopmentEndpoint
{
    public override void Configure()
    {
        Get(LocalApiRoutes.Development.TaskById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DevelopmentTaskRequest req, CancellationToken ct)
    {
        var service = HttpContext.RequestServices.GetRequiredService<IDevelopmentManagementService>();
        try
        {
            await Send.OkAsync((await service.GetTaskAsync(req.ProjectId, req.TaskId, ct).ConfigureAwait(false)).ToResponse(), ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
    }
}

public sealed class StartDevelopmentNextActionEndpoint
    : Endpoint<DevelopmentActionRequest, DevelopmentNextActionResponse>, IDevelopmentEndpoint
{
    public override void Configure()
    {
        Post(LocalApiRoutes.Development.NextAction);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DevelopmentActionRequest req, CancellationToken ct)
    {
        var service = HttpContext.RequestServices.GetRequiredService<IDevelopmentManagementService>();
        try
        {
            var result = await service.StartNextActionAsync(req.ProjectId, req.TaskId, req.OperationId, ct).ConfigureAwait(false);
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
        catch (SelectedFolderValidationException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
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

public sealed class CancelDevelopmentAttemptEndpoint
    : Endpoint<DevelopmentAttemptRequest>, IDevelopmentEndpoint
{
    public override void Configure()
    {
        Post(LocalApiRoutes.Development.CancelAttempt);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(description => description.Accepts<DevelopmentAttemptRequest>());
    }

    public override async Task HandleAsync(DevelopmentAttemptRequest req, CancellationToken ct)
    {
        var service = HttpContext.RequestServices.GetRequiredService<IDevelopmentManagementService>();
        try
        {
            if (!await service.CancelAttemptAsync(req.ProjectId, req.TaskId, req.AttemptId, ct).ConfigureAwait(false))
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

public sealed class ListDevelopmentEventsEndpoint
    : Endpoint<DevelopmentProjectRequest, ListDevelopmentEventsResponse>, IDevelopmentEndpoint
{
    public override void Configure()
    {
        Get(LocalApiRoutes.Development.Events);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DevelopmentProjectRequest req, CancellationToken ct)
    {
        var service = HttpContext.RequestServices.GetRequiredService<IDevelopmentManagementService>();
        try
        {
            var events = await service.ListEventsAsync(req.ProjectId, ct).ConfigureAwait(false);
            await Send.OkAsync(new ListDevelopmentEventsResponse(events.Select(DevelopmentContractMapper.ToResponse).ToArray()), ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
    }
}

public sealed class ListDevelopmentArtifactsEndpoint
    : Endpoint<DevelopmentTaskRequest, ListDevelopmentArtifactsResponse>, IDevelopmentEndpoint
{
    public override void Configure()
    {
        Get(LocalApiRoutes.Development.TaskArtifacts);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DevelopmentTaskRequest req, CancellationToken ct)
    {
        var service = HttpContext.RequestServices.GetRequiredService<IDevelopmentManagementService>();
        try
        {
            var artifacts = await service.ListArtifactsAsync(req.ProjectId, req.TaskId, ct).ConfigureAwait(false);
            await Send.OkAsync(new ListDevelopmentArtifactsResponse(artifacts.Select(DevelopmentContractMapper.ToResponse).ToArray()), ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
    }
}

public sealed class GetDevelopmentArtifactEndpoint
    : Endpoint<DevelopmentArtifactRequest, DevelopmentArtifactContentResponse>, IDevelopmentEndpoint
{
    public override void Configure()
    {
        Get(LocalApiRoutes.Development.ArtifactById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DevelopmentArtifactRequest req, CancellationToken ct)
    {
        var service = HttpContext.RequestServices.GetRequiredService<IDevelopmentManagementService>();
        try
        {
            var artifact = await service.ReadArtifactAsync(req.ProjectId, req.TaskId, req.ArtifactId, ct).ConfigureAwait(false);
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

public sealed class PreviewDevelopmentPatchEndpoint
    : Endpoint<DevelopmentActionRequest, DevelopmentPatchPreviewResponse>, IDevelopmentEndpoint
{
    public override void Configure()
    {
        Post(LocalApiRoutes.Development.PatchPreview);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DevelopmentActionRequest req, CancellationToken ct)
    {
        var service = HttpContext.RequestServices.GetRequiredService<IDevelopmentManagementService>();
        try
        {
            var preview = await service.PreviewAsync(req.ProjectId, req.TaskId, ct).ConfigureAwait(false);
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
        catch (SelectedFolderValidationException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DevelopmentInvalidTransitionException or DevelopmentWorkspaceSecurityException)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(statusCode: StatusCodes.Status409Conflict, cancellation: ct).ConfigureAwait(false);
        }
    }
}

public sealed class ApplyDevelopmentPatchEndpoint
    : Endpoint<DevelopmentActionRequest, DevelopmentApplyResponse>, IDevelopmentEndpoint
{
    public override void Configure()
    {
        Post(LocalApiRoutes.Development.Apply);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DevelopmentActionRequest req, CancellationToken ct)
    {
        var service = HttpContext.RequestServices.GetRequiredService<IDevelopmentManagementService>();
        try
        {
            var result = await service.ApplyAsync(req.ProjectId, req.TaskId, req.OperationId, ct).ConfigureAwait(false);
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
        catch (SelectedFolderValidationException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
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

public sealed class ReconnectDevelopmentRepositoryEndpoint
    : Endpoint<ReconnectDevelopmentRepositoryRequest, DevelopmentProjectDetailResponse>, IDevelopmentEndpoint
{
    public override void Configure()
    {
        Post(LocalApiRoutes.Development.RepositoryConnection);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ReconnectDevelopmentRepositoryRequest req, CancellationToken ct)
    {
        try
        {
            var service = HttpContext.RequestServices.GetRequiredService<IDevelopmentManagementService>();
            var project = await service.ReconnectRepositoryAsync(req.ProjectId, req.SelectedFolderId, req.ExpectedVersion, ct)
                                       .ConfigureAwait(false);
            await Send.OkAsync(project.ToResponse(), ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
        catch (SelectedFolderValidationException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DevelopmentConcurrencyException or DevelopmentWorkspaceSecurityException)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(statusCode: StatusCodes.Status409Conflict, cancellation: ct).ConfigureAwait(false);
        }
    }
}
