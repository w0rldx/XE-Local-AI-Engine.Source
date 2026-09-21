namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using System.Globalization;
using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>
///     What enabling KL divergence will cost on disk, answered BEFORE the operator commits. The number is an estimate
///     over a measured bytes-per-logit constant, and the formula is returned with it so the figure is checkable rather
///     than magic.
/// </summary>
public sealed class GetBenchmarkKldDiskEstimateEndpoint : Endpoint<GetKldDiskEstimateRequest, GetKldDiskEstimateResponse>
{
    private readonly BenchmarkKldBaseCache _cache;
    private readonly BenchmarkRecordService _records;

    public GetBenchmarkKldDiskEstimateEndpoint(BenchmarkRecordService records, BenchmarkKldBaseCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(records);
        _cache = cache;
        _records = records;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Benchmarks.ProjectKldEstimate);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(GetKldDiskEstimateRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        var project = await _records.GetProjectAsync(req.ProjectId, ct);
        if (project is null)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkNotFoundException("Benchmark project was not found.")));
            return;
        }

        var chunks = BenchmarkFidelityPolicy.ClampChunks(req.Chunks ?? project.FidelityChunks);
        var estimated = BenchmarkFidelityPolicy.EstimateKldBytes(chunks, BenchmarkFidelityPolicy.DefaultVocabSize);
        var free = _cache.AvailableFreeBytes();
        await Send.OkAsync(new GetKldDiskEstimateResponse
                  {
                      EstimatedBytes = estimated,
                      FreeDiskBytes = free,
                      CachedBytes = _cache.TotalBytes(),
                      Chunks = chunks,
                      ContextTokens = BenchmarkFidelityPolicy.ContextTokens,
                      VocabSize = BenchmarkFidelityPolicy.DefaultVocabSize,
                      Formula = string.Create(CultureInfo.InvariantCulture,
                          $"chunks x contextTokens x vocabSize x {BenchmarkFidelityPolicy.KldBytesPerLogit} bytes per logit, plus a small header"),
                      FitsOnDisk = free - estimated >= BenchmarkFidelityPolicy.KldFreeSpaceHeadroomBytes
                  }, ct);
    }
}
