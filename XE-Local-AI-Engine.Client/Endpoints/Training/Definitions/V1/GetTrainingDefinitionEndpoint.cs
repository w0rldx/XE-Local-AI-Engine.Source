namespace XE_Local_AI_Engine.Client.Endpoints.Training.Definitions.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;

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
