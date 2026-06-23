namespace XE_Local_AI_Engine.Client.Services.Capabilities.Implementation;

using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Represents capability reporter. Orchestrates the <see cref="ModelCapabilityProber" /> (runtime/model probing)
///     and the <see cref="CapabilityReportComposer" /> (hardware detection + report assembly) behind the
///     <see cref="ICapabilityReporter" /> facade, and owns report throttling plus the hub push.
/// </summary>
internal sealed class CapabilityReporter : ICapabilityReporter, IDisposable
{
    private static readonly TimeSpan ReportThrottleInterval = TimeSpan.FromSeconds(5);

    private readonly ICloudCredentialStore _cloudCredentialStore;
    private readonly CapabilityReportComposer _composer;
    private readonly string _defaultModel;
    private readonly IWorkerHubConnection _hubConnection;
    private readonly ILogger<CapabilityReporter> _logger;
    private readonly INodeSettingsStore _nodeSettingsStore;
    private readonly ModelCapabilityProber _prober;
    private readonly SemaphoreSlim _reportSync = new(initialCount: 1, maxCount: 1);
    private readonly TimeProvider _timeProvider;
    private readonly ITokenStore _tokenStore;
    private DateTimeOffset? _lastReportStartedAt;

    public CapabilityReporter(ModelCapabilityProber prober,
        CapabilityReportComposer composer,
        ICloudCredentialStore cloudCredentialStore,
        INodeSettingsStore nodeSettingsStore,
        IConfiguration configuration,
        IWorkerHubConnection hubConnection,
        ITokenStore tokenStore,
        TimeProvider timeProvider,
        ILogger<CapabilityReporter> logger)
    {
        _prober = prober ?? throw new ArgumentNullException(nameof(prober));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _cloudCredentialStore = cloudCredentialStore ?? throw new ArgumentNullException(nameof(cloudCredentialStore));
        _nodeSettingsStore = nodeSettingsStore ?? throw new ArgumentNullException(nameof(nodeSettingsStore));
        ArgumentNullException.ThrowIfNull(configuration);
        _hubConnection = hubConnection ?? throw new ArgumentNullException(nameof(hubConnection));
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _defaultModel = configuration.GetValue<string>("Agent:LocalChat:DefaultModel")
                        ?? configuration.GetValue<string>("Ollama:ChatModel")
                        ?? throw new InvalidOperationException("Agent:LocalChat:DefaultModel is required for capability reporting.");
    }

    public async Task<ClientCapabilities> DetectCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var hardware = await _composer.DetectHardwareAsync(cancellationToken).ConfigureAwait(false);
        var cloudCredentials = await _cloudCredentialStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var nodeSettings = await _nodeSettingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var detectedAt = _timeProvider.GetUtcNow();

        if (cloudCredentials is not null
            && string.Equals(cloudCredentials.ProviderName, CloudProviderOptions.ProviderAzureFoundry, StringComparison.OrdinalIgnoreCase))
        {
            return CapabilityReportComposer.ComposeCloud(cloudCredentials, nodeSettings, hardware, detectedAt);
        }

        var ollamaStatus = await _prober.DetectOllamaRuntimeAsync(cancellationToken).ConfigureAwait(false);
        var installedModelInventory = await _prober.GetInstalledModelInventoryAsync(cancellationToken).ConfigureAwait(false);
        var installedModelMetadata = await _prober.GetInstalledModelMetadataAsync(installedModelInventory.Models, cancellationToken).ConfigureAwait(false);
        var activeModel = await _prober.DetectActiveModelAsync(cancellationToken).ConfigureAwait(false);

        return _composer.ComposeLocal(hardware, ollamaStatus, installedModelInventory, installedModelMetadata, activeModel, nodeSettings, detectedAt);
    }

    public async Task ReportToApiAsync(CancellationToken cancellationToken = default)
    {
        // Standalone/desktop mode has no remote worker hub to report to (and no Ollama daemon to probe). IsPaired is the
        // canonical standalone-vs-paired signal — the same one IWorkerHubConnection.SendCapabilitiesAsync gates on — so
        // short-circuit BEFORE the capability probe: this skips both the Ollama capability probe and the hub send (which
        // would otherwise probe Ollama, then throw "hub not active" and be swallowed at Debug). No probe, no caught
        // exception, no latency.
        if (!_tokenStore.IsPaired)
        {
            _logger.LogDebug("Skipping capability report because the node is not paired (standalone/desktop mode — no worker hub).");
            return;
        }

        if (!await _reportSync.WaitAsync(millisecondsTimeout: 0, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogDebug("Skipping capability report because another report is already in progress.");
            return;
        }

        try
        {
            var now = _timeProvider.GetUtcNow();
            if (_lastReportStartedAt is not null && now - _lastReportStartedAt.Value < ReportThrottleInterval)
            {
                _logger.LogDebug("Skipping capability report because the last report started at {LastReportStartedAt}.", _lastReportStartedAt);
                return;
            }

            _lastReportStartedAt = now;
            var capabilities = await DetectCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            await _hubConnection.SendCapabilitiesAsync(capabilities, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Reported worker capabilities to API with {ModelCount} installed model(s).",
                capabilities.InstalledModels.Count);
        }
        finally
        {
            _reportSync.Release();
        }
    }

    public async Task<bool> VerifyOllamaAndModelAsync(string? modelName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!await _prober.IsRuntimeReachableAsync(cancellationToken).ConfigureAwait(false))
            {
                _logger.LogWarning("Ollama is not reachable during capability preflight.");
                return false;
            }
        }
        catch (HttpRequestException exception)
        {
            _logger.LogDebug(exception, "Ollama preflight: local endpoint not reachable (expected in desktop mode without an Ollama daemon).");
            return false;
        }

        var installedModels = await _prober.GetInstalledModelNamesAsync(cancellationToken).ConfigureAwait(false);
        if (installedModels.Count == 0)
        {
            _logger.LogWarning("Ollama is reachable but no local models are installed.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(modelName))
        {
            return installedModels.Contains(_defaultModel, StringComparer.OrdinalIgnoreCase);
        }

        if (installedModels.Contains(modelName, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        var canFallback = installedModels.Contains(_defaultModel, StringComparer.OrdinalIgnoreCase);
        if (canFallback)
        {
            _logger.LogWarning("Requested model '{RequestedModel}' not available, using fallback '{FallbackModel}'.",
                modelName,
                _defaultModel);
        }
        else
        {
            _logger.LogWarning("Requested model '{RequestedModel}' is unavailable and fallback model '{FallbackModel}' is not installed.",
                modelName,
                _defaultModel);
        }

        return canFallback;
    }

    public void Dispose()
    {
        _reportSync.Dispose();
    }
}
