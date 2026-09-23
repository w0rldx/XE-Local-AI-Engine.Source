namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

/// <summary>One feed a check reads: the Velopack channel to request and whether prereleases are in scope.</summary>
/// <remarks>
///     A record for its value equality: the service compares the feed it primed a manager for against the feed a
///     check asks for, and that comparison must be by value, not by reference.
/// </remarks>
public sealed record AppUpdateFeed
{
    /// <summary>
    ///     The <c>releases.{channel}.json</c> index name's channel part: <c>win</c>, <c>linux</c>, <c>win-dev</c> or
    ///     <c>linux-dev</c>.
    /// </summary>
    public required string VelopackChannel { get; init; }

    /// <summary>Whether GitHub prereleases are considered at all.</summary>
    public required bool IncludePrereleases { get; init; }

    /// <summary>True for the repository's ordinary release stream; false for the Development stream.</summary>
    public required bool IsMainFeed { get; init; }
}
