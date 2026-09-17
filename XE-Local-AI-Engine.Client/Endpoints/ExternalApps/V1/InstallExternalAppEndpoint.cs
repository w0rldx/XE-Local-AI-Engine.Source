namespace XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ExternalApps;

/// <summary>
///     Installs one application. 202 with the ADMITTED row: admission is synchronous — validate, gate, create the row,
///     append the event — and the pull, the create and the start run on the operation runner afterwards, so the awaited
///     task completes in milliseconds even when the image pull behind it runs for minutes. The endpoint never awaits
///     completion, and the request's own token covers the admission only, so a disconnecting browser cannot abort an
///     install halfway.
///     <para>
///         The body echoes the <c>manifestVersion</c> AND the <c>manifestSha256</c> the preview returned. A mismatch is
///         a 409 <c>ExternalAppManifestChanged</c>: a catalog that republishes v3 with a wider capability set keeps its
///         version number, so the fingerprint is what binds the acceptance to what was read.
///     </para>
/// </summary>
public sealed class InstallExternalAppEndpoint(IExternalAppService apps)
    : Endpoint<InstallExternalAppRequest, ExternalAppInstanceSummaryView>
{
    private readonly IExternalAppService _apps = apps ?? throw new ArgumentNullException(nameof(apps));

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
