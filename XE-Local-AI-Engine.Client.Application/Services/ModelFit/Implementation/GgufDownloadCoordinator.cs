namespace XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using AcquisitionKind = GgufAcquisitionOperationKind;
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
    private readonly TimeProvider _timeProvider;

    public GgufDownloadCoordinator(IGgufDownloadTransaction downloadTransaction,
        GgufAcquisitionIdentityResolver identityResolver,
        IServiceScopeFactory scopeFactory,
        IGgufAcquisitionOperationRegistry operations,
        IGgufDownloadEventPublisher eventPublisher,
        ILogger<GgufDownloadCoordinator> logger,
        TimeProvider timeProvider)
    {
        _downloadTransaction = downloadTransaction ?? throw new ArgumentNullException(nameof(downloadTransaction));
        _identityResolver = identityResolver ?? throw new ArgumentNullException(nameof(identityResolver));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<GgufDownloadTicket> StartAsync(GgufModelRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = await _downloadTransaction.ResolveAsync(request, ct);
        var intent = ToIntent(source);
        var identity = _identityResolver.Resolve(intent);
        var active = _operations.GetNewest(AcquisitionKind.Download, identity.CanonicalModelName);
        if (active is not null && IsActive(active.Phase) && _activeSources.TryGetValue(active.OperationId, out var activeSource))
        {
            if (activeSource == source)
            {
                return new GgufDownloadTicket { ModelName = active.ModelName, AlreadyInFlight = true, OperationId = active.OperationId };
            }

            throw new GgufAcquisitionConflictException();
        }

        PreparedGgufAcquisition reservation;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var preflight = scope.ServiceProvider.GetRequiredService<IGgufAcquisitionPreflight>();
            reservation = await preflight.ResolveAndReserveAsync(intent, ct);
        }

        await using (reservation)
        {
            var totalBytes = checked(source.SourceSizeBytes + (source.Projector?.SourceSizeBytes ?? 0));
            if (reservation.Disposition is GgufAcquisitionDisposition.VerifiedInstalled or GgufAcquisitionDisposition.VerifiedLegacyInstalled)
            {
                var completed = await CompleteVerifiedInstalledAsync(reservation.Identity.CanonicalModelName,
                    totalBytes,
                    reservation.Lease,
                    ct);
                BroadcastStatus(completed, isInitialOrTerminal: true);
                return new GgufDownloadTicket { ModelName = completed.ModelName, AlreadyInFlight = false, OperationId = completed.OperationId };
            }

            var registration = _operations.Start(AcquisitionKind.Download, reservation.Identity.CanonicalModelName, totalBytes);
            if (registration.AlreadyInFlight)
            {
                return new GgufDownloadTicket { ModelName = registration.Status.ModelName, AlreadyInFlight = true, OperationId = registration.Status.OperationId };
            }

            var lease = reservation.TransferLease();
            _activeSources[registration.Status.OperationId] = source;
            BroadcastStatus(registration.Status, isInitialOrTerminal: true);
            _ = RunDownloadAsync(registration.Status.OperationId,
                reservation.Identity,
                source,
                lease,
                registration.CancellationToken);
            return new GgufDownloadTicket { ModelName = registration.Status.ModelName, AlreadyInFlight = false, OperationId = registration.Status.OperationId };
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

    public GgufDownloadStatus? GetStatus(Guid operationId) =>
        MapStatus(_operations.GetStatus(operationId));

    public IReadOnlyList<GgufDownloadStatus> ListStatuses() =>
        _operations.List(AcquisitionKind.Download).Select(MapStatus).OfType<GgufDownloadStatus>().ToArray();

    private static GgufAcquisitionIntent ToIntent(ResolvedGgufDownload source) =>
        new()
        {
            OperationKind = PreflightKind.Download,
            ModelBaseName = source.ModelBaseName,
            Quantization = source.CanonicalQuant,
            Projector = source.Projector is null
                ? null
                : new GgufProjectorAcquisitionMetadata
                {
                    SourceDisplayName = source.Projector.SourceDisplayName,
                    DeclaredSha256 = source.Projector.SourceSha256,
                    DeclaredSizeBytes = source.Projector.SourceSizeBytes
                },
            Download = new GgufDownloadAcquisitionMetadata
            {
                RepoId = source.RepoId,
                ResolvedRevision = source.ResolvedRevision,
                SourceDisplayName = source.SourceDisplayName,
                DeclaredSizeBytes = source.SourceSizeBytes,
                DeclaredSha256 = source.SourceSha256,
                Role = source.Role
            }
        };

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
            _ = await registrar.RegisterIfToolCapableAsync(modelName, token);
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

    private async Task<GgufAcquisitionStatus> CompleteVerifiedInstalledAsync(string modelName,
        long totalBytes,
        InstalledModelMutationLease lease,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var mapStore = scope.ServiceProvider.GetRequiredService<ICoordinatedModelProviderMapStore>();
        ProviderMapMutationReceipt? mapReceipt = null;
        try
        {
            var claim = await mapStore.TryClaimLlamaCppAsync(lease, modelName, cancellationToken);
            if (claim is ProviderMapClaimResult.Conflict)
            {
                throw new GgufAcquisitionConflictException();
            }

            mapReceipt = (claim as ProviderMapClaimResult.Created)?.Receipt;
            var providerResolver = scope.ServiceProvider.GetRequiredService<ILocalModelProviderResolver>();
            providerResolver.InvalidateModelProviderMap();
            var resolvedProvider = await providerResolver.ResolveProviderNameForModelAsync(modelName, lease, cancellationToken);
            if (!string.Equals(resolvedProvider, LlamaServerProviderConstants.ProviderName, StringComparison.OrdinalIgnoreCase))
            {
                throw new GgufAcquisitionConflictException();
            }

            return _operations.RecordTerminal(AcquisitionKind.Download,
                modelName,
                GgufAcquisitionPhase.Completed,
                totalBytes,
                totalBytes);
        }
        catch (Exception exception)
        {
            var compensationFailure = await RestoreVerifiedInstalledRoutingAsync(mapStore,
                scope.ServiceProvider.GetService<ILocalModelProviderResolver>(),
                mapReceipt,
                lease,
                modelName);
            if (compensationFailure is not null)
            {
                throw new AggregateException("Verified-installed download routing could not be finalized or safely restored.",
                    exception,
                    compensationFailure);
            }

            throw;
        }
    }

    private async Task<Exception?> RestoreVerifiedInstalledRoutingAsync(ICoordinatedModelProviderMapStore mapStore,
        ILocalModelProviderResolver? providerResolver,
        ProviderMapMutationReceipt? mapReceipt,
        InstalledModelMutationLease lease,
        string modelName)
    {
        if (mapReceipt is null)
        {
            return null;
        }

        try
        {
            var restore = await mapStore.TryRestoreAsync(lease, mapReceipt, CancellationToken.None);
            providerResolver?.InvalidateModelProviderMap();
            if (restore == ProviderMapRestoreResult.Superseded)
            {
                return new InvalidOperationException("Provider routing changed before verified-installed compensation could be proven.");
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not restore provider routing for verified-installed model {ModelName}.", modelName);
            return exception;
        }

        return null;
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
            if (IsActive(status.Phase))
            {
                _lastProgressPushTicks[status.ModelName] = _timeProvider.GetUtcNow().UtcTicks;
            }
            else
            {
                _lastProgressPushTicks.TryRemove(status.ModelName, out _);
            }

            PublishStatus(status);
            return;
        }

        // Throttled Running progress: push only when at least ProgressPushInterval has elapsed since the last push.
        var now = _timeProvider.GetUtcNow().UtcTicks;
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
        var hubEvent = new GgufDownloadStatusHubEvent
        {
            ModelName = status.ModelName,
            Phase = status.Phase.ToString(),
            CompletedBytes = status.CompletedBytes,
            TotalBytes = status.TotalBytes,
            SanitizedError = status.SanitizedError,
            OperationId = status.OperationId,
            OperationKind = status.OperationKind.ToString(),
            ErrorCode = status.ErrorCode,
            UpdatedAtUtc = status.UpdatedAtUtc
        };

        _ = PublishStatusAsync(hubEvent);
    }

    private async Task PublishStatusAsync(GgufDownloadStatusHubEvent hubEvent)
    {
        try
        {
            // Fire-and-forget from a synchronous Progress<T> callback: there is no request token here, and the push
            // must outlive the caller, so cancellation is intentionally not propagated (MA0032/CA2016 opt-out).
            await _eventPublisher.PublishStatusAsync(hubEvent, CancellationToken.None);
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
            GgufAcquisitionPhase.Downloading,
            update.CompletedBytes,
            update.TotalBytes,
            sanitizedError: null));

        PreparedGgufDownload? prepared = null;
        GgufDownloadCommitReceipt? committed = null;
        ProviderMapMutationReceipt? mapReceipt = null;
        await using (lease)
        {
            try
            {
                SetStatus(operationId, GgufAcquisitionPhase.Downloading, isInitialOrTerminal: true);
                prepared = await _downloadTransaction.PrepareAsync(source,
                    new GgufDownloadDestination
                    {
                        CanonicalModelName = modelName,
                        CanonicalQuant = identity.CanonicalQuantization,
                        RelativeGgufPath = identity.RelativeGgufPath,
                        RelativeSidecarPath = identity.RelativeSidecarPath,
                        ProjectorRelativePath = identity.ProjectorRelativePath
                    },
                    progress,
                    token);
                token.ThrowIfCancellationRequested();
                SetStatus(operationId, GgufAcquisitionPhase.Committing, isInitialOrTerminal: true);
                committed = await _downloadTransaction.CommitAsync(prepared, CancellationToken.None);
                token.ThrowIfCancellationRequested();

                await using var scope = _scopeFactory.CreateAsyncScope();
                var mapStore = scope.ServiceProvider.GetRequiredService<ICoordinatedModelProviderMapStore>();
                var claim = await mapStore.TryClaimLlamaCppAsync(lease, modelName, CancellationToken.None);
                if (claim is ProviderMapClaimResult.Conflict)
                {
                    throw new GgufAcquisitionConflictException();
                }

                mapReceipt = (claim as ProviderMapClaimResult.Created)?.Receipt;
                token.ThrowIfCancellationRequested();
                scope.ServiceProvider.GetService<ILocalModelProviderResolver>()?.InvalidateModelProviderMap();
                token.ThrowIfCancellationRequested();
                var completedBytes = checked(source.SourceSizeBytes + (source.Projector?.SourceSizeBytes ?? 0));
                SetStatus(operationId, GgufAcquisitionPhase.Completed, completedBytes, completedBytes, isInitialOrTerminal: true);
                await RegisterToolCapabilityAsync(modelName, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                if (!await CompensateAsync(prepared, committed, mapReceipt, lease, modelName))
                {
                    PublishCompensationFailure(operationId);
                    return;
                }

                SetStatus(operationId, GgufAcquisitionPhase.Cancelled, isInitialOrTerminal: true);
                _logger.LogInformation("Operator cancelled the GGUF download for {ModelName}.", modelName);
            }
            catch (GgufAcquisitionCleanupException exception)
            {
                PublishCompensationFailure(operationId);
                _logger.LogWarning(exception, "GGUF download preparation cleanup was incomplete for {ModelName}.", modelName);
            }
            catch (GgufDownloadCommitException exception)
            {
                committed = exception.CommitReceipt;
                if (!await CompensateAsync(prepared, committed, mapReceipt, lease, modelName))
                {
                    PublishCompensationFailure(operationId);
                    return;
                }

                SetStatus(operationId,
                    GgufAcquisitionPhase.Failed,
                    errorCode: "DestinationConflict",
                    sanitizedError: exception.Message,
                    isInitialOrTerminal: true);
            }
            catch (HuggingFaceDownloadException exception)
            {
                if (!await CompensateAsync(prepared, committed, mapReceipt, lease, modelName))
                {
                    PublishCompensationFailure(operationId);
                    return;
                }

                SetStatus(operationId, GgufAcquisitionPhase.Failed, errorCode: exception.Reason.ToString(), sanitizedError: exception.Message, isInitialOrTerminal: true);
                _logger.LogWarning("GGUF download failed for {ModelName} ({Reason}).", modelName, exception.Reason);
            }
            catch (InsufficientDiskSpaceException exception)
            {
                if (!await CompensateAsync(prepared, committed, mapReceipt, lease, modelName))
                {
                    PublishCompensationFailure(operationId);
                    return;
                }

                SetStatus(operationId, GgufAcquisitionPhase.Failed, errorCode: "InsufficientStorage", sanitizedError: exception.Message, isInitialOrTerminal: true);
                _logger.LogWarning("GGUF download failed for {ModelName}: insufficient disk space.", modelName);
            }
            catch (GgufAcquisitionConflictException exception)
            {
                if (!await CompensateAsync(prepared, committed, mapReceipt, lease, modelName))
                {
                    PublishCompensationFailure(operationId);
                    return;
                }

                SetStatus(operationId,
                    GgufAcquisitionPhase.Failed,
                    errorCode: "ModelConflict",
                    sanitizedError: "The model is mapped to an incompatible provider.",
                    isInitialOrTerminal: true);
                _logger.LogWarning(exception, "GGUF download failed for {ModelName}: provider map conflict.", modelName);
            }
            catch (Exception exception)
            {
                if (!await CompensateAsync(prepared, committed, mapReceipt, lease, modelName))
                {
                    PublishCompensationFailure(operationId);
                    _logger.LogWarning(exception, "GGUF download failed for {ModelName} and compensation was incomplete.", modelName);
                    return;
                }

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

    private async Task<bool> CompensateAsync(PreparedGgufDownload? prepared,
        GgufDownloadCommitReceipt? committed,
        ProviderMapMutationReceipt? mapReceipt,
        InstalledModelMutationLease lease,
        string modelName)
    {
        var compensationSucceeded = true;
        var mayRollbackCommittedArtifacts = true;
        try
        {
            if (mapReceipt is not null)
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var mapStore = scope.ServiceProvider.GetRequiredService<ICoordinatedModelProviderMapStore>();
                var restore = await mapStore.TryRestoreAsync(lease, mapReceipt, CancellationToken.None);
                scope.ServiceProvider.GetService<ILocalModelProviderResolver>()?.InvalidateModelProviderMap();
                if (restore == ProviderMapRestoreResult.Superseded)
                {
                    compensationSucceeded = false;
                    mayRollbackCommittedArtifacts = false;
                    _logger.LogError("Provider mapping changed while compensating download for {ModelName}; committed artifacts were preserved.",
                        modelName);
                }
            }
        }
        catch (Exception exception)
        {
            compensationSucceeded = false;
            mayRollbackCommittedArtifacts = false;
            _logger.LogError(exception, "Could not restore provider routing while compensating download for {ModelName}.", modelName);
        }

        try
        {
            if (committed is not null)
            {
                if (mayRollbackCommittedArtifacts)
                {
                    await _downloadTransaction.RollbackCommittedAsync(committed, CancellationToken.None);
                }
            }
            else if (prepared is not null)
            {
                await _downloadTransaction.DiscardPreparedAsync(prepared, CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            compensationSucceeded = false;
            _logger.LogError(exception, "Could not remove download-owned artifacts while compensating download for {ModelName}.", modelName);
        }

        if (compensationSucceeded)
        {
            _logger.LogDebug("Compensated GGUF download operation for {ModelName}.", modelName);
        }

        return compensationSucceeded;
    }

    private void PublishCompensationFailure(Guid operationId) =>
        SetStatus(operationId,
            GgufAcquisitionPhase.Failed,
            errorCode: "DownloadCompensationFailed",
            sanitizedError: "Download cleanup requires recovery.",
            isInitialOrTerminal: true);

    private static GgufDownloadStatus? MapStatus(GgufAcquisitionStatus? status)
    {
        if (status is null || status.OperationKind != AcquisitionKind.Download)
        {
            return null;
        }

        var legacyPhase = status.Phase switch
        {
            GgufAcquisitionPhase.Completed => GgufDownloadPhase.Completed,
            GgufAcquisitionPhase.Cancelled => GgufDownloadPhase.Cancelled,
            GgufAcquisitionPhase.Failed => GgufDownloadPhase.Failed,
            _ => GgufDownloadPhase.Running
        };
        return new GgufDownloadStatus
        {
            ModelName = status.ModelName,
            Phase = legacyPhase,
            CompletedBytes = status.CompletedBytes,
            TotalBytes = status.TotalBytes,
            SanitizedError = status.SanitizedError,
            OperationId = status.OperationId,
            OperationKind = status.OperationKind.ToString(),
            StartedAtUtc = status.StartedAtUtc,
            UpdatedAtUtc = status.UpdatedAtUtc,
            ErrorCode = status.ErrorCode
        };
    }

    private static bool IsActive(GgufAcquisitionPhase phase) =>
        phase is GgufAcquisitionPhase.Validating
            or GgufAcquisitionPhase.Downloading
            or GgufAcquisitionPhase.Copying
            or GgufAcquisitionPhase.Committing
            or GgufAcquisitionPhase.Running;
}
