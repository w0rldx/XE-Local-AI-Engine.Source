namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Inference;

/// <summary>
///     FastEndpoints handler that manually invalidates an inference profile, demoting it to Stale (POST
///     model-fit/profiles/invalidate). The target profile id is carried in the body (never a route param), so the POST
///     always has a body. An empty id is rejected with a 400. Calls
///     <see cref="IInferenceProfileService.InvalidateAsync" />; a store-gate rejection (e.g. an unknown profile) returns a
///     failed result that is surfaced as a 400 via <c>AddError</c> + <c>Send.ErrorsAsync</c>, not an exception. On success
///     it returns the demoted profile view.
/// </summary>
public sealed class InvalidateInferenceProfileEndpoint(IInferenceProfileService inferenceProfileService)
    : Endpoint<InvalidateInferenceProfileRequest, InferenceProfileActionResponse>
{
    private readonly IInferenceProfileService _inferenceProfileService = inferenceProfileService ?? throw new ArgumentNullException(nameof(inferenceProfileService));

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
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        var result = await _inferenceProfileService.InvalidateAsync(req.ProfileId, ct).ConfigureAwait(false);

        if (!result.Success || result.Profile is null)
        {
            AddError(result.FailureReason ?? "The profile could not be invalidated.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(new InferenceProfileActionResponse
            {
                Profile = result.Profile.ToDto()
            },
            ct).ConfigureAwait(false);
    }
}
