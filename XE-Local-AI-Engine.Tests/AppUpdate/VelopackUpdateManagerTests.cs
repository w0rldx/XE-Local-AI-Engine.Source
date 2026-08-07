namespace XE_Local_AI_Engine.Tests.AppUpdate;

using System.Reflection;
using System.Net;
using System.Security.Authentication;
using Microsoft.Extensions.Options;
using Velopack;
using Velopack.Exceptions;
using Velopack.Locators;
using Velopack.Sources;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>Proves the real Velopack adapter uses an anonymous public GitHub source and supports portable installs.</summary>
public sealed class VelopackUpdateManagerTests
{
    [Test]
    [Arguments(AppUpdateReleaseTrack.Stable, false)]
    [Arguments(AppUpdateReleaseTrack.Rc, true)]
    public void Factory_PublicSource_HasNullAuthorizationAndExplicitTrack(AppUpdateReleaseTrack track,
        bool expectedPrereleases)
    {
        var factory = new VelopackUpdateManagerFactory(Options.Create(new AppUpdateChannelOptions
        {
            GitHubRepositoryUrl = "https://github.com/example/public-repo",
            ReleaseTrack = track
        }));

        var source = factory.CreateGithubSource();

        AssertEx.Equal("https://github.com/example/public-repo", source.RepoUri.ToString());
        AssertEx.Equal(expectedPrereleases, source.Prerelease);
        AssertEx.Null(ReadAuthorization(source));
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
            AssertEx.Equal(expected: 1, restartArgs.Length);
            AssertEx.Equal("--desktop", restartArgs[0]);
            scheduled = true;
        });

        var applying = await manager.PrepareUpdateAndRestartAsync(["--desktop"], CancellationToken.None);

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

    private sealed class PortableTestUpdateManager(Exception? checkException = null, UpdateInfo? updateInfo = null)
        : UpdateManager(new GithubSource("https://github.com/example/public-repo", null, prerelease: false),
            options: null,
            locator: new TestVelopackLocator("XE-Local-AI-Engine", "0.1.0", Path.GetTempPath()))
    {
        public int CheckCount { get; private set; }

        public bool DownloadCompleted { get; private set; }

        public override bool IsInstalled => true;

        public override bool IsPortable => true;

        public override SemanticVersion? CurrentVersion => SemanticVersion.Parse("0.1.0");

        public override Task<UpdateInfo?> CheckForUpdatesAsync()
        {
            CheckCount++;
            return checkException is null
                ? Task.FromResult(updateInfo)
                : Task.FromException<UpdateInfo?>(checkException);
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
