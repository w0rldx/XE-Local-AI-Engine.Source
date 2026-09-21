namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>
///     Clears the base-logit cache. Refused while any fidelity work item is live: deleting a file a queued
///     measurement is on its way to reading would fail that measurement for a reason the operator never sees.
/// </summary>
public sealed class ClearBenchmarkFidelityCacheEndpoint : Endpoint<GetKldDiskEstimateRequest>
{
    private readonly BenchmarkKldBaseCache _cache;
    private readonly BenchmarkRecordService _records;

    public ClearBenchmarkFidelityCacheEndpoint(BenchmarkRecordService records, BenchmarkKldBaseCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(records);
        _cache = cache;
        _records = records;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.Benchmarks.ProjectFidelityCache);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(GetKldDiskEstimateRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (await _records.GetProjectAsync(req.ProjectId, ct) is null)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkNotFoundException("Benchmark project was not found.")));
            return;
        }

        if (await _records.HasLiveFidelityWorkAsync(ct))
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkConflictException("FidelityWorkInFlight")));
            return;
        }

        _cache.Clear();
        await Send.NoContentAsync(ct);
    }
}
