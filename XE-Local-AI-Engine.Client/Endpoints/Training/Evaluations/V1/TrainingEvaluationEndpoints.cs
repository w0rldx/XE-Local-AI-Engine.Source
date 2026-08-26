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
public sealed class CreateEvaluationEndpoint(IEvaluationRunService evaluations) : Endpoint<CreateEvaluationRequest, EvaluationResponse>
{
    private readonly IEvaluationRunService _evaluations = evaluations ?? throw new ArgumentNullException(nameof(evaluations));

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
        try
        {
            var created = await _evaluations.CreateAsync(new CreateEvaluationCommand(req.TrainingRunId, req.Target, req.ModelName, req.ArtifactId), ct)
                                            .ConfigureAwait(false);
            await Send.ResultAsync(TypedResults.Accepted((string?)null, created.ToResponse())).ConfigureAwait(false);
        }
        catch (EvaluationRejectedException exception)
        {
            // Rejections are operator-facing by construction: no installed base model, no completed staged artifact, or a run
            // that held nothing back.
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}

public sealed class GetEvaluationEndpoint(IEvaluationRunService evaluations) : Endpoint<EvaluationByIdRequest, EvaluationResponse>
{
    private readonly IEvaluationRunService _evaluations = evaluations ?? throw new ArgumentNullException(nameof(evaluations));

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.EvaluationById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(EvaluationByIdRequest req, CancellationToken ct)
    {
        var evaluation = await _evaluations.GetAsync(req.EvaluationId, ct).ConfigureAwait(false);
        if (evaluation is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(evaluation.ToResponse(), ct).ConfigureAwait(false);
    }
}

public sealed class ListEvaluationsEndpoint(IEvaluationRunService evaluations) : Endpoint<ListEvaluationsRequest, ListEvaluationsResponse>
{
    private readonly IEvaluationRunService _evaluations = evaluations ?? throw new ArgumentNullException(nameof(evaluations));

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.Evaluations);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ListEvaluationsRequest req, CancellationToken ct)
    {
        var items = await _evaluations.ListAsync(req.TrainingRunId, ct).ConfigureAwait(false);
        await Send.OkAsync(new ListEvaluationsResponse
        {
            Items = items.Select(item => item.ToResponse()).ToArray()
        }, ct).ConfigureAwait(false);
    }
}

/// <summary>Re-queues an interrupted evaluation; the executor continues at the next unscored sample.</summary>
public sealed class ResumeEvaluationEndpoint(IEvaluationRunService evaluations) : Endpoint<EvaluationByIdRequest, EvaluationResponse>
{
    private readonly IEvaluationRunService _evaluations = evaluations ?? throw new ArgumentNullException(nameof(evaluations));

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
        try
        {
            var resumed = await _evaluations.ResumeAsync(req.EvaluationId, ct).ConfigureAwait(false);
            await Send.OkAsync(resumed.ToResponse(), ct).ConfigureAwait(false);
        }
        catch (EvaluationRejectedException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}

public sealed class CancelEvaluationEndpoint(IEvaluationRunService evaluations) : Endpoint<EvaluationByIdRequest>
{
    private readonly IEvaluationRunService _evaluations = evaluations ?? throw new ArgumentNullException(nameof(evaluations));

    public override void Configure()
    {
        Post(LocalApiRoutes.Training.EvaluationCancel);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Accepts<EvaluationByIdRequest>());
    }

    public override async Task HandleAsync(EvaluationByIdRequest req, CancellationToken ct)
    {
        if (!await _evaluations.CancelAsync(req.EvaluationId, ct).ConfigureAwait(false))
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.NoContentAsync(ct).ConfigureAwait(false);
    }
}

public sealed class DeleteEvaluationEndpoint(IEvaluationRunService evaluations) : Endpoint<DeleteEvaluationRequest>
{
    private readonly IEvaluationRunService _evaluations = evaluations ?? throw new ArgumentNullException(nameof(evaluations));

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
        await _evaluations.DeleteAsync(req.EvaluationId, req.ExpectedVersion, ct).ConfigureAwait(false);
        await Send.NoContentAsync(ct).ConfigureAwait(false);
    }
}
