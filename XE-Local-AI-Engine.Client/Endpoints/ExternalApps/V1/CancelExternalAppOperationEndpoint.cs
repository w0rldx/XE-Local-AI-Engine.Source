namespace XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ExternalApps;

/// <summary>
///     Cancels the operation running on this instance; it settles to <c>Failed</c> with its storage kept, so
///     cancelling an install is recoverable rather than destructive.
/// </summary>
/// <remarks>
///     Nothing in flight is a 409 <c>ExternalAppInvalidTransition</c>: a transient status with no live operation is a
///     crashed one, which the boot reconciler settles rather than a cancel. The 202 carries NO body and the command
///     takes no <c>expectedVersion</c> — docs/wiki/09-api-and-hubs.md ("Design notes on the newer endpoint families").
/// </remarks>
public sealed class CancelExternalAppOperationEndpoint : Endpoint<ExternalAppInstanceRequest>
{
    private readonly IExternalAppService _apps;

    public CancelExternalAppOperationEndpoint(IExternalAppService apps)
    {
        ArgumentNullException.ThrowIfNull(apps);
        _apps = apps;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.ExternalApps.InstanceCancel);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static builder => builder.Produces(StatusCodes.Status202Accepted)
                                             .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                             .Produces(StatusCodes.Status404NotFound)
                                             .ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(ExternalAppInstanceRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        await _apps.CancelAsync(req.InstanceId, ct);
        await Send.ResultAsync(Results.Accepted());
    }
}
