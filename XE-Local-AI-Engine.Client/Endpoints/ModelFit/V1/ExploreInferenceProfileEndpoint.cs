namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Inference;

/// <summary>
///     FastEndpoints handler that explores a node-local GGUF model to draft its launch args (POST
///     model-fit/profiles/explore). Validates the model name (non-blank) and role
///     (<c>chat|embedding|reranker</c>) and the optional <c>contextTokens</c> override (a shape bound of 2048–1048576;
///     the model's train ceiling is the resolver's job) before calling the service's explore overload. A domain
///     rejection — a cloud or missing model, or any
///     sanitized failure reason the service returns — is surfaced as a 400 via <c>AddError</c> + <c>Send.ErrorsAsync</c>,
///     not an exception. A SKIPPED result (the model was serving inference, so nothing ran and nothing was evicted) is
///     also a 400 today, carrying its own retry-when-idle wording rather than the generic failure text. On success it
///     returns the drafted/updated profile view (machine key omitted).
/// </summary>
public sealed class ExploreInferenceProfileEndpoint(IInferenceProfileService inferenceProfileService)
    : Endpoint<ExploreInferenceProfileRequest, InferenceProfileActionResponse>
{
    /// <summary>
    ///     Lowest accepted <c>contextTokens</c> — the launch policy's smallest chat tier. A shape bound: the resolver
    ///     itself has no minimum beyond its 256-token alignment unit.
    /// </summary>
    private const int MinExploreContextTokens = 2048;

    /// <summary>
    ///     Highest accepted <c>contextTokens</c>. A shape bound only; the model-specific ceiling belongs to the
    ///     resolver's cap-and-align step, which is why this endpoint must not try to guess it.
    /// </summary>
    private const int MaxExploreContextTokens = 1_048_576;

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

        if (req.ContextTokens is { } contextTokens && contextTokens is < MinExploreContextTokens or > MaxExploreContextTokens)
        {
            AddError($"Context tokens must be between {MinExploreContextTokens} and {MaxExploreContextTokens}.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        var result = await _inferenceProfileService.ExploreAsync(req.ModelName.Trim(), role, req.ContextTokens, ct).ConfigureAwait(false);

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
