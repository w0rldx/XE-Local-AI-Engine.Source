namespace XE_Local_AI_Engine.Client.Endpoints.Training.Evaluations.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Evaluation;

public sealed class CancelEvaluationEndpoint : Endpoint<EvaluationByIdRequest>
{
    private readonly IEvaluationRunService _evaluations;

    public CancelEvaluationEndpoint(IEvaluationRunService evaluations)
    {
        ArgumentNullException.ThrowIfNull(evaluations);
        _evaluations = evaluations;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Training.EvaluationCancel);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Accepts<EvaluationByIdRequest>()
                               .Produces(StatusCodes.Status204NoContent)
                               .Produces<TrainingErrorResponse>(StatusCodes.Status404NotFound)
                               .Produces<TrainingErrorResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(EvaluationByIdRequest req, CancellationToken ct)
    {
        await _evaluations.CancelAsync(req.EvaluationId, ct);
        await Send.NoContentAsync(ct);
    }
}
