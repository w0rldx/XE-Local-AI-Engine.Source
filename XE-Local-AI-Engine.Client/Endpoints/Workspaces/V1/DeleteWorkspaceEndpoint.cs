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
        Description(static descriptor => descriptor.Produces<WorkspaceConflictResponse>(StatusCodes.Status409Conflict)
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
        // No SelectedFolderConflictException arm: only registration can collide on an alias, and this endpoint's 409 is
        // contractually the stable WorkspaceConflictResponse shape (declared in Configure), not FastEndpoints' ProblemDetails.
        catch (SelectedFolderValidationException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
        catch (WorkspaceRevocationBusyException exception)
        {
            await Send.ResultAsync(Results.Conflict(new WorkspaceConflictResponse
            {
                Code = WorkspaceRevocationBusyException.ErrorCode,
                Message = exception.Message
            })).ConfigureAwait(false);
        }
    }
}
