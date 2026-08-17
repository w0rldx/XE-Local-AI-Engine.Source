namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

using System.Net;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Velopack;
using Velopack.Exceptions;
using Velopack.Sources;

/// <summary>
///     The real Velopack-backed <see cref="IVelopackUpdateManager" />. Wraps a <see cref="UpdateManager" /> over a
///     <see cref="GithubSource" /> for the baked public repo and explicit stable/RC track, with a null access token. All
///     Velopack types stay inside this class. Check failures are reduced to sanitized categories so transport outages
///     can be distinguished from malformed feeds, integrity failures, and unexpected faults without retaining their
///     potentially sensitive exception messages.
/// </summary>
public sealed class VelopackUpdateManager : IVelopackUpdateManager
{
    private readonly Action<UpdateManager, VelopackAsset, string[]> _scheduleApplyAfterExit;
    private readonly UpdateManager _updateManager;

    internal VelopackUpdateManager(UpdateManager updateManager) : this(updateManager,
        static (manager, release, restartArgs) =>
            manager.WaitExitThenApplyUpdates(release, silent: false, restart: true, restartArgs))
    {
    }

    internal VelopackUpdateManager(UpdateManager updateManager,
        Action<UpdateManager, VelopackAsset, string[]> scheduleApplyAfterExit)
    {
        _updateManager = updateManager ?? throw new ArgumentNullException(nameof(updateManager));
        _scheduleApplyAfterExit = scheduleApplyAfterExit
                                  ?? throw new ArgumentNullException(nameof(scheduleApplyAfterExit));
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var (outcome, reason) = ClassifyFailure(exception);
            return new VelopackCheckResult(outcome, AvailableVersion: null, reason);
        }
    }

    public async Task<bool> PrepareUpdateAndRestartAsync(IReadOnlyList<string> restartArgs, CancellationToken ct)
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

        // Start the updater in wait-for-exit mode, but do NOT terminate this host here. ApplyUpdatesAndRestart exits the
        // process synchronously in Velopack 1.2.0, which aborts the HTTP response that tells React to begin restart
        // polling. The endpoint completes { applying: true } first and then requests graceful host shutdown.
        _scheduleApplyAfterExit(_updateManager, updateInfo.TargetFullRelease, [.. restartArgs]);
        return true;
    }

    internal static ClassifiedFailure ClassifyFailure(Exception exception)
    {
        return exception switch
        {
            OperationCanceledException or TimeoutException => new ClassifiedFailure(VelopackCheckOutcome.Offline, AppUpdateFailureReason.Timeout),
            ChecksumFailedException => new ClassifiedFailure(VelopackCheckOutcome.Failed, AppUpdateFailureReason.Integrity),
            AuthenticationException => new ClassifiedFailure(VelopackCheckOutcome.Failed, AppUpdateFailureReason.Tls),
            JsonException or FormatException or InvalidDataException =>
                new ClassifiedFailure(VelopackCheckOutcome.Failed, AppUpdateFailureReason.MalformedFeed),
            HttpRequestException httpException => ClassifyHttpFailure(httpException),
            _ => new ClassifiedFailure(VelopackCheckOutcome.Failed, AppUpdateFailureReason.Unexpected)
        };
    }

    private static ClassifiedFailure ClassifyHttpFailure(HttpRequestException exception)
    {
        if (exception.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout)
        {
            return new ClassifiedFailure(VelopackCheckOutcome.Offline, AppUpdateFailureReason.Timeout);
        }

        return exception.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError or
                HttpRequestError.ConnectionError or
                HttpRequestError.ProxyTunnelError or
                HttpRequestError.ResponseEnded => new ClassifiedFailure(VelopackCheckOutcome.Offline, AppUpdateFailureReason.Transport),
            HttpRequestError.SecureConnectionError => new ClassifiedFailure(VelopackCheckOutcome.Failed, AppUpdateFailureReason.Tls),
            HttpRequestError.InvalidResponse => new ClassifiedFailure(VelopackCheckOutcome.Failed, AppUpdateFailureReason.MalformedFeed),
            _ => new ClassifiedFailure(VelopackCheckOutcome.Failed, AppUpdateFailureReason.Http)
        };
    }

    /// <summary>An update-check failure mapped to the reported outcome and the stable failure reason.</summary>
    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct ClassifiedFailure(VelopackCheckOutcome Outcome, AppUpdateFailureReason Reason);
}

/// <summary>
///     Builds <see cref="VelopackUpdateManager" /> instances bound to the baked anonymous public source policy. Velopack
///     continues to select its Windows/Linux feed channel from the installed package metadata.
/// </summary>
public sealed class VelopackUpdateManagerFactory : IVelopackUpdateManagerFactory
{
    private readonly AppUpdateChannelOptions _options;

    public VelopackUpdateManagerFactory(IOptions<AppUpdateChannelOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public IVelopackUpdateManager Create()
    {
        return new VelopackUpdateManager(new UpdateManager(CreateGithubSource()));
    }

    internal GithubSource CreateGithubSource()
    {
        var policy = _options.SourcePolicy
                     ?? throw new InvalidOperationException("App self-update is not configured for this build.");
        return new GithubSource(policy.GitHubRepositoryUrl, accessToken: null, policy.IncludePrereleases);
    }
}
