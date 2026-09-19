namespace XE_Local_AI_Engine.Client.Endpoints.Training.Comparisons.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.Comparisons.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Comparison;

public sealed class CreateComparisonEndpoint : Endpoint<CreateComparisonRequest, ComparisonResponse>
{
    private readonly IComparisonReportService _comparisons;

    public CreateComparisonEndpoint(IComparisonReportService comparisons)
    {
        ArgumentNullException.ThrowIfNull(comparisons);
        _comparisons = comparisons;
    }

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
        var created = await _comparisons.CreateAsync(new CreateComparisonCommand
        {
            Name = req.Name,
            BaseEvaluationRunId = req.BaseEvaluationRunId,
            TunedEvaluationRunId = req.TunedEvaluationRunId,
            BaseBenchmarkRunId = req.BaseBenchmarkRunId,
            TunedBenchmarkRunId = req.TunedBenchmarkRunId,
            TrainingRunId = req.TrainingRunId
        },
                                            ct);
        await Send.OkAsync(created.ToResponse(), ct);
    }
}

public sealed class ListComparisonsEndpoint : EndpointWithoutRequest<ListComparisonsResponse>
{
    private readonly IComparisonReportService _comparisons;

    public ListComparisonsEndpoint(IComparisonReportService comparisons)
    {
        ArgumentNullException.ThrowIfNull(comparisons);
        _comparisons = comparisons;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.Comparisons);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var items = await _comparisons.ListAsync(ct);
        await Send.OkAsync(new ListComparisonsResponse
        {
            Items = items.Select(item => item.ToResponse()).ToArray()
        }, ct);
    }
}

public sealed class GetComparisonEndpoint : Endpoint<ComparisonByIdRequest, ComparisonResponse>
{
    private readonly IComparisonReportService _comparisons;

    public GetComparisonEndpoint(IComparisonReportService comparisons)
    {
        ArgumentNullException.ThrowIfNull(comparisons);
        _comparisons = comparisons;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.ComparisonById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ComparisonByIdRequest req, CancellationToken ct)
    {
        var report = await _comparisons.GetAsync(req.ComparisonId, ct);
        if (report is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(report.ToResponse(), ct);
    }
}

public sealed class DeleteComparisonEndpoint : Endpoint<DeleteComparisonRequest>
{
    private readonly IComparisonReportService _comparisons;

    public DeleteComparisonEndpoint(IComparisonReportService comparisons)
    {
        ArgumentNullException.ThrowIfNull(comparisons);
        _comparisons = comparisons;
    }

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
        await _comparisons.DeleteAsync(req.ComparisonId, req.ExpectedVersion, ct);
        await Send.NoContentAsync(ct);
    }
}

/// <summary>
///     What the create dialog pre-fills from one training run: the base and tuned model names its lineage implies, and
///     the evaluations that already exist for them. Read-only — it creates nothing.
/// </summary>
public sealed class SuggestComparisonEndpoint : Endpoint<SuggestComparisonRequest, ComparisonSuggestionResponse>
{
    private readonly IComparisonReportService _comparisons;

    public SuggestComparisonEndpoint(IComparisonReportService comparisons)
    {
        ArgumentNullException.ThrowIfNull(comparisons);
        _comparisons = comparisons;
    }

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
        var suggestion = await _comparisons.SuggestAsync(req.TrainingRunId, ct);
        await Send.OkAsync(suggestion.ToResponse(), ct);
    }
}
