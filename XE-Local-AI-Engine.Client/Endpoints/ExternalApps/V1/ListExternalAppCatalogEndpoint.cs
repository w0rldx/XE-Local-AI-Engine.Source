namespace XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;

/// <summary>
///     The catalog cards, each already carrying whether this node has the application installed. The join is performed
///     here rather than left to the SPA because it is the second round-trip the card exists to avoid; the base64
///     <c>files[]</c> asset bodies never cross the wire.
/// </summary>
public sealed class ListExternalAppCatalogEndpoint(IApplicationCatalogProvider catalog, IExternalAppService apps)
    : EndpointWithoutRequest<ExternalAppCatalogResponse>
{
    private readonly IExternalAppService _apps = apps ?? throw new ArgumentNullException(nameof(apps));

    private readonly IApplicationCatalogProvider _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public override void Configure()
    {
        Get(LocalApiRoutes.ExternalApps.Catalog);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var snapshot = await _catalog.GetCatalogAsync(ct);
        var installed = await ExternalAppCatalogEndpointSupport.InstalledByApplicationIdAsync(_apps, ct);

        await Send.OkAsync(ExternalAppMapper.ToCatalogResponse(snapshot, refreshFailureMessage: null, installed), ct);
    }
}
