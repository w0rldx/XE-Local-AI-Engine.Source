namespace XE_Local_AI_Engine.Client.Endpoints.Workspaces.V1;

using FastEndpoints;
using FastEndpoints.Swagger;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Workspace;

public sealed class DeleteWorkspaceEndpoint : Endpoint<DeleteWorkspaceRequest>
{
    private readonly IWorkspaceRevocationService _revocationService;

    public DeleteWorkspaceEndpoint(IWorkspaceRevocationService revocationService)
    {
        ArgumentNullException.ThrowIfNull(revocationService);
        _revocationService = revocationService;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.Workspaces.ById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static descriptor => descriptor.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                                   .Produces(StatusCodes.Status404NotFound)
                                                   .ProducesConflictProblemDetails()
                                                   .AutoTagOverride("Workspaces"));
    }

    public override async Task HandleAsync(DeleteWorkspaceRequest req, CancellationToken ct)
    {
        // No catch: the selected-folder family (unknown id to 404, rejection to 400) is answered by the global SelectedFolderExceptionHandler, where that mapping is stated
        // once. A busy revocation lease throws WorkspaceRevocationBusyException, which the global ConflictExceptionHandler answers with the shared 409 ConflictProblemDetails.
        await _revocationService.RevokeAsync(req.WorkspaceId, ct);
        await Send.NoContentAsync(ct);
    }
}
