namespace XE_Local_AI_Engine.Client.Endpoints.Training.Definitions.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;

/// <summary>Enqueues a generation run for the definition. 202 — the queue owns the work from here.</summary>
public sealed class GenerateTrainingDatasetEndpoint : Endpoint<GenerateTrainingDatasetRequest, TrainingDatasetResponse>
{
    private readonly IDatasetGenerationService _generation;

    public GenerateTrainingDatasetEndpoint(IDatasetGenerationService generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        _generation = generation;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Training.DefinitionGenerate);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<TrainingDatasetResponse>(StatusCodes.Status202Accepted));
    }

    public override async Task HandleAsync(GenerateTrainingDatasetRequest req, CancellationToken ct)
    {
        var dataset = await _generation.StartAsync(req.DefinitionId, req.ExpectedVersion, req.Name, ct);
        await Send.ResultAsync(TypedResults.Accepted((string?)null, dataset.ToResponse()));
    }
}
