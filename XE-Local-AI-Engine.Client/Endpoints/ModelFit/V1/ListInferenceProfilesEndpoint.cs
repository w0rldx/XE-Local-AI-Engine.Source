namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Inference;

/// <summary>
///     FastEndpoints handler that lists every persisted node-local inference profile (GET model-fit/profiles). Thin
///     transport over <see cref="IInferenceProfileService.ListProfilesAsync" />: each row is projected to a sanitized DTO
///     that surfaces the launch-arg facts plus the lifecycle status name (<c>Explored|Frozen|Stale</c>) and NEVER the
///     local-only machine key (the view already omits it).
/// </summary>
public sealed class ListInferenceProfilesEndpoint(IInferenceProfileService inferenceProfileService)
    : EndpointWithoutRequest<ListInferenceProfilesResponse>
{
    private readonly IInferenceProfileService _inferenceProfileService = inferenceProfileService ?? throw new ArgumentNullException(nameof(inferenceProfileService));

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelFit.Profiles);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var profiles = await _inferenceProfileService.ListProfilesAsync(ct).ConfigureAwait(false);

        await Send.OkAsync(new ListInferenceProfilesResponse
            {
                Items = [.. profiles.Select(static profile => profile.ToDto())]
            },
            ct).ConfigureAwait(false);
    }
}
