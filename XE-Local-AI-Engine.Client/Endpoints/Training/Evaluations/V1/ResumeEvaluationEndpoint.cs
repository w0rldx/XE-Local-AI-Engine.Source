namespace XE_Local_AI_Engine.Client.Endpoints.Training.Evaluations.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.Evaluations.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Evaluation;

/// <summary>Re-queues an interrupted evaluation; the executor continues at the next unscored sample.</summary>
public sealed class ResumeEvaluationEndpoint : Endpoint<EvaluationByIdRequest, EvaluationResponse>
{
    private readonly IEvaluationRunService _evaluations;

    public ResumeEvaluationEndpoint(IEvaluationRunService evaluations)
    {
        ArgumentNullException.ThrowIfNull(evaluations);
        _evaluations = evaluations;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Training.EvaluationResume);
        Policies(NodeAuthorizationPolicies.Operator);
        // The id is the whole request and it comes from the route; without declaring that, FastEndpoints answers a
        // bodyless POST with 415 instead of acting.
        Description(builder => builder.Accepts<EvaluationByIdRequest>());
    }

    public override async Task HandleAsync(EvaluationByIdRequest req, CancellationToken ct)
    {
        var resumed = await _evaluations.ResumeAsync(req.EvaluationId, ct);
        await Send.OkAsync(resumed.ToResponse(), ct);
    }
}
