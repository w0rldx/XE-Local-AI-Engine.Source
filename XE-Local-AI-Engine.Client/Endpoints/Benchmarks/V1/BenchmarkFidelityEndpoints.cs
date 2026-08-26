namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using System.Globalization;
using FastEndpoints;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>
///     What enabling KL divergence will cost on disk, answered BEFORE the operator commits. The number is an estimate
///     over a measured bytes-per-logit constant, and the formula is returned with it so the figure is checkable rather
///     than magic.
/// </summary>
public sealed class GetBenchmarkKldDiskEstimateEndpoint(IBenchmarkStore store, BenchmarkKldBaseCache cache)
    : Endpoint<GetKldDiskEstimateRequest, GetKldDiskEstimateResponse>
{
    private readonly BenchmarkKldBaseCache _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    private readonly IBenchmarkStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Get(LocalApiRoutes.Benchmarks.ProjectKldEstimate);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(GetKldDiskEstimateRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        var project = await _store.GetProjectAsync(req.ProjectId, ct).ConfigureAwait(false);
        if (project is null)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkNotFoundException("Benchmark project was not found."))).ConfigureAwait(false);
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
                  }, ct)
                  .ConfigureAwait(false);
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
public sealed class UpdateBenchmarkProjectFidelityEndpoint(IBenchmarkProjectService projects, IBenchmarkStore store)
    : Endpoint<UpdateBenchmarkProjectFidelityRequest, BenchmarkProjectFidelityChangeResponse>
{
    private readonly IBenchmarkProjectService _projects = projects ?? throw new ArgumentNullException(nameof(projects));
    private readonly IBenchmarkStore _store = store ?? throw new ArgumentNullException(nameof(store));

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
        try
        {
            var change = await _projects.UpdateFidelityAsync(req.ProjectId,
                                            req.ExpectedVersion,
                                            new BenchmarkProjectFidelitySettings(req.FidelityEnabled,
                                                req.FidelityKldEnabled,
                                                req.FidelityChunks,
                                                req.FidelityKldBaseModelName),
                                            req.MeasureExisting,
                                            ct)
                                        .ConfigureAwait(false);
            var runCount = await _store.CountRunsAsync(req.ProjectId, ct).ConfigureAwait(false);
            await Send.OkAsync(new BenchmarkProjectFidelityChangeResponse
                      {
                          Project = change.Project.ToDetail(runCount,
                              await BenchmarkJudgePolicyProjection.ReadAsync(_store, req.ProjectId, ct).ConfigureAwait(false)),
                          EnqueuedRunIds = change.EnqueuedRunIds,
                          EnqueuedCount = change.EnqueuedRunIds.Count
                      }, ct)
                      .ConfigureAwait(false);
        }
        catch (Exception exception) when (BenchmarkExceptionFilter.IsHandled(exception))
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(exception)).ConfigureAwait(false);
        }
    }
}

/// <summary>Re-measures one run's quant fidelity. A new immutable attempt, never an overwrite of the last one.</summary>
public sealed class StartBenchmarkRunFidelityEndpoint(IBenchmarkStore store, IBenchmarkQueueSignal signal)
    : Endpoint<StartRunFidelityRequest>
{
    private readonly IBenchmarkQueueSignal _signal = signal ?? throw new ArgumentNullException(nameof(signal));
    private readonly IBenchmarkStore _store = store ?? throw new ArgumentNullException(nameof(store));

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
        var run = await _store.GetRunAsync(req.RunId, ct).ConfigureAwait(false);
        if (run is null)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkNotFoundException("Benchmark run was not found."))).ConfigureAwait(false);
            return;
        }

        var project = await _store.GetProjectAsync(run.ProjectId, ct).ConfigureAwait(false);
        try
        {
            _ = await _store.EnqueueFidelityAsync(req.RunId, project?.FidelityKldEnabled == true ? "kld" : "ppl", ct).ConfigureAwait(false);
        }
        catch (BenchmarkStoreException exception)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(exception)).ConfigureAwait(false);
            return;
        }

        _signal.Wake();
        await Send.ResultAsync(Results.Accepted()).ConfigureAwait(false);
    }
}

/// <summary>The immutable measurement history behind a run's displayed numbers.</summary>
public sealed class ListBenchmarkFidelityAttemptsEndpoint(IBenchmarkStore store)
    : Endpoint<ListBenchmarkFidelityAttemptsRequest, ListBenchmarkFidelityAttemptsResponse>
{
    private readonly IBenchmarkStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Get(LocalApiRoutes.Benchmarks.RunFidelityAttempts);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(ListBenchmarkFidelityAttemptsRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (await _store.GetRunAsync(req.RunId, ct).ConfigureAwait(false) is null)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkNotFoundException("Benchmark run was not found."))).ConfigureAwait(false);
            return;
        }

        var attempts = await _store.ListFidelityAttemptsAsync(req.RunId, ct).ConfigureAwait(false);
        await Send.OkAsync(new ListBenchmarkFidelityAttemptsResponse
                  {
                      Items = [.. attempts.Select(static attempt => new BenchmarkFidelityAttemptResponse
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
                      })]
                  }, ct)
                  .ConfigureAwait(false);
    }
}

/// <summary>
///     Clears the base-logit cache. Refused while any fidelity work item is live: deleting a file a queued
///     measurement is on its way to reading would fail that measurement for a reason the operator never sees.
/// </summary>
public sealed class ClearBenchmarkFidelityCacheEndpoint(IBenchmarkStore store, BenchmarkKldBaseCache cache)
    : Endpoint<GetKldDiskEstimateRequest>
{
    private readonly BenchmarkKldBaseCache _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    private readonly IBenchmarkStore _store = store ?? throw new ArgumentNullException(nameof(store));

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
        if (await _store.GetProjectAsync(req.ProjectId, ct).ConfigureAwait(false) is null)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkNotFoundException("Benchmark project was not found."))).ConfigureAwait(false);
            return;
        }

        if (await _store.HasLiveFidelityWorkAsync(ct).ConfigureAwait(false))
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkConflictException("FidelityWorkInFlight"))).ConfigureAwait(false);
            return;
        }

        _cache.Clear();
        await Send.NoContentAsync(ct).ConfigureAwait(false);
    }
}
