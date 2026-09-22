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
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AppUpdateService> _logger;
    internal const string DesktopShellLeaseFileName = "desktop-shell.lock";
    private IVelopackUpdateManager? _primedUpdateManager;
    private FileStream? _standaloneUpdateLease;
    private readonly Action<FileStream>? _retainAcceptedLease;
    private bool _applyScheduled;

    public AppUpdateService(IVelopackUpdateManagerFactory updateManagerFactory,
        IAppUpdateState state,
        IOptions<AppUpdateChannelOptions> channelOptions,
        AppUpdateHostContext hostContext,
        ILogger<AppUpdateService> logger,
        TimeProvider timeProvider)
        : this(updateManagerFactory, state, channelOptions, hostContext, logger, timeProvider, retainAcceptedLease: null)
    {
    }

    internal AppUpdateService(IVelopackUpdateManagerFactory updateManagerFactory,
        IAppUpdateState state,
        IOptions<AppUpdateChannelOptions> channelOptions,
        AppUpdateHostContext hostContext,
        ILogger<AppUpdateService> logger,
        TimeProvider timeProvider,
        Action<FileStream>? retainAcceptedLease)
    {
        _retainAcceptedLease = retainAcceptedLease;
        _updateManagerFactory = updateManagerFactory ?? throw new ArgumentNullException(nameof(updateManagerFactory));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        ArgumentNullException.ThrowIfNull(channelOptions);
        _channelOptions = channelOptions.Value;
        _hostContext = hostContext ?? throw new ArgumentNullException(nameof(hostContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

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
        _standaloneUpdateLease?.Dispose();
        _operationGate.Dispose();
    }

    private async Task<AppUpdateSnapshot> CheckForUpdatesSerializedAsync(TimeSpan? minInterval, CancellationToken ct)
    {
        await _operationGate.WaitAsync(ct);
        try
        {
            var current = _state.Current;
            if (minInterval is { } interval && !IsStale(current.LastCheckedUtc, interval, _timeProvider.GetUtcNow()))
            {
                return current;
            }

            return await CheckForUpdatesCoreAsync(ct);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<AppUpdateSnapshot> CheckForUpdatesCoreAsync(CancellationToken ct)
    {
        if (!_hostContext.IsLocalMode)
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
            result = await manager.CheckForUpdateAsync(ct);
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

    private static bool IsStale(DateTimeOffset? checkedAtUtc, TimeSpan minInterval, DateTimeOffset now) =>
        checkedAtUtc is not { } checkedAt || now - checkedAt >= minInterval;

    public async Task<bool> ApplyAsync(CancellationToken ct)
    {
        if (!_hostContext.IsLocalMode || !_channelOptions.IsConfigured)
        {
            return false;
        }

        await _operationGate.WaitAsync(ct);
        FileStream? shellLease = null;
        try
        {
            if (!_state.Current.UpdateAvailable || _applyScheduled)
            {
                return false;
            }

#pragma warning disable CA2000 // The async finally disposes this lease unless accepted update ownership transfers to the service or process-exit holder.
            shellLease = AcquireStandaloneShellLease();
#pragma warning restore CA2000
            try
            {
                var manager = TakeUpdateManager();
                var applying = await manager.PrepareUpdateAndRestartAsync(_hostContext.RestartArgs, ct);
                if (applying)
                {
                    _applyScheduled = true;
                    _standaloneUpdateLease = shellLease;
                    shellLease = null;
                    if (_standaloneUpdateLease is { } acceptedLease && _retainAcceptedLease is not null)
                    {
                        _retainAcceptedLease(acceptedLease);
                        _standaloneUpdateLease = null;
                    }
                }

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
        }
        finally
        {
            if (shellLease is not null)
            {
                await shellLease.DisposeAsync();
            }

            _operationGate.Release();
        }
    }

    internal static void RetainLeaseUntilProcessExit(FileStream lease)
    {
        // Even ProcessExit runs before the process exits; only the OS may release this accepted-update lock.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => GC.KeepAlive(lease);
    }

    private FileStream? AcquireStandaloneShellLease()
    {
        if (_hostContext.IsShellOwned)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(_hostContext.DataDirectory) || !Path.IsPathFullyQualified(_hostContext.DataDirectory))
        {
            throw new AppUpdateException("The update could not verify the desktop lifetime. Restart XE and try again.");
        }

        try
        {
            return new FileStream(Path.Combine(_hostContext.DataDirectory, DesktopShellLeaseFileName),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        // A lock another process holds is the expected answer, not a fault: Windows reports ERROR_SHARING_VIOLATION
        // (0x80070020) or ERROR_LOCK_VIOLATION (0x80070021), Linux reports EAGAIN (11) from the advisory lock.
        catch (IOException exception) when (exception.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021) or 11)
        {
            throw new AppUpdateException("Close the native XE window, then apply this update from your browser.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new AppUpdateException("The update could not verify the desktop lifetime. Restart XE and try again.", exception);
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
        if (_hostContext.IsLocalMode && _channelOptions.IsConfigured)
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

        _state.Store(new AppUpdateSnapshot
        {
            CurrentVersion = currentVersion,
            AvailableVersion = null,
            UpdateAvailable = false,
            IsConfigured = _channelOptions.IsConfigured,
            IsDesktop = _hostContext.IsLocalMode,
            CheckStatus = checkStatus,
            LastCheckedUtc = null
        });
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
        new()
        {
            CurrentVersion = currentVersion,
            AvailableVersion = availableVersion,
            UpdateAvailable = updateAvailable,
            IsConfigured = isConfigured,
            IsDesktop = _hostContext.IsLocalMode,
            CheckStatus = checkStatus,
            LastCheckedUtc = _timeProvider.GetUtcNow()
        };

    private AppUpdateSnapshot FailedSnapshot(string currentVersion, AppUpdateFailureReason reason)
    {
        var safeReason = reason is AppUpdateFailureReason.None ? AppUpdateFailureReason.Unexpected : reason;
        _logger.LogWarning("The app self-update check failed ({FailureReason}).", safeReason);
        return Snapshot(currentVersion, isConfigured: true, checkStatus: AppUpdateCheckStatus.Failed);
    }
}
