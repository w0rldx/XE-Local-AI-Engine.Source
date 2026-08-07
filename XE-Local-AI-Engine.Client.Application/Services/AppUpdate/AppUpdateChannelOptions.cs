namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

/// <summary>The release selection policy for the public GitHub feed.</summary>
public enum AppUpdateReleaseTrack
{
    /// <summary>Only stable GitHub releases are considered.</summary>
    Stable,

    /// <summary>Stable and prerelease GitHub releases are considered.</summary>
    Rc
}

/// <summary>
///     Immutable public update-source policy. The absence of an authentication setting is deliberate: official builds
///     consume public GitHub releases anonymously, while Velopack independently selects the OS package channel from the
///     installed package metadata.
/// </summary>
public sealed record AppUpdateSourcePolicy(string GitHubRepositoryUrl, bool IncludePrereleases);

/// <summary>
///     Public update-feed configuration baked into the artifact. <see cref="ReleaseTrack" /> controls stable versus RC
///     visibility only; it does not replace or overload Velopack's independent Windows/Linux package channel.
/// </summary>
public sealed class AppUpdateChannelOptions
{
    /// <summary>The configuration section these options bind to.</summary>
    public const string SectionName = "AppUpdate";

    /// <summary>Artifact flavor retained for publish compatibility (<c>main</c> or <c>tester</c>).</summary>
    public string Channel { get; init; } = "main";

    /// <summary>The public GitHub repository URL releases are read from.</summary>
    public string GitHubRepositoryUrl { get; init; } = string.Empty;

    /// <summary>Whether this artifact follows stable releases or may also consume release candidates.</summary>
    public AppUpdateReleaseTrack ReleaseTrack { get; init; } = AppUpdateReleaseTrack.Stable;

    /// <summary>True when the baked public repository URL is usable.</summary>
    public bool IsConfigured => IsGitHubRepositoryUrl(GitHubRepositoryUrl);

    /// <summary>The validated anonymous source policy, or <see langword="null" /> for an unbaked build.</summary>
    public AppUpdateSourcePolicy? SourcePolicy => IsConfigured
        ? new AppUpdateSourcePolicy(GitHubRepositoryUrl.TrimEnd('/'), ReleaseTrack == AppUpdateReleaseTrack.Rc)
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
