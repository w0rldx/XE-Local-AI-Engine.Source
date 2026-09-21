namespace XE_Local_AI_Engine.Client.Endpoints.AgentHome.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Removes one run's directory: its log, its commands and its exported patch.
/// </summary>
/// <remarks>
///     Nothing already applied is undone — an apply writes into the operator's own folders — so a run whose patch
///     was landed may be deleted like any other. The refusals are deliberately blunt: an unknown id, an unminted one,
///     one resolving outside the runs root and a linked directory all answer 404 with no path in the body, so a
///     caller cannot map the disk by probing. A run holding the execution lease is a 409, and so is one whose patch
///     is being applied, whose outcome entry is written into the directory the delete would pull out from under it.
/// </remarks>
public sealed class DeleteAgentHomeRunEndpoint : Endpoint<AgentHomeRunByIdRequest>
{
    private readonly IAgentHomeRunDeleteService _service;

    public DeleteAgentHomeRunEndpoint(IAgentHomeRunDeleteService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.AgentHomeRuns.ById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                             .Produces(StatusCodes.Status404NotFound)
                                             .ProducesProblemDetails(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(AgentHomeRunByIdRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        switch (await _service.DeleteAsync(req.RunId, ct))
        {
            case AgentHomeRunDeleteOutcome.Deleted:
                await Send.NoContentAsync(ct);
                return;
            case AgentHomeRunDeleteOutcome.Conflict:
                AddError("This run cannot be deleted right now. A run may be in flight; try again in a moment.");
                await Send.ErrorsAsync(StatusCodes.Status409Conflict, ct);
                return;
            default:
                await Send.NotFoundAsync(ct);
                return;
        }
    }
}
