namespace XE_Local_AI_Engine.Client.BackgroundServices;

using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Runs ONE app self-update check per app start, off the startup path: after a short non-blocking delay it asks
///     <see cref="IAppUpdateService" /> to check GitHub for a newer release and record the result in
///     <see cref="IAppUpdateState" />. Modeled on <c>LlamaCppUpdateCheckService</c>.
/// </summary>
/// <remarks>
///     Recording the result is what lets the status endpoint surface "update available" without re-hitting GitHub on
///     every poll. <b>Desktop + configured only, offline-tolerant:</b> the service is registered only in desktop mode,
///     and <see cref="IAppUpdateService.RefreshIfStaleAsync" /> additionally no-ops when unconfigured and degrades an
///     offline or malformed feed to a recorded snapshot rather than a crash. <b>Notify-only:</b> it never downloads or
///     applies — an apply is always operator-initiated through the update endpoint.
/// </remarks>
public sealed class AppUpdateCheckService : BackgroundService
{
    // Keep the automatic probe well away from startup and within the anonymous GitHub rate budget.
    internal static readonly TimeSpan DefaultStartupDelay = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan DefaultMinimumCheckInterval = TimeSpan.FromMinutes(10);

    private readonly IAppUpdateService _appUpdateService;
    private readonly ILogger<AppUpdateCheckService> _logger;
    private readonly INodeRuntimeSettings _nodeRuntimeSettings;
    private readonly TimeSpan _startupDelay;
    private readonly TimeProvider _timeProvider;

    public AppUpdateCheckService(IAppUpdateService appUpdateService,
        INodeRuntimeSettings nodeRuntimeSettings,
        TimeProvider timeProvider,
        ILogger<AppUpdateCheckService> logger)
        : this(appUpdateService, nodeRuntimeSettings, timeProvider, logger, DefaultStartupDelay)
    {
    }

    // Test seam: injects the startup delay so the one-shot check can be exercised without a 10s wait.
    internal AppUpdateCheckService(IAppUpdateService appUpdateService,
        INodeRuntimeSettings nodeRuntimeSettings,
        TimeProvider timeProvider,
        ILogger<AppUpdateCheckService> logger,
        TimeSpan startupDelay)
    {
        _appUpdateService = appUpdateService ?? throw new ArgumentNullException(nameof(appUpdateService));
        _nodeRuntimeSettings = nodeRuntimeSettings ?? throw new ArgumentNullException(nameof(nodeRuntimeSettings));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _startupDelay = startupDelay;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (_startupDelay > TimeSpan.Zero)
            {
                await Task.Delay(_startupDelay, stoppingToken);
            }

            // The gate goes AFTER the startup delay so a node parked on an undecided profile is not also holding the
            // delay open, and BEFORE the one-shot check so CheckOnceAsync stays the pure check.
            await ExternalAccessGate.WaitUntilDecidedAsync(_nodeRuntimeSettings, _timeProvider, stoppingToken);
            if (!await _nodeRuntimeSettings.GetAutoCheckApplicationUpdatesAsync(stoppingToken))
            {
                _logger.LogDebug("The automatic application-update check is disabled by the node's external-access settings.");
                return;
            }

            await CheckOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown during the delay or the check — nothing to record.
        }
    }

    // Internal so a test can drive the one-shot check directly (no delay, deterministic).
    internal async Task CheckOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _appUpdateService.RefreshIfStaleAsync(DefaultMinimumCheckInterval, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Never crash startup or emit feed/parser details over an update check; log a fixed message and move on.
            _logger.LogWarning("The app self-update check could not complete.");
        }
    }
}
