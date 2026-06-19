namespace XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;

using System.Text;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.ModelFit.Fit;
using XE_Local_AI_Engine.Client.Services.ModelFit.Validation;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     The local model advisor — the box-aware rewrite of the Docker/llmfit recommendation backend (plan §7.3). On each
///     refresh it profiles the node hardware (Lane C1 <see cref="IHardwareProfiler" />), discovers candidate GGUF repos
///     and inspects their files (Lane B <see cref="IHuggingFaceGgufDiscovery" />), estimates each file's memory fit with
///     the pure <see cref="MemoryFitEstimator" />, drops the files that do not fit or lack the header metadata to compute
///     a fit, ranks the survivors by headroom, serializes them to the advisor recommendation JSON, parses them through
///     the reused <see cref="RecommendationJsonParser" /> scaffold and replaces the cached recommendation snapshot.
///     <para>
///         <b>Three seams stay separate.</b> The advisor never spawns processes or downloads files during a refresh —
///         that is the operator-driven download/start path (<see cref="DownloadAsync" /> / <see cref="StartAsync" />,
///         consumed by Lane C3 endpoints), which delegates to Lane B's store and Lane A's supervisor respectively.
///     </para>
///     <para>
///         It owns NO scheduler state and publishes no SignalR — the dispatcher owns the run row. Logs carry the
///         snapshot id, operation, use-case and sanitized status only; raw discovery payloads are never logged.
///     </para>
/// </summary>
public sealed class ModelFitRefreshService : IModelFitRefreshService
{
    /// <summary>Sentinel written into the plaintext <c>ApprovedImageId</c> column now that approved images are gone (plan §6.1).</summary>
    private const string AdvisorSnapshotSource = "local-advisor";

    /// <summary>Sentinel written into the plaintext <c>ProviderName</c> column — the advisor targets llama.cpp only (plan §6.1).</summary>
    private const string AdvisorProviderName = "llama.cpp";

    /// <summary>Default context window the KV-cache fit is sized against when the request supplies no <c>CtxTarget</c>.</summary>
    private const int DefaultCtxTarget = 8192;

    /// <summary>How many GGUF repos to inspect per refresh (each inspect is an HTTP range read, not a download).</summary>
    private const int DefaultRepoSearchLimit = 12;

    private readonly IHardwareProfiler _hardwareProfiler;
    private readonly IHuggingFaceGgufDiscovery _discovery;
    private readonly MemoryFitEstimator _estimator;
    private readonly IGgufModelStore _modelStore;
    private readonly IGgufModelRegistry _modelRegistry;
    private readonly ILlamaServerProcessSupervisor _supervisor;
    private readonly ModelFitRequestValidator _requestValidator;
    private readonly IModelFitSnapshotStore _snapshotStore;
    private readonly IModelFitRecommendationStore _recommendationStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ModelFitRefreshService> _logger;

    public ModelFitRefreshService(IHardwareProfiler hardwareProfiler,
        IHuggingFaceGgufDiscovery discovery,
        MemoryFitEstimator estimator,
        IGgufModelStore modelStore,
        IGgufModelRegistry modelRegistry,
        ILlamaServerProcessSupervisor supervisor,
        ModelFitRequestValidator requestValidator,
        IModelFitSnapshotStore snapshotStore,
        IModelFitRecommendationStore recommendationStore,
        TimeProvider timeProvider,
        ILogger<ModelFitRefreshService> logger)
    {
        _hardwareProfiler = hardwareProfiler ?? throw new ArgumentNullException(nameof(hardwareProfiler));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _estimator = estimator ?? throw new ArgumentNullException(nameof(estimator));
        _modelStore = modelStore ?? throw new ArgumentNullException(nameof(modelStore));
        _modelRegistry = modelRegistry ?? throw new ArgumentNullException(nameof(modelRegistry));
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _requestValidator = requestValidator ?? throw new ArgumentNullException(nameof(requestValidator));
        _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
        _recommendationStore = recommendationStore ?? throw new ArgumentNullException(nameof(recommendationStore));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ModelFitRefreshResult> RefreshAsync(ModelFitRefreshRequest request,
        Func<string, int?, CancellationToken, Task>? reportProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Benchmark is deferred (recommend-only path, plan §2 decision #8). Reject before any snapshot row is created.
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
        var snapshot = await _snapshotStore.CreateRunningAsync(new ModelFitSnapshotInput(ApprovedImageId: AdvisorSnapshotSource,
                Operation: request.Operation,
                UseCase: request.UseCase,
                ProviderName: AdvisorProviderName,
                ModelName: null,
                Status: ModelFitRunStatus.Running,
                StartedAtUtc: startedAtUtc),
            cancellationToken).ConfigureAwait(false);

        var snapshotId = snapshot.Id;

        if (reportProgress is not null)
        {
            await reportProgress("Profiling hardware and discovering GGUF models…", null, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var profile = await _hardwareProfiler.GetProfileAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false);
            var recommendations = await BuildRecommendationsAsync(request, quant, ctxTarget, profile, cancellationToken)
                .ConfigureAwait(false);

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
                    durationMs: completedAtUtc - startedAtUtc,
                    rawJson: advisorJson,
                    stderrExcerpt: null,
                    diagnosticsJson: "advisor recommendation JSON parse failed",
                    completedAtUtc: completedAtUtc,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                _logger.LogWarning("Advisor emitted unparseable recommendation JSON for snapshot {SnapshotId}.", snapshotId);
                return Failed(snapshotId, "Advisor recommendation could not be assembled.");
            }

            await _snapshotStore.MarkTerminalAsync(snapshotId,
                ModelFitRunStatus.Succeeded,
                exitCode: 0,
                durationMs: completedAtUtc - startedAtUtc,
                rawJson: advisorJson,
                stderrExcerpt: null,
                diagnosticsJson: parse.SystemDiagnosticsJson,
                completedAtUtc: completedAtUtc,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var inserted = await _recommendationStore.ReplaceForSnapshotAsync(snapshotId, parse.Recommendations, cancellationToken)
                                                     .ConfigureAwait(false);

            _logger.LogInformation("Advisor refresh succeeded for snapshot {SnapshotId} (use-case {UseCase}, {Count} recommendations, {Mode}).",
                snapshotId,
                request.UseCase ?? "(default)",
                inserted,
                profile is { GpuAccelAvailable: true, VramKnown: true } ? "GPU" : "CPU");

            return new ModelFitRefreshResult(snapshotId, ModelFitRunStatus.Succeeded, inserted, SanitizedError: null);
        }
        catch (OperationCanceledException)
        {
            // The node token was cancelled mid-refresh: record Cancelled (NOT Failed) then re-throw so the dispatcher
            // marks the scheduler run cancelled. Terminal write uses CancellationToken.None — the run is ending precisely
            // because its own token was cancelled.
            var cancelledAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            await _snapshotStore.MarkTerminalAsync(snapshotId,
                ModelFitRunStatus.Cancelled,
                exitCode: null,
                durationMs: cancelledAtUtc - startedAtUtc,
                rawJson: null,
                stderrExcerpt: null,
                diagnosticsJson: null,
                completedAtUtc: cancelledAtUtc,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);

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
                durationMs: failedAtUtc - startedAtUtc,
                rawJson: null,
                stderrExcerpt: null,
                diagnosticsJson: "GGUF discovery failed",
                completedAtUtc: failedAtUtc,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            _logger.LogWarning(exception, "Advisor refresh failed during GGUF discovery for snapshot {SnapshotId}.", snapshotId);
            return Failed(snapshotId, "GGUF discovery failed.");
        }
    }

    /// <summary>
    ///     Operator-driven download of a chosen GGUF file (Lane C3 endpoint calls this). Delegates to Lane B's
    ///     <see cref="IGgufModelStore.EnsureModelAsync" />; the advisor never downloads during a refresh.
    /// </summary>
    public Task<GgufModelHandle> DownloadAsync(GgufModelRequest request,
        IProgress<PullProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _modelStore.EnsureModelAsync(request, progress, cancellationToken);
    }

    /// <summary>
    ///     Operator-driven start of a downloaded model (Lane C3 endpoint calls this). Delegates to Lane A's supervisor
    ///     ensure-running for the requested role and returns the local endpoint; the advisor never spawns during a refresh.
    /// </summary>
    public Task<LlamaServerEndpoint> StartAsync(string modelName, ModelRole role, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        return _supervisor.EnsureRunningAsync(modelName, role, cancellationToken);
    }

    /// <summary>
    ///     Discovers candidate GGUF repos, inspects their files, estimates each file's fit at the chosen quant, drops the
    ///     non-fitting / insufficient-metadata files, and ranks the survivors by descending headroom (best fit first),
    ///     capped at the request limit.
    /// </summary>
    private async Task<IReadOnlyList<AdvisorRecommendation>> BuildRecommendationsAsync(ModelFitRefreshRequest request,
        string quant,
        int ctxTarget,
        HardwareProfile profile,
        CancellationToken cancellationToken)
    {
        var query = new GgufSearchQuery
        {
            SearchText = request.UseCase,
            Limit = DefaultRepoSearchLimit,
            Sort = GgufSearchSort.Downloads
        };

        var repos = await _discovery.SearchAsync(query, cancellationToken).ConfigureAwait(false);

        // Which models are already downloaded (best-effort: a registry failure just marks all as not-installed).
        var installedKeys = await ListInstalledKeysAsync(cancellationToken).ConfigureAwait(false);

        var candidates = new List<AdvisorRecommendation>();
        foreach (var repo in repos)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidate = await EvaluateRepoAsync(repo.RepoId, quant, ctxTarget, profile, installedKeys, cancellationToken)
                .ConfigureAwait(false);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        // Rank by best fit (largest headroom first), break ties by repo id for determinism, then cap to the limit.
        return candidates
               .OrderByDescending(candidate => candidate.Estimate.HeadroomBytes)
               .ThenBy(candidate => candidate.RepoId, StringComparer.Ordinal)
               .Take(request.Limit)
               .ToList();
    }

    /// <summary>
    ///     Inspects one repo, selects its best quant file, estimates the fit, and returns a ranked candidate — or
    ///     <see langword="null" /> when the repo has no usable file, the file lacks the metadata to compute a weights term,
    ///     or the estimate does not fit the budget (plan §7.3 tolerance).
    /// </summary>
    private async Task<AdvisorRecommendation?> EvaluateRepoAsync(string repoId,
        string quant,
        int ctxTarget,
        HardwareProfile profile,
        HashSet<string> installedKeys,
        CancellationToken cancellationToken)
    {
        var detail = await _discovery.InspectRepoAsync(repoId, cancellationToken).ConfigureAwait(false);

        var file = SelectQuantFile(detail.Files, quant);
        if (file is null)
        {
            return null;
        }

        var estimate = _estimator.Estimate(file.Quant,
            file.ParamCount,
            file.SizeBytes,
            file.BlockCount ?? 0,
            file.AttentionHeadCountKV ?? 0,
            file.EmbeddingLength ?? 0,
            file.AttentionHeadCount ?? 0,
            ctxTarget,
            profile,
            kvCacheQuantized: false);

        // Drop insufficient-metadata files (no weights term computable) and non-fitting files.
        if (estimate.EstimatedBytes <= _estimator.OverheadBytes || !estimate.Fits)
        {
            return null;
        }

        var modelName = GgufModelName.Format(repoId, file.Quant);
        return new AdvisorRecommendation(repoId,
            modelName,
            file.FileName,
            file.Quant,
            estimate,
            installedKeys.Contains(modelName));
    }

    private async Task<HashSet<string>> ListInstalledKeysAsync(CancellationToken cancellationToken)
    {
        try
        {
            var installed = await _modelRegistry.ListAsync(cancellationToken).ConfigureAwait(false);
            return installed.Select(entry => entry.ModelName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug(exception, "Could not list installed GGUF models for advisor install-state; reporting all not-installed.");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    ///     Selects the file matching <paramref name="quant" /> (case-insensitive) when present; otherwise the first usable
    ///     file in the repo. Returns <see langword="null" /> when the repo exposes no files.
    /// </summary>
    private static GgufRepoFile? SelectQuantFile(IReadOnlyList<GgufRepoFile> files, string quant)
    {
        if (files.Count == 0)
        {
            return null;
        }

        return files.FirstOrDefault(file => string.Equals(file.Quant, quant, StringComparison.OrdinalIgnoreCase))
               ?? files[0];
    }

    /// <summary>
    ///     Emits the advisor recommendation JSON in the <c>{ models:[…], system:{…} }</c> shape the reused
    ///     <see cref="RecommendationJsonParser" /> consumes (same field names the parser maps — <c>name</c>,
    ///     <c>best_quant</c>, <c>memory_required_gb</c>, <c>vram_required_gb</c>, <c>fit_level</c>, <c>run_mode</c>,
    ///     <c>context_length</c>, <c>installed</c>, <c>score</c>). The <c>vram_required_gb</c>/<c>memory_required_gb</c>
    ///     fields carry the estimate so the parser fills <c>RequiredVramMb</c>/<c>RequiredRamMb</c> (today null).
    /// </summary>
    private static string SerializeAdvisorJson(IReadOnlyList<AdvisorRecommendation> recommendations, HardwareProfile profile)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
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
                // Score = headroom in GB (more headroom ranks higher); the parser stores it verbatim.
                writer.WriteNumber("score", Math.Round(estimate.HeadroomBytes / (double)(1024 * 1024 * 1024), 3));
                writer.WriteNumber("memory_required_gb", Math.Round(estimatedGb, 3));
                if (estimate.Mode == FitMode.Gpu)
                {
                    writer.WriteNumber("vram_required_gb", Math.Round(estimatedGb, 3));
                }

                writer.WriteString("repo_id", recommendation.RepoId);
                writer.WriteString("file_name", recommendation.FileName);
                writer.WriteBoolean("installed", recommendation.IsInstalled);
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
        return new ModelFitRefreshResult(snapshotId, ModelFitRunStatus.Failed, RecommendationCount: 0, sanitizedError);
    }

    /// <summary>One ranked advisor candidate: the repo/file/quant identity plus its computed memory-fit estimate.</summary>
    private sealed record AdvisorRecommendation(
        string RepoId,
        string ModelName,
        string FileName,
        string Quant,
        MemoryFitEstimate Estimate,
        bool IsInstalled);
}
