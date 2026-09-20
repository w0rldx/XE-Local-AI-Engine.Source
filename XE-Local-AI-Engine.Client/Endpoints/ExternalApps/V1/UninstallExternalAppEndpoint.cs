namespace XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ExternalApps;

/// <summary>
///     Removes the containers, the network, the rows AND the instance's data directory — all of it an explicit
///     operator act, which is why it is a DELETE rather than a lifecycle verb.
/// </summary>
/// <remarks>
///     <c>expectedVersion</c> rides in the QUERY here, not a body, bound through <c>[QueryParam]</c> on
///     <see cref="UninstallExternalAppRequest.ExpectedVersion" /> so the OpenAPI document declares a query parameter
///     instead of a request body the generated client would fill and this endpoint would never read. The member is
///     validated for PRESENCE, so a delete with no query is a 400 rather than a delete at version 0. Why the query:
///     docs/wiki/09-api-and-hubs.md ("Design notes on the newer endpoint families").
/// </remarks>
public sealed class UninstallExternalAppEndpoint : Endpoint<UninstallExternalAppRequest, ExternalAppInstanceSummaryView>
{
    private readonly IExternalAppService _apps;

    public UninstallExternalAppEndpoint(IExternalAppService apps)
    {
        ArgumentNullException.ThrowIfNull(apps);
        _apps = apps;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.ExternalApps.InstanceById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static builder => builder.Produces<ExternalAppInstanceSummaryView>(StatusCodes.Status202Accepted)
                                             .Produces(StatusCodes.Status404NotFound)
                                             .ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(UninstallExternalAppRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        // The validator's NotNull rule ran first, so the value is present here and only here.
        var summary = await _apps.UninstallAsync(req.InstanceId, req.ExpectedVersion!.Value, ct);
        await Send.ResultAsync(Results.Accepted(value: ExternalAppMapper.ToSummaryView(summary)));
    }
}
