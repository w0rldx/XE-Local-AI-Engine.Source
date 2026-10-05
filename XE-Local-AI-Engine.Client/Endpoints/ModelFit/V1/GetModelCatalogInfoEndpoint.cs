namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit.Catalog;
using XE_Local_AI_Engine.Client.Services.ModelFit.Gguf;

/// <summary>
///     FastEndpoints handler for the curated model catalog's provenance (GET model-fit/catalog): which catalog build is
///     in effect (bundled / remote / remote-last-good), its version, and when it was last fetched.
/// </summary>
/// <remarks>
///     Read-only — it never triggers a fetch; see <see cref="RefreshModelCatalogEndpoint" /> for the operator-forced
///     refresh.
/// </remarks>
public sealed class GetModelCatalogInfoEndpoint : EndpointWithoutRequest<ModelCatalogInfoResponse>
{
    private readonly IModelCatalogProvider _catalogProvider;
    private readonly IOptions<ModelCatalogOptions> _options;
    private readonly IGgufVariantRecommender _recommender;

    public GetModelCatalogInfoEndpoint(IModelCatalogProvider catalogProvider,
        IOptions<ModelCatalogOptions> options,
        IGgufVariantRecommender recommender)
    {
        ArgumentNullException.ThrowIfNull(catalogProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(recommender);
        _catalogProvider = catalogProvider;
        _options = options;
        _recommender = recommender;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelFit.CatalogInfo);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var snapshot = await _catalogProvider.GetCatalogAsync(ct);
        var refreshSourceConfigured = !string.IsNullOrWhiteSpace(_options.Value.RefreshUrl);
        // Graded against the cached hardware profile, never the live process-VRAM probe: a page load starts no process and downloads nothing.
        var verdicts = await _recommender.ClassifyAgainstProfileAsync([.. snapshot.TestedEntries().Select(entry => entry.TestedSizeBytes.GetValueOrDefault())], ct);
        await Send.OkAsync(snapshot.ToResponse(refreshSourceConfigured, verdicts), ct);
    }
}
