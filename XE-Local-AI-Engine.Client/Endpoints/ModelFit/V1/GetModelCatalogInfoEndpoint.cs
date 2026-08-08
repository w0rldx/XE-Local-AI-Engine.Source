namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit.Catalog;

/// <summary>
///     FastEndpoints handler for the curated model catalog's provenance (GET model-fit/catalog): which catalog build is
///     currently in effect (bundled / remote / remote-last-good), its version, and when it was last fetched. Read-only —
///     never triggers a fetch (see <see cref="RefreshModelCatalogEndpoint" /> for the operator-forced refresh).
/// </summary>
public sealed class GetModelCatalogInfoEndpoint(
    IModelCatalogProvider catalogProvider,
    IOptions<ModelCatalogOptions> options)
    : EndpointWithoutRequest<ModelCatalogInfoResponse>
{
    private readonly IModelCatalogProvider _catalogProvider = catalogProvider ?? throw new ArgumentNullException(nameof(catalogProvider));
    private readonly IOptions<ModelCatalogOptions> _options = options ?? throw new ArgumentNullException(nameof(options));

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelFit.CatalogInfo);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var snapshot = await _catalogProvider.GetCatalogAsync(ct).ConfigureAwait(false);
        var refreshSourceConfigured = !string.IsNullOrWhiteSpace(_options.Value.RefreshUrl);
        await Send.OkAsync(snapshot.ToResponse(refreshSourceConfigured), ct).ConfigureAwait(false);
    }
}
