namespace XE_Local_AI_Engine.Client.Endpoints.Development.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Development;

public sealed class DetectDevelopmentRepositoryProfileEndpoint : Endpoint<DevelopmentProfileDetectionRequest, DevelopmentProfileDetectionResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service;

    public DetectDevelopmentRepositoryProfileEndpoint(IDevelopmentManagementService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

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
            var detection = await _service.DetectRepositoryProfileAsync(req.SelectedFolderId, ct);
            await Send.OkAsync(new DevelopmentProfileDetectionResponse
            {
                ProfileId = detection.ProfileId,
                BuildTarget = detection.BuildTarget,
                Candidates = detection.Candidates
            }, ct);
        }
        catch (Exception exception) when (exception is DevelopmentWorkspaceSecurityException or DirectoryNotFoundException)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct);
        }
    }
}
