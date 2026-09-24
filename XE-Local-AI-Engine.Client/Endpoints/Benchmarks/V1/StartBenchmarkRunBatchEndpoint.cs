namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>
///     Enqueues a whole model × KV-type matrix against one project. Per-item outcomes, not all-or-nothing: one
///     ineligible model must not cost the operator the other nine cells.
/// </summary>
public sealed class StartBenchmarkRunBatchEndpoint : Endpoint<StartBenchmarkRunBatchRequest, StartBenchmarkRunBatchResponse>
{
    private const int MaxItems = 50;
    private readonly IBenchmarkRunBatchService _batches;

    public StartBenchmarkRunBatchEndpoint(IBenchmarkRunBatchService batches)
    {
        ArgumentNullException.ThrowIfNull(batches);
        _batches = batches;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Benchmarks.ProjectRunsBatch);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<StartBenchmarkRunBatchResponse>(StatusCodes.Status200OK)
                                      .ProducesProblem(StatusCodes.Status400BadRequest)
                                      .ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(StartBenchmarkRunBatchRequest req, CancellationToken ct)
    {
        if (req.Items.Count is 0 or > MaxItems)
        {
            AddError($"A batch must carry between 1 and {MaxItems} items.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var result = await _batches.StartAsync(new BenchmarkRunBatchRequest
        {
            ProjectId = req.ProjectId,
            ExpectedProjectVersion = req.ExpectedProjectVersion,
            Items =
            [
                .. req.Items.Select(static item => new BenchmarkRunBatchItem
                {
                    ModelName = item.ModelName,
                    KvCacheType = item.KvCacheType
                })
            ],
            RepeatCount = req.RepeatCount,
            Warmup = req.Warmup,
            RepeatMode = req.RepeatMode,
            AnswerVarianceTemperature = req.AnswerVarianceTemperature
        }, ct);
        await Send.OkAsync(new StartBenchmarkRunBatchResponse
        {
            ProjectVersion = result.ProjectVersion,
            Started =
            [
                .. result.Started.Select(static item => new StartedBenchmarkRunBatchItemResponse
                {
                    ModelName = item.ModelName,
                    KvCacheType = item.KvCacheType,
                    RunIds = item.RunIds
                })
            ],
            Rejected = [.. result.Rejected.Select(ToResponse)]
        }, ct);
    }

    private static RejectedBenchmarkRunBatchItemResponse ToResponse(BenchmarkRunBatchRejectedItem item)
    {
        var (code, message) = item.Kind switch
        {
            BenchmarkRunBatchRejectionKind.NotAttempted => (BenchmarkErrorCode.NotAttempted, item.Message),
            BenchmarkRunBatchRejectionKind.TimeBudget => (BenchmarkErrorCode.BatchTimeBudget, item.Message),
            _ when item.Failure is NotSupportedException => (BenchmarkErrorCode.UnsupportedSnapshot, item.Message),
            _ when item.Failure is not null => Classify(item.Failure),
            _ => throw new InvalidOperationException("A failed benchmark batch item must carry its failure.")
        };

        return new RejectedBenchmarkRunBatchItemResponse
        {
            ModelName = item.ModelName,
            KvCacheType = item.KvCacheType,
            Code = code.ToString(),
            Message = message
        };
    }

    private static (BenchmarkErrorCode Code, string Message) Classify(Exception exception)
    {
        var (_, code, message) = BenchmarkEndpointSupport.Classify(exception);
        return (code, message);
    }
}
