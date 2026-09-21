namespace XE_Local_AI_Engine.Client.Endpoints.Training.Exports.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Export;

/// <summary>Registers a smoke-passed, quality-approved artifact as a local model, with its training lineage attached.</summary>
public sealed class PromoteTrainingArtifactEndpoint : Endpoint<PromoteTrainingArtifactRequest, PromoteTrainingArtifactResponse>
{
    private readonly IArtifactPromotionService _promotion;

    public PromoteTrainingArtifactEndpoint(IArtifactPromotionService promotion)
    {
        ArgumentNullException.ThrowIfNull(promotion);
        _promotion = promotion;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Training.ArtifactPromote);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<PromoteTrainingArtifactResponse>(StatusCodes.Status200OK)
                               .ProducesProblemFE(StatusCodes.Status400BadRequest));
    }

    public override async Task HandleAsync(PromoteTrainingArtifactRequest req, CancellationToken ct)
    {
        var modelName = await _promotion.PromoteAsync(req.ArtifactId, req.ModelName, ct);
        await Send.OkAsync(new PromoteTrainingArtifactResponse
        {
            ModelName = modelName
        }, ct);
    }
}
