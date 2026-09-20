namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Inference;

/// <summary>
///     FastEndpoints handler that freezes an Explored inference profile (POST model-fit/profiles/freeze) through
///     <see cref="IInferenceProfileService.FreezeAsync" />, returning the frozen profile view.
/// </summary>
/// <remarks>
///     The profile id rides the body, never a route param, so the POST always has a body. An empty id, a freeze the
///     gate finds no most-recent successful benchmark for, and any other store-gate rejection are 400s via
///     <c>AddError</c> + <c>Send.ErrorsAsync</c>, not exceptions.
/// </remarks>
public sealed class FreezeInferenceProfileEndpoint : Endpoint<FreezeInferenceProfileRequest, InferenceProfileActionResponse>
{
    private readonly IInferenceProfileService _inferenceProfileService;

    public FreezeInferenceProfileEndpoint(IInferenceProfileService inferenceProfileService)
    {
        ArgumentNullException.ThrowIfNull(inferenceProfileService);
        _inferenceProfileService = inferenceProfileService;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelFit.ProfilesFreeze);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(FreezeInferenceProfileRequest req, CancellationToken ct)
    {
        if (req.ProfileId == Guid.Empty)
        {
            AddError("A profile id is required.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var result = await _inferenceProfileService.FreezeAsync(req.ProfileId, ct);

        if (!result.Success || result.Profile is null)
        {
            AddError(result.FailureReason ?? "The profile could not be frozen.");
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
