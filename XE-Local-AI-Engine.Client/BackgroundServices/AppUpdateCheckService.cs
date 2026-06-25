namespace XE_Local_AI_Engine.Client.BackgroundServices;

using XE_Local_AI_Engine.Client.Services.AppUpdate;

/// <summary>
///     Runs ONE app self-update check per app start, off the startup path: after a short non-blocking delay it asks
///     <see cref="IAppUpdateService" /> to check GitHub for a newer release and record the result in
///     <see cref="IAppUpdateState" />, so the status endpoint can surface "update available" without re-hitting GitHub on
///     every poll. Modeled on <c>LlamaCppUpdateCheckService</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Desktop + signed-in only, offline-tolerant.</b> The service itself is only registered in desktop mode;
///         <see cref="IAppUpdateService.CheckForUpdatesAsync" /> additionally no-ops when signed out / unconfigured and
///         degrades a 401/403/offline to a recorded snapshot — never a crash.
///     </para>
///     <para>
///         <b>Notify-only.</b> It never downloads or applies — apply is always operator-initiated via the update endpoint.
///     </para>
/// </remarks>
public sealed class AppUpdateCheckService : BackgroundService
{
    // A short, non-blocking delay so the host finishes coming up before the (network) GitHub probe runs.
    private static readonly TimeSpan DefaultStartupDelay = TimeSpan.FromSeconds(10);

    private readonly IAppUpdateService _appUpdateService;
    private readonly ILogger<AppUpdateCheckService> _logger;
    private readonly TimeSpan _startupDelay;

    public AppUpdateCheckService(IAppUpdateService appUpdateService, ILogger<AppUpdateCheckService> logger)
        : this(appUpdateService, logger, DefaultStartupDelay)
    {
    }

    // Test seam: injects the startup delay so the one-shot check can be exercised without a 10s wait.
    internal AppUpdateCheckService(IAppUpdateService appUpdateService,
        ILogger<AppUpdateCheckService> logger,
        TimeSpan startupDelay)
    {
        _appUpdateService = appUpdateService ?? throw new ArgumentNullException(nameof(appUpdateService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _startupDelay = startupDelay;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (_startupDelay > TimeSpan.Zero)
            {
                await Task.Delay(_startupDelay, stoppingToken).ConfigureAwait(false);
            }

            await CheckOnceAsync(stoppingToken).ConfigureAwait(false);
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
            await _appUpdateService.CheckForUpdatesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Never crash startup over an update check — the service degrades failures internally; log and move on.
            _logger.LogWarning(exception, "The app self-update check could not complete.");
        }
    }
}
