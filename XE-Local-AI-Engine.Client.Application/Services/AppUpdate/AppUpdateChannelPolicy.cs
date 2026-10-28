namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

/// <summary>Maps the operator's channel to the feeds a single check must read. No Velopack types, so it is data.</summary>
public static class AppUpdateChannelPolicy
{
    /// <summary>The Development feed's channel suffix: <c>win</c> becomes <c>win-dev</c>.</summary>
    public const string DevelopmentChannelSuffix = "-dev";

    /// <summary>The feeds to read for <paramref name="channel" /> on the OS named by <paramref name="osChannel" />.</summary>
    /// <remarks>
    ///     Two invariants the callers rely on and the tests pin. The main feed is always FIRST and there is always
    ///     exactly ONE of it, which is what lets the recommended version be taken from it without a positional guess.
    ///     And no channel but <see cref="AppUpdateChannel.Development" /> ever produces a <c>-dev</c> feed, so a
    ///     Stable or Preview node can never request the development stream.
    /// </remarks>
    public static IReadOnlyList<AppUpdateFeed> ResolveFeeds(AppUpdateChannel channel, string osChannel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(osChannel);

        return channel switch
        {
            AppUpdateChannel.Preview => [MainFeed(osChannel, includePrereleases: true)],
            AppUpdateChannel.Development =>
            [
                MainFeed(osChannel, includePrereleases: true),
                new AppUpdateFeed
                {
                    VelopackChannel = osChannel + DevelopmentChannelSuffix,
                    IncludePrereleases = true,
                    IsMainFeed = false
                }
            ],
            _ => [MainFeed(osChannel, includePrereleases: false)]
        };
    }

    private static AppUpdateFeed MainFeed(string osChannel, bool includePrereleases)
    {
        return new AppUpdateFeed
        {
            VelopackChannel = osChannel,
            IncludePrereleases = includePrereleases,
            IsMainFeed = true
        };
    }
}
