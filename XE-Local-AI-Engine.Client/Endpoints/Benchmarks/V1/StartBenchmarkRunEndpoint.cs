namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

public sealed class StartBenchmarkRunEndpoint : Endpoint<StartBenchmarkRunRequest, BenchmarkRunDetailResponse>
{
    private readonly IBenchmarkRunFreezeService _runs;

    public StartBenchmarkRunEndpoint(IBenchmarkRunFreezeService runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        _runs = runs;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Benchmarks.ProjectRuns);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<BenchmarkRunDetailResponse>(StatusCodes.Status202Accepted)
                                      .ProducesProblem(StatusCodes.Status400BadRequest)
                                      .ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict)
                                      .ProducesProblem(StatusCodes.Status422UnprocessableEntity));
    }

    public override async Task HandleAsync(StartBenchmarkRunRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ModelName))
        {
            AddError("A primary model is required.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        // Absent/blank is Auto and stays null; anything outside the allow-list is the caller's mistake, not an
        // unsupported runtime, so it is a 400 here rather than the 422 an unlaunchable-but-known type gets.
        if (!BenchmarkKvCacheType.TryNormalize(req.KvCacheType, out var kvCacheType))
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Problem(StatusCodes.Status400BadRequest,
                BenchmarkErrorCode.InvalidRequest,
                "The requested KV-cache type is not supported."));
            return;
        }

        try
        {
            // The FIRST run of the group is the response: it is the one that starts, so it is the one an operator's
            // live pane should open on. The rest are reachable through its repeatGroupId.
            var created = await _runs.StartAsync(new BenchmarkRunStartRequest
            {
                ProjectId = req.ProjectId,
                PrimaryModelName = req.ModelName,
                ExpectedProjectVersion = req.ExpectedProjectVersion,
                KvCacheType = kvCacheType,
                RepeatCount = req.RepeatCount,
                Warmup = req.Warmup,
                RepeatMode = req.RepeatMode,
                AnswerVarianceTemperature = req.AnswerVarianceTemperature
            }, scope: null, ct);
            await Send.ResultAsync(Results.Accepted(value: created[0].ToDetail()));
        }
        catch (KeyNotFoundException exception)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(exception));
        }
        catch (NotSupportedException exception)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Problem(StatusCodes.Status422UnprocessableEntity,
                BenchmarkErrorCode.UnsupportedSnapshot,
                exception.Message));
        }
    }
}
