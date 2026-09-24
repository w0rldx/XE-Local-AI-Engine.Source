namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

using System.Net;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Velopack;
using Velopack.Exceptions;

/// <summary>
///     The real Velopack-backed <see cref="IVelopackUpdateManager" />, wrapping an <see cref="UpdateManager" /> over a
///     <see cref="PaginatingGithubSource" /> for the baked public repo and one resolved feed, with a null access token.
/// </summary>
/// <remarks>
///     All Velopack types stay inside this class. Check failures are reduced to sanitized categories so transport
///     outages can be distinguished from malformed feeds, integrity failures and unexpected faults without retaining
///     their potentially sensitive exception messages.
/// </remarks>
public sealed class VelopackUpdateManager : IVelopackUpdateManager
{
    private readonly Action<UpdateManager, VelopackAsset, string[]> _scheduleApplyAfterExit;
    private readonly UpdateManager _updateManager;

    private readonly PaginatingGithubSource? _source;

    internal VelopackUpdateManager(UpdateManager updateManager, PaginatingGithubSource? source = null) : this(updateManager,
        static (manager, release, restartArgs) =>
            manager.WaitExitThenApplyUpdates(release, silent: false, restart: true, restartArgs),
        source)
    {
    }

    internal VelopackUpdateManager(UpdateManager updateManager,
        Action<UpdateManager, VelopackAsset, string[]> scheduleApplyAfterExit,
        PaginatingGithubSource? source = null)
    {
        _updateManager = updateManager ?? throw new ArgumentNullException(nameof(updateManager));
        _scheduleApplyAfterExit = scheduleApplyAfterExit
                                  ?? throw new ArgumentNullException(nameof(scheduleApplyAfterExit));
        _source = source;
    }

    public bool IsInstalled => _updateManager.IsInstalled;

    public string CurrentVersion => _updateManager.CurrentVersion?.ToString() ?? "0.0.0";

    public async Task<VelopackCheckResult> CheckForUpdateAsync(CancellationToken ct)
    {
        // A raw-exe / dev run is not a Velopack install — there is nothing to check against. Report up-to-date so the
        // status endpoint degrades gracefully instead of throwing inside Velopack.
        if (!_updateManager.IsInstalled)
        {
            return new VelopackCheckResult
            {
                Outcome = VelopackCheckOutcome.UpToDate,
                AvailableVersion = null
            };
        }

        try
        {
            // Velopack 1.2.0's CheckForUpdatesAsync is parameterless — it takes no CancellationToken, so `ct` cannot be
            // flowed into the check itself (do not "fix" this by passing ct; the overload does not exist).
            var updateInfo = await _updateManager.CheckForUpdatesAsync();

            // Read AFTER the check: the source only captures the feed while serving it, and no extra request is made.
            var recommended = _source is null ? null : AppUpdateVersions.NewestStable(_source.LastFeedAssets);
            if (updateInfo is null)
            {
                return new VelopackCheckResult
                {
                    Outcome = VelopackCheckOutcome.UpToDate,
                    AvailableVersion = null,
                    RecommendedVersion = recommended
                };
            }

            var version = updateInfo.TargetFullRelease.Version.ToString();
            return new VelopackCheckResult
            {
                Outcome = VelopackCheckOutcome.UpdateAvailable,
                AvailableVersion = version,
                RecommendedVersion = recommended
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var (outcome, reason) = ClassifyFailure(exception);
            return new VelopackCheckResult
            {
                Outcome = outcome,
                AvailableVersion = null,
                FailureReason = reason
            };
        }
    }

    public async Task<bool> PrepareUpdateAndRestartAsync(IReadOnlyList<string> restartArgs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(restartArgs);

        if (!_updateManager.IsInstalled)
        {
            return false;
        }

        var updateInfo = await _updateManager.CheckForUpdatesAsync();
        if (updateInfo is null)
        {
            return false;
        }

        await _updateManager.DownloadUpdatesAsync(updateInfo, progress: null, ct);

        // Start the updater in wait-for-exit mode, but do NOT terminate this host here: ApplyUpdatesAndRestart exits the process
        // synchronously in Velopack 1.2.0, aborting the response that tells React to poll. The endpoint answers first, then stops the host.
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
///     Builds <see cref="VelopackUpdateManager" /> instances bound to the baked anonymous public source policy, one per
///     feed the operator's channel resolves to. The feed channel is passed explicitly on every check rather than read
///     back from the installed package metadata.
/// </summary>
public sealed class VelopackUpdateManagerFactory : IVelopackUpdateManagerFactory
{
    private readonly AppUpdateChannelOptions _options;

    public VelopackUpdateManagerFactory(IOptions<AppUpdateChannelOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public IReadOnlyList<AppUpdateFeed> ResolveFeeds(AppUpdateChannel channel)
    {
        return AppUpdateChannelPolicy.ResolveFeeds(channel, VelopackRuntimeInfo.SystemOs.GetOsShortName());
    }

    public IVelopackUpdateManager Create(AppUpdateFeed feed)
    {
        var source = CreateGithubSource(feed);

        // ExplicitChannel is the ONLY channel lever: the channel baked into the installed package is never trusted,
        // so a node that once applied a -dev package still follows the operator's current choice.

        // Velopack's version-downgrade option stays unassigned; its default of false is what makes every channel
        // switch forward-only (research/velopack-1.2.0-verification.md section a, D6). Naming it here fails a guard.
        return new VelopackUpdateManager(new UpdateManager(source, new UpdateOptions
        {
            ExplicitChannel = feed.VelopackChannel
        }), source);
    }

    internal PaginatingGithubSource CreateGithubSource(AppUpdateFeed feed)
    {
        ArgumentNullException.ThrowIfNull(feed);

        var policy = _options.SourcePolicy
                     ?? throw new InvalidOperationException("App self-update is not configured for this build.");
        return new PaginatingGithubSource(policy.GitHubRepositoryUrl, feed.IncludePrereleases, feed.VelopackChannel);
    }
}
