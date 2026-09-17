namespace XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ExternalApps;

/// <summary>
///     What the Update dialog needs before it asks for anything: the target manifest version and its fingerprint,
///     every target variable with the current values masked, the permissions this update ADDS, the resource verdict,
///     and whether the update can proceed at all.
///     <para>
///         An application that has LEFT the catalog is a 200 carrying a blocked preview — <c>canUpdate: false</c>,
///         <c>blockedReason: "CatalogMissing"</c>, no added permissions — and not a 409, because the dialog must be
///         able to say WHY there is nothing to update. <c>POST …/update</c> in that same state answers 404: the
///         preview explains, the command refuses.
///     </para>
///     <para>
///         It takes no <c>expectedVersion</c>. It changes nothing, and the version it would guard is the one the
///         operator is about to read.
///     </para>
/// </summary>
public sealed class GetExternalAppUpdatePreviewEndpoint(IExternalAppService apps)
    : Endpoint<ExternalAppInstanceRequest, ExternalAppUpdatePreview>
{
    private readonly IExternalAppService _apps = apps ?? throw new ArgumentNullException(nameof(apps));

    public override void Configure()
    {
        Get(LocalApiRoutes.ExternalApps.InstanceUpdatePreview);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                             .Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(ExternalAppInstanceRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var preview = await _apps.PreviewUpdateAsync(req.InstanceId, ct);
        await Send.OkAsync(ExternalAppMapper.ToUpdatePreview(preview), ct);
    }
}
