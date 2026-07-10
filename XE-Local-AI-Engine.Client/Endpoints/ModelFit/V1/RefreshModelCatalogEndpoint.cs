namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit.Catalog;

/// <summary>
///     FastEndpoints handler for an operator-forced catalog refresh (POST model-fit/catalog/refresh). Bypasses the TTL
///     and attempts one remote fetch immediately when a refresh URL is configured; a no-op (returns the bundled
///     snapshot) otherwise. A failed fetch/validation never surfaces as an error here — the provider's fallback chain
///     (last-good, else bundled) means this endpoint always returns 200 with whatever catalog is now in effect.
/// </summary>
public sealed class RefreshModelCatalogEndpoint(IModelCatalogProvider catalogProvider)
    : EndpointWithoutRequest<ModelCatalogInfoResponse>
{
    private readonly IModelCatalogProvider _catalogProvider = catalogProvider ?? throw new ArgumentNullException(nameof(catalogProvider));

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelFit.CatalogRefresh);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var snapshot = await _catalogProvider.RefreshAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(snapshot.ToResponse(), ct).ConfigureAwait(false);
    }
}
