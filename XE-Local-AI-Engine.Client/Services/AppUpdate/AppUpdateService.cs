namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

using Microsoft.Extensions.Options;

/// <summary>
///     Default <see cref="IAppUpdateService" />. Reads the GitHub session from <see cref="IGitHubTokenStore" />, builds a
///     per-check <see cref="IVelopackUpdateManager" /> via the factory, maps the check outcome to an
///     <see cref="AppUpdateSnapshot" />, and stores it in <see cref="IAppUpdateState" />. The token is used only to
///     construct the manager and is never logged or returned.
/// </summary>
public sealed class AppUpdateService : IAppUpdateService
{
    private readonly IAppUpdateState _state;
    private readonly IGitHubTokenStore _tokenStore;
    private readonly IVelopackUpdateManagerFactory _updateManagerFactory;
    private readonly AppUpdateChannelOptions _channelOptions;
    private readonly AppUpdateHostContext _hostContext;
    private readonly ILogger<AppUpdateService> _logger;

    public AppUpdateService(IGitHubTokenStore tokenStore,
        IVelopackUpdateManagerFactory updateManagerFactory,
        IAppUpdateState state,
        IOptions<AppUpdateChannelOptions> channelOptions,
        AppUpdateHostContext hostContext,
        ILogger<AppUpdateService> logger)
    {
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _updateManagerFactory = updateManagerFactory ?? throw new ArgumentNullException(nameof(updateManagerFactory));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        ArgumentNullException.ThrowIfNull(channelOptions);
        _channelOptions = channelOptions.Value;
        _hostContext = hostContext ?? throw new ArgumentNullException(nameof(hostContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<AppUpdateSnapshot> CheckForUpdatesAsync(CancellationToken ct)
    {
        // Inert outside desktop mode or on an unconfigured build — record a signed-out snapshot, make no GitHub call.
        if (!_hostContext.IsDesktop || !_channelOptions.IsConfigured)
        {
            return StoreSnapshot(SignedOutSnapshot(currentVersion: "0.0.0", isOffline: false));
        }

        var session = await _tokenStore.GetSessionAsync(ct).ConfigureAwait(false);
        if (session is null)
        {
            // Signed out → no token → no GitHub call (test: WhenSignedOut_DoesNotCheck).
            return StoreSnapshot(SignedOutSnapshot(currentVersion: "0.0.0", isOffline: false));
        }

        var manager = _updateManagerFactory.Create(session.AccessToken);
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
        catch (Exception exception)
        {
            // The manager maps known auth/offline failures itself; any leak here is recorded as offline, never crashes.
            _logger.LogWarning(exception, "The app self-update check could not complete.");
            return StoreSnapshot(OfflineSnapshot(currentVersion, session.Login));
        }

        var snapshot = result.Outcome switch
        {
            VelopackCheckOutcome.UpdateAvailable => Snapshot(currentVersion,
                result.AvailableVersion,
                updateAvailable: true,
                AppUpdateAuthState.SignedIn,
                isOffline: false,
                session.Login),
            VelopackCheckOutcome.UpToDate => Snapshot(currentVersion,
                availableVersion: null,
                updateAvailable: false,
                AppUpdateAuthState.SignedIn,
                isOffline: false,
                session.Login),
            VelopackCheckOutcome.Unauthorized => Snapshot(currentVersion,
                availableVersion: null,
                updateAvailable: false,
                AppUpdateAuthState.ReauthRequired,
                isOffline: false,
                session.Login),
            VelopackCheckOutcome.Forbidden => Snapshot(currentVersion,
                availableVersion: null,
                updateAvailable: false,
                AppUpdateAuthState.NoAccess,
                isOffline: false,
                session.Login),
            _ => OfflineSnapshot(currentVersion, session.Login)
        };

        return StoreSnapshot(snapshot);
    }

    public async Task<bool> ApplyAsync(CancellationToken ct)
    {
        if (!_hostContext.IsDesktop || !_channelOptions.IsConfigured)
        {
            return false;
        }

        var session = await _tokenStore.GetSessionAsync(ct).ConfigureAwait(false);
        if (session is null)
        {
            return false;
        }

        var manager = _updateManagerFactory.Create(session.AccessToken);

        try
        {
            // Re-uses the current process restart args so the relaunch comes back up in desktop mode and re-binds the
            // persisted loopback port. On a real apply this does not return (the process is replaced); a false result
            // means the live re-check found nothing to apply (a stale "update available" snapshot has gone away).
            return await manager.ApplyUpdateAndRestartAsync(_hostContext.RestartArgs, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Sanitized: never surface the token, repo URL, or local path to the caller.
            _logger.LogWarning(exception, "Applying the app self-update failed.");
            throw new AppUpdateException("The update could not be applied. Please try again later.", exception);
        }
    }

    private AppUpdateSnapshot StoreSnapshot(AppUpdateSnapshot snapshot)
    {
        _state.Store(snapshot);
        return snapshot;
    }

    private AppUpdateSnapshot SignedOutSnapshot(string currentVersion, bool isOffline) =>
        Snapshot(currentVersion, availableVersion: null, updateAvailable: false, AppUpdateAuthState.SignedOut, isOffline, login: null);

    private AppUpdateSnapshot OfflineSnapshot(string currentVersion, string login) =>
        Snapshot(currentVersion, availableVersion: null, updateAvailable: false, AppUpdateAuthState.SignedIn, isOffline: true, login);

    private AppUpdateSnapshot Snapshot(string currentVersion,
        string? availableVersion,
        bool updateAvailable,
        AppUpdateAuthState authState,
        bool isOffline,
        string? login) =>
        new(currentVersion,
            availableVersion,
            updateAvailable,
            authState,
            _hostContext.IsDesktop,
            isOffline,
            login,
            DateTimeOffset.UtcNow);
}
