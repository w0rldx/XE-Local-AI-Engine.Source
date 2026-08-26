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
