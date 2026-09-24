namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

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
    private readonly INodeSettingsStore _settingsStore;
    internal const string DesktopShellLeaseFileName = "desktop-shell.lock";
    private IVelopackUpdateManager? _primedUpdateManager;
    private AppUpdateFeed? _primedFeed;
    private IVelopackUpdateManager? _winningUpdateManager;
    private FileStream? _standaloneUpdateLease;
    private readonly Action<FileStream>? _retainAcceptedLease;
    private bool _applyScheduled;

    public AppUpdateService(IVelopackUpdateManagerFactory updateManagerFactory,
        IAppUpdateState state,
        IOptions<AppUpdateChannelOptions> channelOptions,
        AppUpdateHostContext hostContext,
        ILogger<AppUpdateService> logger,
        TimeProvider timeProvider,
        INodeSettingsStore settingsStore)
        : this(updateManagerFactory, state, channelOptions, hostContext, logger, timeProvider, settingsStore,
            retainAcceptedLease: null)
    {
    }

    internal AppUpdateService(IVelopackUpdateManagerFactory updateManagerFactory,
        IAppUpdateState state,
        IOptions<AppUpdateChannelOptions> channelOptions,
        AppUpdateHostContext hostContext,
        ILogger<AppUpdateService> logger,
        TimeProvider timeProvider,
        INodeSettingsStore settingsStore,
        Action<FileStream>? retainAcceptedLease)
    {
        _retainAcceptedLease = retainAcceptedLease;
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
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

    public async Task<AppUpdateSnapshot> GetStatusAsync(CancellationToken ct)
    {
        var selected = await ResolveChannelAsync(ct);
        var current = _state.Current;

        // The node-settings SAVE path writes the channel without checking, so the cached offer can belong to the
        // channel the operator just left. Dropping it keeps AvailableChannel null exactly when nothing is offered.
        var stale = selected != current.SelectedChannel;

        // A record `with` on the cached snapshot: nothing is stored back, so a concurrent check cannot be clobbered.
        return current with
        {
            SelectedChannel = selected,
            DefaultChannel = _channelOptions.DefaultChannel,
            AvailableVersion = stale ? null : current.AvailableVersion,
            UpdateAvailable = !stale && current.UpdateAvailable,
            AvailableChannel = stale ? null : current.AvailableChannel
        };
    }

    public async Task<AppUpdateSnapshot> SetChannelAsync(AppUpdateChannel channel, CancellationToken ct)
    {
        // Through UpdateAsync so the mutation runs against the record the write lands on: a sibling writer's field
        // is never lost. The persist is the authority — a check under a policy that was not stored would lie.
        await _settingsStore.UpdateAsync(current => current with
        {
            UpdateChannel = AppUpdateChannelNames.ToWire(channel)
        }, ct);

        // CheckForUpdatesAsync passes minInterval: null, i.e. NO rate floor: a check under a new policy is not a
        // duplicate. The startup check and the manual refresh keep passing their own floors, unchanged.
        return await CheckForUpdatesAsync(ct);
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

            // The channel is checked too, not only the clock: node settings expose UpdateChannel on a SAVE that
            // runs no check, and a check under a new policy is never the duplicate the floor exists to suppress.
            if (minInterval is { } interval
                && !IsStale(current.LastCheckedUtc, interval, _timeProvider.GetUtcNow())
                && current.SelectedChannel == await ResolveChannelAsync(ct))
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

    /// <summary>The channel this node follows: the operator's stored choice, else the channel baked into the build.</summary>
    /// <remarks>
    ///     A null or unrecognised stored value reads as the baked default, which is the update visibility this
    ///     artifact has always had. The load answers from the settings store's in-memory cache, so this is not a file
    ///     read per poll.
    /// </remarks>
    private async Task<AppUpdateChannel> ResolveChannelAsync(CancellationToken ct)
    {
        var stored = (await _settingsStore.LoadAsync(ct)).UpdateChannel;
        return AppUpdateChannelNames.TryParse(stored, out var channel) ? channel : _channelOptions.DefaultChannel;
    }

    private async Task<AppUpdateSnapshot> CheckForUpdatesCoreAsync(CancellationToken ct)
    {
        var selectedChannel = await ResolveChannelAsync(ct);

        if (!_hostContext.IsLocalMode)
        {
            return StoreSnapshot(Snapshot("0.0.0", selectedChannel, isConfigured: _channelOptions.IsConfigured));
        }

        if (!_channelOptions.IsConfigured)
        {
            return StoreSnapshot(Snapshot("0.0.0", selectedChannel, isConfigured: false));
        }

        var feeds = _updateManagerFactory.ResolveFeeds(selectedChannel);
        var results = new List<VelopackCheckResult>(feeds.Count);
        var currentVersion = "0.0.0";
        string? recommendedVersion = null;
        VelopackCheckResult? winner = null;
        AppUpdateFeed? winningFeed = null;
        _winningUpdateManager = null;

        foreach (var feed in feeds)
        {
            var manager = TakeUpdateManager(feed);

            // Channel-independent: every manager wraps the same installation.
            currentVersion = manager.CurrentVersion;

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
                // No exception attached: feed messages can carry URLs or local paths. A thrown feed becomes one
                // Failed result rather than ending the check, so a broken dev feed cannot hide a main-feed update.
                _logger.LogWarning("The app self-update check failed ({FailureReason}).", AppUpdateFailureReason.Unexpected);
                result = new VelopackCheckResult
                {
                    Outcome = VelopackCheckOutcome.Failed,
                    AvailableVersion = null,
                    FailureReason = AppUpdateFailureReason.Unexpected
                };
            }

            results.Add(result);

            // The main feed is the ONLY source of the recommended version, and the policy guarantees exactly one.
            if (feed.IsMainFeed)
            {
                recommendedVersion = result.RecommendedVersion;
            }

            // Strictly higher, so a tie between two feeds keeps the earlier (main) one and produces one offer.
            if (result.Outcome is VelopackCheckOutcome.UpdateAvailable
                && AppUpdateVersions.IsHigher(result.AvailableVersion, winner?.AvailableVersion))
            {
                winner = result;
                winningFeed = feed;
                _winningUpdateManager = manager;
            }
        }

        if (winner is not null && winningFeed is not null)
        {
            return StoreSnapshot(Snapshot(currentVersion,
                selectedChannel,
                availableVersion: winner.AvailableVersion,
                updateAvailable: true,
                isConfigured: true,
                checkStatus: AppUpdateCheckStatus.Ready,
                recommendedVersion: recommendedVersion,
                availableChannel: OfferingChannel(winningFeed, winner.AvailableVersion)));
        }

        var status = AggregateOutcome(results);
        if (status is AppUpdateCheckStatus.Failed)
        {
            var reason = results.FirstOrDefault(result => result.Outcome is VelopackCheckOutcome.Failed)?.FailureReason
                         ?? AppUpdateFailureReason.Unexpected;
            return StoreSnapshot(FailedSnapshot(currentVersion, selectedChannel, reason, recommendedVersion));
        }

        return StoreSnapshot(Snapshot(currentVersion,
            selectedChannel,
            isConfigured: true,
            checkStatus: status,
            recommendedVersion: recommendedVersion));
    }

    /// <summary>
    ///     The lowest channel that also offers this version, so the dialog can name the stream the build came from.
    /// </summary>
    /// <remarks>
    ///     Derived from the VERSION, not from the operator's own channel: a Development user offered a plain stable
    ///     release must be told it is a stable release.
    /// </remarks>
    private static AppUpdateChannel OfferingChannel(AppUpdateFeed winningFeed, string? availableVersion)
    {
        if (!winningFeed.IsMainFeed)
        {
            return AppUpdateChannel.Development;
        }

        return AppUpdateVersions.IsPrerelease(availableVersion) ? AppUpdateChannel.Preview : AppUpdateChannel.Stable;
    }

    /// <summary>The reported status when no feed offered an update.</summary>
    /// <remarks>
    ///     One successful feed decides it: a Development user whose dev feed is momentarily 404 must still be told
    ///     the main feed answered, rather than being shown an offline node.
    /// </remarks>
    private static AppUpdateCheckStatus AggregateOutcome(IReadOnlyList<VelopackCheckResult> results)
    {
        if (results.Any(static result => result.Outcome is VelopackCheckOutcome.UpToDate or VelopackCheckOutcome.UpdateAvailable))
        {
            return AppUpdateCheckStatus.Ready;
        }

        if (results.Any(static result => result.Outcome is VelopackCheckOutcome.Failed))
        {
            return AppUpdateCheckStatus.Failed;
        }

        return results.Count is 0 ? AppUpdateCheckStatus.Failed : AppUpdateCheckStatus.Offline;
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

            // Re-check under the CURRENT policy so the apply uses the manager that found the winner: a dev-feed
            // winner applied through a main-feed manager would find nothing. The core call does not re-take the gate.
            await CheckForUpdatesCoreAsync(ct);
            if (!_state.Current.UpdateAvailable || _winningUpdateManager is null)
            {
                return false;
            }

#pragma warning disable CA2000 // The async finally disposes this lease unless accepted update ownership transfers to the service or process-exit holder.
            shellLease = AcquireStandaloneShellLease();
#pragma warning restore CA2000
            try
            {
                var manager = _winningUpdateManager;
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

                var applied = _state.Current;
                StoreSnapshot(Snapshot(manager.CurrentVersion,
                    applied.SelectedChannel,
                    isConfigured: true,
                    checkStatus: AppUpdateCheckStatus.Ready,
                    recommendedVersion: applied.RecommendedVersion));
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
                // The constructor cannot await, so the STORED channel is not read here: only CurrentVersion is,
                // and it is channel-independent. GetStatusAsync re-stamps the live channel before the SPA sees this.
                _primedFeed = DefaultChannelMainFeed();
                _primedUpdateManager = _updateManagerFactory.Create(_primedFeed);
                currentVersion = _primedUpdateManager.CurrentVersion;
            }
            catch (Exception)
            {
                _primedUpdateManager = null;
                _primedFeed = null;
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
            LastCheckedUtc = null,
            SelectedChannel = _channelOptions.DefaultChannel,
            DefaultChannel = _channelOptions.DefaultChannel
        });
    }

    /// <summary>The manager for one feed, reusing the primed instance only when it was primed for that same feed.</summary>
    private IVelopackUpdateManager TakeUpdateManager(AppUpdateFeed feed)
    {
        if (_primedUpdateManager is { } primed && _primedFeed == feed)
        {
            _primedUpdateManager = null;
            _primedFeed = null;
            return primed;
        }

        return _updateManagerFactory.Create(feed);
    }

    /// <summary>The main feed of the channel baked into this artifact.</summary>
    private AppUpdateFeed DefaultChannelMainFeed()
    {
        return _updateManagerFactory.ResolveFeeds(_channelOptions.DefaultChannel)[0];
    }

    private AppUpdateSnapshot Snapshot(string currentVersion,
        AppUpdateChannel selectedChannel,
        string? availableVersion = null,
        bool updateAvailable = false,
        bool isConfigured = false,
        AppUpdateCheckStatus checkStatus = AppUpdateCheckStatus.NotChecked,
        string? recommendedVersion = null,
        AppUpdateChannel? availableChannel = null) =>
        new()
        {
            CurrentVersion = currentVersion,
            AvailableVersion = availableVersion,
            UpdateAvailable = updateAvailable,
            IsConfigured = isConfigured,
            IsDesktop = _hostContext.IsLocalMode,
            CheckStatus = checkStatus,
            LastCheckedUtc = _timeProvider.GetUtcNow(),
            SelectedChannel = selectedChannel,
            DefaultChannel = _channelOptions.DefaultChannel,
            RecommendedVersion = recommendedVersion,
            AvailableChannel = availableChannel
        };

    private AppUpdateSnapshot FailedSnapshot(string currentVersion,
        AppUpdateChannel selectedChannel,
        AppUpdateFailureReason reason,
        string? recommendedVersion)
    {
        var safeReason = reason is AppUpdateFailureReason.None ? AppUpdateFailureReason.Unexpected : reason;
        _logger.LogWarning("The app self-update check failed ({FailureReason}).", safeReason);
        return Snapshot(currentVersion,
            selectedChannel,
            isConfigured: true,
            checkStatus: AppUpdateCheckStatus.Failed,
            recommendedVersion: recommendedVersion);
    }
}
