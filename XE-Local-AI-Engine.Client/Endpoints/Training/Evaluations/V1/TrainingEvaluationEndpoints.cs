namespace XE_Local_AI_Engine.Client.Endpoints.Training.Evaluations.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.Evaluations.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
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
        // EvaluationRejectedException reaches the global DomainValidationExceptionHandler as the same 400: its
        // rejections are operator-facing by construction — no installed base model, no completed staged artifact,
        // or a run that held nothing back.
        var created = await _evaluations.CreateAsync(new CreateEvaluationCommand(req.TrainingRunId, req.Target, req.ModelName, req.ArtifactId), ct);
        await Send.ResultAsync(TypedResults.Accepted((string?)null, created.ToResponse()));
    }
}

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
        Description(builder => builder.Accepts<EvaluationByIdRequest>());
    }

    public override async Task HandleAsync(EvaluationByIdRequest req, CancellationToken ct)
    {
        if (!await _evaluations.CancelAsync(req.EvaluationId, ct))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}

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
