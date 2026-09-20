namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Inference;

/// <summary>
///     FastEndpoints handler listing every persisted node-local inference profile (GET model-fit/profiles), a thin
///     transport over <see cref="IInferenceProfileService.ListProfilesAsync" />.
/// </summary>
/// <remarks>
///     Each row is projected to a sanitized DTO carrying the launch-arg facts plus the lifecycle status name
///     (<c>Explored|Frozen|Stale</c>) and NEVER the local-only machine key, which the view already omits.
/// </remarks>
public sealed class ListInferenceProfilesEndpoint : EndpointWithoutRequest<ListInferenceProfilesResponse>
{
    private readonly IInferenceProfileService _inferenceProfileService;

    public ListInferenceProfilesEndpoint(IInferenceProfileService inferenceProfileService)
    {
        ArgumentNullException.ThrowIfNull(inferenceProfileService);
        _inferenceProfileService = inferenceProfileService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelFit.Profiles);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var profiles = await _inferenceProfileService.ListProfilesAsync(ct);

        await Send.OkAsync(new ListInferenceProfilesResponse
            {
                Items = [.. profiles.Select(static profile => profile.ToDto())]
            },
            ct);
    }
}
