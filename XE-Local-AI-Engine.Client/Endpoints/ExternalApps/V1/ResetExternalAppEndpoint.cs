namespace XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ExternalApps;

/// <summary>
///     Wipes the instance's volumes and rebuilds it from the stored snapshot, restoring the desired state it had. Data-destroying by design, which is why it takes the version the operator last read.
///     <para>
///         202 with the ADMITTED row — the snapshot as the synchronous admission left it, taken before the operation
///         runner starts, which is why the status set carries the transient values. The endpoint never awaits the
///         container work and never polls, and the request's own token covers the admission only, so a client that
///         disconnects cancels nothing. Follow the rest on the hub.
///     </para>
/// </summary>
public sealed class ResetExternalAppEndpoint(IExternalAppService apps)
    : Endpoint<ExternalAppInstanceCommandRequest, ExternalAppInstanceSummaryView>
{
    private readonly IExternalAppService _apps = apps ?? throw new ArgumentNullException(nameof(apps));

    public override void Configure()
    {
        Post(LocalApiRoutes.ExternalApps.InstanceReset);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static builder => builder.Produces<ExternalAppInstanceSummaryView>(StatusCodes.Status202Accepted)
                                             .Produces(StatusCodes.Status404NotFound)
                                             .ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(ExternalAppInstanceCommandRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        // The validator's NotNull rule ran first, so the value is present here and only here.
        var summary = await _apps.ResetAsync(req.InstanceId, req.ExpectedVersion!.Value, ct);
        await Send.ResultAsync(Results.Accepted(value: ExternalAppMapper.ToSummaryView(summary)));
    }
}
