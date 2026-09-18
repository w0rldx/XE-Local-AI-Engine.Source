namespace XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ExternalApps;

/// <summary>
///     Cancels the operation running on this instance; it settles to <c>Failed</c> with its storage kept, so cancelling
///     an install is recoverable rather than destructive. Nothing in flight is a 409
///     <c>ExternalAppInvalidTransition</c> — a transient status with no live operation is a crashed one, which the boot
///     reconciler settles rather than a cancel.
///     <para>
///         The ONE command without <c>expectedVersion</c>. It names no target state, and requiring the operator's tab
///         to be current before it can stop a runaway pull would defeat the point.
///     </para>
///     <para>
///         202 with NO body. The cancellation is a request to the running operation, which settles through its own
///         <c>finally</c>; a row read here would be the row as it stands mid-settle, which is not the outcome and not
///         the admission either. The hub ping carries the settled state.
///     </para>
/// </summary>
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
