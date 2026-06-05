namespace XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.ModelFit.Validation;

/// <summary>
///     Default <see cref="IModelFitRefreshService" /> . Resolves the approved image, validates the intent
///     params, computes node hardware overrides, opens a snapshot run, invokes the narrow HostAgent runner, tolerantly
///     parses the recommendation JSON, and replaces the cached normalized recommendation snapshot. It owns NO scheduler
///     state and publishes no SignalR — the dispatcher owns the run row. Logs carry the snapshot id, approved image id,
///     operation and sanitized status only; raw stdout/stderr is never logged.
/// </summary>
public sealed class ModelFitRefreshService : IModelFitRefreshService
{
    /// <summary>Maximum stderr length persisted; longer excerpts are truncated to bound the at-rest payload.</summary>
    private const int MaxStderrExcerptLength = 4096;

    private readonly IApprovedImageResolver _imageResolver;
    private readonly ModelFitRequestValidator _requestValidator;
    private readonly ICapabilityReporter _capabilityReporter;
    private readonly IModelFitUtilityRunner _runner;
    private readonly IModelFitSnapshotStore _snapshotStore;
    private readonly IModelFitRecommendationStore _recommendationStore;
    private readonly IApprovedUtilityImageStore _approvedImageStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ModelFitRefreshService> _logger;

    public ModelFitRefreshService(
        IApprovedImageResolver imageResolver,
        ModelFitRequestValidator requestValidator,
        ICapabilityReporter capabilityReporter,
        IModelFitUtilityRunner runner,
        IModelFitSnapshotStore snapshotStore,
        IModelFitRecommendationStore recommendationStore,
        IApprovedUtilityImageStore approvedImageStore,
        TimeProvider timeProvider,
        ILogger<ModelFitRefreshService> logger)
    {
        _imageResolver = imageResolver ?? throw new ArgumentNullException(nameof(imageResolver));
        _requestValidator = requestValidator ?? throw new ArgumentNullException(nameof(requestValidator));
        _capabilityReporter = capabilityReporter ?? throw new ArgumentNullException(nameof(capabilityReporter));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
        _recommendationStore = recommendationStore ?? throw new ArgumentNullException(nameof(recommendationStore));
        _approvedImageStore = approvedImageStore ?? throw new ArgumentNullException(nameof(approvedImageStore));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ModelFitRefreshResult> RefreshAsync(
        ModelFitRefreshRequest request,
        Func<string, int?, CancellationToken, Task>? reportProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Benchmark normalization (and its live JSON) is deferred per the plan's benchmark-scope decision — this marker
        // builds the recommendation path end-to-end only. Reject benchmark before any snapshot row is created.
        if (request.Operation != ModelFitOperation.Recommend)
        {
            return Failed(snapshotId: null, "Benchmark refresh is not yet enabled.");
        }

        // (1) Pre-run config validation: approved-image resolution. A rejection is config, not a run — no snapshot row.
        var resolution = await _imageResolver.ResolveAsync(request.ApprovedImageId, request.Operation, cancellationToken)
                                             .ConfigureAwait(false);
        if (!resolution.IsResolved)
        {
            return Failed(snapshotId: null, resolution.RejectionReason ?? "The approved image could not be resolved.");
        }

        // (2) Pre-run config validation: intent params. Recommend ignores model name (model is null for recommend).
        var validationError = _requestValidator.GetValidationError(
            request.Operation,
            request.UseCase,
            request.Limit,
            request.ProviderName,
            modelName: null);
        if (validationError is not null)
        {
            return Failed(snapshotId: null, validationError);
        }

        // (3) Hardware overrides from node capability detection (best-effort: zero overrides if it throws).
        var (ramOverrideGb, vramOverrideGb, cpuCoresOverride) = await ComputeHardwareOverridesAsync(cancellationToken)
            .ConfigureAwait(false);

        // (4) Open the Running snapshot row.
        var startedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var snapshot = await _snapshotStore.CreateRunningAsync(
            new ModelFitSnapshotInput(
                ApprovedImageId: request.ApprovedImageId,
                Operation: request.Operation,
                UseCase: request.UseCase,
                ProviderName: request.ProviderName,
                ModelName: null,
                Status: ModelFitRunStatus.Running,
                StartedAtUtc: startedAtUtc),
            cancellationToken).ConfigureAwait(false);

        var snapshotId = snapshot.Id;

        // (5) Progress (scheduler callback is optional — null-check it).
        if (reportProgress is not null)
        {
            await reportProgress("Running llmfit recommend…", null, cancellationToken).ConfigureAwait(false);
        }

        // (6) Run the utility. OCE propagates out — handled below so the snapshot is recorded Cancelled.
        ModelFitUtilityRunResult runResult;
        try
        {
            runResult = await _runner.RunAsync(
                new ModelFitUtilityRunRequest(
                    ImageReference: resolution.ImageReference!,
                    Operation: request.Operation,
                    UseCase: request.UseCase,
                    Limit: request.Limit,
                    ModelName: null,
                    ProviderName: request.ProviderName,
                    ProviderUrl: null,
                    AttachRuntimeNetwork: false, // recommend runs fully offline (verified Marker 0).
                    CpuCoresOverride: cpuCoresOverride,
                    RamOverrideGb: ramOverrideGb,
                    VramOverrideGb: vramOverrideGb,
                    TimeoutSeconds: null),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The node token was cancelled mid-run: record Cancelled (NOT Failed) then re-throw so the dispatcher marks
            // the scheduler run cancelled. Terminal write uses CancellationToken.None — the run is ending precisely
            // because its own token was cancelled.
            var cancelledAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            await _snapshotStore.MarkTerminalAsync(
                snapshotId,
                ModelFitRunStatus.Cancelled,
                exitCode: null,
                durationMs: cancelledAtUtc - startedAtUtc,
                rawJson: null,
                stderrExcerpt: null,
                diagnosticsJson: null,
                completedAtUtc: cancelledAtUtc,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);

            _logger.LogInformation(
                "Model-fit refresh cancelled for snapshot {SnapshotId} (image {ApprovedImageId}, operation {Operation}).",
                snapshotId,
                request.ApprovedImageId,
                request.Operation);

            throw;
        }

        var completedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        // (7) Map the terminal result.
        if (runResult.Status == ModelFitRunStatus.Succeeded && runResult.ExitCode == 0)
        {
            return await CompleteSucceededRunAsync(request, snapshotId, runResult, completedAtUtc, cancellationToken)
                .ConfigureAwait(false);
        }

        // Returned (not thrown) terminal failure: Failed / TimedOut / Cancelled. Record it, touch usage without a
        // successful-run stamp, and return failed.
        var failureStatus = runResult.Status is ModelFitRunStatus.TimedOut or ModelFitRunStatus.Cancelled
            ? runResult.Status
            : ModelFitRunStatus.Failed;

        await _snapshotStore.MarkTerminalAsync(
            snapshotId,
            failureStatus,
            exitCode: runResult.ExitCode,
            durationMs: completedAtUtc - startedAtUtc,
            rawJson: string.IsNullOrEmpty(runResult.StandardOutput) ? null : runResult.StandardOutput,
            stderrExcerpt: Truncate(runResult.StandardError),
            diagnosticsJson: runResult.SanitizedError,
            completedAtUtc: completedAtUtc,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await TouchUsedAsync(request.ApprovedImageId, completedAtUtc, lastSuccessfulRunAtUtc: null, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogWarning(
            "Model-fit refresh did not succeed for snapshot {SnapshotId} (image {ApprovedImageId}, operation {Operation}, status {Status}).",
            snapshotId,
            request.ApprovedImageId,
            request.Operation,
            failureStatus);

        return new ModelFitRefreshResult(snapshotId, failureStatus, RecommendationCount: 0, runResult.SanitizedError);
    }

    private async Task<ModelFitRefreshResult> CompleteSucceededRunAsync(
        ModelFitRefreshRequest request,
        Guid snapshotId,
        ModelFitUtilityRunResult runResult,
        long completedAtUtc,
        CancellationToken cancellationToken)
    {
        var startedAtUtc = completedAtUtc - runResult.DurationMs;
        var parse = RecommendationJsonParser.Parse(runResult.StandardOutput);
        if (!parse.IsSuccess)
        {
            // Tolerant-parse failure: the run exited 0 but the JSON was malformed. Record Failed (raw stored for audit),
            // touch usage without a successful-run stamp, and return failed — NEVER throw out of the service for bad JSON.
            await _snapshotStore.MarkTerminalAsync(
                snapshotId,
                ModelFitRunStatus.Failed,
                exitCode: runResult.ExitCode,
                durationMs: completedAtUtc - startedAtUtc,
                rawJson: string.IsNullOrEmpty(runResult.StandardOutput) ? null : runResult.StandardOutput,
                stderrExcerpt: Truncate(runResult.StandardError),
                diagnosticsJson: "recommendation JSON parse failed",
                completedAtUtc: completedAtUtc,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            await TouchUsedAsync(request.ApprovedImageId, completedAtUtc, lastSuccessfulRunAtUtc: null, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogWarning(
                "Model-fit refresh produced unparseable recommendation JSON for snapshot {SnapshotId} (image {ApprovedImageId}).",
                snapshotId,
                request.ApprovedImageId);

            return Failed(snapshotId, "Recommendation JSON parse failed.");
        }

        // Parse OK → mark Succeeded (sets is_latest_successful transactionally), replace normalized rows, touch usage.
        await _snapshotStore.MarkTerminalAsync(
            snapshotId,
            ModelFitRunStatus.Succeeded,
            exitCode: runResult.ExitCode,
            durationMs: completedAtUtc - startedAtUtc,
            rawJson: runResult.StandardOutput,
            stderrExcerpt: Truncate(runResult.StandardError),
            diagnosticsJson: parse.SystemDiagnosticsJson,
            completedAtUtc: completedAtUtc,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var inserted = await _recommendationStore.ReplaceForSnapshotAsync(snapshotId, parse.Recommendations, cancellationToken)
                                                 .ConfigureAwait(false);

        await TouchUsedAsync(request.ApprovedImageId, completedAtUtc, lastSuccessfulRunAtUtc: completedAtUtc, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Model-fit refresh succeeded for snapshot {SnapshotId} (image {ApprovedImageId}, {Count} recommendations).",
            snapshotId,
            request.ApprovedImageId,
            inserted);

        return new ModelFitRefreshResult(snapshotId, ModelFitRunStatus.Succeeded, inserted, SanitizedError: null);
    }

    /// <summary>
    ///     Best-effort node hardware overrides for the recommend run. RAM/VRAM are converted to whole GB; VRAM is only
    ///     supplied when CUDA is available (the container otherwise sees no GPU). CPU cores are left at 0 — the container
    ///     already detects host cores correctly, only the GPU is hidden. A capability-detection failure proceeds with
    ///     zero overrides rather than failing the refresh.
    /// </summary>
    private async Task<(int RamOverrideGb, int VramOverrideGb, int CpuCoresOverride)> ComputeHardwareOverridesAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var capabilities = await _capabilityReporter.DetectCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            var ramOverrideGb = capabilities.RamMb is { } ramMb ? (int)(ramMb / 1024) : 0;
            var vramOverrideGb = capabilities is { CudaAvailable: true, VramMb: { } vramMb } ? (int)(vramMb / 1024) : 0;
            return (ramOverrideGb, vramOverrideGb, 0);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Node capability detection failed for model-fit refresh; proceeding with zero hardware overrides.");
            return (0, 0, 0);
        }
    }

    private async Task TouchUsedAsync(
        string approvedImageId,
        long lastUsedAtUtc,
        long? lastSuccessfulRunAtUtc,
        CancellationToken cancellationToken)
    {
        _ = await _approvedImageStore.TouchUsedAsync(approvedImageId, lastUsedAtUtc, lastSuccessfulRunAtUtc, cancellationToken)
                                     .ConfigureAwait(false);
    }

    private static string? Truncate(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return value.Length <= MaxStderrExcerptLength ? value : value[..MaxStderrExcerptLength];
    }

    private static ModelFitRefreshResult Failed(Guid? snapshotId, string sanitizedError) =>
        new(snapshotId, ModelFitRunStatus.Failed, RecommendationCount: 0, sanitizedError);
}
