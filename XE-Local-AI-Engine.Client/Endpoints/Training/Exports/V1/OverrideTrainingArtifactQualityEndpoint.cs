namespace XE_Local_AI_Engine.Client.Endpoints.Training.Exports.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Export;

public sealed class OverrideTrainingArtifactQualityEndpoint : Endpoint<OverrideArtifactQualityRequest, ArtifactQualityResponse>
{
    private readonly IArtifactQualityService _quality;

    public OverrideTrainingArtifactQualityEndpoint(IArtifactQualityService quality)
    {
        ArgumentNullException.ThrowIfNull(quality);
        _quality = quality;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Training.ArtifactQualityOverride);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<ArtifactQualityResponse>(StatusCodes.Status200OK)
                                      .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(OverrideArtifactQualityRequest req, CancellationToken ct)
    {
        var artifact = await _quality.OverrideAsync(req.ArtifactId, req.ExpectedVersion.GetValueOrDefault(), req.Reason, ct);
        await Send.OkAsync(DecideTrainingArtifactQualityEndpoint.ToQualityResponse(artifact), ct);
    }
}
