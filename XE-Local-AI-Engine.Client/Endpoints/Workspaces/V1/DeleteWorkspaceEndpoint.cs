namespace XE_Local_AI_Engine.Client.Endpoints.Workspaces.V1;

using FastEndpoints;
using FastEndpoints.Swagger;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Workspace;

public sealed class DeleteWorkspaceEndpoint(IWorkspaceRevocationService revocationService)
    : Endpoint<DeleteWorkspaceRequest>
{
    private readonly IWorkspaceRevocationService _revocationService = revocationService ?? throw new ArgumentNullException(nameof(revocationService));

    public override void Configure()
    {
        Delete(LocalApiRoutes.Workspaces.ById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static descriptor => descriptor.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                                   .Produces(StatusCodes.Status404NotFound)
                                                   .ProducesProblemDetails(StatusCodes.Status409Conflict)
                                                   .AutoTagOverride("Workspaces"));
    }

    public override async Task HandleAsync(DeleteWorkspaceRequest req, CancellationToken ct)
    {
        try
        {
            await _revocationService.RevokeAsync(req.WorkspaceId, ct).ConfigureAwait(false);
            await Send.NoContentAsync(ct).ConfigureAwait(false);
        }
        catch (SelectedFolderNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
        // No SelectedFolderConflictException arm: only registration can collide on an alias. A busy revocation lease
        // throws WorkspaceRevocationBusyException, which the global ConflictExceptionHandler answers with the shared
        // 409 ConflictProblemDetails (conflictType = WorkspaceRevocationBusy) — never hand-built here.
        catch (SelectedFolderValidationException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}
