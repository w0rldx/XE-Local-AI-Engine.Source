namespace XE_Local_AI_Engine.Tests.AppUpdate;

using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>Pins the channel-to-feed plan, including the two invariants the service and the status response rely on.</summary>
[Category(TestCategories.Unit)]
public sealed class AppUpdateChannelPolicyTests
{
    [Test]
    [Arguments("win")]
    [Arguments("linux")]
    public void ResolveFeeds_ForStable_ReadsTheMainFeedWithoutPrereleases(string os)
    {
        var feeds = AppUpdateChannelPolicy.ResolveFeeds(AppUpdateChannel.Stable, os);

        AssertEx.Equal(expected: 1, feeds.Count);
        AssertEx.Equal(os, feeds[0].VelopackChannel);
        AssertEx.False(feeds[0].IncludePrereleases);
        AssertEx.True(feeds[0].IsMainFeed);
    }

    [Test]
    [Arguments("win")]
    [Arguments("linux")]
    public void ResolveFeeds_ForPreview_ReadsTheMainFeedWithPrereleases(string os)
    {
        var feeds = AppUpdateChannelPolicy.ResolveFeeds(AppUpdateChannel.Preview, os);

        AssertEx.Equal(expected: 1, feeds.Count);
        AssertEx.Equal(os, feeds[0].VelopackChannel);
        AssertEx.True(feeds[0].IncludePrereleases);
        AssertEx.True(feeds[0].IsMainFeed);
    }

    [Test]
    [Arguments("win")]
    [Arguments("linux")]
    public void ResolveFeeds_ForDevelopment_ReadsTheMainFeedAndTheDevFeed(string os)
    {
        var feeds = AppUpdateChannelPolicy.ResolveFeeds(AppUpdateChannel.Development, os);

        AssertEx.Equal(expected: 2, feeds.Count);
        AssertEx.Equal(os, feeds[0].VelopackChannel);
        AssertEx.True(feeds[0].IncludePrereleases);
        AssertEx.True(feeds[0].IsMainFeed);
        AssertEx.Equal(os + "-dev", feeds[1].VelopackChannel);
        AssertEx.True(feeds[1].IncludePrereleases);
        AssertEx.False(feeds[1].IsMainFeed);
    }

    [Test]
    public void ResolveFeeds_ForEveryChannel_PutsExactlyOneMainFeedFirst()
    {
        // The recommended (newest stable) version is taken from the main feed's result. This invariant is what lets
        // that selection state the rule instead of guessing a position.
        foreach (var channel in Enum.GetValues<AppUpdateChannel>())
        {
            var feeds = AppUpdateChannelPolicy.ResolveFeeds(channel, "win");

            AssertEx.True(feeds[0].IsMainFeed, $"{channel}'s first feed is not the main feed.");
            AssertEx.Equal(expected: 1, feeds.Count(feed => feed.IsMainFeed), $"{channel} has more than one main feed.");
        }
    }

    [Test]
    [Arguments(AppUpdateChannel.Stable)]
    [Arguments(AppUpdateChannel.Preview)]
    public void ResolveFeeds_ForStableAndPreview_NeverNamesADevelopmentChannel(AppUpdateChannel channel)
    {
        foreach (var os in new[] { "win", "linux" })
        {
            foreach (var feed in AppUpdateChannelPolicy.ResolveFeeds(channel, os))
            {
                AssertEx.False(feed.VelopackChannel.EndsWith(AppUpdateChannelPolicy.DevelopmentChannelSuffix, StringComparison.Ordinal),
                    $"{channel} asked for '{feed.VelopackChannel}'.");
            }
        }
    }
}
