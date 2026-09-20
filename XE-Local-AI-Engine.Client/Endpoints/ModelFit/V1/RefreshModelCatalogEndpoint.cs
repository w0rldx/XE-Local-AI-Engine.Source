namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit.Catalog;

/// <summary>
///     FastEndpoints handler for an operator-forced catalog refresh (POST model-fit/catalog/refresh): it bypasses the
///     TTL and attempts one remote fetch when a refresh URL is configured, and is a no-op otherwise.
/// </summary>
/// <remarks>
///     A failed fetch or validation never surfaces as an error: the provider's fallback chain (last-good, else bundled)
///     means this always returns 200 with whatever catalog is now in effect. A 200 therefore cannot be read as success
///     — <see cref="ModelCatalogInfoResponse.RefreshSourceConfigured" /> is what tells "refreshed" from "there is
///     nothing to refresh from". Same silent-success class as the image-model download.
/// </remarks>
public sealed class RefreshModelCatalogEndpoint : EndpointWithoutRequest<ModelCatalogInfoResponse>
{
    private readonly IModelCatalogProvider _catalogProvider;
    private readonly IOptions<ModelCatalogOptions> _options;

    public RefreshModelCatalogEndpoint(
        IModelCatalogProvider catalogProvider,
        IOptions<ModelCatalogOptions> options)
    {
        ArgumentNullException.ThrowIfNull(catalogProvider);
        ArgumentNullException.ThrowIfNull(options);
        _catalogProvider = catalogProvider;
        _options = options;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelFit.CatalogRefresh);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var snapshot = await _catalogProvider.RefreshAsync(ct);
        var refreshSourceConfigured = !string.IsNullOrWhiteSpace(_options.Value.RefreshUrl);
        await Send.OkAsync(snapshot.ToResponse(refreshSourceConfigured), ct);
    }
}
