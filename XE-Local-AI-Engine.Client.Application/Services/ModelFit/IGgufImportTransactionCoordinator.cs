namespace XE_Local_AI_Engine.Client.Services.ModelFit;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using AcquisitionKind = XE_Local_AI_Engine.Client.Services.ModelFit.GgufAcquisitionOperationKind;
using PreflightKind = XE_Local_AI_Engine.Client.Services.Models.GgufAcquisitionOperationKind;

public sealed record PreviewGgufImportResult(
    string ModelBaseName,
    string? DetectedQuantization,
    IReadOnlyList<string> CanonicalQuantizationChoices,
    string? CanonicalModelName,
    string? FinalFileName,
    long SizeBytes,
    string SourceDisplayName,
    string? Architecture,
    uint? GgufVersion,
    IReadOnlyList<string> Warnings,
    bool? HasSufficientStorage,
    string PreviewToken,
    DateTimeOffset ExpiresAtUtc);

public sealed record StartGgufImportCommand(
    string SourcePath,
    string PreviewToken,
    string ModelBaseName,
    string Quantization);

public sealed record GgufImportTicket(Guid OperationId, string OperationKind, string ModelName);

public sealed class GgufImportApplicationException(string errorCode, string sanitizedMessage) : Exception(sanitizedMessage)
{
    public string ErrorCode { get; } = errorCode;
}

public interface IGgufImportTransactionCoordinator
{
    Task<PreviewGgufImportResult> PreviewAsync(string sourcePath, CancellationToken cancellationToken = default);
    Task<GgufImportTicket> StartAsync(StartGgufImportCommand command, CancellationToken cancellationToken = default);
    bool Cancel(Guid operationId);
    GgufAcquisitionStatus? GetStatus(Guid operationId);
    IReadOnlyList<GgufAcquisitionStatus> ListStatuses();
}

public sealed class GgufImportTransactionCoordinator : IGgufImportTransactionCoordinator
{
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(10);
    private readonly IGgufDownloadEventPublisher _eventPublisher;
    private readonly IGgufImportInspector _inspector;
    private readonly IGgufModelImporter _importer;
    private readonly ILogger<GgufImportTransactionCoordinator> _logger;
    private readonly IGgufAcquisitionOperationRegistry _operations;
    private readonly ConcurrentDictionary<string, PreviewState> _previews = new(StringComparer.Ordinal);
    private readonly GgufAcquisitionIdentityResolver _resolver;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;

    public GgufImportTransactionCoordinator(IGgufImportInspector inspector,
        IGgufModelImporter importer,
        GgufAcquisitionIdentityResolver resolver,
        IGgufAcquisitionOperationRegistry operations,
        IServiceScopeFactory scopeFactory,
        IGgufDownloadEventPublisher eventPublisher,
        TimeProvider timeProvider,
        ILogger<GgufImportTransactionCoordinator> logger)
    {
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _importer = importer ?? throw new ArgumentNullException(nameof(importer));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<PreviewGgufImportResult> PreviewAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var inspection = await InspectSupportedAsync(sourcePath, allowQuantizationRequired: true, cancellationToken).ConfigureAwait(false);
        var modelBaseName = InferModelBaseName(inspection.SourceDisplayName, inspection.DetectedQuantization);
        ResolvedGgufAcquisitionIdentity? identity = null;
        if (inspection.DetectedQuantization is not null)
        {
            identity = _resolver.Resolve(new GgufAcquisitionIntent(PreflightKind.Import, modelBaseName, inspection.DetectedQuantization));
        }

        RemoveExpiredPreviews();
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var expiresAt = _timeProvider.GetUtcNow().Add(PreviewLifetime);
        _previews[token] = new PreviewState(sourcePath, inspection, expiresAt);
        return new PreviewGgufImportResult(modelBaseName,
            inspection.DetectedQuantization,
            inspection.DetectedQuantization is null ? Array.Empty<string>() : new[] { inspection.DetectedQuantization },
            identity?.CanonicalModelName,
            identity?.FinalFileName,
            inspection.SizeBytes,
            inspection.SourceDisplayName,
            inspection.Architecture,
            inspection.GgufVersion,
            inspection.Warnings,
            HasSufficientStorage: null,
            token,
            expiresAt);
    }

    public async Task<GgufImportTicket> StartAsync(StartGgufImportCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.SourcePath)
            || string.IsNullOrWhiteSpace(command.PreviewToken)
            || string.IsNullOrWhiteSpace(command.ModelBaseName)
            || string.IsNullOrWhiteSpace(command.Quantization))
        {
            throw new GgufImportApplicationException("InvalidRequest", "The source, preview token, model base name, and quantization are required.");
        }

        if (!_previews.TryRemove(command.PreviewToken, out var preview)
            || preview.ExpiresAtUtc <= _timeProvider.GetUtcNow()
            || !string.Equals(preview.SourcePath, command.SourcePath, StringComparison.Ordinal))
        {
            throw new GgufImportApplicationException("InvalidPreviewToken", "The import preview is missing, expired, or does not match the selected file.");
        }

        var inspection = await InspectSupportedAsync(command.SourcePath, allowQuantizationRequired: true, cancellationToken).ConfigureAwait(false);
        if (!InspectionMatches(preview.Inspection, inspection))
        {
            throw new GgufImportApplicationException("StalePreview", "The selected file changed after it was previewed.");
        }

        PreparedGgufAcquisition reservation;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var preflight = scope.ServiceProvider.GetRequiredService<IGgufAcquisitionPreflight>();
            reservation = await preflight.ResolveAndReserveAsync(
                new GgufAcquisitionIntent(PreflightKind.Import, command.ModelBaseName, command.Quantization),
                cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            _logger.LogDebug(exception, "Rejected a GGUF import identity during acquisition preflight.");
            throw new GgufImportApplicationException("UnsupportedQuantization", "The model name or quantization is not supported.");
        }
        catch (InvalidOperationException)
        {
            throw new GgufImportApplicationException("ModelConflict", "The model name or destination is already in use.");
        }

        await using (reservation.ConfigureAwait(false))
        {
            var registration = _operations.Start(AcquisitionKind.Import,
                reservation.Identity.CanonicalModelName,
                inspection.SizeBytes);
            if (registration.AlreadyInFlight)
            {
                throw new GgufImportApplicationException("AcquisitionAlreadyActive", "An import for this model is already active.");
            }

            var lease = reservation.TransferLease();
            Publish(registration.Status);
            _ = RunImportAsync(registration.Status.OperationId,
                command.SourcePath,
                reservation.Identity,
                lease,
                registration.CancellationToken);
            return new GgufImportTicket(registration.Status.OperationId,
                registration.Status.OperationKind.ToString(),
                registration.Status.ModelName);
        }
    }

    public bool Cancel(Guid operationId) => _operations.GetStatus(operationId)?.OperationKind == AcquisitionKind.Import
                                            && _operations.Cancel(operationId);

    public GgufAcquisitionStatus? GetStatus(Guid operationId)
    {
        var status = _operations.GetStatus(operationId);
        return status?.OperationKind == AcquisitionKind.Import ? status : null;
    }

    public IReadOnlyList<GgufAcquisitionStatus> ListStatuses() => _operations.List(AcquisitionKind.Import);

    private async Task RunImportAsync(Guid operationId,
        string sourcePath,
        ResolvedGgufAcquisitionIdentity identity,
        InstalledModelMutationLease lease,
        CancellationToken cancellationToken)
    {
        PreparedGgufImport? prepared = null;
        GgufImportCommitReceipt? committed = null;
        ProviderMapMutationReceipt? mapReceipt = null;
        await using (lease.ConfigureAwait(false))
        {
            try
            {
                var progress = new Progress<GgufImportProgress>(value => UpdateAndPublish(operationId,
                    GgufAcquisitionPhase.Running,
                    value.CompletedBytes,
                    value.TotalBytes));
                prepared = await _importer.PrepareAsync(new GgufImportSource(sourcePath),
                    new GgufImportDestination(identity.CanonicalModelName,
                        identity.CanonicalQuantization,
                        identity.RelativeGgufPath,
                        identity.RelativeSidecarPath,
                        LocalModelOrigin.Imported),
                    progress,
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                committed = await _importer.CommitAsync(prepared, CancellationToken.None).ConfigureAwait(false);

                await using var scope = _scopeFactory.CreateAsyncScope();
                var mapStore = scope.ServiceProvider.GetRequiredService<ICoordinatedModelProviderMapStore>();
                var claim = await mapStore.TryClaimLlamaCppAsync(lease, identity.CanonicalModelName, CancellationToken.None).ConfigureAwait(false);
                if (claim is ProviderMapClaimResult.Conflict)
                {
                    throw new GgufImportApplicationException("ModelConflict", "The model is mapped to an incompatible provider.");
                }

                mapReceipt = (claim as ProviderMapClaimResult.Created)?.Receipt;
                scope.ServiceProvider.GetService<ILocalModelProviderResolver>()?.InvalidateModelProviderMap();
                var status = _operations.Update(operationId,
                    GgufAcquisitionPhase.Completed,
                    committed.RegistryEntry.SizeBytes,
                    committed.RegistryEntry.SizeBytes);
                Publish(status);
            }
            catch (OperationCanceledException)
            {
                await CompensateAsync(prepared, committed, mapReceipt, lease, identity.CanonicalModelName).ConfigureAwait(false);
                UpdateAndPublish(operationId, GgufAcquisitionPhase.Cancelled);
            }
            catch (GgufImportException exception)
            {
                await CompensateAsync(prepared, committed, mapReceipt, lease, identity.CanonicalModelName).ConfigureAwait(false);
                UpdateAndPublish(operationId,
                    GgufAcquisitionPhase.Failed,
                    errorCode: MapRejectionCode(exception.Reason),
                    sanitizedError: exception.Message);
            }
            catch (GgufImportApplicationException exception)
            {
                await CompensateAsync(prepared, committed, mapReceipt, lease, identity.CanonicalModelName).ConfigureAwait(false);
                UpdateAndPublish(operationId, GgufAcquisitionPhase.Failed, errorCode: exception.ErrorCode, sanitizedError: exception.Message);
            }
            catch (Exception exception)
            {
                await CompensateAsync(prepared, committed, mapReceipt, lease, identity.CanonicalModelName).ConfigureAwait(false);
                UpdateAndPublish(operationId, GgufAcquisitionPhase.Failed, errorCode: "ImportFailed", sanitizedError: "Import failed.");
                _logger.LogWarning(exception, "GGUF import failed for {ModelName}.", identity.CanonicalModelName);
            }
        }
    }

    private async Task CompensateAsync(PreparedGgufImport? prepared,
        GgufImportCommitReceipt? committed,
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
            _logger.LogError(exception, "Could not restore the provider mapping while compensating import for {ModelName}.", modelName);
        }

        try
        {
            if (committed is not null)
            {
                await _importer.RollbackCommittedAsync(committed, CancellationToken.None).ConfigureAwait(false);
            }
            else if (prepared is not null)
            {
                await _importer.DiscardPreparedAsync(prepared, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not remove import-owned artifacts while compensating import for {ModelName}.", modelName);
        }

        _logger.LogDebug("Compensated GGUF import operation for {ModelName}.", modelName);
    }

    private async Task<GgufImportInspection> InspectSupportedAsync(string sourcePath,
        bool allowQuantizationRequired,
        CancellationToken cancellationToken)
    {
        try
        {
            var inspection = await _inspector.InspectAsync(new GgufImportSource(sourcePath), cancellationToken).ConfigureAwait(false);
            var blocking = inspection.Rejections.FirstOrDefault(rejection => !allowQuantizationRequired
                                                                             || rejection != GgufImportRejectionCode.QuantizationRequired);
            if (blocking != default || inspection.Rejections.Contains(GgufImportRejectionCode.InvalidSource))
            {
                throw new GgufImportApplicationException(MapRejectionCode(blocking), "The selected GGUF is not supported for local import.");
            }

            return inspection;
        }
        catch (GgufImportException exception)
        {
            throw new GgufImportApplicationException(MapRejectionCode(exception.Reason), exception.Message);
        }
    }

    private void UpdateAndPublish(Guid operationId,
        GgufAcquisitionPhase phase,
        long? completedBytes = null,
        long? totalBytes = null,
        string? errorCode = null,
        string? sanitizedError = null)
    {
        Publish(_operations.Update(operationId, phase, completedBytes, totalBytes, errorCode, sanitizedError));
    }

    private void Publish(GgufAcquisitionStatus status)
    {
        _ = PublishAsync(new GgufDownloadStatusHubEvent(status.ModelName,
            status.Phase.ToString(),
            status.CompletedBytes,
            status.TotalBytes,
            status.SanitizedError,
            status.OperationId,
            status.OperationKind.ToString(),
            status.ErrorCode));
    }

    private async Task PublishAsync(GgufDownloadStatusHubEvent statusEvent)
    {
        try
        {
            await _eventPublisher.PublishStatusAsync(statusEvent).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not push GGUF acquisition status for {ModelName}.", statusEvent.ModelName);
        }
    }

    private void RemoveExpiredPreviews()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var preview in _previews)
        {
            if (preview.Value.ExpiresAtUtc <= now)
            {
                _previews.TryRemove(preview.Key, out _);
            }
        }
    }

    private static bool InspectionMatches(GgufImportInspection left, GgufImportInspection right) =>
        left.SizeBytes == right.SizeBytes
        && left.GgufVersion == right.GgufVersion
        && string.Equals(left.Architecture, right.Architecture, StringComparison.Ordinal)
        && left.Workload == right.Workload
        && string.Equals(left.DetectedQuantization, right.DetectedQuantization, StringComparison.Ordinal)
        && string.Equals(left.SourceDisplayName, right.SourceDisplayName, StringComparison.Ordinal);

    private static string InferModelBaseName(string sourceDisplayName, string? quantization)
    {
        var baseName = Path.GetFileNameWithoutExtension(sourceDisplayName);
        if (quantization is not null && baseName.EndsWith($"-{quantization}", StringComparison.OrdinalIgnoreCase))
        {
            baseName = baseName[..^(quantization.Length + 1)];
        }

        return baseName.Replace(':', '-').Trim();
    }

    private static string MapRejectionCode(GgufImportRejectionCode rejection) => rejection switch
    {
        GgufImportRejectionCode.InvalidSource => "InvalidPath",
        GgufImportRejectionCode.DestinationConflict => "DestinationConflict",
        GgufImportRejectionCode.UnsupportedVersion => "UnsupportedGgufVersion",
        GgufImportRejectionCode.UnsupportedModelType or GgufImportRejectionCode.SplitModel => "UnsupportedModelKind",
        GgufImportRejectionCode.UnsupportedArchitecture => "UnsupportedArchitecture",
        GgufImportRejectionCode.QuantizationRequired or GgufImportRejectionCode.UnsupportedQuantization => "UnsupportedQuantization",
        _ => "UnsupportedFileType"
    };

    private sealed record PreviewState(string SourcePath, GgufImportInspection Inspection, DateTimeOffset ExpiresAtUtc);
}
