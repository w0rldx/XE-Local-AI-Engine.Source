namespace XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;

/// <summary>
///     One catalog manifest in full, minus the base64 asset bodies and with every <c>secret</c> variable's declared
///     default nulled — a shipped secret default is never a wire value.
/// </summary>
public sealed class GetExternalAppCatalogApplicationEndpoint(IApplicationCatalogProvider catalog)
    : Endpoint<ExternalAppApplicationRequest, ExternalAppManifestView>
{
    private readonly IApplicationCatalogProvider _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public override void Configure()
    {
        Get(LocalApiRoutes.ExternalApps.CatalogApplicationById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static builder => builder.Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(ExternalAppApplicationRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var manifest = await _catalog.GetApplicationAsync(req.ApplicationId, ct).ConfigureAwait(false);
        if (manifest is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(ExternalAppMapper.ToManifestView(manifest), ct).ConfigureAwait(false);
    }
}
