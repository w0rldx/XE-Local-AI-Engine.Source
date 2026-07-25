namespace XE_Local_AI_Engine.Client.BackgroundServices;

using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Keeps one operator-selected local chat model resident by periodically routing it to its owning provider and
///     invoking the provider's idempotent warm operation. Settings are re-read on every poll, so enable/disable, model,
///     and interval changes take effect without restarting the node.
/// </summary>
/// <remarks>
///     For llama.cpp, every due warm call reaches <c>EnsureRunningAsync</c>: a cold model is loaded, while an already
///     running process is reused and its idle timestamp is refreshed. The service must not skip that reuse touch merely
///     because a process is resident, or the supervisor's idle reaper would still evict it.
/// </remarks>
public sealed class KeepModelWarmBackgroundService : BackgroundService
{
    internal static readonly TimeSpan SettingsPollInterval = TimeSpan.FromSeconds(StoredNodeSettings.MinKeepModelWarmIntervalSeconds);

    private readonly ILogger<KeepModelWarmBackgroundService> _logger;
    private readonly INodeRuntimeSettings _runtimeSettings;
    private readonly ILocalModelProviderResolver _providerResolver;
    private readonly TimeProvider _timeProvider;
    private long? _lastWarmAttemptTimestamp;
    private string? _lastWarmModelName;
    private bool _missingModelWarningLogged;

    public KeepModelWarmBackgroundService(INodeRuntimeSettings runtimeSettings,
        ILocalModelProviderResolver providerResolver,
        TimeProvider timeProvider,
        ILogger<KeepModelWarmBackgroundService> logger)
    {
        _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
        _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Do not let a configured cold-model load block IHostedService.StartAsync and therefore host startup.
        await Task.Yield();

        try
        {
            await RunIterationAsync(stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(SettingsPollInterval, _timeProvider);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RunIterationAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }

    /// <summary>Runs one deterministic live-settings poll. Internal so tests can drive cadence without wall-clock waits.</summary>
    internal async Task RunIterationAsync(CancellationToken cancellationToken)
    {
        try
        {
            var enabled = await _runtimeSettings.GetKeepModelWarmEnabledAsync(cancellationToken).ConfigureAwait(false);
            if (!enabled)
            {
                ResetCadence();
                return;
            }

            var modelName = await _runtimeSettings.GetKeepModelWarmModelNameAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(modelName))
            {
                ResetCadence();
                if (!_missingModelWarningLogged)
                {
                    _logger.LogWarning("Keep model warm is enabled, but no model is selected.");
                    _missingModelWarningLogged = true;
                }

                return;
            }

            _missingModelWarningLogged = false;
            var interval = await _runtimeSettings.GetKeepModelWarmIntervalAsync(cancellationToken).ConfigureAwait(false);
            interval = NormalizeInterval(interval);

            var now = _timeProvider.GetTimestamp();
            var modelChanged = !string.Equals(_lastWarmModelName, modelName, StringComparison.OrdinalIgnoreCase);
            if (!modelChanged
                && _lastWarmAttemptTimestamp is { } lastAttempt
                && _timeProvider.GetElapsedTime(lastAttempt, now) < interval)
            {
                return;
            }

            // Stamp before the call so a failed load retries at the configured cadence rather than hammering every poll.
            _lastWarmModelName = modelName;
            _lastWarmAttemptTimestamp = now;

            var provider = await _providerResolver.ResolveProviderForModelAsync(modelName, cancellationToken).ConfigureAwait(false);
            await provider.WarmModelAsync(modelName, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Keep model warm refreshed model {ModelName} through provider {ProviderName}.", modelName, provider.ProviderName);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Keep model warm iteration failed; the service will retry.");
        }
    }

    private static TimeSpan NormalizeInterval(TimeSpan interval)
    {
        var minimum = TimeSpan.FromSeconds(StoredNodeSettings.MinKeepModelWarmIntervalSeconds);
        var maximum = TimeSpan.FromSeconds(StoredNodeSettings.MaxKeepModelWarmIntervalSeconds);
        return interval < minimum || interval > maximum
            ? TimeSpan.FromSeconds(StoredNodeSettings.DefaultKeepModelWarmIntervalSeconds)
            : interval;
    }

    private void ResetCadence()
    {
        _lastWarmModelName = null;
        _lastWarmAttemptTimestamp = null;
    }
}
