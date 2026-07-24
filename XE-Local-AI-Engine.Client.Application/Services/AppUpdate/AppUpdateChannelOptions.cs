namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

/// <summary>
///     The build-flavor update channel config, baked at publish time into
///     <c>appsettings.AppUpdate.json</c>: the GitHub repository the running build self-updates from and the GitHub App
///     client_id the device flow authenticates with. Both are PUBLIC config — the device-flow client_id needs no client
///     secret and the repo URL is public — so neither is a secret. The values are fixed per artifact (no runtime switch),
///     which is what guarantees a tester build can never point at the main repo and vice-versa. When unset (dev / CI /
///     unbaked) the updater is inert.
/// </summary>
public sealed class AppUpdateChannelOptions
{
    /// <summary>The configuration section these options bind to.</summary>
    public const string SectionName = "AppUpdate";

    /// <summary>The flavor name baked into this artifact — <c>main</c> or <c>tester</c>.</summary>
    public string Channel { get; init; } = "main";

    /// <summary>The GitHub repository URL releases are read from (e.g. <c>https://github.com/owner/repo</c>); empty when unbaked.</summary>
    public string GitHubRepositoryUrl { get; init; } = string.Empty;

    /// <summary>The GitHub App client_id the device flow uses (public, not a secret); empty when unbaked.</summary>
    public string GitHubAppClientId { get; init; } = string.Empty;

    /// <summary>
    ///     True only when a GitHub repository URL and a structurally valid GitHub App client ID were baked. Placeholder
    ///     text must leave the updater inert instead of being treated as live configuration.
    /// </summary>
    public bool IsConfigured => IsGitHubRepositoryUrl(GitHubRepositoryUrl) && IsGitHubAppClientId(GitHubAppClientId);

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

    private static bool IsGitHubAppClientId(string value)
    {
        return value.Length >= 16
               && value.StartsWith("Iv", StringComparison.Ordinal)
               && value.All(static character => char.IsAsciiLetterOrDigit(character) || character == '.');
    }
}
