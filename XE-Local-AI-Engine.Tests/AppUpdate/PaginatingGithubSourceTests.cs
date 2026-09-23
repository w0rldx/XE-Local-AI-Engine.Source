namespace XE_Local_AI_Engine.Tests.AppUpdate;

using System.Globalization;
using System.Reflection;
using Velopack.Logging;
using Velopack.Sources;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the two reasons this source exists: Velopack 1.2.0 reads only the 10 newest releases, and truncates
///     before filtering, so development snapshots could blind a Stable user.
/// </summary>
/// <remarks>Driven through Velopack's own <see cref="IFileDownloader" /> seam with canned JSON — no network.</remarks>
[Category(TestCategories.Unit)]
public sealed class PaginatingGithubSourceTests
{
    private const string RepositoryUrl = "https://github.com/example/public-repo";

    [Test]
    public async Task GetReleaseFeed_WithTwoCannedPages_SeesReleasesFromBothPages()
    {
        // Page 1 is full, so a second page must be requested. Upstream's single per_page=10 fetch cannot see page 2.
        var pageOne = Releases([.. Enumerable.Range(1, 100).Select(index => Release($"0.{index}.0", "win", IsoDate(index)))]);
        var pageTwo = Releases([Release("9.9.0", "win", IsoDate(day: 200))]);
        var downloader = new FakeVelopackFileDownloader(new Dictionary<string, string>
        {
            [PageUrl(1)] = pageOne,
            [PageUrl(2)] = pageTwo
        });
        MapFeeds(downloader, "win", "9.9.0", "0.100.0", "0.99.0");
        var source = NewSource(downloader, prerelease: false, "win");

        var feed = await source.GetReleaseFeed(NullVelopackLogger.Instance, appId: "XE-Local-AI-Engine", "win");

        AssertEx.Equal(expected: 2, downloader.StringRequests.Count);
        AssertEx.Contains(feed.Assets, asset => asset.Version.ToString() == "9.9.0");
    }

    [Test]
    public async Task GetReleaseFeed_StopsAtAShortPage()
    {
        var downloader = new FakeVelopackFileDownloader(new Dictionary<string, string>
        {
            [PageUrl(1)] = Releases([Release("1.0.0", "win", IsoDate(day: 3)), Release("0.9.0", "win", IsoDate(day: 2))])
        });
        MapFeeds(downloader, "win", "1.0.0", "0.9.0");
        var source = NewSource(downloader, prerelease: false, "win");

        await source.GetReleaseFeed(NullVelopackLogger.Instance, appId: "XE-Local-AI-Engine", "win");

        AssertEx.Equal(expected: 1, downloader.StringRequests.Count);
    }

    [Test]
    public async Task GetReleaseFeed_NeverRequestsMoreThanTheHardPageCap()
    {
        var downloader = new FakeVelopackFileDownloader(new Dictionary<string, string>());
        for (var page = 1; page <= PaginatingGithubSource.MaxPages + 2; page++)
        {
            var offset = page * 100;
            downloader.Map(PageUrl(page),
                Releases([.. Enumerable.Range(1, 100).Select(index => Release($"0.{offset + index}.0", "win", IsoDate(offset + index)))]));
        }

        MapFeeds(downloader, "win", "0.600.0", "0.599.0", "0.598.0");
        var source = NewSource(downloader, prerelease: false, "win");

        await source.GetReleaseFeed(NullVelopackLogger.Instance, appId: "XE-Local-AI-Engine", "win");

        AssertEx.Equal(PaginatingGithubSource.MaxPages, downloader.StringRequests.Count);
    }

    [Test]
    public async Task GetReleaseFeed_SkipsReleasesWithoutTheRequestedIndex()
    {
        // The development releases carry only releases.win-dev.json. A win check must not download a single one of
        // their feed documents, and none of their versions may reach the assets.
        var downloader = new FakeVelopackFileDownloader(new Dictionary<string, string>
        {
            [PageUrl(1)] = Releases(
            [
                Release("2.0.0-dev.1", "win-dev", IsoDate(day: 9)),
                Release("1.0.0", "win", IsoDate(day: 8)),
                Release("2.0.0-dev.2", "win-dev", IsoDate(day: 7))
            ])
        });
        MapFeeds(downloader, "win", "1.0.0");
        var source = NewSource(downloader, prerelease: true, "win");

        var feed = await source.GetReleaseFeed(NullVelopackLogger.Instance, appId: "XE-Local-AI-Engine", "win");

        AssertEx.Equal(expected: 1, downloader.ByteRequests.Count);
        AssertEx.Equal(expected: 1, feed.Assets.Length);
        AssertEx.Equal("1.0.0", feed.Assets[0].Version.ToString());
    }

    [Test]
    public async Task GetReleaseFeed_DownloadsAtMostThreeFeedDocuments()
    {
        var downloader = new FakeVelopackFileDownloader(new Dictionary<string, string>
        {
            [PageUrl(1)] = Releases([.. Enumerable.Range(1, 8).Select(index => Release($"1.{index}.0", "win", IsoDate(index)))])
        });
        MapFeeds(downloader, "win", "1.8.0", "1.7.0", "1.6.0");
        var source = NewSource(downloader, prerelease: false, "win");

        var feed = await source.GetReleaseFeed(NullVelopackLogger.Instance, appId: "XE-Local-AI-Engine", "win");

        AssertEx.Equal(PaginatingGithubSource.MaxReleasesPerIndex, downloader.ByteRequests.Count);
        AssertEx.Equal("1.8.0,1.7.0,1.6.0", string.Join(',', feed.Assets.Select(asset => asset.Version.ToString())));
    }

    [Test]
    public async Task GetReleaseFeed_WhenTheNewestReleasesAreAllPrereleases_StillKeepsOneStable()
    {
        // Preview and Development read the main feed with prereleases on. Trimming to the newest three would leave
        // a feed of nothing but release candidates, and recommendedVersion null for the two channels it serves.
        var downloader = new FakeVelopackFileDownloader(new Dictionary<string, string>
        {
            [PageUrl(1)] = Releases(
            [
                Release("1.0.0-rc.3", "win", IsoDate(day: 9), prerelease: true),
                Release("1.0.0-rc.2", "win", IsoDate(day: 8), prerelease: true),
                Release("1.0.0-rc.1", "win", IsoDate(day: 7), prerelease: true),
                Release("0.9.0", "win", IsoDate(day: 6)),
                Release("0.8.0", "win", IsoDate(day: 5))
            ])
        });
        MapFeeds(downloader, "win", "1.0.0-rc.3", "1.0.0-rc.2", "1.0.0-rc.1", "0.9.0");
        var source = NewSource(downloader, prerelease: true, "win");

        var feed = await source.GetReleaseFeed(NullVelopackLogger.Instance, appId: "XE-Local-AI-Engine", "win");

        AssertEx.Equal(PaginatingGithubSource.MaxReleasesPerIndex + 1, downloader.ByteRequests.Count);
        AssertEx.Equal("1.0.0-rc.3,1.0.0-rc.2,1.0.0-rc.1,0.9.0",
            string.Join(',', feed.Assets.Select(asset => asset.Version.ToString())));
        AssertEx.Equal("0.9.0", AppUpdateVersions.NewestStable(feed.Assets));
    }

    [Test]
    public async Task GetReleaseFeed_SkipsAReleasePayloadWithNoAssetsKey()
    {
        // Velopack's own GithubSource null-checks release.Assets, so a page missing the key on one entry is in
        // contract. Dereferencing it would fail the whole check instead of skipping one malformed release.
        var downloader = new FakeVelopackFileDownloader(new Dictionary<string, string>
        {
            [PageUrl(1)] = Releases([ReleaseWithoutAssets("2.0.0", IsoDate(day: 9)), Release("1.0.0", "win", IsoDate(day: 8))])
        });
        MapFeeds(downloader, "win", "1.0.0");
        var source = NewSource(downloader, prerelease: false, "win");

        var feed = await source.GetReleaseFeed(NullVelopackLogger.Instance, appId: "XE-Local-AI-Engine", "win");

        AssertEx.Equal(expected: 1, feed.Assets.Length);
        AssertEx.Equal("1.0.0", feed.Assets[0].Version.ToString());
    }

    [Test]
    public async Task GetReleaseFeed_WhenPrereleasesAreExcluded_IgnoresPrereleaseReleases()
    {
        var downloader = new FakeVelopackFileDownloader(new Dictionary<string, string>
        {
            [PageUrl(1)] = Releases(
            [
                Release("2.0.0-rc.1", "win", IsoDate(day: 9), prerelease: true),
                Release("1.0.0", "win", IsoDate(day: 8))
            ])
        });
        MapFeeds(downloader, "win", "1.0.0");
        var source = NewSource(downloader, prerelease: false, "win");

        var feed = await source.GetReleaseFeed(NullVelopackLogger.Instance, appId: "XE-Local-AI-Engine", "win");

        AssertEx.Equal(expected: 1, feed.Assets.Length);
        AssertEx.Equal("1.0.0", feed.Assets[0].Version.ToString());
    }

    [Test]
    public async Task GetReleaseFeed_RecordsTheAssetsItProduced()
    {
        var downloader = new FakeVelopackFileDownloader(new Dictionary<string, string>
        {
            [PageUrl(1)] = Releases([Release("1.0.0", "win", IsoDate(day: 3))])
        });
        MapFeeds(downloader, "win", "1.0.0");
        var source = NewSource(downloader, prerelease: false, "win");

        AssertEx.Empty(source.LastFeedAssets);

        var feed = await source.GetReleaseFeed(NullVelopackLogger.Instance, appId: "XE-Local-AI-Engine", "win");

        AssertEx.Equal(feed.Assets.Length, source.LastFeedAssets.Count);
        AssertEx.Equal("1.0.0", source.LastFeedAssets[0].Version.ToString());
    }

    [Test]
    public void Constructor_KeepsTheAnonymousAccessShape()
    {
        var source = NewSource(new FakeVelopackFileDownloader(new Dictionary<string, string>()), prerelease: false, "win");

        AssertEx.Null(typeof(GithubSource)
                      .GetProperty("Authorization", BindingFlags.Instance | BindingFlags.NonPublic)
                      ?.GetValue(source));
    }

    private static PaginatingGithubSource NewSource(IFileDownloader downloader, bool prerelease, string channel)
    {
        return PaginatingGithubSource.WithDownloader(RepositoryUrl, prerelease, channel, downloader);
    }

    private static string PageUrl(int page)
    {
        return $"https://api.github.com/repos/example/public-repo/releases?per_page={PaginatingGithubSource.PageSize}&page={page}";
    }

    /// <summary>Maps the feed document each named version's release asset URL resolves to.</summary>
    private static void MapFeeds(FakeVelopackFileDownloader downloader, string channel, params string[] versions)
    {
        foreach (var version in versions)
        {
            downloader.Map(AssetUrl(version, channel),
                $$"""
                  {"Assets":[{"PackageId":"XE-Local-AI-Engine","Version":"{{version}}","Type":"Full","FileName":"XE-Local-AI-Engine-{{version}}-full.nupkg","SHA1":"a","SHA256":"b","Size":1}]}
                  """);
        }
    }

    private static string AssetUrl(string version, string channel)
    {
        return $"https://github.com/example/public-repo/releases/download/v{version}/releases.{channel}.json";
    }

    private static string IsoDate(int day)
    {
        return new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(day).ToString("o", CultureInfo.InvariantCulture);
    }

    private static string Release(string version, string channel, string publishedAt, bool prerelease = false)
    {
        var assetUrl = AssetUrl(version, channel);
        return $$"""
                 {"name":"v{{version}}","prerelease":{{(prerelease ? "true" : "false")}},"published_at":"{{publishedAt}}","assets":[{"url":"{{assetUrl}}","browser_download_url":"{{assetUrl}}","name":"releases.{{channel}}.json","content_type":"application/json"}]}
                 """;
    }

    /// <summary>A releases-API entry with no <c>assets</c> key at all, which Velopack's own shape allows.</summary>
    private static string ReleaseWithoutAssets(string version, string publishedAt)
    {
        return $$"""
                 {"name":"v{{version}}","prerelease":false,"published_at":"{{publishedAt}}"}
                 """;
    }

    private static string Releases(IReadOnlyList<string> releases)
    {
        return $"[{string.Join(',', releases)}]";
    }
}
