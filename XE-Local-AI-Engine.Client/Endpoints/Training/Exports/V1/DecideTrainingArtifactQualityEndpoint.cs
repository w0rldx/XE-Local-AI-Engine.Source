namespace XE_Local_AI_Engine.Client.Endpoints.Training.Exports.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Export;

public sealed class DecideTrainingArtifactQualityEndpoint : Endpoint<DecideArtifactQualityRequest, ArtifactQualityResponse>
{
    private readonly IArtifactQualityService _quality;

    public DecideTrainingArtifactQualityEndpoint(IArtifactQualityService quality)
    {
        ArgumentNullException.ThrowIfNull(quality);
        _quality = quality;
    }

    public override void Configure()
    {
        Put(LocalApiRoutes.Training.ArtifactQuality);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<ArtifactQualityResponse>(StatusCodes.Status200OK)
                                      .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(DecideArtifactQualityRequest req, CancellationToken ct)
    {
        var artifact = await _quality.DecideAsync(req.ArtifactId, req.ComparisonId, req.ExpectedVersion.GetValueOrDefault(), ct);
        await Send.OkAsync(ToQualityResponse(artifact), ct);
    }

    internal static ArtifactQualityResponse ToQualityResponse(TrainingArtifactRecord artifact)
    {
        var decision = ArtifactQualityService.ReadDecision(artifact)
                       ?? throw new InvalidOperationException("The persisted artifact quality decision could not be read.");
        return new ArtifactQualityResponse
        {
            ArtifactId = artifact.Id,
            ComparisonId = decision.ComparisonId,
            ArtifactSha256 = decision.ArtifactSha256,
            Outcome = decision.Outcome.ToString(),
            FailureCodes = decision.FailureCodes,
            OverrideReason = decision.OverrideReason,
            DiscardedAtUtc = artifact.DiscardedAtUtc,
            DiscardReason = artifact.DiscardReason,
            DiscardCleanupPending = artifact.DiscardCleanupPending,
            Version = artifact.Version
        };
    }
}
