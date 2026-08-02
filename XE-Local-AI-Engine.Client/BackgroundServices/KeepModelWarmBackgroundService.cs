namespace XE_Local_AI_Engine.Client.BackgroundServices;

using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;

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
    private readonly ILlamaServerProcessSupervisor _processSupervisor;
    private readonly ILlamaCppSourceBuildActivity _sourceBuildActivity;
    private readonly LlamaServerSupervisorOptions _supervisorOptions;
    private readonly TimeProvider _timeProvider;
    private bool _activeTtlCadenceWarningLogged;
    private FailureSignature? _lastFailure;
    private long? _lastWarmAttemptTimestamp;
    private string? _lastWarmModelName;
    private bool _insufficientCapacityWarningLogged;
    private bool _missingModelWarningLogged;

    public KeepModelWarmBackgroundService(INodeRuntimeSettings runtimeSettings,
        ILocalModelProviderResolver providerResolver,
        ILlamaServerProcessSupervisor processSupervisor,
        ILlamaCppSourceBuildActivity sourceBuildActivity,
        LlamaServerSupervisorOptions supervisorOptions,
        TimeProvider timeProvider,
        ILogger<KeepModelWarmBackgroundService> logger)
    {
        _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
        _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
        _processSupervisor = processSupervisor ?? throw new ArgumentNullException(nameof(processSupervisor));
        _sourceBuildActivity = sourceBuildActivity ?? throw new ArgumentNullException(nameof(sourceBuildActivity));
        _supervisorOptions = supervisorOptions ?? throw new ArgumentNullException(nameof(supervisorOptions));
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
        string? attemptedModelName = null;
        try
        {
            var enabled = await _runtimeSettings.GetKeepModelWarmEnabledAsync(cancellationToken).ConfigureAwait(false);
            if (!enabled)
            {
                ResetCadence();
                ResetFailureLogging();
                _insufficientCapacityWarningLogged = false;
                return;
            }

            if (_supervisorOptions.MaxLoadedProcesses < 2)
            {
                ResetCadence();
                if (!_insufficientCapacityWarningLogged)
                {
                    _logger.LogWarning("Keep model warm is enabled, but the active llama.cpp process cap is {MaxLoadedProcesses}; at least two slots are required. Restart after raising the cap.",
                        _supervisorOptions.MaxLoadedProcesses);
                    _insufficientCapacityWarningLogged = true;
                }

                return;
            }

            _insufficientCapacityWarningLogged = false;
            if (_processSupervisor.IsKeepWarmSuppressed() || _sourceBuildActivity.ActiveBuildId is not null)
            {
                // Runtime mutation/build activity wins. Clearing cadence makes the first poll after it finishes warm
                // immediately instead of waiting out a timestamp that belonged to the previous runtime.
                ResetCadence();
                ResetFailureLogging();
                return;
            }

            var modelName = await _runtimeSettings.GetKeepModelWarmModelNameAsync(cancellationToken).ConfigureAwait(false);
            attemptedModelName = modelName;
            if (string.IsNullOrWhiteSpace(modelName))
            {
                ResetCadence();
                ResetFailureLogging();
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
            var effectiveInterval = ResolveEffectiveInterval(interval);
            if (effectiveInterval is null)
            {
                ResetCadence();
                return;
            }

            var now = _timeProvider.GetTimestamp();
            var modelChanged = !string.Equals(_lastWarmModelName, modelName, StringComparison.OrdinalIgnoreCase);
            if (!modelChanged
                && _lastWarmAttemptTimestamp is { } lastAttempt
                && _timeProvider.GetElapsedTime(lastAttempt, now) < effectiveInterval.Value)
            {
                return;
            }

            // Stamp before the call so a failed load retries at the configured cadence rather than hammering every poll.
            _lastWarmModelName = modelName;
            _lastWarmAttemptTimestamp = now;

            var provider = await _providerResolver.ResolveProviderForModelAsync(modelName, cancellationToken).ConfigureAwait(false);
            await provider.WarmModelAsync(modelName, cancellationToken).ConfigureAwait(false);
            ResetFailureLogging();
            _logger.LogDebug("Keep model warm refreshed model {ModelName} through provider {ProviderName}.", modelName, provider.ProviderName);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failure = new FailureSignature(attemptedModelName, exception.GetType(), exception.Message);
            if (exception is LlamaRuntimeException)
            {
                // The llama.cpp supervisor logs its own operational failure with process/model context. Keep this layer
                // at Debug to avoid duplicating the same warning every time the background policy calls through it.
                _lastFailure = failure;
                _logger.LogDebug(exception, "Keep model warm could not refresh the llama.cpp model; the service will retry at the configured cadence.");
            }
            else if (_lastFailure == failure)
            {
                _logger.LogDebug(exception, "Keep model warm iteration failed again; the service will retry at the configured cadence.");
            }
            else
            {
                _lastFailure = failure;
                _logger.LogWarning(exception, "Keep model warm iteration failed; the service will retry at the configured cadence.");
            }
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

    private TimeSpan? ResolveEffectiveInterval(TimeSpan configuredInterval)
    {
        var activeTtl = _supervisorOptions.IdleTimeToLive;
        var safeCadenceCeiling = TimeSpan.FromTicks(activeTtl.Ticks / 2);
        if (safeCadenceCeiling < SettingsPollInterval)
        {
            safeCadenceCeiling = SettingsPollInterval;
        }

        if (configuredInterval <= safeCadenceCeiling && configuredInterval < activeTtl)
        {
            _activeTtlCadenceWarningLogged = false;
            return configuredInterval;
        }

        if (!_activeTtlCadenceWarningLogged)
        {
            if (safeCadenceCeiling < activeTtl)
            {
                _logger.LogWarning("Keep model warm interval {ConfiguredInterval} is too close to the active llama.cpp idle TTL {IdleTtl}; using {EffectiveInterval} until the runtime restarts.",
                    configuredInterval, activeTtl, safeCadenceCeiling);
            }
            else
            {
                _logger.LogWarning("Keep model warm cannot run because the active llama.cpp idle TTL {IdleTtl} is not longer than the {PollInterval} settings poll interval.",
                    activeTtl, SettingsPollInterval);
            }

            _activeTtlCadenceWarningLogged = true;
        }

        return safeCadenceCeiling < activeTtl ? safeCadenceCeiling : null;
    }

    private void ResetFailureLogging()
    {
        _lastFailure = null;
    }

    private void ResetCadence()
    {
        _lastWarmModelName = null;
        _lastWarmAttemptTimestamp = null;
    }

    private sealed record FailureSignature(string? ModelName, Type ExceptionType, string Message);
}
