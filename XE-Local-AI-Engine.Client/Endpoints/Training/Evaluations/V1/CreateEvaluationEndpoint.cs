namespace XE_Local_AI_Engine.Client.Endpoints.Training.Evaluations.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.Evaluations.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Evaluation;

/// <summary>
///     Enqueues an evaluation of one side of a training run against that run's own frozen hold-out membership. The
///     queue is single-consumer, so this only enqueues — scoring starts once nothing else is holding the GPU.
/// </summary>
public sealed class CreateEvaluationEndpoint : Endpoint<CreateEvaluationRequest, EvaluationResponse>
{
    private readonly IEvaluationRunService _evaluations;

    public CreateEvaluationEndpoint(IEvaluationRunService evaluations)
    {
        ArgumentNullException.ThrowIfNull(evaluations);
        _evaluations = evaluations;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Training.Evaluations);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<EvaluationResponse>(StatusCodes.Status202Accepted)
                               .ProducesProblemFE(StatusCodes.Status400BadRequest));
    }

    public override async Task HandleAsync(CreateEvaluationRequest req, CancellationToken ct)
    {
        // EvaluationRejectedException reaches the global DomainValidationExceptionHandler as the same 400: its rejections are operator-facing by construction — no installed
        // base model, no completed staged artifact, or a run that held nothing back.
        var created = await _evaluations.CreateAsync(new CreateEvaluationCommand
        {
            TrainingRunId = req.TrainingRunId,
            Target = req.Target,
            ModelNameOverride = req.ModelName,
            ArtifactId = req.ArtifactId
        }, ct);
        await Send.ResultAsync(TypedResults.Accepted((string?)null, created.ToResponse()));
    }
}
