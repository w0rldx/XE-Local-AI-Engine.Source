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
            await Send.OkAsync(new DevelopmentPatchPreviewResponse
            {
                SubjectHash = preview.SubjectHash,
                PatchHash = preview.PatchHash,
                ManifestHash = preview.ManifestHash,
                ExpectedResultHash = preview.ExpectedResultHash,
                Patch = preview.Patch,
                ChangedFiles = preview.ChangedFiles
            },
                ct);
        }
        catch (DevelopmentWorkspaceSecurityException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(statusCode: StatusCodes.Status409Conflict, cancellation: ct);
        }
    }
}
