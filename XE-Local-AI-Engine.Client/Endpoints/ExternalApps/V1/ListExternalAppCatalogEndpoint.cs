namespace XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;

/// <summary>The catalog cards, each already carrying whether this node has the application installed.</summary>
/// <remarks>
///     The join is performed here rather than left to the SPA because it is the second round-trip the card exists to
///     avoid; the base64 <c>files[]</c> asset bodies never cross the wire.
/// </remarks>
public sealed class ListExternalAppCatalogEndpoint : EndpointWithoutRequest<ExternalAppCatalogResponse>
{
    private readonly IExternalAppService _apps;

    private readonly IApplicationCatalogProvider _catalog;

    public ListExternalAppCatalogEndpoint(IApplicationCatalogProvider catalog, IExternalAppService apps)
    {
        ArgumentNullException.ThrowIfNull(apps);
        ArgumentNullException.ThrowIfNull(catalog);
        _apps = apps;
        _catalog = catalog;
    }

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
