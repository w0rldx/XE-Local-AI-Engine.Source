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
