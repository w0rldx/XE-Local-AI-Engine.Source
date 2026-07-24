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
    ///     <para>
    ///         This is the SINGLE predicate every self-update code path gates on — the update check
    ///         (<c>AppUpdateService</c>), the apply, and the device-flow sign-in (<c>GitHubAuthService</c>). Gating any
    ///         one of them on a weaker check (e.g. a bare non-empty client ID) splits the build: the check goes inert
    ///         while the device flow POSTs an unusable client_id to github.com and fails there as a transport error
    ///         instead of here as a clear configuration error.
    ///     </para>
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

    // GITHUB APP ONLY — the `Iv` prefix is deliberate, not an oversight.
    //
    // GitHub App client IDs carry an `Iv` prefix (legacy `Iv1.<hex>`, current `Iv23li…`); GitHub OAuth App client IDs
    // carry `Ov` (`Ov23li…`) or, older still, a bare 20-hex string. The device flow itself accepts all of them, so an
    // `Ov` ID would sail through the network calls — and then fail to do the one thing this updater needs.
    //
    // The reason: GitHubAuthService.StartAsync intentionally sends NO `scope` parameter, because a GitHub App's token
    // permissions come from its fine-grained configuration (contents:read on the release repo). An OAuth App has no
    // fine-grained permissions — it derives them from `scope` alone — so an OAuth App client ID here would mint a
    // zero-scope token that cannot read the private release repo. Accepting `Ov` would therefore convert a build-time
    // rejection into a runtime 404/403 on every update check. It is rejected on purpose; do not widen this to `Ov`
    // without also giving StartAsync an OAuth `scope` path.
    //
    // Note that GitHub does not publish a spec for this identifier's shape, so treat the prefix as a placeholder /
    // wrong-value guard (it mainly catches the numeric App ID operators paste by mistake), not as a security control.
    // It is deliberately kept identical to the assertion in publish/package-tester-win.ps1 (`^Iv[0-9A-Za-z.]{14,}$`)
    // so a build that packages cannot then go inert at run time.
    private static bool IsGitHubAppClientId(string value)
    {
        return value.Length >= 16
               && value.StartsWith("Iv", StringComparison.Ordinal)
               && value.All(static character => char.IsAsciiLetterOrDigit(character) || character == '.');
    }
}
