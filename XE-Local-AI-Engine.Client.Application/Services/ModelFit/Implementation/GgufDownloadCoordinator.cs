namespace XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using AcquisitionKind = XE_Local_AI_Engine.Client.Services.ModelFit.GgufAcquisitionOperationKind;
using PreflightKind = XE_Local_AI_Engine.Client.Services.Models.GgufAcquisitionOperationKind;

/// <summary>
///     Default <see cref="IGgufDownloadCoordinator" />. Starts each download on a detached task wired to a per-model
///     <see cref="CancellationTokenSource" /> kept in an in-memory registry, captures the latest sanitized progress, and
///     lets a separate request cancel the in-flight download by model name.
///     <para>
///         <b>Singleton.</b> The registry must outlive any one request scope (the download runs after the HTTP request
///         that started it returns). It composes the singleton staged Hugging Face transaction <see cref="IGgufDownloadTransaction" />.
///     </para>
///     <para>
///         <b>Honest limits.</b> Progress and cancellation are best-effort and process-local: the registry is RAM-only
///         (a node restart drops in-flight state — the partial <c>.part</c> file resumes on the next Start), and cancel
///         is cooperative (it signals the token; the store stops at the next await/byte boundary). It never reports a
///         path/URL/token.
///     </para>
/// </summary>
public sealed class GgufDownloadCoordinator : IGgufDownloadCoordinator
{
    // Minimum gap between two pushed Running progress updates for the same model — protects the socket from a
    // high-frequency byte callback. Terminal phase changes (Completed/Cancelled/Failed) and the initial Running push
    // always go out immediately, bypassing the throttle.
    private static readonly TimeSpan ProgressPushInterval = TimeSpan.FromSeconds(1);

    private readonly IGgufDownloadEventPublisher _eventPublisher;
    private readonly ConcurrentDictionary<Guid, ResolvedGgufDownload> _activeSources = new();
    private readonly IGgufDownloadTransaction _downloadTransaction;
    private readonly GgufAcquisitionIdentityResolver _identityResolver;
    private readonly IGgufAcquisitionOperationRegistry _operations;

    // Last instant a Running progress push was broadcast per model, so high-frequency byte callbacks are throttled to at
    // most one push per ProgressPushInterval. Keyed by canonical model name; the entry is dropped on terminal phase.
    private readonly ConcurrentDictionary<string, long> _lastProgressPushTicks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<GgufDownloadCoordinator> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public GgufDownloadCoordinator(IGgufDownloadTransaction downloadTransaction,
        GgufAcquisitionIdentityResolver identityResolver,
        IServiceScopeFactory scopeFactory,
        IGgufAcquisitionOperationRegistry operations,
        IGgufDownloadEventPublisher eventPublisher,
        ILogger<GgufDownloadCoordinator> logger)
    {
        _downloadTransaction = downloadTransaction ?? throw new ArgumentNullException(nameof(downloadTransaction));
        _identityResolver = identityResolver ?? throw new ArgumentNullException(nameof(identityResolver));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<GgufDownloadTicket> StartAsync(GgufModelRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = await _downloadTransaction.ResolveAsync(request, ct).ConfigureAwait(false);
        var intent = ToIntent(source);
        var identity = _identityResolver.Resolve(intent);
        var active = _operations.GetNewest(AcquisitionKind.Download, identity.CanonicalModelName);
        if (active?.Phase == GgufAcquisitionPhase.Running && _activeSources.TryGetValue(active.OperationId, out var activeSource))
        {
            if (activeSource == source)
            {
                return new GgufDownloadTicket(active.ModelName, AlreadyInFlight: true, active.OperationId);
            }

            throw new InvalidOperationException("ModelConflict");
        }

        PreparedGgufAcquisition reservation;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var preflight = scope.ServiceProvider.GetRequiredService<IGgufAcquisitionPreflight>();
            reservation = await preflight.ResolveAndReserveAsync(intent, ct).ConfigureAwait(false);
        }

        await using (reservation.ConfigureAwait(false))
        {
            var totalBytes = checked(source.SourceSizeBytes + (source.Projector?.SourceSizeBytes ?? 0));
            var registration = _operations.Start(AcquisitionKind.Download, reservation.Identity.CanonicalModelName, totalBytes);
            if (registration.AlreadyInFlight)
            {
                return new GgufDownloadTicket(registration.Status.ModelName, AlreadyInFlight: true, registration.Status.OperationId);
            }

            if (reservation.Disposition is GgufAcquisitionDisposition.VerifiedInstalled or GgufAcquisitionDisposition.VerifiedLegacyInstalled)
            {
                try
                {
                    await CompleteVerifiedInstalledAsync(registration.Status.OperationId,
                        reservation.Identity.CanonicalModelName,
                        totalBytes,
                        reservation.Lease,
                        ct).ConfigureAwait(false);
                }
                catch
                {
                    SetStatus(registration.Status.OperationId,
                        GgufAcquisitionPhase.Failed,
                        errorCode: "DownloadFailed",
                        sanitizedError: "Download failed.",
                        isInitialOrTerminal: true);
                    throw;
                }
                return new GgufDownloadTicket(registration.Status.ModelName, AlreadyInFlight: false, registration.Status.OperationId);
            }

            var lease = reservation.TransferLease();
            _activeSources[registration.Status.OperationId] = source;
            BroadcastStatus(registration.Status, isInitialOrTerminal: true);
            _ = RunDownloadAsync(registration.Status.OperationId,
                reservation.Identity,
                source,
                lease,
                registration.CancellationToken);
            return new GgufDownloadTicket(registration.Status.ModelName, AlreadyInFlight: false, registration.Status.OperationId);
        }
    }

    public bool Cancel(string modelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        return _operations.CancelNewest(AcquisitionKind.Download, modelName);
    }

    public GgufDownloadStatus? GetStatus(string modelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        return MapStatus(_operations.GetNewest(AcquisitionKind.Download, modelName));
    }

    public GgufDownloadStatus? GetStatus(Guid operationId) => MapStatus(_operations.GetStatus(operationId));

    public IReadOnlyList<GgufDownloadStatus> ListStatuses() =>
        _operations.List(AcquisitionKind.Download).Select(MapStatus).OfType<GgufDownloadStatus>().ToArray();

    private static GgufAcquisitionIntent ToIntent(ResolvedGgufDownload source) =>
        new(PreflightKind.Download,
            source.ModelBaseName,
            source.CanonicalQuant,
            source.Projector is null
                ? null
                : new GgufProjectorAcquisitionMetadata(source.Projector.SourceDisplayName,
                    source.Projector.SourceSha256,
                    source.Projector.SourceSizeBytes),
            new GgufDownloadAcquisitionMetadata(source.RepoId,
                source.ResolvedRevision,
                source.SourceDisplayName,
                source.SourceSizeBytes,
                source.SourceSha256,
                source.Role));

    /// <summary>
    ///     Adds the just-installed model to the tool-capable allow-list when its GGUF chat template advertises tool
    ///     calling. Best-effort: a failure here leaves the operator with the existing (possibly stale) list, which is the
    ///     pre-change behaviour — never a failed download.
    /// </summary>
    private async Task RegisterToolCapabilityAsync(string modelName, CancellationToken token)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var registrar = scope.ServiceProvider.GetRequiredService<IToolCapableModelRegistrar>();
            _ = await registrar.RegisterIfToolCapableAsync(modelName, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception,
                "Could not record tool capability for {ModelName}; the configured tool-capable model list still applies.",
                modelName);
        }
    }

    private async Task CompleteVerifiedInstalledAsync(Guid operationId,
        string modelName,
        long totalBytes,
        InstalledModelMutationLease lease,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var mapStore = scope.ServiceProvider.GetRequiredService<ICoordinatedModelProviderMapStore>();
        var claim = await mapStore.TryClaimLlamaCppAsync(lease, modelName, cancellationToken).ConfigureAwait(false);
        if (claim is ProviderMapClaimResult.Conflict)
        {
            SetStatus(operationId,
                GgufAcquisitionPhase.Failed,
                errorCode: "ModelConflict",
                sanitizedError: "The model is mapped to an incompatible provider.",
                isInitialOrTerminal: true);
            return;
        }

        scope.ServiceProvider.GetService<ILocalModelProviderResolver>()?.InvalidateModelProviderMap();
        SetStatus(operationId, GgufAcquisitionPhase.Completed, totalBytes, totalBytes, isInitialOrTerminal: true);
    }

    // Records the latest status in the registry and broadcasts it to connected operator clients. Running progress pushes
    // are throttled to at most one per ProgressPushInterval per model so a high-frequency byte callback never floods the
    // socket; the initial Running push and every terminal phase (Completed/Cancelled/Failed) bypass the throttle and go
    // out immediately. The registry write is always unconditional, so the list endpoint still serves the freshest bytes.
    private void SetStatus(Guid operationId,
        GgufAcquisitionPhase phase,
        long? completedBytes = null,
        long? totalBytes = null,
        string? errorCode = null,
        string? sanitizedError = null,
        bool isInitialOrTerminal = false)
    {
        var status = _operations.Update(operationId, phase, completedBytes, totalBytes, errorCode, sanitizedError);
        BroadcastStatus(status, isInitialOrTerminal);
    }

    private void BroadcastStatus(GgufAcquisitionStatus status, bool isInitialOrTerminal = false)
    {
        if (isInitialOrTerminal)
        {
            // Drop the throttle bookkeeping on a terminal phase; the initial push primes it so the first throttled
            // progress tick still has to wait out the interval.
            if (status.Phase == GgufAcquisitionPhase.Running)
            {
                _lastProgressPushTicks[status.ModelName] = DateTimeOffset.UtcNow.UtcTicks;
            }
            else
            {
                _lastProgressPushTicks.TryRemove(status.ModelName, out _);
            }

            PublishStatus(status);
            return;
        }

        // Throttled Running progress: push only when at least ProgressPushInterval has elapsed since the last push.
        var now = DateTimeOffset.UtcNow.UtcTicks;
        var last = _lastProgressPushTicks.TryGetValue(status.ModelName, out var ticks) ? ticks : 0L;
        if (now - last < ProgressPushInterval.Ticks)
        {
            return;
        }

        _lastProgressPushTicks[status.ModelName] = now;
        PublishStatus(status);
    }

    // Maps the internal status to the sanitized hub event at the broadcast boundary (no internal type leaks) and pushes
    // it fire-and-forget. The Progress<T> callback is synchronous and must not block byte flow, so a push failure is
    // swallowed with a debug log — the list endpoint remains the authoritative one-shot hydrate either way.
    private void PublishStatus(GgufAcquisitionStatus status)
    {
        var hubEvent = new GgufDownloadStatusHubEvent(status.ModelName,
            status.Phase.ToString(),
            status.CompletedBytes,
            status.TotalBytes,
            status.SanitizedError,
            status.OperationId,
            status.OperationKind.ToString(),
            status.ErrorCode);

        _ = PublishStatusAsync(hubEvent);
    }

    private async Task PublishStatusAsync(GgufDownloadStatusHubEvent hubEvent)
    {
        try
        {
            await _eventPublisher.PublishStatusAsync(hubEvent).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not push the GGUF download status for {ModelName}; the list endpoint still serves it.", hubEvent.ModelName);
        }
    }

    private async Task RunDownloadAsync(Guid operationId,
        ResolvedGgufAcquisitionIdentity identity,
        ResolvedGgufDownload source,
        InstalledModelMutationLease lease,
        CancellationToken token)
    {
        var modelName = identity.CanonicalModelName;
        var progress = new Progress<PullProgress>(update => SetStatus(operationId,
            GgufAcquisitionPhase.Running,
            update.CompletedBytes,
            update.TotalBytes,
            sanitizedError: null));

        PreparedGgufDownload? prepared = null;
        GgufDownloadCommitReceipt? committed = null;
        ProviderMapMutationReceipt? mapReceipt = null;
        await using (lease.ConfigureAwait(false))
        {
            try
            {
                prepared = await _downloadTransaction.PrepareAsync(source,
                    new GgufDownloadDestination(modelName,
                        identity.CanonicalQuantization,
                        identity.RelativeGgufPath,
                        identity.RelativeSidecarPath,
                        identity.ProjectorRelativePath),
                    progress,
                    token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                committed = await _downloadTransaction.CommitAsync(prepared, CancellationToken.None).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                await using var scope = _scopeFactory.CreateAsyncScope();
                var mapStore = scope.ServiceProvider.GetRequiredService<ICoordinatedModelProviderMapStore>();
                var claim = await mapStore.TryClaimLlamaCppAsync(lease, modelName, CancellationToken.None).ConfigureAwait(false);
                if (claim is ProviderMapClaimResult.Conflict)
                {
                    throw new InvalidOperationException("ModelConflict");
                }

                mapReceipt = (claim as ProviderMapClaimResult.Created)?.Receipt;
                token.ThrowIfCancellationRequested();
                scope.ServiceProvider.GetService<ILocalModelProviderResolver>()?.InvalidateModelProviderMap();
                token.ThrowIfCancellationRequested();
                var completedBytes = checked(source.SourceSizeBytes + (source.Projector?.SourceSizeBytes ?? 0));
                SetStatus(operationId, GgufAcquisitionPhase.Completed, completedBytes, completedBytes, isInitialOrTerminal: true);
                await RegisterToolCapabilityAsync(modelName, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await CompensateAsync(prepared, committed, mapReceipt, lease, modelName).ConfigureAwait(false);
                SetStatus(operationId, GgufAcquisitionPhase.Cancelled, isInitialOrTerminal: true);
                _logger.LogInformation("Operator cancelled the GGUF download for {ModelName}.", modelName);
            }
            catch (HuggingFaceDownloadException exception)
            {
                await CompensateAsync(prepared, committed, mapReceipt, lease, modelName).ConfigureAwait(false);
                SetStatus(operationId, GgufAcquisitionPhase.Failed, errorCode: exception.Reason.ToString(), sanitizedError: exception.Message, isInitialOrTerminal: true);
                _logger.LogWarning("GGUF download failed for {ModelName} ({Reason}).", modelName, exception.Reason);
            }
            catch (InsufficientDiskSpaceException exception)
            {
                await CompensateAsync(prepared, committed, mapReceipt, lease, modelName).ConfigureAwait(false);
                SetStatus(operationId, GgufAcquisitionPhase.Failed, errorCode: "InsufficientStorage", sanitizedError: exception.Message, isInitialOrTerminal: true);
                _logger.LogWarning("GGUF download failed for {ModelName}: insufficient disk space.", modelName);
            }
            catch (InvalidOperationException exception) when (string.Equals(exception.Message, "ModelConflict", StringComparison.Ordinal))
            {
                await CompensateAsync(prepared, committed, mapReceipt, lease, modelName).ConfigureAwait(false);
                SetStatus(operationId,
                    GgufAcquisitionPhase.Failed,
                    errorCode: "ModelConflict",
                    sanitizedError: "The model is mapped to an incompatible provider.",
                    isInitialOrTerminal: true);
                _logger.LogWarning(exception, "GGUF download failed for {ModelName}: provider map conflict.", modelName);
            }
            catch (Exception exception)
            {
                await CompensateAsync(prepared, committed, mapReceipt, lease, modelName).ConfigureAwait(false);
                SetStatus(operationId,
                    GgufAcquisitionPhase.Failed,
                    errorCode: "DownloadFailed",
                    sanitizedError: "Download failed.",
                    isInitialOrTerminal: true);
                _logger.LogWarning(exception, "GGUF download failed for {ModelName}.", modelName);
            }
            finally
            {
                _activeSources.TryRemove(operationId, out _);
            }
        }
    }

    private async Task CompensateAsync(PreparedGgufDownload? prepared,
        GgufDownloadCommitReceipt? committed,
        ProviderMapMutationReceipt? mapReceipt,
        InstalledModelMutationLease lease,
        string modelName)
    {
        try
        {
            if (mapReceipt is not null)
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var mapStore = scope.ServiceProvider.GetRequiredService<ICoordinatedModelProviderMapStore>();
                _ = await mapStore.TryRestoreAsync(lease, mapReceipt, CancellationToken.None).ConfigureAwait(false);
                scope.ServiceProvider.GetService<ILocalModelProviderResolver>()?.InvalidateModelProviderMap();
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not restore provider routing while compensating download for {ModelName}.", modelName);
        }

        try
        {
            if (committed is not null)
            {
                await _downloadTransaction.RollbackCommittedAsync(committed, CancellationToken.None).ConfigureAwait(false);
            }
            else if (prepared is not null)
            {
                await _downloadTransaction.DiscardPreparedAsync(prepared, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not remove download-owned artifacts while compensating download for {ModelName}.", modelName);
        }

        _logger.LogDebug("Compensated GGUF download operation for {ModelName}.", modelName);
    }

    private static GgufDownloadStatus? MapStatus(GgufAcquisitionStatus? status)
    {
        if (status is null || status.OperationKind != AcquisitionKind.Download)
        {
            return null;
        }

        return new GgufDownloadStatus(status.ModelName,
            Enum.Parse<GgufDownloadPhase>(status.Phase.ToString()),
            status.CompletedBytes,
            status.TotalBytes,
            status.SanitizedError,
            status.OperationId,
            status.OperationKind.ToString(),
            status.StartedAtUtc,
            status.UpdatedAtUtc,
            status.ErrorCode);
    }

}
