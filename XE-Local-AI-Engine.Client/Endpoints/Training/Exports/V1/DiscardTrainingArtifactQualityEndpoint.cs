namespace XE_Local_AI_Engine.Client.Endpoints.Training.Exports.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Export;

public sealed class DiscardTrainingArtifactQualityEndpoint : Endpoint<DiscardArtifactQualityRequest, ArtifactQualityResponse>
{
    private readonly ITrainingExportService _exports;

    public DiscardTrainingArtifactQualityEndpoint(ITrainingExportService exports)
    {
        ArgumentNullException.ThrowIfNull(exports);
        _exports = exports;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Training.ArtifactQualityDiscard);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<ArtifactQualityResponse>(StatusCodes.Status200OK)
                                      .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(DiscardArtifactQualityRequest req, CancellationToken ct)
    {
        var artifact = await _exports.DiscardArtifactQualityAsync(req.ArtifactId, req.ExpectedVersion.GetValueOrDefault(), req.Reason, ct);
        await Send.OkAsync(DecideTrainingArtifactQualityEndpoint.ToQualityResponse(artifact), ct);
    }
}
