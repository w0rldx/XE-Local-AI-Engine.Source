namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;

/// <summary>
///     FastEndpoints handler for the latest cached recommendation snapshot (GET model-fit/recommendations/latest). Reads
///     cached state only — it never runs the advisor. A cache-miss returns an explicit <c>hasCache:false</c> 200 (not a
///     404) so the UI can render the empty/diagnostics state. The provider-name query param is gone: the advisor is the
///     single recommendation backend and writes the fixed <c>llama.cpp</c> provider sentinel into the snapshot key.
/// </summary>
public sealed class GetLatestRecommendationsEndpoint(IModelFitQueryService modelFitQueryService)
    : Endpoint<GetLatestRecommendationsRequest, GetLatestRecommendationsResponse>
{
    /// <summary>The advisor's fixed provider sentinel — the snapshot key the box-aware recommendation is cached under.</summary>
    private const string AdvisorProviderName = "llama.cpp";

    private readonly IModelFitQueryService _modelFitQueryService = modelFitQueryService ?? throw new ArgumentNullException(nameof(modelFitQueryService));

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelFit.RecommendationsLatest);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetLatestRecommendationsRequest req, CancellationToken ct)
    {
        var useCase = string.IsNullOrWhiteSpace(req.UseCase) ? null : req.UseCase;

        var view = await _modelFitQueryService.GetLatestRecommendationsAsync(useCase, AdvisorProviderName, ct).ConfigureAwait(false);

        // Explicit empty state on a cache-miss — never a swallowed 404.
        var response = view is null ? ModelFitMapper.EmptyCache() : view.ToResponse();
        await Send.OkAsync(response, ct).ConfigureAwait(false);
    }
}
