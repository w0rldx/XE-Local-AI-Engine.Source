namespace XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1;

using FastEndpoints;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Containers.Bridge;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;

/// <summary>Forces a catalog fetch past the TTL.</summary>
/// <remarks>
///     A failed fetch is a 200 carrying the last-good document and <c>refreshFailureMessage</c>, not an error status:
///     the catalog the node can still serve is the useful answer, and "your click just failed" is a different sentence
///     from "the cache is stale", which rides on <c>lastRefreshFailure</c>.
/// </remarks>
public sealed class RefreshExternalAppCatalogEndpoint : EndpointWithoutRequest<ExternalAppCatalogResponse>
{
    private readonly IExternalAppService _apps;

    private readonly IApplicationCatalogProvider _catalog;

    private readonly ContainerBridgeEndpointSource _bridge;

    private readonly bool _refreshUrlConfigured;

    private readonly NodeStartupSettings _startupSettings;

    public RefreshExternalAppCatalogEndpoint(IApplicationCatalogProvider catalog,
        IExternalAppService apps,
        NodeStartupSettings startupSettings,
        ContainerBridgeEndpointSource bridge,
        IOptions<ExternalAppCatalogOptions> catalogOptions)
    {
        ArgumentNullException.ThrowIfNull(apps);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(startupSettings);
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(catalogOptions);
        _apps = apps;
        _catalog = catalog;
        _startupSettings = startupSettings;
        _bridge = bridge;
        _refreshUrlConfigured = ExternalAppCatalogOptions.IsAcceptedRefreshUrl(catalogOptions.Value.RefreshUrl);
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.ExternalApps.CatalogRefresh);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var refresh = await _catalog.RefreshAsync(ct);
        var installed = await ExternalAppCatalogEndpointSupport.InstalledByApplicationIdAsync(_apps, ct);

        await Send.OkAsync(ExternalAppMapper.ToCatalogResponse(refresh.Snapshot,
            refresh.FailureMessage,
            installed,
            _startupSettings.ExternalAppsAwaitingCatalog,
            bridgeOpen: _bridge.Current is not null,
            _refreshUrlConfigured), ct);
    }
}
