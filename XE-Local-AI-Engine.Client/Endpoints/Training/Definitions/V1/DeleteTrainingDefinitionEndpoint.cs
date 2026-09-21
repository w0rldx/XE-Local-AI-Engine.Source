namespace XE_Local_AI_Engine.Client.Endpoints.Training.Definitions.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;

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
