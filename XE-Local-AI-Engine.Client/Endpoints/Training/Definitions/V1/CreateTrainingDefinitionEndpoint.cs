namespace XE_Local_AI_Engine.Client.Endpoints.Training.Definitions.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;

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
