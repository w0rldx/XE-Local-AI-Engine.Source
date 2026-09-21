namespace XE_Local_AI_Engine.Client.Endpoints.Training.Definitions.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;

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
