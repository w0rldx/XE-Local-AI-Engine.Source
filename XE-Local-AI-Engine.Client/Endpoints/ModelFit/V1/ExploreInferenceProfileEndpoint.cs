namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Inference;

/// <summary>
///     FastEndpoints handler that explores a node-local GGUF model to draft its launch args (POST
///     model-fit/profiles/explore). Validates the model name (non-blank) and role
///     (<c>chat|embedding|reranker</c>) before calling <see cref="IInferenceProfileService.ExploreAsync" />. A domain
///     rejection — a cloud or missing model, or any
///     sanitized failure reason the service returns — is surfaced as a 400 via <c>AddError</c> + <c>Send.ErrorsAsync</c>,
///     not an exception. A SKIPPED result (the model was serving inference, so nothing ran and nothing was evicted) is
///     also a 400 today, carrying its own retry-when-idle wording rather than the generic failure text. On success it
///     returns the drafted/updated profile view (machine key omitted).
/// </summary>
public sealed class ExploreInferenceProfileEndpoint(IInferenceProfileService inferenceProfileService)
    : Endpoint<ExploreInferenceProfileRequest, InferenceProfileActionResponse>
{
    private readonly IInferenceProfileService _inferenceProfileService = inferenceProfileService ?? throw new ArgumentNullException(nameof(inferenceProfileService));

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelFit.ProfilesExplore);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ExploreInferenceProfileRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ModelName))
        {
            AddError("A model name is required.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        if (ModelFitMapper.TryParseRole(req.Role) is not { } role)
        {
            AddError("Role is not supported.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        var result = await _inferenceProfileService.ExploreAsync(req.ModelName.Trim(), role, ct).ConfigureAwait(false);

        // A skip is not a failure: the model was busy, nothing was measured and nothing was evicted. It still returns
        // 400 (the response DTO carries no skip state), so the WORDING is what tells the operator to simply retry.
        if (result.Skipped)
        {
            AddError(result.FailureReason ?? "Skipped: the model is in use; profiling did not run. Retry when the model is idle.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        if (!result.Success || result.Profile is null)
        {
            AddError(result.FailureReason ?? "The model could not be explored.");
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
