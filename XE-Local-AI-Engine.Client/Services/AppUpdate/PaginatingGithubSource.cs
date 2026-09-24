namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

using System.Text.Json;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

/// <summary>
///     A <see cref="GithubSource" /> that pages the GitHub releases API and trims each check to the newest releases
///     that actually carry the requested feed index.
/// </summary>
/// <remarks>
///     Velopack 1.2.0's <see cref="GithubSource" /> reads only the 10 newest releases (<c>per_page=10&amp;page=1</c>,
///     no pagination) and applies the prerelease filter AFTER that truncation, so 11 development snapshots would push
///     every stable release out of a Stable user's window and silently stop all updates
///     (<c>research/velopack-1.2.0-verification.md</c> §b). Everything else — the anonymous header shape, asset-URL
///     resolution, the package download, the GitHub Enterprise API base — is inherited unchanged.
/// </remarks>
public sealed class PaginatingGithubSource : GithubSource
{
    /// <summary>GitHub's maximum page size for the releases API.</summary>
    internal const int PageSize = 100;

    /// <summary>The hard page cap: 5 x 100 releases is far past any plausible history, and bounds a hostile feed.</summary>
    internal const int MaxPages = 5;

    /// <summary>
    ///     How many releases carrying this feed index a single check may read. Three, not one, because Velopack's delta
    ///     strategy walks back through the feed and a one-release feed would force a full package download every time.
    /// </summary>
    internal const int MaxReleasesPerIndex = 3;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly string _releaseIndexName;
    private IFileDownloader? _downloader;

    public PaginatingGithubSource(string repositoryUrl, bool prerelease, string velopackChannel)
        : base(repositoryUrl, accessToken: null, prerelease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(velopackChannel);
        _releaseIndexName = $"releases.{velopackChannel}.json";
    }

    /// <summary>The downloader every request goes through: the injected test one, else the base class's real one.</summary>
    public override IFileDownloader Downloader => _downloader ?? base.Downloader;

    /// <summary>Test seam: drive a check from canned JSON with no network.</summary>
    /// <remarks>
    ///     A factory rather than a constructor parameter, because the host architecture rule forbids a
    ///     DI-constructible host class from naming a Velopack type in ANY constructor, public or not.
    /// </remarks>
    internal static PaginatingGithubSource WithDownloader(string repositoryUrl, bool prerelease, string velopackChannel,
        IFileDownloader downloader)
    {
        ArgumentNullException.ThrowIfNull(downloader);
        return new PaginatingGithubSource(repositoryUrl, prerelease, velopackChannel)
        {
            _downloader = downloader
        };
    }

    /// <summary>The assets of the most recent feed this source produced; empty before the first check.</summary>
    /// <remarks>
    ///     This is how the recommended (newest stable) version is derived without a third network round-trip:
    ///     <see cref="UpdateManager" /> exposes only <c>UpdateInfo?</c> and discards the feed it fetched.
    /// </remarks>
    internal IReadOnlyList<VelopackAsset> LastFeedAssets { get; private set; } = [];

    public override async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
        Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        var feed = await base.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease);
        LastFeedAssets = feed.Assets;
        return feed;
    }

    /// <summary>Pages the releases API, then keeps only the newest releases that carry this source's feed index.</summary>
    /// <remarks>
    ///     The filter ORDER is the fix: upstream truncates to 10 releases and only then drops prereleases, which is
    ///     what lets development snapshots blind a Stable user. Here the truncation is last and per feed index, so a
    ///     release carrying only <c>releases.win-dev.json</c> never enters a <c>win</c> check's list. Documents per
    ///     check are bounded at <see cref="MaxReleasesPerIndex" /> plus the one stable release kept below the trim.
    /// </remarks>
    protected override async Task<GithubRelease[]> GetReleases(bool includePrereleases)
    {
        var collected = new List<GithubRelease>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var uri = new Uri(GetApiBaseUrl(RepoUri), $"repos{RepoUri.AbsolutePath}/releases?per_page={PageSize}&page={page}");
            var json = await Downloader.DownloadString(uri.ToString(), GetRequestHeaders("application/vnd.github.v3+json"));

            // Velopack's own deserializer (Velopack.Util.CompiledJson) is internal, so the parse is ours. A
            // JsonException propagates on purpose: VelopackUpdateManager.ClassifyFailure maps it to MalformedFeed.
            var releases = JsonSerializer.Deserialize<GithubRelease[]>(json, SerializerOptions);
            if (releases is null || releases.Length == 0)
            {
                break;
            }

            collected.AddRange(releases);
            if (releases.Length < PageSize)
            {
                break;
            }
        }

        // A release with no publication date sorts FIRST under OrderByDescending on a nullable, so a draft-shaped
        // release would outrank every real one. Dropping it here is load-bearing, not redundant.
        var carrying = collected
                       .Where(release => includePrereleases || !release.Prerelease)
                       .Where(release => release.PublishedAt is not null)
                       .Where(release => release.Assets?.Any(asset =>
                           string.Equals(asset.Name, _releaseIndexName, StringComparison.OrdinalIgnoreCase)) == true)
                       .OrderByDescending(release => release.PublishedAt)
                       .ToArray();
        var kept = new List<GithubRelease>(carrying.Take(MaxReleasesPerIndex));

        // On Preview and Development the newest releases can all be prereleases, which would leave the feed with no
        // stable version at all and report no recommended version. One extra document restores it.
        if (!kept.Exists(static release => !release.Prerelease)
            && Array.Find(carrying, static release => !release.Prerelease) is { } newestStable)
        {
            kept.Add(newestStable);
        }

        return [.. kept];
    }
}
