namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Inference;

/// <summary>
///     FastEndpoints handler that benchmarks a drafted inference profile (POST model-fit/profiles/benchmark). The target
///     profile id is carried in the body (never a route param), so the POST always has a body. An empty id is rejected
///     with a 400. Calls <see cref="IInferenceProfileService.BenchmarkAsync" />; a failed harness leaves the snapshot
///     Failed and is surfaced as a 400 via <c>AddError</c> + <c>Send.ErrorsAsync</c>, not an exception. A SKIPPED result
///     (the model was serving inference, so nothing ran and nothing was evicted) is also a 400 today, carrying its own
///     retry-when-idle wording rather than the generic failure text. On success it
///     returns the measured metrics + snapshot id + (un-frozen) profile view — never the raw <c>/metrics</c> scrape.
/// </summary>
public sealed class BenchmarkInferenceProfileEndpoint(IInferenceProfileService inferenceProfileService)
    : Endpoint<BenchmarkInferenceProfileRequest, BenchmarkInferenceProfileResponse>
{
    private readonly IInferenceProfileService _inferenceProfileService = inferenceProfileService ?? throw new ArgumentNullException(nameof(inferenceProfileService));

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelFit.ProfilesBenchmark);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(BenchmarkInferenceProfileRequest req, CancellationToken ct)
    {
        if (req.ProfileId == Guid.Empty)
        {
            AddError("A profile id is required.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        var result = await _inferenceProfileService
                           .BenchmarkAsync(req.ProfileId, req.AllowPreSpawnVramPressure, ct)
                           .ConfigureAwait(false);

        // A skip is not a failure: the model was busy, nothing was measured and nothing was evicted. It still returns
        // 400 (the response DTO carries no skip state), so the WORDING is what tells the operator to simply retry.
        if (result.Skipped)
        {
            AddError(result.FailureReason ?? "Skipped: the model is in use; the benchmark did not run. Retry when the model is idle.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        if (!result.Success || result.Profile is null)
        {
            AddError(result.FailureReason ?? "The profile could not be benchmarked.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(new BenchmarkInferenceProfileResponse
            {
                SnapshotId = result.SnapshotId,
                Metrics = result.Metrics?.ToDto(),
                Profile = result.Profile.ToDto()
            },
            ct).ConfigureAwait(false);
    }
}
