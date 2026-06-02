namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;

/// <summary>
///     FastEndpoints handler for listing the approved utility images (GET model-fit/approved-images). Read-only registry
///     projection; runs no llmfit.
/// </summary>
public sealed class ListApprovedImagesEndpoint(IModelFitQueryService modelFitQueryService)
    : EndpointWithoutRequest<ListApprovedImagesResponse>
{
    private readonly IModelFitQueryService _modelFitQueryService = modelFitQueryService ?? throw new ArgumentNullException(nameof(modelFitQueryService));

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelFit.ApprovedImages);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var images = await _modelFitQueryService.ListApprovedImagesAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(new ListApprovedImagesResponse
            {
                Items = [.. images.Select(static image => image.ToResponse())]
            },
            ct).ConfigureAwait(false);
    }
}
