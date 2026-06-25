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

    /// <summary>True only when both the repo URL and client_id were baked, so the updater can actually run.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(GitHubRepositoryUrl) && !string.IsNullOrWhiteSpace(GitHubAppClientId);
}
