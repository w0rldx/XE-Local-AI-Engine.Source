namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using Microsoft.Extensions.Options;
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
/// <remarks>
///     The 200-always contract is deliberate, but it used to be indistinguishable from success: with no
///     <c>ModelCatalog:RefreshUrl</c> configured — which is the state of every stock node, since no appsettings file
///     ships that section — this returned the unchanged bundled snapshot and the UI showed a green "catalog refreshed"
///     toast for an action that could not possibly have done anything. The response now carries
///     <see cref="ModelCatalogInfoResponse.RefreshSourceConfigured" /> so the caller can tell "refreshed" from
///     "there is nothing to refresh from". Same silent-success class as the image-model download.
/// </remarks>
public sealed class RefreshModelCatalogEndpoint(
    IModelCatalogProvider catalogProvider,
    IOptions<ModelCatalogOptions> options)
    : EndpointWithoutRequest<ModelCatalogInfoResponse>
{
    private readonly IModelCatalogProvider _catalogProvider = catalogProvider ?? throw new ArgumentNullException(nameof(catalogProvider));
    private readonly IOptions<ModelCatalogOptions> _options = options ?? throw new ArgumentNullException(nameof(options));

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelFit.CatalogRefresh);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var snapshot = await _catalogProvider.RefreshAsync(ct).ConfigureAwait(false);
        var refreshSourceConfigured = !string.IsNullOrWhiteSpace(_options.Value.RefreshUrl);
        await Send.OkAsync(snapshot.ToResponse(refreshSourceConfigured), ct).ConfigureAwait(false);
    }
}
