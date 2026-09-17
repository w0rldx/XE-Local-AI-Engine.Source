namespace XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;

/// <summary>
///     Forces a catalog fetch past the TTL. A failed fetch is a 200 carrying the last-good document and
///     <c>refreshFailureMessage</c>, not an error status: the catalog the node can still serve is the useful answer,
///     and "your click just failed" is a different sentence from "the cache is stale", which rides on
///     <c>lastRefreshFailure</c>.
/// </summary>
public sealed class RefreshExternalAppCatalogEndpoint(IApplicationCatalogProvider catalog, IExternalAppService apps)
    : EndpointWithoutRequest<ExternalAppCatalogResponse>
{
    private readonly IExternalAppService _apps = apps ?? throw new ArgumentNullException(nameof(apps));

    private readonly IApplicationCatalogProvider _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public override void Configure()
    {
        Post(LocalApiRoutes.ExternalApps.CatalogRefresh);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var refresh = await _catalog.RefreshAsync(ct);
        var installed = await ExternalAppCatalogEndpointSupport.InstalledByApplicationIdAsync(_apps, ct);

        await Send.OkAsync(ExternalAppMapper.ToCatalogResponse(refresh.Snapshot, refresh.FailureMessage, installed), ct);
    }
}
