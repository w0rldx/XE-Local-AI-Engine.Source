namespace XE_Local_AI_Engine.Client.Endpoints.Development.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Development;

public sealed class PreviewDevelopmentPatchEndpoint : Endpoint<DevelopmentActionRequest, DevelopmentPatchPreviewResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service;

    public PreviewDevelopmentPatchEndpoint(IDevelopmentManagementService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

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
            var preview = await _service.PreviewAsync(req.ProjectId, req.TaskId, ct);
            await Send.OkAsync(new DevelopmentPatchPreviewResponse(preview.SubjectHash,
                    preview.PatchHash,
                    preview.ManifestHash,
                    preview.ExpectedResultHash,
                    preview.Patch,
                    preview.ChangedFiles),
                ct);
        }
        catch (DevelopmentWorkspaceSecurityException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(statusCode: StatusCodes.Status409Conflict, cancellation: ct);
        }
    }
}

public sealed class ApplyDevelopmentPatchEndpoint : Endpoint<DevelopmentActionRequest, DevelopmentApplyResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service;

    public ApplyDevelopmentPatchEndpoint(IDevelopmentManagementService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

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
            // No run named: this is the operator's own apply, and it is refused for a task a LIVE workflow run is
            // driving. The 409 that refusal becomes is the same shape every other Development precondition uses.
            var result = await _service.ApplyAsync(req.ProjectId, req.TaskId, req.OperationId, onBehalfOfWorkflowRunId: null, ct);
            await Send.OkAsync(new DevelopmentApplyResponse(result.OperationId,
                    result.Phase,
                    result.Outcome,
                    result.Status,
                    result.Version,
                    result.Sequence),
                ct);
        }
        catch (DevelopmentWorkspaceSecurityException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(statusCode: StatusCodes.Status409Conflict, cancellation: ct);
        }
    }
}
