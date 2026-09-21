namespace XE_Local_AI_Engine.Client.Endpoints.Training.Evaluations.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Evaluation;

public sealed class DeleteEvaluationEndpoint : Endpoint<DeleteEvaluationRequest>
{
    private readonly IEvaluationRunService _evaluations;

    public DeleteEvaluationEndpoint(IEvaluationRunService evaluations)
    {
        ArgumentNullException.ThrowIfNull(evaluations);
        _evaluations = evaluations;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.Training.EvaluationById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces(StatusCodes.Status204NoContent)
                               .Produces<TrainingErrorResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(DeleteEvaluationRequest req, CancellationToken ct)
    {
        await _evaluations.DeleteAsync(req.EvaluationId, req.ExpectedVersion, ct);
        await Send.NoContentAsync(ct);
    }
}
