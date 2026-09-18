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

/// <summary>
///     Changes a project's quant-fidelity settings. Unlike every other project write this one is allowed on a FROZEN
///     project: the settings decide what gets measured next, not what the existing runs were measured against.
///     <para>
///         A base-model or chunk-count change mints a new expected comparability digest, so figures measured under the
///         old one start reading as <c>kld-stale</c>. Nothing is deleted and no attempt is rewritten — the stale
///         reading IS the honest answer, and the operator re-measures the runs they care about.
///     </para>
/// </summary>
public sealed class UpdateBenchmarkProjectFidelityEndpoint : Endpoint<UpdateBenchmarkProjectFidelityRequest, BenchmarkProjectFidelityChangeResponse>
{
    private readonly IBenchmarkProjectService _projects;
    private readonly BenchmarkRecordService _records;

    public UpdateBenchmarkProjectFidelityEndpoint(IBenchmarkProjectService projects, BenchmarkRecordService records)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(records);
        _projects = projects;
        _records = records;
    }

    public override void Configure()
    {
        Patch(LocalApiRoutes.Benchmarks.ProjectFidelity);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status400BadRequest)
                                      .ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(UpdateBenchmarkProjectFidelityRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        var change = await _projects.UpdateFidelityAsync(req.ProjectId,
                                        req.ExpectedVersion,
                                        new BenchmarkProjectFidelitySettings(req.FidelityEnabled,
                                            req.FidelityKldEnabled,
                                            req.FidelityChunks,
                                            req.FidelityKldBaseModelName),
                                        req.MeasureExisting,
                                        ct);
        var runCount = await _records.CountRunsAsync(req.ProjectId, ct);
        await Send.OkAsync(new BenchmarkProjectFidelityChangeResponse
                  {
                      Project = await BenchmarkProjectDetailProjection.ReadAsync(_records, change.Project, runCount, ct),
                      EnqueuedRunIds = change.EnqueuedRunIds,
                      EnqueuedCount = change.EnqueuedRunIds.Count
                  }, ct);
    }
}

/// <summary>Re-measures one run's quant fidelity. A new immutable attempt, never an overwrite of the last one.</summary>
public sealed class StartBenchmarkRunFidelityEndpoint : Endpoint<StartRunFidelityRequest>
{
    private readonly IBenchmarkQueueSignal _signal;
    private readonly BenchmarkRecordService _records;

    public StartBenchmarkRunFidelityEndpoint(BenchmarkRecordService records, IBenchmarkQueueSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(records);
        _signal = signal;
        _records = records;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Benchmarks.RunFidelity);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(StartRunFidelityRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        var run = await _records.GetRunAsync(req.RunId, ct);
        if (run is null)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkNotFoundException("Benchmark run was not found.")));
            return;
        }

        var project = await _records.GetProjectAsync(run.ProjectId, ct);
        _ = await _records.EnqueueFidelityAsync(req.RunId, project?.FidelityKldEnabled == true ? "kld" : "ppl", ct);
        _signal.Wake();
        await Send.ResultAsync(Results.Accepted());
    }
}

/// <summary>The immutable measurement history behind a run's displayed numbers.</summary>
public sealed class ListBenchmarkFidelityAttemptsEndpoint : Endpoint<ListBenchmarkFidelityAttemptsRequest, ListBenchmarkFidelityAttemptsResponse>
{
    private readonly BenchmarkRecordService _records;

    public ListBenchmarkFidelityAttemptsEndpoint(BenchmarkRecordService records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _records = records;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Benchmarks.RunFidelityAttempts);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(ListBenchmarkFidelityAttemptsRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (await _records.GetRunAsync(req.RunId, ct) is null)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkNotFoundException("Benchmark run was not found.")));
            return;
        }

        var attempts = await _records.ListFidelityAttemptsAsync(req.RunId, ct);
        await Send.OkAsync(new ListBenchmarkFidelityAttemptsResponse
                  {
                      Items =
                      [
                          .. attempts.Select(static attempt => new BenchmarkFidelityAttemptResponse
                          {
                              Id = attempt.Id,
                              Sequence = attempt.Sequence,
                              Kind = attempt.Kind,
                              Status = attempt.Status.ToString(),
                              PerplexityMean = attempt.PerplexityMean,
                              PerplexityStdErr = attempt.PerplexityStdErr,
                              PerplexityChunks = attempt.PerplexityChunks,
                              PerplexityContextTokens = attempt.PerplexityContextTokens,
                              CorpusId = attempt.CorpusId,
                              KldMean = attempt.KldMean,
                              KldP99 = attempt.KldP99,
                              TopTokenAgreement = attempt.TopTokenAgreement,
                              BaseModelName = attempt.BaseModelName,
                              BaseModelContentFingerprint = attempt.BaseModelContentFingerprint,
                              BaseLogitsDigest = attempt.BaseLogitsDigest,
                              ErrorMessage = attempt.ErrorMessage,
                              EnqueuedAtUtc = attempt.EnqueuedAtUtc,
                              StartedAtUtc = attempt.StartedAtUtc,
                              CompletedAtUtc = attempt.CompletedAtUtc
                          })
                      ]
                  }, ct);
    }
}

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
