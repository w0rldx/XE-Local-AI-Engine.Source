namespace XE_Local_AI_Engine.Client.Endpoints.Training.Definitions.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;

public sealed class ListTrainingDefinitionsEndpoint : EndpointWithoutRequest<ListTrainingDefinitionsResponse>
{
    private readonly IDatasetDefinitionService _definitions;

    public ListTrainingDefinitionsEndpoint(IDatasetDefinitionService definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        _definitions = definitions;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.Definitions);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var records = await _definitions.ListAsync(ct);
        await Send.OkAsync(new ListTrainingDefinitionsResponse
        {
            Items = records.Select(record => record.ToResponse()).ToArray()
        }, ct);
    }
}

public sealed class GetTrainingDefinitionEndpoint : Endpoint<GetTrainingDefinitionRequest, TrainingDefinitionResponse>
{
    private readonly IDatasetDefinitionService _definitions;

    public GetTrainingDefinitionEndpoint(IDatasetDefinitionService definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        _definitions = definitions;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.DefinitionById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetTrainingDefinitionRequest req, CancellationToken ct)
    {
        var record = await _definitions.GetAsync(req.DefinitionId, ct);
        if (record is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(record.ToResponse(), ct);
    }
}

public sealed class CreateTrainingDefinitionEndpoint : Endpoint<CreateTrainingDefinitionRequest, TrainingDefinitionResponse>
{
    private readonly IDatasetDefinitionService _definitions;

    public CreateTrainingDefinitionEndpoint(IDatasetDefinitionService definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        _definitions = definitions;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Training.Definitions);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CreateTrainingDefinitionRequest req, CancellationToken ct)
    {
        var record = await _definitions.CreateAsync(new DatasetDefinitionDraft { Name = req.Name, Body = req.Body }, ct);
        await Send.CreatedAtAsync<GetTrainingDefinitionEndpoint>(new
                  {
                      definitionId = record.Id
                  }, record.ToResponse(), cancellation: ct);
    }
}

public sealed class UpdateTrainingDefinitionEndpoint : Endpoint<UpdateTrainingDefinitionRequest, TrainingDefinitionResponse>
{
    private readonly IDatasetDefinitionService _definitions;

    public UpdateTrainingDefinitionEndpoint(IDatasetDefinitionService definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        _definitions = definitions;
    }

    public override void Configure()
    {
        Put(LocalApiRoutes.Training.DefinitionById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(UpdateTrainingDefinitionRequest req, CancellationToken ct)
    {
        var record = await _definitions.UpdateAsync(req.DefinitionId, req.ExpectedVersion, new DatasetDefinitionDraft { Name = req.Name, Body = req.Body }, ct);
        await Send.OkAsync(record.ToResponse(), ct);
    }
}

public sealed class DeleteTrainingDefinitionEndpoint : Endpoint<DeleteTrainingDefinitionRequest>
{
    private readonly IDatasetDefinitionService _definitions;

    public DeleteTrainingDefinitionEndpoint(IDatasetDefinitionService definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        _definitions = definitions;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.Training.DefinitionById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DeleteTrainingDefinitionRequest req, CancellationToken ct)
    {
        await _definitions.DeleteAsync(req.DefinitionId, req.ExpectedVersion, ct);
        await Send.NoContentAsync(ct);
    }
}

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
