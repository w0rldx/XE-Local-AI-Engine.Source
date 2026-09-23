namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

/// <summary>The update channel a node follows: which releases it is willing to see at all.</summary>
/// <remarks>
///     This is the user-facing channel, distinct from <see cref="AppUpdateChannelOptions.Channel" /> (the packaging
///     flavour <c>main</c>/<c>tester</c>/<c>dev</c>) and from Velopack's OS package channel (<c>win</c>/<c>linux</c>).
/// </remarks>
public enum AppUpdateChannel
{
    /// <summary>Stable GitHub releases only.</summary>
    Stable,

    /// <summary>Stable releases and release candidates.</summary>
    Preview,

    /// <summary>Stable releases, release candidates and automated development snapshots.</summary>
    Development
}

/// <summary>The one vocabulary for the update channel: the wire value, the stored value, and the config value.</summary>
public static class AppUpdateChannelNames
{
    /// <summary>Wire, stored and configuration literal for <see cref="AppUpdateChannel.Stable" />.</summary>
    public const string Stable = "stable";

    /// <summary>Wire, stored and configuration literal for <see cref="AppUpdateChannel.Preview" />.</summary>
    public const string Preview = "preview";

    /// <summary>Wire, stored and configuration literal for <see cref="AppUpdateChannel.Development" />.</summary>
    public const string Development = "development";

    /// <summary>The three channels in presentation order, least to most adventurous.</summary>
    public static IReadOnlyList<string> All { get; } = [Stable, Preview, Development];

    /// <summary>
    ///     Parses one of the three literals. The comparison is ordinal and does not trim, so <c>"Stable"</c> and
    ///     <c>" stable "</c> are rejected rather than silently accepted.
    /// </summary>
    public static bool TryParse(string? value, out AppUpdateChannel channel)
    {
        switch (value)
        {
            case Stable:
                channel = AppUpdateChannel.Stable;
                return true;
            case Preview:
                channel = AppUpdateChannel.Preview;
                return true;
            case Development:
                channel = AppUpdateChannel.Development;
                return true;
            default:
                channel = AppUpdateChannel.Stable;
                return false;
        }
    }

    /// <summary>The wire literal for a channel. Never throws: a status response must always render.</summary>
    public static string ToWire(AppUpdateChannel channel)
    {
        return channel switch
        {
            AppUpdateChannel.Preview => Preview,
            AppUpdateChannel.Development => Development,
            _ => Stable
        };
    }
}
