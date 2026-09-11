namespace XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ExternalApps;

/// <summary>
///     Removes the containers, the network, the rows AND the instance's data directory — all of it an explicit
///     operator act, which is why it is a DELETE rather than a lifecycle verb.
///     <para>
///         <c>expectedVersion</c> rides in the QUERY here, not a body: a DELETE with a body is awkward on every layer
///         that touches it, and intermediaries may drop it. It is bound through <c>[QueryParam]</c> on
///         <see cref="UninstallExternalAppRequest.ExpectedVersion" /> so the OpenAPI document declares a query
///         parameter instead of a request body the generated client would fill and this endpoint would never read.
///         The member is validated for PRESENCE, so a delete with no query is a 400 rather than a delete at version 0.
///     </para>
/// </summary>
public sealed class UninstallExternalAppEndpoint(IExternalAppService apps)
    : Endpoint<UninstallExternalAppRequest, ExternalAppInstanceSummaryView>
{
    private readonly IExternalAppService _apps = apps ?? throw new ArgumentNullException(nameof(apps));

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
        var summary = await _apps.UninstallAsync(req.InstanceId, req.ExpectedVersion!.Value, ct).ConfigureAwait(false);
        await Send.ResultAsync(Results.Accepted(value: ExternalAppMapper.ToSummaryView(summary))).ConfigureAwait(false);
    }
}
