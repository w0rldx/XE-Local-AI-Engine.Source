namespace XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ExternalApps;

/// <summary>
///     Rebuilds the instance against the catalog's current manifest, answering 202 with the admitted row.
/// </summary>
/// <remarks>
///     Admission runs every install precondition against the TARGET manifest before anything is stopped, so an installed application cannot update into a manifest
///     this version would refuse to install. <c>acceptPermissions</c> acknowledges a WIDENING and the SERVER owns the diff, so it is a boolean rather than a list the
///     client could get wrong; a widening without it is a 409 whose body carries the added names. <c>variables</c> is the TARGET manifest's full declared set: the
///     target may declare a variable the installed snapshot never had, and one of them may be required.
/// </remarks>
public sealed class UpdateExternalAppEndpoint : Endpoint<UpdateExternalAppRequest, ExternalAppInstanceSummaryView>
{
    private readonly IExternalAppService _apps;

    public UpdateExternalAppEndpoint(IExternalAppService apps)
    {
        ArgumentNullException.ThrowIfNull(apps);
        _apps = apps;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.ExternalApps.InstanceUpdate);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static builder => builder.Produces<ExternalAppInstanceSummaryView>(StatusCodes.Status202Accepted)
                                             .Produces(StatusCodes.Status404NotFound)
                                             .ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(UpdateExternalAppRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        // The validator's NotNull rule ran first, so the value is present here and only here.
        var summary = await _apps.UpdateAsync(req.InstanceId,
                                     req.ExpectedVersion!.Value,
                                     new UpdateCommand(req.ManifestVersion, req.ManifestSha256, req.AcceptPermissions, req.Variables),
                                     ct);

        await Send.ResultAsync(Results.Accepted(value: ExternalAppMapper.ToSummaryView(summary)));
    }
}
