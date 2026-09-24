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
