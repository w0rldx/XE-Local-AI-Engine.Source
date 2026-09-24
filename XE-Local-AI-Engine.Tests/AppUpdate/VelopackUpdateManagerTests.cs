namespace XE_Local_AI_Engine.Tests.AppUpdate;

using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Authentication;
using Microsoft.Extensions.Options;
using Velopack;
using Velopack.Exceptions;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>Proves the real Velopack adapter uses an anonymous public GitHub source and supports portable installs.</summary>
[Category(TestCategories.Unit)]
public sealed class VelopackUpdateManagerTests
{
    [Test]
    [Arguments(AppUpdateChannel.Stable)]
    [Arguments(AppUpdateChannel.Preview)]
    [Arguments(AppUpdateChannel.Development)]
    public void Factory_ForEachFeed_BuildsAnAnonymousPaginatingSource(AppUpdateChannel channel)
    {
        var factory = new VelopackUpdateManagerFactory(Options.Create(new AppUpdateChannelOptions
        {
            GitHubRepositoryUrl = "https://github.com/example/public-repo"
        }));

        foreach (var feed in AppUpdateChannelPolicy.ResolveFeeds(channel, "win"))
        {
            var source = factory.CreateGithubSource(feed);

            AssertEx.Equal("https://github.com/example/public-repo", source.RepoUri.ToString());
            AssertEx.Equal(feed.IncludePrereleases, source.Prerelease);
            AssertEx.Null(ReadAuthorization(source));
        }
    }

    [Test]
    public async Task CheckForUpdate_WhenNotInstalled_ReportsUpToDateWithoutReadingAFeed()
    {
        // A raw-exe / dev run: the short-circuit must happen before Velopack is called, and it must report no
        // recommended version because no feed was read.
        var updateManager = new PortableTestUpdateManager(isInstalled: false);
        var manager = new VelopackUpdateManager(updateManager);

        var result = await manager.CheckForUpdateAsync(CancellationToken.None);

        AssertEx.Equal(VelopackCheckOutcome.UpToDate, result.Outcome);
        AssertEx.Null(result.AvailableVersion);
        AssertEx.Null(result.RecommendedVersion);
        AssertEx.Equal(expected: 0, updateManager.CheckCount);
    }

    [Test]
    public async Task CheckForUpdate_WhenTheFeedHasAStableRelease_ReportsItAsRecommended()
    {
        // The feed really is populated by a PaginatingGithubSource over the fake downloader, so this proves the
        // no-extra-round-trip capture works end to end rather than stubbing LastFeedAssets.
        var downloader = new FakeVelopackFileDownloader(new Dictionary<string, string>
        {
            ["https://api.github.com/repos/example/public-repo/releases?per_page=100&page=1"] =
                ReleasesJson("2.0.0-rc.1", "1.0.0"),
            [FeedUrl("2.0.0-rc.1")] = FeedJson("2.0.0-rc.1"),
            [FeedUrl("1.0.0")] = FeedJson("1.0.0")
        });
        var source = PaginatingGithubSource.WithDownloader("https://github.com/example/public-repo", prerelease: true, "win", downloader);
        await source.GetReleaseFeed(NullVelopackLogger.Instance, appId: "XE-Local-AI-Engine", "win");

        var updateManager = new PortableTestUpdateManager(updateInfo: new UpdateInfo(Asset("2.0.0-rc.1"), false, null, []));
        var manager = new VelopackUpdateManager(updateManager, source);

        var result = await manager.CheckForUpdateAsync(CancellationToken.None);

        AssertEx.Equal(VelopackCheckOutcome.UpdateAvailable, result.Outcome);
        AssertEx.Equal("2.0.0-rc.1", result.AvailableVersion);
        AssertEx.Equal("1.0.0", result.RecommendedVersion);
    }

    private static VelopackAsset Asset(string version)
    {
        return new VelopackAsset
        {
            PackageId = "XE-Local-AI-Engine",
            Version = SemanticVersion.Parse(version),
            Type = VelopackAssetType.Full,
            FileName = $"XE-Local-AI-Engine-{version}-full.nupkg",
            SHA1 = "a",
            SHA256 = "b",
            Size = 1
        };
    }

    private static string FeedUrl(string version)
    {
        return $"https://github.com/example/public-repo/releases/download/v{version}/releases.win.json";
    }

    private static string FeedJson(string version)
    {
        return "{\"Assets\":[{\"PackageId\":\"XE-Local-AI-Engine\",\"Version\":\"" + version
                                                                                   + "\",\"Type\":\"Full\",\"FileName\":\"XE-Local-AI-Engine-" + version
                                                                                   + "-full.nupkg\",\"SHA1\":\"a\",\"SHA256\":\"b\",\"Size\":1}]}";
    }

    private static string ReleasesJson(params string[] versions)
    {
        var entries = versions.Select((version, index) =>
        {
            var url = FeedUrl(version);
            var published = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                            .AddDays(versions.Length - index)
                            .ToString("o", CultureInfo.InvariantCulture);
            var prerelease = version.Contains('-', StringComparison.Ordinal) ? "true" : "false";
            return "{\"name\":\"v" + version + "\",\"prerelease\":" + prerelease
                   + ",\"published_at\":\"" + published + "\",\"assets\":[{\"url\":\"" + url
                   + "\",\"browser_download_url\":\"" + url
                   + "\",\"name\":\"releases.win.json\",\"content_type\":\"application/json\"}]}";
        });
        return "[" + string.Join(',', entries) + "]";
    }

    [Test]
    public async Task CheckForUpdate_WhenInstalledPortableBuild_ChecksFeed()
    {
        var updateManager = new PortableTestUpdateManager();
        var manager = new VelopackUpdateManager(updateManager);

        var result = await manager.CheckForUpdateAsync(CancellationToken.None);

        AssertEx.True(updateManager.IsPortable);
        AssertEx.Equal(1, updateManager.CheckCount);
        AssertEx.Equal(VelopackCheckOutcome.UpToDate, result.Outcome);
    }

    [Test]
    public async Task PrepareUpdate_WhenUpdateExists_DownloadsThenSchedulesWaitForExitWithoutTerminatingHost()
    {
        var asset = new VelopackAsset
        {
            PackageId = "XE-Local-AI-Engine",
            Version = SemanticVersion.Parse("0.2.0"),
            Type = VelopackAssetType.Full,
            FileName = "XE-Local-AI-Engine-0.2.0-full.nupkg",
            SHA1 = "a",
            SHA256 = "b",
            Size = 1
        };
        var updateInfo = new UpdateInfo(asset, false, null, []);
        var updateManager = new PortableTestUpdateManager(updateInfo: updateInfo);
        var scheduled = false;
        var manager = new VelopackUpdateManager(updateManager, (_, scheduledAsset, restartArgs) =>
        {
            AssertEx.True(updateManager.DownloadCompleted);
            AssertEx.True(ReferenceEquals(asset, scheduledAsset));
            AssertEx.True(restartArgs.SequenceEqual(["--mcp-only", "--no-browser", "--port", "41234"], StringComparer.Ordinal));
            scheduled = true;
        });

        var applying = await manager.PrepareUpdateAndRestartAsync(["--mcp-only", "--no-browser", "--port", "41234"], CancellationToken.None);

        AssertEx.True(applying);
        AssertEx.True(scheduled);
    }

    [Test]
    public async Task CheckForUpdate_WhenFeedIsMalformed_ReturnsFailedNotOffline()
    {
        var manager = new VelopackUpdateManager(new PortableTestUpdateManager(new FormatException("malformed feed")));

        var result = await manager.CheckForUpdateAsync(CancellationToken.None);

        AssertEx.Equal(VelopackCheckOutcome.Failed, result.Outcome);
        AssertEx.Equal(AppUpdateFailureReason.MalformedFeed, result.FailureReason);
        AssertEx.Null(result.AvailableVersion);
    }

    [Test]
    [MethodDataSource(nameof(TransportFailures))]
    public async Task CheckForUpdate_WhenTransportOrTimeoutFails_ReturnsOffline(Exception exception,
        AppUpdateFailureReason expectedReason)
    {
        var manager = new VelopackUpdateManager(new PortableTestUpdateManager(exception));

        var result = await manager.CheckForUpdateAsync(CancellationToken.None);

        AssertEx.Equal(VelopackCheckOutcome.Offline, result.Outcome);
        AssertEx.Equal(expectedReason, result.FailureReason);
    }

    [Test]
    [MethodDataSource(nameof(NonTransportFailures))]
    public async Task CheckForUpdate_WhenNonTransportFailureOccurs_ReturnsFailed(Exception exception,
        AppUpdateFailureReason expectedReason)
    {
        var manager = new VelopackUpdateManager(new PortableTestUpdateManager(exception));

        var result = await manager.CheckForUpdateAsync(CancellationToken.None);

        AssertEx.Equal(VelopackCheckOutcome.Failed, result.Outcome);
        AssertEx.Equal(expectedReason, result.FailureReason);
    }

    public static IEnumerable<Func<(Exception, AppUpdateFailureReason)>> TransportFailures()
    {
        yield return () => (new HttpRequestException(HttpRequestError.NameResolutionError), AppUpdateFailureReason.Transport);
        yield return () => (new HttpRequestException(HttpRequestError.ConnectionError), AppUpdateFailureReason.Transport);
        yield return () => (new HttpRequestException(HttpRequestError.ResponseEnded), AppUpdateFailureReason.Transport);
        yield return () => (new TimeoutException("timed out"), AppUpdateFailureReason.Timeout);
        yield return () => (new TaskCanceledException("timed out"), AppUpdateFailureReason.Timeout);
        yield return () => (new HttpRequestException("gateway timeout", null, HttpStatusCode.GatewayTimeout), AppUpdateFailureReason.Timeout);
    }

    public static IEnumerable<Func<(Exception, AppUpdateFailureReason)>> NonTransportFailures()
    {
        yield return () => (new HttpRequestException(HttpRequestError.SecureConnectionError), AppUpdateFailureReason.Tls);
        yield return () => (new AuthenticationException("certificate validation failed"), AppUpdateFailureReason.Tls);
        yield return () => (new HttpRequestException(HttpRequestError.InvalidResponse), AppUpdateFailureReason.MalformedFeed);
        yield return () => (new ChecksumFailedException("/secret/package.nupkg"), AppUpdateFailureReason.Integrity);
        yield return () => (new InvalidDataException("invalid feed"), AppUpdateFailureReason.MalformedFeed);
        yield return () => (new InvalidOperationException("programming error"), AppUpdateFailureReason.Unexpected);
        yield return () => (new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden), AppUpdateFailureReason.Http);
    }

    private static object? ReadAuthorization(GithubSource source)
    {
        return typeof(GithubSource).GetProperty("Authorization", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(source);
    }

    private sealed class PortableTestUpdateManager : UpdateManager
    {
        private readonly Exception? _checkException;
        private readonly bool _isInstalled;
        private readonly UpdateInfo? _updateInfo;

        public PortableTestUpdateManager(Exception? checkException = null, UpdateInfo? updateInfo = null,
            bool isInstalled = true)
            : base(new GithubSource("https://github.com/example/public-repo", null, prerelease: false),
                options: null,
                locator: new TestVelopackLocator("XE-Local-AI-Engine", "0.1.0", Path.GetTempPath()))
        {
            _checkException = checkException;
            _updateInfo = updateInfo;
            _isInstalled = isInstalled;
        }

        public int CheckCount { get; private set; }

        public bool DownloadCompleted { get; private set; }

        public override bool IsInstalled => _isInstalled;

        public override bool IsPortable => true;

        public override SemanticVersion? CurrentVersion => SemanticVersion.Parse("0.1.0");

        public override Task<UpdateInfo?> CheckForUpdatesAsync()
        {
            CheckCount++;
            return _checkException is null
                ? Task.FromResult(_updateInfo)
                : Task.FromException<UpdateInfo?>(_checkException);
        }

        public override Task DownloadUpdatesAsync(UpdateInfo updates,
            Action<int>? progress = null,
            CancellationToken cancelToken = default)
        {
            DownloadCompleted = true;
            return Task.CompletedTask;
        }
    }
}
