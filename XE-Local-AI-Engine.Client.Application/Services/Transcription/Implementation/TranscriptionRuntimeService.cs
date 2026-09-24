namespace XE_Local_AI_Engine.Client.Services.Transcription.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Transcription.Capture;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>
///     Default <see cref="ITranscriptionRuntimeService" />. Pure composition: it owns no state of its own and simply
///     joins the supervisor, the static catalogue, the installed-runtime record, the node settings and the effective
///     hardware profile into the shapes the endpoints return.
/// </summary>
public sealed class TranscriptionRuntimeService : ITranscriptionRuntimeService
{
    private readonly IWhisperBackendSelector _backendSelector;
    private readonly IWhisperModelDownloadCoordinator _downloadCoordinator;
    private readonly IWhisperInstalledRuntimeStore _installedRuntimeStore;
    private readonly WhisperModelPathResolver _pathResolver;
    private readonly IRuntimeDeviceAudit _runtimeDeviceAudit;
    private readonly IWhisperRuntimeActivityGate _activityGate;
    private readonly INodeSettingsStore _settingsStore;
    private readonly IWhisperServerSupervisor _supervisor;
    private readonly IProcessAudioCaptureSource _processCapture;
    private readonly TranscriptionOptions _options;

    public TranscriptionRuntimeService(IWhisperServerSupervisor supervisor,
        IWhisperRuntimeActivityGate activityGate,
        IWhisperInstalledRuntimeStore installedRuntimeStore,
        IWhisperBackendSelector backendSelector,
        IWhisperModelDownloadCoordinator downloadCoordinator,
        WhisperModelPathResolver pathResolver,
        INodeSettingsStore settingsStore,
        IRuntimeDeviceAudit runtimeDeviceAudit,
        IProcessAudioCaptureSource processCapture,
        IOptions<TranscriptionOptions> options)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _activityGate = activityGate ?? throw new ArgumentNullException(nameof(activityGate));
        _installedRuntimeStore = installedRuntimeStore ?? throw new ArgumentNullException(nameof(installedRuntimeStore));
        _backendSelector = backendSelector ?? throw new ArgumentNullException(nameof(backendSelector));
        _downloadCoordinator = downloadCoordinator ?? throw new ArgumentNullException(nameof(downloadCoordinator));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _runtimeDeviceAudit = runtimeDeviceAudit ?? throw new ArgumentNullException(nameof(runtimeDeviceAudit));
        _processCapture = processCapture ?? throw new ArgumentNullException(nameof(processCapture));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task<TranscriptionRuntimeView> GetRuntimeAsync(CancellationToken ct)
    {
        var settings = await LoadSettingsAsync(ct);
        var managedRuntime = await _installedRuntimeStore.ReadAsync(ct);
        var recommended = await GetRecommendedModelAsync(ct);

        return new TranscriptionRuntimeView
        {
            Enabled = _options.Enabled,
            Runtime = _supervisor.GetStatus(),
            Activity = _activityGate.GetSnapshot(),
            ManagedRuntime = managedRuntime,
            SelectedModelId = NormalizeSelection(settings.TranscriptionSelectedModelId),
            RecommendedModelId = recommended.Id,
            // The EFFECTIVE idle timeout, not the stored default: the supervisor's TTL is seeded from Transcription:IdleTimeoutMinutes when no operator value is
            // stored (NodeRuntimeSettings.GetTranscriptionIdleTimeout), so the bare default would report 15 while the reaper fires at the configured value.
            IdleTimeoutMinutes = settings.TranscriptionIdleTimeoutMinutes
                                 ?? (_options.IdleTimeoutMinutes > 0 ? _options.IdleTimeoutMinutes : StoredNodeSettings.DefaultTranscriptionIdleTimeoutMinutes),
            VadInstalled = _pathResolver.IsVadInstalled(),
            // Named, because two adjacent booleans swapped positionally would report the VAD state as the capture capability, with no mapper test able to catch it.
            // The capability, not the operating system: false on Windows below the documented process-loopback build too, and computed here so the two projecting routes cannot disagree.
            ProcessCaptureSupported = _processCapture.IsSupported
        };
    }

    /// <inheritdoc />
    public Task<WhisperServerEvictResult> EjectAsync(CancellationToken ct) =>
        _supervisor.EvictAsync(ct);

    /// <inheritdoc />
    public async Task<TranscriptionModelCatalogView> GetModelsAsync(CancellationToken ct)
    {
        var settings = await LoadSettingsAsync(ct);
        var recommended = await GetRecommendedModelAsync(ct);
        return BuildCatalogView(NormalizeSelection(settings.TranscriptionSelectedModelId), recommended.Id);
    }

    /// <inheritdoc />
    public async Task<WhisperModelEntry> GetRecommendedModelAsync(CancellationToken ct)
    {
        // The EFFECTIVE profile, not the raw hardware one: a node whose GPU runtime has determinately fallen back to
        // CPU must be sized against RAM, or it is recommended a model it cannot actually run at speed.
        var profile = await _runtimeDeviceAudit.GetEffectiveProfileAsync(forceRefreshProfile: false, ct);
        var backend = await _backendSelector.SelectBackendAsync(ct);
        return WhisperModelRecommendation.Recommend(profile, backend);
    }

    /// <inheritdoc />
    public async Task<TranscriptionModelCatalogView> SelectModelAsync(string? modelId, CancellationToken ct)
    {
        string? validatedId = null;
        if (!string.IsNullOrWhiteSpace(modelId))
        {
            validatedId = (WhisperModelCatalog.Find(modelId)
                           ?? throw new ArgumentException("The requested transcription model is not in the catalogue.", nameof(modelId))).Id;
        }

        // Through UpdateAsync, never a SaveAsync composed from an earlier LoadAsync: the settings file is written
        // whole, and a read-modify-write under the store's own lock is what keeps a concurrent save from being lost.
        await _settingsStore.UpdateAsync(current => current with
        {
            TranscriptionSelectedModelId = validatedId
        }, ct);

        var recommended = await GetRecommendedModelAsync(ct);
        return BuildCatalogView(validatedId, recommended.Id);
    }

    /// <inheritdoc />
    public async Task<string> ResolveEffectiveModelIdAsync(CancellationToken ct)
    {
        var settings = await LoadSettingsAsync(ct);
        if (NormalizeSelection(settings.TranscriptionSelectedModelId) is { } selected)
        {
            return selected;
        }

        return (await GetRecommendedModelAsync(ct)).Id;
    }

    private TranscriptionModelCatalogView BuildCatalogView(string? selectedModelId, string recommendedModelId)
    {
        var models = WhisperModelCatalog.Models
                                        .Select(entry => new TranscriptionModelView
                                        {
                                            Entry = entry,
                                            Installed = _pathResolver.IsInstalled(entry),
                                            Download = _downloadCoordinator.GetStatus(entry.Id)
                                        })
                                        .ToArray();

        return new TranscriptionModelCatalogView
        {
            Models = models,
            SelectedModelId = selectedModelId,
            RecommendedModelId = recommendedModelId
        };
    }

    private async Task<StoredNodeSettings> LoadSettingsAsync(CancellationToken ct) =>
        await _settingsStore.LoadAsync(ct) ?? new StoredNodeSettings();

    /// <summary>
    ///     A stored id that is no longer in the catalogue reads as no selection at all, so a node whose catalogue row
    ///     was retired falls back to the recommendation instead of reporting a model that cannot be loaded.
    /// </summary>
    private static string? NormalizeSelection(string? storedModelId) =>
        string.IsNullOrWhiteSpace(storedModelId) ? null : WhisperModelCatalog.Find(storedModelId)?.Id;
}
