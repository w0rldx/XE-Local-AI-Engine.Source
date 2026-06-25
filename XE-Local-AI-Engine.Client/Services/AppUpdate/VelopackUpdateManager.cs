namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

using System.Net;
using Microsoft.Extensions.Options;
using Velopack;
using Velopack.Sources;
using XE_Local_AI_Engine.Client.Services.AppUpdate;

/// <summary>
///     The real Velopack-backed <see cref="IVelopackUpdateManager" />. Wraps a <see cref="UpdateManager" /> over a
///     <see cref="GithubSource" /> for the baked flavor repo (prerelease included while shipping <c>rc.N</c>), with the
///     user access token passed as the GitHub auth token. All Velopack types stay inside this class. Auth / offline
///     failures from <see cref="UpdateManager.CheckForUpdatesAsync" /> are mapped to a <see cref="VelopackCheckResult" />
///     rather than thrown, so a revoked token or an offline box never crashes the background check.
/// </summary>
public sealed class VelopackUpdateManager : IVelopackUpdateManager
{
    private readonly UpdateManager _updateManager;

    internal VelopackUpdateManager(UpdateManager updateManager)
    {
        _updateManager = updateManager ?? throw new ArgumentNullException(nameof(updateManager));
    }

    public bool IsInstalled => _updateManager.IsInstalled;

    public string CurrentVersion => _updateManager.CurrentVersion?.ToString() ?? "0.0.0";

    public async Task<VelopackCheckResult> CheckForUpdateAsync(CancellationToken ct)
    {
        // A raw-exe / dev run is not a Velopack install — there is nothing to check against. Report up-to-date so the
        // status endpoint degrades gracefully instead of throwing inside Velopack.
        if (!_updateManager.IsInstalled)
        {
            return new VelopackCheckResult(VelopackCheckOutcome.UpToDate, AvailableVersion: null);
        }

        try
        {
            // Velopack 1.2.0's CheckForUpdatesAsync is parameterless — it takes no CancellationToken, so `ct` cannot be
            // flowed into the check itself (do not "fix" this by passing ct; the overload does not exist).
            var updateInfo = await _updateManager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (updateInfo is null)
            {
                return new VelopackCheckResult(VelopackCheckOutcome.UpToDate, AvailableVersion: null);
            }

            var version = updateInfo.TargetFullRelease.Version.ToString();
            return new VelopackCheckResult(VelopackCheckOutcome.UpdateAvailable, version);
        }
        catch (HttpRequestException exception)
        {
            return new VelopackCheckResult(MapHttpFailure(exception.StatusCode), AvailableVersion: null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Any other read failure (DNS, TLS, malformed feed) is treated as offline — advertise no update, never crash.
            return new VelopackCheckResult(VelopackCheckOutcome.Offline, AvailableVersion: null);
        }
    }

    public async Task<bool> ApplyUpdateAndRestartAsync(IReadOnlyList<string> restartArgs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(restartArgs);

        if (!_updateManager.IsInstalled)
        {
            return false;
        }

        var updateInfo = await _updateManager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (updateInfo is null)
        {
            return false;
        }

        await _updateManager.DownloadUpdatesAsync(updateInfo, progress: null, ct).ConfigureAwait(false);

        // Applies the downloaded release and restarts into the new version. Re-using the current restart args keeps the
        // relaunch in desktop mode so the persisted loopback port is re-bound and the browser tab reconnects. This call
        // replaces the process and does not return on success — the `return true` below is unreachable on a real apply
        // but keeps the seam honest for tests / non-restarting hosts.
        _updateManager.ApplyUpdatesAndRestart(updateInfo.TargetFullRelease, [.. restartArgs]);
        return true;
    }

    private static VelopackCheckOutcome MapHttpFailure(HttpStatusCode? statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.Unauthorized => VelopackCheckOutcome.Unauthorized,
            HttpStatusCode.Forbidden => VelopackCheckOutcome.Forbidden,
            _ => VelopackCheckOutcome.Offline
        };
    }
}

/// <summary>
///     Builds <see cref="VelopackUpdateManager" /> instances bound to the baked flavor repo and a user
///     access token. The repo URL comes from <see cref="AppUpdateChannelOptions" /> — fixed per artifact, so a tester
///     build can never construct a manager against the main repo.
/// </summary>
public sealed class VelopackUpdateManagerFactory : IVelopackUpdateManagerFactory
{
    private readonly AppUpdateChannelOptions _options;

    public VelopackUpdateManagerFactory(IOptions<AppUpdateChannelOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public IVelopackUpdateManager Create(string accessToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        // prerelease:true while shipping rc.N so a published RC is seen as an available update. The token is sent by
        // GithubSource as Authorization: Bearer for private-repo reads.
        var source = new GithubSource(_options.GitHubRepositoryUrl, accessToken, prerelease: true);
        return new VelopackUpdateManager(new UpdateManager(source));
    }
}
