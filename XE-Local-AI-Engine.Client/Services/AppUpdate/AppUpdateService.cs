namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

using Microsoft.Extensions.Options;

/// <summary>Orchestrates anonymous public-release checks and operator-initiated Velopack applies.</summary>
public sealed class AppUpdateService : IAppUpdateService, IDisposable
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly IAppUpdateState _state;
    private readonly IVelopackUpdateManagerFactory _updateManagerFactory;
    private readonly AppUpdateChannelOptions _channelOptions;
    private readonly AppUpdateHostContext _hostContext;
    private readonly ILogger<AppUpdateService> _logger;
    private IVelopackUpdateManager? _primedUpdateManager;

    public AppUpdateService(IVelopackUpdateManagerFactory updateManagerFactory,
        IAppUpdateState state,
        IOptions<AppUpdateChannelOptions> channelOptions,
        AppUpdateHostContext hostContext,
        ILogger<AppUpdateService> logger)
    {
        _updateManagerFactory = updateManagerFactory ?? throw new ArgumentNullException(nameof(updateManagerFactory));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        ArgumentNullException.ThrowIfNull(channelOptions);
        _channelOptions = channelOptions.Value;
        _hostContext = hostContext ?? throw new ArgumentNullException(nameof(hostContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        PrimeInitialSnapshot();
    }

    public Task<AppUpdateSnapshot> CheckForUpdatesAsync(CancellationToken ct) =>
        CheckForUpdatesSerializedAsync(minInterval: null, ct);

    public Task<AppUpdateSnapshot> RefreshIfStaleAsync(TimeSpan minInterval, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minInterval, TimeSpan.Zero);
        return CheckForUpdatesSerializedAsync(minInterval, ct);
    }

    public void Dispose()
    {
        _operationGate.Dispose();
    }

    private async Task<AppUpdateSnapshot> CheckForUpdatesSerializedAsync(TimeSpan? minInterval, CancellationToken ct)
    {
        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = _state.Current;
            if (minInterval is { } interval && !IsStale(current.LastCheckedUtc, interval))
            {
                return current;
            }

            return await CheckForUpdatesCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<AppUpdateSnapshot> CheckForUpdatesCoreAsync(CancellationToken ct)
    {
        if (!_hostContext.IsDesktop)
        {
            return StoreSnapshot(Snapshot("0.0.0", isConfigured: _channelOptions.IsConfigured));
        }

        if (!_channelOptions.IsConfigured)
        {
            return StoreSnapshot(Snapshot("0.0.0", isConfigured: false));
        }

        var manager = TakeUpdateManager();
        var currentVersion = manager.CurrentVersion;

        VelopackCheckResult result;
        try
        {
            result = await manager.CheckForUpdateAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Do not attach the exception: feed/parser messages can contain URLs or local paths.
            _logger.LogWarning("The app self-update check failed ({FailureReason}).", AppUpdateFailureReason.Unexpected);
            return StoreSnapshot(Snapshot(currentVersion, isConfigured: true, checkStatus: AppUpdateCheckStatus.Failed));
        }

        var snapshot = result.Outcome switch
        {
            VelopackCheckOutcome.UpdateAvailable => Snapshot(currentVersion,
                availableVersion: result.AvailableVersion,
                updateAvailable: true,
                isConfigured: true,
                checkStatus: AppUpdateCheckStatus.Ready),
            VelopackCheckOutcome.UpToDate => Snapshot(currentVersion,
                isConfigured: true,
                checkStatus: AppUpdateCheckStatus.Ready),
            VelopackCheckOutcome.Offline => Snapshot(currentVersion,
                isConfigured: true,
                checkStatus: AppUpdateCheckStatus.Offline),
            VelopackCheckOutcome.Failed => FailedSnapshot(currentVersion, result.FailureReason),
            _ => FailedSnapshot(currentVersion, AppUpdateFailureReason.Unexpected)
        };

        return StoreSnapshot(snapshot);
    }

    private static bool IsStale(DateTimeOffset? checkedAtUtc, TimeSpan minInterval) =>
        checkedAtUtc is not { } checkedAt || DateTimeOffset.UtcNow - checkedAt >= minInterval;

    public async Task<bool> ApplyAsync(CancellationToken ct)
    {
        if (!_hostContext.IsDesktop || !_channelOptions.IsConfigured)
        {
            return false;
        }

        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_state.Current.UpdateAvailable)
            {
                return false;
            }

            var manager = TakeUpdateManager();
            var applying = await manager.PrepareUpdateAndRestartAsync(_hostContext.RestartArgs, ct).ConfigureAwait(false);
            // Clear the advertised update for both outcomes. On false, the live feed no longer has an applicable update;
            // on true, this prevents a concurrent/retried request from scheduling a second updater before shutdown.
            StoreSnapshot(Snapshot(manager.CurrentVersion,
                isConfigured: true,
                checkStatus: AppUpdateCheckStatus.Ready));

            return applying;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Do not attach the exception to the log: downloader errors may include the feed URL or local paths.
            _logger.LogWarning("Applying the app self-update failed.");
            throw new AppUpdateException("The update could not be applied. Please try again later.", exception);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private AppUpdateSnapshot StoreSnapshot(AppUpdateSnapshot snapshot)
    {
        _state.Store(snapshot);
        return snapshot;
    }

    private void PrimeInitialSnapshot()
    {
        if (_state.Current != AppUpdateSnapshot.Empty)
        {
            return;
        }

        var currentVersion = "0.0.0";
        var checkStatus = AppUpdateCheckStatus.NotChecked;
        if (_hostContext.IsDesktop && _channelOptions.IsConfigured)
        {
            try
            {
                _primedUpdateManager = _updateManagerFactory.Create();
                currentVersion = _primedUpdateManager.CurrentVersion;
            }
            catch (Exception)
            {
                _primedUpdateManager = null;
                checkStatus = AppUpdateCheckStatus.Failed;
                _logger.LogWarning("The app self-update version could not be determined ({FailureReason}).",
                    AppUpdateFailureReason.Unexpected);
            }
        }

        _state.Store(new AppUpdateSnapshot(currentVersion,
            AvailableVersion: null,
            UpdateAvailable: false,
            IsConfigured: _channelOptions.IsConfigured,
            IsDesktop: _hostContext.IsDesktop,
            CheckStatus: checkStatus,
            LastCheckedUtc: null));
    }

    private IVelopackUpdateManager TakeUpdateManager()
    {
        var manager = _primedUpdateManager;
        _primedUpdateManager = null;
        return manager ?? _updateManagerFactory.Create();
    }

    private AppUpdateSnapshot Snapshot(string currentVersion,
        string? availableVersion = null,
        bool updateAvailable = false,
        bool isConfigured = false,
        AppUpdateCheckStatus checkStatus = AppUpdateCheckStatus.NotChecked) =>
        new(currentVersion,
            availableVersion,
            updateAvailable,
            isConfigured,
            _hostContext.IsDesktop,
            checkStatus,
            DateTimeOffset.UtcNow);

    private AppUpdateSnapshot FailedSnapshot(string currentVersion, AppUpdateFailureReason reason)
    {
        var safeReason = reason is AppUpdateFailureReason.None ? AppUpdateFailureReason.Unexpected : reason;
        _logger.LogWarning("The app self-update check failed ({FailureReason}).", safeReason);
        return Snapshot(currentVersion, isConfigured: true, checkStatus: AppUpdateCheckStatus.Failed);
    }
}
