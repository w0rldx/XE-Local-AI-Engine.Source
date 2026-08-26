namespace XE_Local_AI_Engine.Client.Endpoints.Training.Definitions.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;

public sealed class ListTrainingDefinitionsEndpoint(IDatasetDefinitionService definitions)
    : EndpointWithoutRequest<ListTrainingDefinitionsResponse>
{
    private readonly IDatasetDefinitionService _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.Definitions);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var records = await _definitions.ListAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(new ListTrainingDefinitionsResponse
        {
            Items = records.Select(record => record.ToResponse()).ToArray()
        }, ct).ConfigureAwait(false);
    }
}

public sealed class GetTrainingDefinitionEndpoint(IDatasetDefinitionService definitions)
    : Endpoint<GetTrainingDefinitionRequest, TrainingDefinitionResponse>
{
    private readonly IDatasetDefinitionService _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.DefinitionById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetTrainingDefinitionRequest req, CancellationToken ct)
    {
        var record = await _definitions.GetAsync(req.DefinitionId, ct).ConfigureAwait(false);
        if (record is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(record.ToResponse(), ct).ConfigureAwait(false);
    }
}

public sealed class CreateTrainingDefinitionEndpoint(IDatasetDefinitionService definitions)
    : Endpoint<CreateTrainingDefinitionRequest, TrainingDefinitionResponse>
{
    private readonly IDatasetDefinitionService _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));

    public override void Configure()
    {
        Post(LocalApiRoutes.Training.Definitions);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CreateTrainingDefinitionRequest req, CancellationToken ct)
    {
        var record = await _definitions.CreateAsync(new DatasetDefinitionDraft(req.Name, req.Body), ct).ConfigureAwait(false);
        await Send.CreatedAtAsync<GetTrainingDefinitionEndpoint>(new
                  {
                      definitionId = record.Id
                  }, record.ToResponse(), cancellation: ct)
                  .ConfigureAwait(false);
    }
}

public sealed class UpdateTrainingDefinitionEndpoint(IDatasetDefinitionService definitions)
    : Endpoint<UpdateTrainingDefinitionRequest, TrainingDefinitionResponse>
{
    private readonly IDatasetDefinitionService _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));

    public override void Configure()
    {
        Put(LocalApiRoutes.Training.DefinitionById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(UpdateTrainingDefinitionRequest req, CancellationToken ct)
    {
        var record = await _definitions.UpdateAsync(req.DefinitionId, req.ExpectedVersion, new DatasetDefinitionDraft(req.Name, req.Body), ct)
                                       .ConfigureAwait(false);
        await Send.OkAsync(record.ToResponse(), ct).ConfigureAwait(false);
    }
}

public sealed class DeleteTrainingDefinitionEndpoint(IDatasetDefinitionService definitions)
    : Endpoint<DeleteTrainingDefinitionRequest>
{
    private readonly IDatasetDefinitionService _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));

    public override void Configure()
    {
        Delete(LocalApiRoutes.Training.DefinitionById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DeleteTrainingDefinitionRequest req, CancellationToken ct)
    {
        await _definitions.DeleteAsync(req.DefinitionId, req.ExpectedVersion, ct).ConfigureAwait(false);
        await Send.NoContentAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>Enqueues a generation run for the definition. 202 — the queue owns the work from here.</summary>
public sealed class GenerateTrainingDatasetEndpoint(IDatasetGenerationService generation)
    : Endpoint<GenerateTrainingDatasetRequest, TrainingDatasetResponse>
{
    private readonly IDatasetGenerationService _generation = generation ?? throw new ArgumentNullException(nameof(generation));

    public override void Configure()
    {
        Post(LocalApiRoutes.Training.DefinitionGenerate);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<TrainingDatasetResponse>(StatusCodes.Status202Accepted));
    }

    public override async Task HandleAsync(GenerateTrainingDatasetRequest req, CancellationToken ct)
    {
        var dataset = await _generation.StartAsync(req.DefinitionId, req.ExpectedVersion, req.Name, ct).ConfigureAwait(false);
        await Send.ResultAsync(TypedResults.Accepted((string?)null, dataset.ToResponse())).ConfigureAwait(false);
    }
}
