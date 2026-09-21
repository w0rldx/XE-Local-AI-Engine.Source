namespace XE_Local_AI_Engine.Client.Endpoints.Training.Evaluations.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.Evaluations.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Evaluation;

public sealed class ListEvaluationsEndpoint : Endpoint<ListEvaluationsRequest, ListEvaluationsResponse>
{
    private readonly IEvaluationRunService _evaluations;

    public ListEvaluationsEndpoint(IEvaluationRunService evaluations)
    {
        ArgumentNullException.ThrowIfNull(evaluations);
        _evaluations = evaluations;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.Evaluations);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ListEvaluationsRequest req, CancellationToken ct)
    {
        var items = await _evaluations.ListAsync(req.TrainingRunId, ct);
        await Send.OkAsync(new ListEvaluationsResponse
        {
            Items = items.Select(item => item.ToResponse()).ToArray()
        }, ct);
    }
}
