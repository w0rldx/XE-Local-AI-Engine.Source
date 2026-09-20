namespace XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;

using System.Globalization;
using System.Text;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.ModelFit.Catalog;
using XE_Local_AI_Engine.Client.Services.ModelFit.Fit;
using XE_Local_AI_Engine.Client.Services.ModelFit.Validation;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     The local model advisor — the box-aware rewrite of the Docker/llmfit recommendation backend.
/// </summary>
/// <remarks>
///     Each refresh profiles the node hardware via the device-audited effective profile from
///     <see cref="IRuntimeDeviceAudit" /> — so a silent CPU fallback sizes models against system RAM — then discovers
///     candidate GGUF repos, estimates each file's memory fit, ranks the survivors and replaces the cached snapshot.
///     THREE SEAMS STAY SEPARATE: it never spawns or downloads during a refresh (<see cref="DownloadAsync" /> /
///     <see cref="StartAsync" /> do), owns no scheduler state and publishes no SignalR, and never logs a raw payload.
/// </remarks>
public sealed class ModelFitRefreshService : IModelFitRefreshService
{
    /// <summary>Sentinel written into the plaintext <c>ApprovedImageId</c> column now that approved images are gone.</summary>
    private const string AdvisorSnapshotSource = "local-advisor";

    /// <summary>Sentinel written into the plaintext <c>ProviderName</c> column — the advisor targets llama.cpp only.</summary>
    private const string AdvisorProviderName = "llama.cpp";

    /// <summary>Default context window the KV-cache fit is sized against when the request supplies no <c>CtxTarget</c>.</summary>
    private const int DefaultCtxTarget = 8192;

    /// <summary>How many GGUF repos to inspect per refresh (each inspect is an HTTP range read, not a download).</summary>
    private const int DefaultRepoSearchLimit = 12;

    /// <summary>Capability-tier granularity for ranking (~1 GiB).</summary>
    /// <remarks>
    ///     Fitting models are bucketed by estimated footprint ÷ this so a trivially-larger model does not always
    ///     outrank a much newer or more popular peer; within a tier the download and recency boosts decide.
    /// </remarks>
    private const long CapabilityBucketBytes = 1024L * 1024 * 1024;

    /// <summary>
    ///     Max concurrent HF inspections per refresh. Bounded so a refresh is fast (the repos are inspected in parallel
    ///     rather than one-at-a-time) yet polite to HuggingFace (we never open more than this many range reads at once).
    /// </summary>
    private const int MaxConcurrentRepoInspections = 5;

    /// <summary>Per-HF-call timeout.</summary>
    /// <remarks>
    ///     A single stalled repo inspection, or the initial search, must not block the whole refresh, so each call is
    ///     wrapped in a linked CTS cancelled after this budget. A repo that times out is skipped and the refresh still
    ///     succeeds with the other candidates; a search timeout surfaces as a clean Failed run.
    /// </remarks>
    private static readonly TimeSpan PerHuggingFaceCallTimeout = TimeSpan.FromSeconds(20);

    private readonly ICatalogRecommendationService _catalogRecommendationService;
    private readonly IHuggingFaceGgufDiscovery _discovery;
    private readonly MemoryFitEstimator _estimator;

    private readonly IRuntimeDeviceAudit _runtimeAudit;
    private readonly ILogger<ModelFitRefreshService> _logger;
    private readonly IGgufModelRegistry _modelRegistry;
    private readonly IGgufModelStore _modelStore;
    private readonly IModelFitRecommendationStore _recommendationStore;
    private readonly ModelFitRequestValidator _requestValidator;
    private readonly IModelFitSnapshotStore _snapshotStore;
    private readonly ILlamaServerProcessSupervisor _supervisor;
    private readonly TimeProvider _timeProvider;

    public ModelFitRefreshService(IRuntimeDeviceAudit runtimeAudit,
        IHuggingFaceGgufDiscovery discovery,
        MemoryFitEstimator estimator,
        IGgufModelStore modelStore,
        IGgufModelRegistry modelRegistry,
        ILlamaServerProcessSupervisor supervisor,
        ModelFitRequestValidator requestValidator,
        IModelFitSnapshotStore snapshotStore,
        IModelFitRecommendationStore recommendationStore,
        ICatalogRecommendationService catalogRecommendationService,
        TimeProvider timeProvider,
        ILogger<ModelFitRefreshService> logger)
    {
        _runtimeAudit = runtimeAudit ?? throw new ArgumentNullException(nameof(runtimeAudit));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _estimator = estimator ?? throw new ArgumentNullException(nameof(estimator));
        _modelStore = modelStore ?? throw new ArgumentNullException(nameof(modelStore));
        _modelRegistry = modelRegistry ?? throw new ArgumentNullException(nameof(modelRegistry));
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _requestValidator = requestValidator ?? throw new ArgumentNullException(nameof(requestValidator));
        _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
        _recommendationStore = recommendationStore ?? throw new ArgumentNullException(nameof(recommendationStore));
        _catalogRecommendationService = catalogRecommendationService ?? throw new ArgumentNullException(nameof(catalogRecommendationService));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ModelFitRefreshResult> RefreshAsync(ModelFitRefreshRequest request,
        Func<string, int?, CancellationToken, Task>? reportProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Only Recommend is supported; reject every other operation before creating a snapshot row.
        if (request.Operation != ModelFitOperation.Recommend)
        {
            return Failed(snapshotId: null, "Benchmark refresh is not yet enabled.");
        }

        // Pre-run config validation: intent params. The advisor targets llama.cpp, so the provider is fixed to the
        // sentinel for the (still-provider-aware) validator's allowlist of use-case + limit bounds.
        var validationError = _requestValidator.GetValidationError(request.Operation,
            request.UseCase,
            request.Limit,
            AdvisorProviderName,
            modelName: null);
        if (validationError is not null)
        {
            return Failed(snapshotId: null, validationError);
        }

        var quant = string.IsNullOrWhiteSpace(request.QuantOverride) ? MemoryFitEstimator.DefaultQuant : request.QuantOverride.Trim();
        var ctxTarget = request.CtxTarget is > 0 ? request.CtxTarget.Value : DefaultCtxTarget;

        // Open the Running snapshot row (sentinel image/provider — the approved-image concept is gone).
        var startedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var snapshot = await _snapshotStore.CreateRunningAsync(new ModelFitSnapshotInput
        {
            ApprovedImageId = AdvisorSnapshotSource,
            Operation = request.Operation,
            UseCase = request.UseCase,
            ProviderName = AdvisorProviderName,
            ModelName = null,
            Status = ModelFitRunStatus.Running,
            StartedAtUtc = startedAtUtc
        },
            cancellationToken);

        var snapshotId = snapshot.Id;

        if (reportProgress is not null)
        {
            await reportProgress("Profiling hardware and discovering GGUF models…", arg2: null, cancellationToken);
        }

        try
        {
            // Size against the EFFECTIVE profile — degraded to CPU-mode when the device audit reports a silent
            // CPU fallback — so the advisor never recommends models that only fit in VRAM the runtime cannot actually use.
            var profile = await _runtimeAudit.GetEffectiveProfileAsync(forceRefreshProfile: false, cancellationToken);
            var recommendations = await BuildRecommendationsAsync(request, quant, ctxTarget, profile, cancellationToken);

            var completedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

            // Serialize the ranked fits to the advisor recommendation JSON and parse them through the reused scaffold.
            var advisorJson = SerializeAdvisorJson(recommendations, profile);
            var parse = RecommendationJsonParser.Parse(advisorJson);
            if (!parse.IsSuccess)
            {
                // Should not happen (we emit the JSON ourselves) — record Failed and never throw out of the service.
                await _snapshotStore.MarkTerminalAsync(snapshotId,
                    ModelFitRunStatus.Failed,
                    exitCode: null,
                    completedAtUtc - startedAtUtc,
                    advisorJson,
                    stderrExcerpt: null,
                    "advisor recommendation JSON parse failed",
                    completedAtUtc,
                    cancellationToken);

                _logger.LogWarning("Advisor emitted unparseable recommendation JSON for snapshot {SnapshotId}.", snapshotId);
                return Failed(snapshotId, "Advisor recommendation could not be assembled.");
            }

            await _snapshotStore.MarkTerminalAsync(snapshotId,
                ModelFitRunStatus.Succeeded,
                exitCode: 0,
                completedAtUtc - startedAtUtc,
                advisorJson,
                stderrExcerpt: null,
                parse.SystemDiagnosticsJson,
                completedAtUtc,
                cancellationToken);

            var inserted = await _recommendationStore.ReplaceForSnapshotAsync(snapshotId, parse.Recommendations, cancellationToken);

            _logger.LogInformation("Advisor refresh succeeded for snapshot {SnapshotId} (use-case {UseCase}, {Count} recommendations, {Mode}).",
                snapshotId,
                request.UseCase ?? "(default)",
                inserted,
                profile is { GpuAccelAvailable: true, VramKnown: true } ? "GPU" : "CPU");

            return new ModelFitRefreshResult { SnapshotId = snapshotId, Status = ModelFitRunStatus.Succeeded, RecommendationCount = inserted, SanitizedError = null };
        }
        catch (OperationCanceledException)
        {
            // The node token was cancelled mid-refresh: record Cancelled (NOT Failed), then re-throw so the dispatcher marks the scheduler run cancelled.
            // The terminal write uses CancellationToken.None, because the run is ending precisely because its own token was cancelled.
            var cancelledAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            await _snapshotStore.MarkTerminalAsync(snapshotId,
                ModelFitRunStatus.Cancelled,
                exitCode: null,
                cancelledAtUtc - startedAtUtc,
                rawJson: null,
                stderrExcerpt: null,
                diagnosticsJson: null,
                cancelledAtUtc,
                CancellationToken.None);

            _logger.LogInformation("Advisor refresh cancelled for snapshot {SnapshotId}.", snapshotId);
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or IOException or TimeoutException or HuggingFaceDownloadException)
        {
            // A discovery/network failure must be recorded as a Failed run with a sanitized reason — never throw the raw
            // exception out of the service. The reason is generic (no URL / payload).
            var failedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            await _snapshotStore.MarkTerminalAsync(snapshotId,
                ModelFitRunStatus.Failed,
                exitCode: null,
                failedAtUtc - startedAtUtc,
                rawJson: null,
                stderrExcerpt: null,
                "GGUF discovery failed",
                failedAtUtc,
                cancellationToken);

            _logger.LogWarning(exception, "Advisor refresh failed during GGUF discovery for snapshot {SnapshotId}.", snapshotId);
            return Failed(snapshotId, "GGUF discovery failed.");
        }
    }

    /// <summary>
    ///     Operator-driven download of a chosen GGUF file (a model-fit endpoint calls this). Delegates to
    ///     <see cref="IGgufModelStore.EnsureModelAsync" />; the advisor never downloads during a refresh.
    /// </summary>
    public Task<GgufModelHandle> DownloadAsync(GgufModelRequest request,
        IProgress<PullProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _modelStore.EnsureModelAsync(request, progress, cancellationToken);
    }

    /// <summary>Operator-driven start of a downloaded model (a model-fit endpoint calls this).</summary>
    /// <remarks>
    ///     Delegates to the llama-server process supervisor's ensure-running for the requested role and returns the
    ///     local endpoint; the advisor never spawns during a refresh.
    /// </remarks>
    public Task<LlamaServerEndpoint> StartAsync(string modelName, ModelRole role, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        return _supervisor.EnsureRunningAsync(modelName, role, cancellationToken);
    }

    /// <summary>
    ///     Builds the merged recommendation set: the curated catalog lane (PRIMARY — "recommended" / "canRun"
    ///     sections) followed by the live Hugging Face discovery lane, the secondary "explore" section.
    /// </summary>
    /// <remarks>
    ///     A catalog-lane failure never fails the whole refresh — the explore lane still succeeds on its own (see
    ///     <see cref="BuildCatalogRecommendationsAsync" />).
    /// </remarks>
    private async Task<IReadOnlyList<AdvisorRecommendation>> BuildRecommendationsAsync(ModelFitRefreshRequest request,
        string quant,
        int ctxTarget,
        HardwareProfile profile,
        CancellationToken cancellationToken)
    {
        // Which models are already downloaded (best-effort: a registry failure just marks all as not-installed).
        var installedKeys = await ListInstalledKeysAsync(cancellationToken);

        var catalogRecommendations = await BuildCatalogRecommendationsAsync(request, quant, ctxTarget, profile, installedKeys, cancellationToken);
        var exploreRecommendations = await BuildExploreRecommendationsAsync(request, quant, ctxTarget, profile, installedKeys, cancellationToken);

        return [.. catalogRecommendations, .. exploreRecommendations];
    }

    /// <summary>
    ///     Runs the catalog ranking lane and maps its "Recommended" / "Can run" sections (each already ordered tier →
    ///     fit class → quant quality → recency → id) to <see cref="AdvisorRecommendation" /> rows, capped at the limit.
    /// </summary>
    /// <remarks>
    ///     Any failure (catalog provider, discovery, estimator) is caught and logged — the catalog lane degrading to
    ///     empty must never fail the run, since the explore lane alone is still a useful recommendation set.
    /// </remarks>
    private async Task<IReadOnlyList<AdvisorRecommendation>> BuildCatalogRecommendationsAsync(ModelFitRefreshRequest request,
        string quant,
        int ctxTarget,
        HardwareProfile profile,
        HashSet<string> installedKeys,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _catalogRecommendationService
                               .BuildRecommendationsAsync(request.UseCase, quant, ctxTarget, profile, installedKeys, cancellationToken);

            var recommended = result.Recommended.Take(request.Limit).Select(candidate => ToAdvisorRecommendation(candidate, "recommended"));
            var canRun = result.CanRun.Take(request.Limit).Select(candidate => ToAdvisorRecommendation(candidate, "canRun"));
            return [.. recommended, .. canRun];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TimeoutException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Catalog recommendation lane failed; the run continues with the explore lane only.");
            return [];
        }
    }

    private static AdvisorRecommendation ToAdvisorRecommendation(CatalogRecommendationCandidate candidate, string section)
    {
        var entry = candidate.Entry;
        var releaseDate = DateOnly.TryParse(entry.ReleaseDate, CultureInfo.InvariantCulture, out var parsed)
            ? new DateTimeOffset(parsed.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            : default;

        return new AdvisorRecommendation
        {
            RepoId = entry.GgufRepo,
            ModelName = candidate.ModelName,
            FileName = candidate.File.FileName,
            Quant = candidate.File.Quant,
            Estimate = candidate.Estimate,
            IsInstalled = candidate.IsInstalled,
            IsTrustedPublisher = GgufPublisherTrust.IsTrustedPublisher(entry.GgufRepo),
            Downloads = 0,
            LastModified = releaseDate,
            Section = section,
            Tier = entry.Tier,
            CatalogId = entry.Id,
            CatalogDisplayName = entry.DisplayName,
            CatalogNotes = entry.Notes,
            KvQuantAdvisory = candidate.KvQuantAdvisory,
            KvBytesPerTokenAtCtx = candidate.KvBytesPerTokenAtCtx,
            KvBytesPerTokenQuant = candidate.KvBytesPerTokenAtCtx is null ? null : KvCacheQuant.Q8_0,
            AttentionArchTag = candidate.AttentionArchTag
        };
    }

    /// <summary>
    ///     Discovers candidate GGUF repos, inspects their files, estimates each file's fit at the chosen quant, drops
    ///     the non-fitting and insufficient-metadata files, and ranks the survivors by descending headroom.
    /// </summary>
    /// <remarks>The secondary "explore" section — the catalog lane is the primary recommendation source.</remarks>
    private async Task<IReadOnlyList<AdvisorRecommendation>> BuildExploreRecommendationsAsync(ModelFitRefreshRequest request,
        string quant,
        int ctxTarget,
        HardwareProfile profile,
        HashSet<string> installedKeys,
        CancellationToken cancellationToken)
    {
        // Discover candidate repos by the use-case's mapped search terms (see ModelFitUseCaseSearch — the literal
        // use-case word under-matches the Hub), merged + de-duped and capped to the inspection budget.
        var repos = await SearchCandidateReposAsync(request.UseCase, cancellationToken);

        // Inspect the repos in parallel with bounded concurrency. A stalled or failing repo is skipped (null candidate) inside the body, so it never reaches the caller's outer catch and never fails
        // the whole run. The semaphore caps concurrent HF range reads; the tasks are materialized and awaited BEFORE the gate is disposed, so every inspection completes while the gate is still alive.
        var inspectionGate = new SemaphoreSlim(MaxConcurrentRepoInspections, MaxConcurrentRepoInspections);
        AdvisorRecommendation?[] candidates;
        try
        {
            var evaluations = repos
                              .Select(repo => EvaluateRepoWithGuardAsync(repo, quant, ctxTarget, profile, installedKeys, inspectionGate, cancellationToken))
                              .ToList();
            candidates = await Task.WhenAll(evaluations);
        }
        finally
        {
            inspectionGate.Dispose();
        }

        // Rank the models that FIT, then cap. Capability-first but BUCKETED to ~1 GiB, so a trivially-larger model cannot outrank a much newer or far more popular peer; downloads, recency
        // and trust decide WITHIN a tier, with repo id last so the order never depends on which inspection finished first. See docs/wiki/07-model-fit.md ("The refresh service (the advisor)").
        return candidates
               .Where(candidate => candidate is not null)
               .Select(candidate => candidate!)
               .OrderByDescending(candidate => candidate.Estimate.EstimatedBytes / CapabilityBucketBytes)
               .ThenByDescending(candidate => candidate.Downloads)
               .ThenByDescending(candidate => candidate.LastModified)
               .ThenByDescending(candidate => candidate.IsTrustedPublisher)
               .ThenBy(candidate => candidate.RepoId, StringComparer.Ordinal)
               .Take(request.Limit)
               .ToList();
    }

    /// <summary>
    ///     Discovers candidate GGUF repos for a use-case by running one trending search per mapped term
    ///     (<see cref="ModelFitUseCaseSearch" />), then merging the per-term results round-robin.
    /// </summary>
    /// <remarks>
    ///     Round-robin gives fair representation across terms; the merge is de-duped by repo id and capped to
    ///     <see cref="DefaultRepoSearchLimit" /> so the downstream per-repo header reads stay bounded regardless of the
    ///     term count. A single term failing or timing out is tolerated; only when EVERY term's search fails is a
    ///     timeout surfaced, so the run records a clean Failed instead of an empty Succeeded hiding an unreachable Hub.
    /// </remarks>
    private async Task<IReadOnlyList<GgufRepoSummary>> SearchCandidateReposAsync(string? useCase, CancellationToken cancellationToken)
    {
        var terms = ModelFitUseCaseSearch.Resolve(useCase);

        // Two discovery passes per term: Trending (current download/like velocity) AND LastModified (most recently updated). The recency pass surfaces newly-released big models a trending-only
        // search misses once the pool is capped. Trending keeps the established popular repos, and both passes are merged round-robin so neither crowds the other out of the bounded inspection budget.
        var searches = terms
                       .SelectMany(term => new[]
                       {
                           SearchSingleTermAsync(term, GgufSearchSort.Trending, cancellationToken),
                           SearchSingleTermAsync(term, GgufSearchSort.LastModified, cancellationToken)
                       })
                       .ToList();

        var results = await Task.WhenAll(searches);

        if (results.All(static result => result is null))
        {
            // Every search failed (e.g. the Hub is unreachable / every call timed out): surface a timeout so the caller's
            // outer catch records a clean Failed run rather than a misleading empty Succeeded.
            throw new TimeoutException("GGUF discovery search failed for every use-case term.");
        }

        var lists = results.Where(static result => result is not null).Select(static result => result!).ToList();
        return MergeReposRoundRobin(lists, DefaultRepoSearchLimit);
    }

    /// <summary>Runs one GGUF search for a single term under the given <paramref name="sort" /> and a per-call timeout.</summary>
    /// <remarks>
    ///     Returns the results, or <see langword="null" /> when this search timed out or failed (network, IO, parse) —
    ///     a tolerated outcome merged away by <see cref="SearchCandidateReposAsync" />. Genuine outer-token
    ///     cancellation (node shutdown) is re-thrown unchanged so the run records Cancelled.
    /// </remarks>
    private async Task<IReadOnlyList<GgufRepoSummary>?> SearchSingleTermAsync(string term, GgufSearchSort sort, CancellationToken cancellationToken)
    {
        var query = new GgufSearchQuery
        {
            SearchText = term,
            Limit = DefaultRepoSearchLimit,
            // Sort is supplied by the caller: Trending (HF recency-weighted popularity) for the established-popular pass, LastModified for the freshness pass.
            // The publisher-trust signal and the fit ranking keep quality up without excluding any repo from the candidate pool.
            Sort = sort
        };

        using var searchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        searchCts.CancelAfter(PerHuggingFaceCallTimeout);
        try
        {
            return await _discovery.SearchAsync(query, searchCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The per-term timeout CTS fired (not the node token): tolerate this term and let the others stand in.
            _logger.LogDebug("GGUF discovery search timed out for a use-case term.");
            return null;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException
                                              or TimeoutException or HuggingFaceDownloadException)
        {
            _logger.LogDebug(exception, "GGUF discovery search failed for a use-case term.");
            return null;
        }
    }

    /// <summary>
    ///     Merges the per-term (already trending-sorted) repo lists round-robin — each list's 1st, then each list's
    ///     2nd, … — de-duped by repo id (case-insensitive, first occurrence wins) and capped to <paramref name="cap" />.
    /// </summary>
    /// <remarks>
    ///     Round-robin keeps every term fairly represented in the bounded candidate pool instead of letting the first
    ///     term's results crowd out the rest.
    /// </remarks>
    private static IReadOnlyList<GgufRepoSummary> MergeReposRoundRobin(IReadOnlyList<IReadOnlyList<GgufRepoSummary>> lists, int cap)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<GgufRepoSummary>(cap);
        var longest = lists.Count == 0 ? 0 : lists.Max(static list => list.Count);

        for (var index = 0; index < longest && merged.Count < cap; index++)
        {
            foreach (var list in lists)
            {
                if (index >= list.Count)
                {
                    continue;
                }

                var repo = list[index];
                if (seen.Add(repo.RepoId))
                {
                    merged.Add(repo);
                    if (merged.Count >= cap)
                    {
                        break;
                    }
                }
            }
        }

        return merged;
    }

    /// <summary>
    ///     Bounded-concurrency, fault-isolated wrapper around <see cref="EvaluateRepoAsync" />: acquires the
    ///     inspection gate and applies a per-repo timeout via a linked CTS.
    /// </summary>
    /// <remarks>
    ///     Any single-repo failure (network, IO, timeout, parse) becomes a <see langword="null" /> candidate so one
    ///     bad repo never fails the whole refresh. Outer-token cancellation (node shutdown) is re-thrown so the run
    ///     records Cancelled.
    /// </remarks>
    private async Task<AdvisorRecommendation?> EvaluateRepoWithGuardAsync(GgufRepoSummary summary,
        string quant,
        int ctxTarget,
        HardwareProfile profile,
        HashSet<string> installedKeys,
        SemaphoreSlim inspectionGate,
        CancellationToken cancellationToken)
    {
        await inspectionGate.WaitAsync(cancellationToken);
        try
        {
            using var repoCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            repoCts.CancelAfter(PerHuggingFaceCallTimeout);

            return await EvaluateRepoAsync(summary, quant, ctxTarget, profile, installedKeys, repoCts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Node shutdown: propagate so the refresh records Cancelled (not a silently-skipped repo).
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TimeoutException
                                              or OperationCanceledException or HuggingFaceDownloadException
                                              or JsonException or FormatException or InvalidOperationException)
        {
            // A single repo timed out or failed (its per-repo CTS fired, or the inspect/parse threw): skip it. The refresh
            // still succeeds with the other candidates — this must never bubble to the caller's outer Failed/Cancelled catch.
            _logger.LogDebug(exception, "Skipping GGUF repo during advisor refresh (inspection failed or timed out).");
            return null;
        }
        finally
        {
            inspectionGate.Release();
        }
    }

    /// <summary>
    ///     Inspects one repo and walks the quant ladder to pick the highest-quality quant whose memory-fit estimate
    ///     fits the budget, down to the <see cref="QuantLadder.DefaultFloorQuant" /> quality floor.
    /// </summary>
    /// <remarks>
    ///     <see langword="null" /> when the repo has no usable file, no file has the metadata to compute a weights
    ///     term, or no quant at or above the floor fits the budget. The ladder walk is what keeps a large new model —
    ///     whose default <c>Q4_K_M</c> is too big — in the list at the largest quant that actually runs.
    /// </remarks>
    private async Task<AdvisorRecommendation?> EvaluateRepoAsync(GgufRepoSummary summary,
        string quant,
        int ctxTarget,
        HardwareProfile profile,
        HashSet<string> installedKeys,
        CancellationToken cancellationToken)
    {
        var repoId = summary.RepoId;
        var detail = await _discovery.InspectRepoAsync(repoId, cancellationToken);

        var selected = GgufFileSelector.SelectBestFit(_estimator, detail.Files, quant, ctxTarget, profile);
        if (selected is null)
        {
            return null;
        }

        var (file, estimate) = selected;
        var modelName = GgufModelName.Format(repoId, file.Quant);
        return new AdvisorRecommendation
        {
            RepoId = repoId,
            ModelName = modelName,
            FileName = file.FileName,
            Quant = file.Quant,
            Estimate = estimate,
            IsInstalled = installedKeys.Contains(modelName),
            IsTrustedPublisher = summary.IsTrustedPublisher,
            Downloads = summary.Downloads,
            LastModified = summary.LastModified,
            Section = "explore"
        };
    }

    private async Task<HashSet<string>> ListInstalledKeysAsync(CancellationToken cancellationToken)
    {
        try
        {
            var installed = await _modelRegistry.ListAsync(cancellationToken);
            return installed.Select(entry => entry.ModelName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug(exception, "Could not list installed GGUF models for advisor install-state; reporting all not-installed.");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    ///     Emits the advisor recommendation JSON in the <c>{ models:[…], system:{…} }</c> shape the reused
    ///     <see cref="RecommendationJsonParser" /> consumes.
    /// </summary>
    /// <remarks>
    ///     Same field names the parser maps: <c>name</c>, <c>best_quant</c>, <c>memory_required_gb</c>,
    ///     <c>vram_required_gb</c>, <c>fit_level</c>, <c>run_mode</c>, <c>context_length</c>, <c>installed</c>,
    ///     <c>score</c>. The <c>vram_required_gb</c>/<c>memory_required_gb</c> fields carry the estimate so the parser
    ///     fills <c>RequiredVramMb</c>/<c>RequiredRamMb</c> (today null).
    /// </remarks>
    private static string SerializeAdvisorJson(IReadOnlyList<AdvisorRecommendation> recommendations, HardwareProfile profile)
    {
        // The fit budget the score is normalized against, read from the estimator rather than re-derived here.
        // A duplicated copy of this expression was missed when the GPU budget moved from total VRAM to free VRAM, so the score below was normalized against a budget the fit verdicts no longer used.
        var budgetBytes = MemoryFitEstimator.ResolveFitBudgetBytes(profile);

        using var buffer = new MemoryStream();
#pragma warning disable MA0045 // Utf8JsonWriter over an in-memory buffer: no I/O to await; synchronous canonical-bytes function.
        using (var writer = new Utf8JsonWriter(buffer))
#pragma warning restore MA0045
        {
            writer.WriteStartObject();

            writer.WriteStartArray("models");
            foreach (var recommendation in recommendations)
            {
                var estimate = recommendation.Estimate;
                var estimatedGb = estimate.EstimatedBytes / (double)(1024 * 1024 * 1024);

                writer.WriteStartObject();
                writer.WriteString("name", recommendation.ModelName);
                writer.WriteString("best_quant", recommendation.Quant);
                writer.WriteString("fit_level", estimate.Mode == FitMode.Gpu ? "GPU" : "CPU");
                writer.WriteString("run_mode", estimate.Mode.ToString());
                // Estimate confidence is "Approximate" when the KV geometry leaned on a derived head_dim, or the weights on the on-disk file size (missing explicit
                // header metadata), so the UI can present the figure conservatively. native_format flags a non-requantizable quant (MXFP4) priced at its own density.
                writer.WriteString("fit_confidence", estimate.Confidence.ToString());
                if (estimate.NativeQuantFormat)
                {
                    writer.WriteBoolean("native_format", value: true);
                }

                // Score is how fully the model uses the fit budget (0–100%), so the most-capable model that fits scores highest, matching the rank order.
                // A "headroom GB" score instead ranks the SMALLEST model highest and reads as a non-monotonic column. The parser stores the score verbatim.
                var score = budgetBytes > 0
                    ? Math.Clamp(Math.Round(estimate.EstimatedBytes / (double)budgetBytes * 100d, digits: 1), min: 0d, max: 100d)
                    : 0d;
                writer.WriteNumber("score", score);
                writer.WriteNumber("memory_required_gb", Math.Round(estimatedGb, digits: 3));
                if (estimate.Mode == FitMode.Gpu)
                {
                    writer.WriteNumber("vram_required_gb", Math.Round(estimatedGb, digits: 3));
                }

                writer.WriteString("repo_id", recommendation.RepoId);
                writer.WriteString("file_name", recommendation.FileName);
                writer.WriteBoolean("installed", recommendation.IsInstalled);

                // Catalog-lane fields: section splits the response into primary recommended/canRun catalog rows and secondary explore (live-HF) ones; tier/catalog metadata are null on an
                // explore row. expert_offload/gpu_gb/cpu_gb carry the MoE offload split, so the UI labels a model honestly ("experts on CPU — slower, higher quality"), not just a fit verdict.
                writer.WriteString("section", recommendation.Section);
                if (recommendation.Tier is not null)
                {
                    writer.WriteString("tier", recommendation.Tier);
                }

                if (recommendation.CatalogId is not null)
                {
                    writer.WriteString("catalog_id", recommendation.CatalogId);
                }

                if (recommendation.CatalogDisplayName is not null)
                {
                    writer.WriteString("catalog_display_name", recommendation.CatalogDisplayName);
                }

                if (recommendation.CatalogNotes is not null)
                {
                    writer.WriteString("catalog_notes", recommendation.CatalogNotes);
                }

                writer.WriteBoolean("expert_offload", estimate.ExpertsOffloaded);
                if (estimate is { ExpertsOffloaded: true, GpuBytes: not null, CpuBytes: not null })
                {
                    writer.WriteNumber("gpu_gb", Math.Round(estimate.GpuBytes.Value / (double)(1024 * 1024 * 1024), digits: 3));
                    writer.WriteNumber("cpu_gb", Math.Round(estimate.CpuBytes.Value / (double)(1024 * 1024 * 1024), digits: 3));
                }

                // Advisory-only quantized-KV estimate (catalog lane), never membership or ranking — those use the fp16 estimate above, the chat launch using an fp16 KV cache. It rides the
                // diagnostics blob (no column or DTO, like the catalog and expert-offload fields) so the UI can show the headroom a flash-attention runtime unlocks. Absent on explore-lane rows.
                if (recommendation.KvQuantAdvisory is { } kvAdvisory)
                {
                    writer.WriteString("kv_quant", kvAdvisory.Quant.ToString());
                    writer.WriteNumber("kv_quant_estimated_gb", Math.Round(kvAdvisory.EstimatedBytes / (double)(1024 * 1024 * 1024), digits: 3));
                    writer.WriteNumber("kv_quant_headroom_gb", Math.Round(kvAdvisory.HeadroomBytes / (double)(1024 * 1024 * 1024), digits: 3));
                    writer.WriteBoolean("kv_quant_fits", kvAdvisory.Fits);
                    writer.WriteBoolean("kv_quant_requires_flash_attention", kvAdvisory.RequiresFlashAttention);
                }

                // KV cost per context token and attention shape, at the chat launch's q8_0 element size (NOT the fp16 ranking estimate), so the figure answers "what will this cost me on this node".
                // The quant rides along: an unlabelled KV byte count is ambiguous by 2x. Absent when the header cannot size the KV term; null on a pre-existing snapshot.
                if (recommendation.KvBytesPerTokenAtCtx is { } kvBytesPerToken && recommendation.KvBytesPerTokenQuant is { } kvBytesQuant)
                {
                    writer.WriteNumber("kv_bytes_per_token", kvBytesPerToken);
                    writer.WriteString("kv_bytes_per_token_quant", kvBytesQuant.ToString());
                }

                if (recommendation.AttentionArchTag is not null)
                {
                    writer.WriteString("attention_arch", recommendation.AttentionArchTag);
                }

                // Recency and trust boosts surfaced to the UI: release_date carries the repo's last-modified timestamp (a "newer model" signal), and the parser preserves both in the recommendation
                // diagnostics blob. A date is emitted only when HF supplied one — a default(DateTimeOffset) surfaces as a year-0001 "ancient" date and wrongly sinks the repo in the recency tie-break.
                if (recommendation.LastModified != default)
                {
                    writer.WriteString("release_date", recommendation.LastModified.ToString("O", CultureInfo.InvariantCulture));
                }

                writer.WriteBoolean("is_trusted_publisher", recommendation.IsTrustedPublisher);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartObject("system");
            writer.WriteBoolean("gpu_accel", profile.GpuAccelAvailable);
            writer.WriteString("gpu_vendor", profile.GpuVendor.ToString());
            writer.WriteBoolean("vram_known", profile.VramKnown);
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static ModelFitRefreshResult Failed(Guid? snapshotId, string sanitizedError)
    {
        return new ModelFitRefreshResult { SnapshotId = snapshotId, Status = ModelFitRunStatus.Failed, RecommendationCount = 0, SanitizedError = sanitizedError };
    }

    /// <summary>
    ///     One ranked advisor candidate: the repo/file/quant identity plus its computed memory-fit estimate, the soft
    ///     publisher-trust signal, the repo's lifetime download count and its last-modified timestamp.
    /// </summary>
    /// <remarks>
    ///     Downloads, trust and recency are ranking boosts carried from the search summary — none excludes a candidate.
    /// </remarks>
    private sealed record AdvisorRecommendation
    {
        public required string RepoId { get; init; }

        public required string ModelName { get; init; }

        public required string FileName { get; init; }

        public required string Quant { get; init; }

        public required MemoryFitEstimate Estimate { get; init; }

        public required bool IsInstalled { get; init; }

        public required bool IsTrustedPublisher { get; init; }

        public required long Downloads { get; init; }

        public required DateTimeOffset LastModified { get; init; }

        /// <summary>
        ///     Which recommendation section this row belongs to: <c>"recommended"</c> / <c>"canRun"</c> (catalog lane,
        ///     primary) or <c>"explore"</c> (live Hugging Face discovery lane, secondary — the default for the pre-existing
        ///     construction sites).
        /// </summary>
        public string Section { get; init; } = "explore";

        /// <summary>The catalog entry's editorial tier (S/A/B), or <see langword="null" /> for an explore-lane row.</summary>
        public string? Tier { get; init; }

        /// <summary>The catalog entry id, or <see langword="null" /> for an explore-lane row.</summary>
        public string? CatalogId { get; init; }

        /// <summary>The catalog entry's curated display name, or <see langword="null" /> for an explore-lane row.</summary>
        public string? CatalogDisplayName { get; init; }

        /// <summary>The catalog entry's optional user-facing note, or <see langword="null" /> when absent/not-catalog.</summary>
        public string? CatalogNotes { get; init; }

        /// <summary>
        ///     Advisory-only quantized-KV estimate for a catalog-lane row (<see langword="null" /> for an explore-lane row or
        ///     when the header lacks the KV-sizing metadata). Never used for membership/ranking — see <see cref="KvQuantAdvisory" />.
        /// </summary>
        public KvQuantAdvisory? KvQuantAdvisory { get; init; }

        /// <summary>
        ///     KV-cache bytes per token of context at the run's context target, or <see langword="null" /> when the header
        ///     cannot size the KV term. Always paired with <see cref="KvBytesPerTokenQuant" />: unlabelled, the figure
        ///     is ambiguous by a factor of two.
        /// </summary>
        public long? KvBytesPerTokenAtCtx { get; init; }

        /// <summary>The element size <see cref="KvBytesPerTokenAtCtx" /> was computed at (the chat launch default, <see cref="KvCacheQuant.Q8_0" />).</summary>
        public KvCacheQuant? KvBytesPerTokenQuant { get; init; }

        /// <summary>The candidate's attention shape as a stable lowercase token (<c>mla</c>/<c>swa</c>/<c>gqa</c>/<c>mha</c>).</summary>
        public string? AttentionArchTag { get; init; }
    }
}
