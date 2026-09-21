namespace XE_Local_AI_Engine.Client.Endpoints.Training.Evaluations.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.Evaluations.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Evaluation;

public sealed class GetEvaluationEndpoint : Endpoint<EvaluationByIdRequest, EvaluationResponse>
{
    private readonly IEvaluationRunService _evaluations;

    public GetEvaluationEndpoint(IEvaluationRunService evaluations)
    {
        ArgumentNullException.ThrowIfNull(evaluations);
        _evaluations = evaluations;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.EvaluationById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(EvaluationByIdRequest req, CancellationToken ct)
    {
        var evaluation = await _evaluations.GetAsync(req.EvaluationId, ct);
        if (evaluation is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(evaluation.ToResponse(), ct);
    }
}
