namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Images.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Images.Catalog;

/// <summary>
///     The curated image-model catalog. Operator-gated; no path or token is surfaced.
/// </summary>
/// <remarks>
///     Joins three things the UI would otherwise correlate itself: the bundled catalog entries, which of them are
///     already installed, and how each one's weights compare to this box's measured memory budget. Every entry carries
///     its whole file-set in the exact shape <c>POST images/models/downloads</c> accepts, so installing is one click.
///     The join lives in <see cref="ImageModelCatalogService" />.
/// </remarks>
public sealed class GetImageModelCatalogEndpoint : EndpointWithoutRequest<GetImageModelCatalogResponse>
{
    private readonly ImageModelCatalogService _catalogService;

    public GetImageModelCatalogEndpoint(ImageModelCatalogService catalogService)
    {
        ArgumentNullException.ThrowIfNull(catalogService);
        _catalogService = catalogService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Images.ModelCatalog);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var view = await _catalogService.GetCatalogViewAsync(ct);
        await Send.OkAsync(view.ToResponse(), ct);
    }
}
