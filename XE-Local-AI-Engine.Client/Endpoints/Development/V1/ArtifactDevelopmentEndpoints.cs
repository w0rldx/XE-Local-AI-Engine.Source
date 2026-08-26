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
