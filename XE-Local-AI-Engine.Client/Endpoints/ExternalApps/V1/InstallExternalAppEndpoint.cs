namespace XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ExternalApps;

/// <summary>Installs one application, answering 202 with the ADMITTED row.</summary>
/// <remarks>
///     The body echoes the <c>manifestVersion</c> AND the <c>manifestSha256</c> the preview returned. A mismatch is a
///     409 <c>ExternalAppManifestChanged</c>: a catalog that republishes v3 with a wider capability set keeps its
///     version number, so the fingerprint is what binds the acceptance to what was read. Admission, the operation
///     runner and the hub: docs/wiki/09-api-and-hubs.md ("Design notes on the newer endpoint families").
/// </remarks>
public sealed class InstallExternalAppEndpoint : Endpoint<InstallExternalAppRequest, ExternalAppInstanceSummaryView>
{
    private readonly IExternalAppService _apps;

    public InstallExternalAppEndpoint(IExternalAppService apps)
    {
        ArgumentNullException.ThrowIfNull(apps);
        _apps = apps;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.ExternalApps.Instances);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static builder => builder.Produces<ExternalAppInstanceSummaryView>(StatusCodes.Status202Accepted)
                                             .Produces(StatusCodes.Status404NotFound)
                                             .ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(InstallExternalAppRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var summary = await _apps.InstallAsync(new InstallCommand(req.ApplicationId,
                                         req.DisplayName,
                                         req.ManifestVersion,
                                         req.ManifestSha256,
                                         req.Variables,
                                         req.AcceptPermissions),
                                     ct);

        await Send.ResultAsync(Results.Accepted(value: ExternalAppMapper.ToSummaryView(summary)));
    }
}
