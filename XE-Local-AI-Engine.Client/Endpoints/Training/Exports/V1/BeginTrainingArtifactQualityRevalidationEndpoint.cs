namespace XE_Local_AI_Engine.Client.Endpoints.Training.Exports.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Export;

public sealed class BeginTrainingArtifactQualityRevalidationEndpoint : Endpoint<BeginArtifactQualityRevalidationRequest, ArtifactQualityResponse>
{
    private readonly IArtifactQualityService _quality;

    public BeginTrainingArtifactQualityRevalidationEndpoint(IArtifactQualityService quality)
    {
        ArgumentNullException.ThrowIfNull(quality);
        _quality = quality;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Training.ArtifactQualityRevalidation);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<ArtifactQualityResponse>(StatusCodes.Status200OK)
                                      .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(BeginArtifactQualityRevalidationRequest req, CancellationToken ct)
    {
        var artifact = await _quality.BeginRevalidationAsync(req.ArtifactId, req.ExpectedVersion.GetValueOrDefault(), ct);
        await Send.OkAsync(DecideTrainingArtifactQualityEndpoint.ToQualityResponse(artifact), ct);
    }
}
