namespace XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ExternalApps;

/// <summary>
///     Everything the install dialog needs BEFORE it asks for anything: the manifest fingerprint the install must echo
///     back, the declared and effective permissions, EVERY declared variable, the resource verdict, the missing
///     capabilities and whether the install can proceed at all. The service evaluates gpu, <c>requires</c>, resources
///     and the runtime — the SPA never re-derives the verdict from the parts.
/// </summary>
public sealed class GetExternalAppInstallPreviewEndpoint : Endpoint<ExternalAppApplicationRequest, ExternalAppInstallPreview>
{
    private readonly IExternalAppService _apps;

    public GetExternalAppInstallPreviewEndpoint(IExternalAppService apps)
    {
        ArgumentNullException.ThrowIfNull(apps);
        _apps = apps;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.ExternalApps.InstallPreview);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static builder => builder.Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(ExternalAppApplicationRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var preview = await _apps.PreviewInstallAsync(req.ApplicationId, ct);

        // Nothing reconciled to produce this resolution, so the foreign-container count is 0 rather than a cached
        // observation reported as a fresh one.
        var runtime = ExternalAppMapper.ToRuntimeResponse(preview.Runtime, foreignInstallContainers: 0);

        await Send.OkAsync(ExternalAppMapper.ToPreview(preview, runtime), ct);
    }
}
