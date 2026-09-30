namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

using Microsoft.Extensions.Options;
using Velopack;

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
        // switch forward-only. Naming it here fails a guard.
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
