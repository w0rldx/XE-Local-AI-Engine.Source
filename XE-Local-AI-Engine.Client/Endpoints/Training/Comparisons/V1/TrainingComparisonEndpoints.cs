namespace XE_Local_AI_Engine.Client.Endpoints.Training.Comparisons.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.Comparisons.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Comparison;
using XE_Local_AI_Engine.Client.Services.Training.Evaluation;

public sealed class CreateComparisonEndpoint(IComparisonReportService comparisons) : Endpoint<CreateComparisonRequest, ComparisonResponse>
{
    private readonly IComparisonReportService _comparisons = comparisons ?? throw new ArgumentNullException(nameof(comparisons));

    public override void Configure()
    {
        Post(LocalApiRoutes.Training.Comparisons);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<ComparisonResponse>(StatusCodes.Status200OK)
                               .ProducesProblemFE(StatusCodes.Status400BadRequest)
                               .Produces<TrainingErrorResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(CreateComparisonRequest req, CancellationToken ct)
    {
        try
        {
            var created = await _comparisons.CreateAsync(new CreateComparisonCommand(req.Name,
                                                req.BaseEvaluationRunId,
                                                req.TunedEvaluationRunId,
                                                req.BaseBenchmarkRunId,
                                                req.TunedBenchmarkRunId,
                                                req.TrainingRunId),
                                            ct)
                                            .ConfigureAwait(false);
            await Send.OkAsync(created.ToResponse(), ct).ConfigureAwait(false);
        }
        catch (EvaluationRejectedException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (TrainingEndpointSupport.IsHandled(exception))
        {
            await Send.ResultAsync(TrainingEndpointSupport.Error(exception)).ConfigureAwait(false);
        }
    }
}

public sealed class ListComparisonsEndpoint(IComparisonReportService comparisons) : EndpointWithoutRequest<ListComparisonsResponse>
{
    private readonly IComparisonReportService _comparisons = comparisons ?? throw new ArgumentNullException(nameof(comparisons));

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.Comparisons);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var items = await _comparisons.ListAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(new ListComparisonsResponse
        {
            Items = items.Select(item => item.ToResponse()).ToArray()
        }, ct).ConfigureAwait(false);
    }
}

public sealed class GetComparisonEndpoint(IComparisonReportService comparisons) : Endpoint<ComparisonByIdRequest, ComparisonResponse>
{
    private readonly IComparisonReportService _comparisons = comparisons ?? throw new ArgumentNullException(nameof(comparisons));

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.ComparisonById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ComparisonByIdRequest req, CancellationToken ct)
    {
        var report = await _comparisons.GetAsync(req.ComparisonId, ct).ConfigureAwait(false);
        if (report is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(report.ToResponse(), ct).ConfigureAwait(false);
    }
}

public sealed class DeleteComparisonEndpoint(IComparisonReportService comparisons) : Endpoint<DeleteComparisonRequest>
{
    private readonly IComparisonReportService _comparisons = comparisons ?? throw new ArgumentNullException(nameof(comparisons));

    public override void Configure()
    {
        Delete(LocalApiRoutes.Training.ComparisonById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces(StatusCodes.Status204NoContent)
                               .Produces<TrainingErrorResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(DeleteComparisonRequest req, CancellationToken ct)
    {
        try
        {
            await _comparisons.DeleteAsync(req.ComparisonId, req.ExpectedVersion, ct).ConfigureAwait(false);
            await Send.NoContentAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (TrainingEndpointSupport.IsHandled(exception))
        {
            await Send.ResultAsync(TrainingEndpointSupport.Error(exception)).ConfigureAwait(false);
        }
    }
}

/// <summary>
///     What the create dialog pre-fills from one training run: the base and tuned model names its lineage implies, and
///     the evaluations that already exist for them. Read-only — it creates nothing.
/// </summary>
public sealed class SuggestComparisonEndpoint(IComparisonReportService comparisons)
    : Endpoint<SuggestComparisonRequest, ComparisonSuggestionResponse>
{
    private readonly IComparisonReportService _comparisons = comparisons ?? throw new ArgumentNullException(nameof(comparisons));

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.ComparisonSuggest);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<ComparisonSuggestionResponse>(StatusCodes.Status200OK)
                               .ProducesProblemFE(StatusCodes.Status400BadRequest));
    }

    public override async Task HandleAsync(SuggestComparisonRequest req, CancellationToken ct)
    {
        try
        {
            var suggestion = await _comparisons.SuggestAsync(req.TrainingRunId, ct).ConfigureAwait(false);
            await Send.OkAsync(suggestion.ToResponse(), ct).ConfigureAwait(false);
        }
        catch (EvaluationRejectedException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}
