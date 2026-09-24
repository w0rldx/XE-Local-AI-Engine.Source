namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

/// <summary>Immutable public update-source policy.</summary>
/// <remarks>
///     The absence of an authentication setting is deliberate: official builds consume public GitHub releases anonymously.
///     Prerelease visibility is not part of this policy — it is derived from the selected channel per feed
///     (<c>AppUpdateChannelPolicy.ResolveFeeds</c>), so a baked setting cannot silently disagree with the operator's choice.
/// </remarks>
public sealed class AppUpdateSourcePolicy
{
    public required string GitHubRepositoryUrl { get; init; }
}

/// <summary>
///     Public update-feed configuration baked into the artifact. <see cref="DefaultChannel" /> is the channel a node
///     follows until an operator chooses one; it does not replace or overload Velopack's independent Windows/Linux
///     package channel.
/// </summary>
public sealed class AppUpdateChannelOptions
{
    /// <summary>The configuration section these options bind to.</summary>
    public const string SectionName = "AppUpdate";

    /// <summary>Artifact flavor retained for publish compatibility (<c>main</c>, <c>tester</c> or <c>dev</c>).</summary>
    public string Channel { get; init; } = "main";

    /// <summary>The public GitHub repository URL releases are read from.</summary>
    public string GitHubRepositoryUrl { get; init; } = string.Empty;

    /// <summary>
    ///     The channel a node follows when its operator has never chosen one, i.e. the update visibility this artifact
    ///     flavour has always had. The runtime choice lives in <c>StoredNodeSettings.UpdateChannel</c> and wins over this.
    /// </summary>
    public AppUpdateChannel DefaultChannel { get; init; } = AppUpdateChannel.Stable;

    /// <summary>True when the baked public repository URL is usable.</summary>
    public bool IsConfigured => IsGitHubRepositoryUrl(GitHubRepositoryUrl);

    /// <summary>The validated anonymous source policy, or <see langword="null" /> for an unbaked build.</summary>
    public AppUpdateSourcePolicy? SourcePolicy =>
        IsConfigured
            ? new AppUpdateSourcePolicy
            {
                GitHubRepositoryUrl = GitHubRepositoryUrl.TrimEnd('/')
            }
            : null;

    private static bool IsGitHubRepositoryUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        var repositorySegments = uri.Segments
                                    .Where(static segment => segment != "/")
                                    .Select(static segment => segment.TrimEnd('/'))
                                    .ToArray();
        return repositorySegments.Length == 2
               && repositorySegments.All(static segment =>
                   !string.IsNullOrWhiteSpace(segment)
                   && !segment.StartsWith("REPLACE_", StringComparison.OrdinalIgnoreCase)
                   && !segment.StartsWith("CHANGE_ME", StringComparison.OrdinalIgnoreCase)
                   && !segment.StartsWith("TODO", StringComparison.OrdinalIgnoreCase));
    }
}
