namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Inference;

/// <summary>
///     FastEndpoints handler that manually invalidates an inference profile, demoting it to Stale (POST
///     model-fit/profiles/invalidate) through <see cref="IInferenceProfileService.InvalidateAsync" />.
/// </summary>
/// <remarks>
///     The profile id rides the body, never a route param, so the POST always has a body. An empty id and a store-gate
///     rejection (an unknown profile, say) are 400s via <c>AddError</c> + <c>Send.ErrorsAsync</c>, not exceptions;
///     success returns the demoted profile view.
/// </remarks>
public sealed class InvalidateInferenceProfileEndpoint : Endpoint<InvalidateInferenceProfileRequest, InferenceProfileActionResponse>
{
    private readonly IInferenceProfileService _inferenceProfileService;

    public InvalidateInferenceProfileEndpoint(IInferenceProfileService inferenceProfileService)
    {
        ArgumentNullException.ThrowIfNull(inferenceProfileService);
        _inferenceProfileService = inferenceProfileService;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelFit.ProfilesInvalidate);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(InvalidateInferenceProfileRequest req, CancellationToken ct)
    {
        if (req.ProfileId == Guid.Empty)
        {
            AddError("A profile id is required.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var result = await _inferenceProfileService.InvalidateAsync(req.ProfileId, ct);

        if (!result.Success || result.Profile is null)
        {
            AddError(result.FailureReason ?? "The profile could not be invalidated.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        await Send.OkAsync(new InferenceProfileActionResponse
            {
                Profile = result.Profile.ToDto()
            },
            ct);
    }
}
