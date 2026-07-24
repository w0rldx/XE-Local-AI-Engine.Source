namespace XE_Local_AI_Engine.Tests.AppUpdate;

using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Covers <see cref="AppUpdateChannelOptions.IsConfigured" /> — the single predicate every self-update path gates on
///     (the update check, the apply, and the device-flow sign-in). Anything it accepts must be usable end to end, and
///     anything it rejects must leave the updater inert rather than half-live.
/// </summary>
public sealed class AppUpdateChannelOptionsTests
{
    // The shape publish/package-tester-win.ps1 asserts before it bakes a client ID into an artifact. Kept here verbatim
    // so a drift between the packaging gate and the runtime gate fails a test instead of shipping an inert build.
    private const string PackagingScriptClientIdPattern = "^Iv[0-9A-Za-z.]{14,}$";

    private const string ValidRepositoryUrl = "https://github.com/example/tester-repo";

    // Legacy GitHub App client-ID form (Iv1. + hex) and the current form (Iv23li…). Both must be accepted.
    private const string LegacyClientId = "Iv1.testclientid0000";
    private const string ModernClientId = "Iv23liAbCdEfGhIjKlMn";

    [Test]
    [Arguments(ValidRepositoryUrl, LegacyClientId)]
    [Arguments(ValidRepositoryUrl, ModernClientId)]
    // Exactly 16 characters is the documented floor (Iv + 14), so it must be accepted rather than sit on the wrong
    // side of an off-by-one.
    [Arguments(ValidRepositoryUrl, "Iv23li0000000000")]
    // A trailing slash is a normal way to paste a repo URL and still names exactly owner/repo.
    [Arguments("https://github.com/example/tester-repo/", LegacyClientId)]
    // The host comparison is case-insensitive by design (Uri does not normalize case for us in every path).
    [Arguments("https://GitHub.com/example/tester-repo", LegacyClientId)]
    // Repo names legitimately contain dots and dashes; those must not be mistaken for placeholders.
    [Arguments("https://github.com/w0rldx/XE-Local-AI-Engine.Tester-App", LegacyClientId)]
    public void IsConfigured_WhenRepositoryAndGitHubAppClientIdAreValid_ReturnsTrue(string repositoryUrl, string clientId)
    {
        var options = new AppUpdateChannelOptions
        {
            Channel = "tester",
            GitHubRepositoryUrl = repositoryUrl,
            GitHubAppClientId = clientId
        };

        AssertEx.True(options.IsConfigured);
    }

    [Test]
    // --- repository URL ---
    // The main channel ships these placeholders on purpose; they must never read as live configuration.
    [Arguments("https://github.com/REPLACE_OWNER/REPLACE_REPO", LegacyClientId)]
    [Arguments("https://github.com/CHANGE_ME/CHANGE_ME", LegacyClientId)]
    [Arguments("https://github.com/TODO_OWNER/TODO_REPO", LegacyClientId)]
    // Wrong host — releases would be read from somewhere other than github.com.
    [Arguments("https://example.com/example/tester-repo", LegacyClientId)]
    [Arguments("https://raw.githubusercontent.com/example/tester-repo", LegacyClientId)]
    // Plaintext http would let a downgrade observe the update traffic.
    [Arguments("http://github.com/example/tester-repo", LegacyClientId)]
    // Fewer than two path segments — not a repository.
    [Arguments("https://github.com/example", LegacyClientId)]
    [Arguments("https://github.com/", LegacyClientId)]
    [Arguments("https://github.com", LegacyClientId)]
    // More than two path segments — a page inside the repo, not the repo itself.
    [Arguments("https://github.com/example/tester-repo/releases", LegacyClientId)]
    [Arguments("https://github.com/example/tester-repo/releases/latest", LegacyClientId)]
    // A query or fragment means the value was copied out of a browser address bar, not authored.
    [Arguments("https://github.com/example/tester-repo?tab=readme", LegacyClientId)]
    [Arguments("https://github.com/example/tester-repo#readme", LegacyClientId)]
    // Not an absolute URI at all.
    [Arguments("github.com/example/tester-repo", LegacyClientId)]
    [Arguments("", LegacyClientId)]
    // --- client ID ---
    [Arguments(ValidRepositoryUrl, "")]
    [Arguments(ValidRepositoryUrl, "   ")]
    [Arguments(ValidRepositoryUrl, "REPLACE_MAIN_CLIENT_ID")]
    // The numeric App ID pasted where the client ID belongs — the mistake publish/README.md warns about explicitly.
    [Arguments(ValidRepositoryUrl, "123456")]
    [Arguments(ValidRepositoryUrl, "1234567890123456")]
    // Fifteen characters — one below the floor.
    [Arguments(ValidRepositoryUrl, "Iv23li000000000")]
    // Characters GitHub never issues in a client ID; a hyphen usually means a whole different identifier was pasted.
    [Arguments(ValidRepositoryUrl, "Iv1.test-client-id00")]
    [Arguments(ValidRepositoryUrl, "Iv1.test client id00")]
    // Case matters: the prefix is `Iv`, never `iv` or `IV`.
    [Arguments(ValidRepositoryUrl, "iv23liAbCdEfGhIjKlMn")]
    [Arguments(ValidRepositoryUrl, "IV23liAbCdEfGhIjKlMn")]
    public void IsConfigured_WhenConfigurationIsMissingOrInvalid_ReturnsFalse(string repositoryUrl, string clientId)
    {
        var options = new AppUpdateChannelOptions
        {
            Channel = "tester",
            GitHubRepositoryUrl = repositoryUrl,
            GitHubAppClientId = clientId
        };

        AssertEx.False(options.IsConfigured);
    }

    /// <summary>
    ///     A GitHub OAuth App client ID (`Ov…`) is rejected DELIBERATELY, not by omission. The device flow would accept
    ///     it, but GitHubAuthService.StartAsync sends no OAuth `scope` (a GitHub App draws its permissions from its
    ///     fine-grained configuration instead), so an OAuth App would mint a zero-scope token that cannot read the
    ///     private release repo. Rejecting it at configuration time turns a per-check runtime 403 into a build-time
    ///     failure. Widening this to `Ov` requires giving StartAsync a `scope` path first.
    /// </summary>
    [Test]
    [Arguments("Ov23liAbCdEfGhIjKlMn")]
    [Arguments("Ov1.testclientid0000")]
    public void IsConfigured_WhenClientIdIsAnOAuthAppId_ReturnsFalse(string oauthAppClientId)
    {
        var options = new AppUpdateChannelOptions
        {
            Channel = "tester",
            GitHubRepositoryUrl = ValidRepositoryUrl,
            GitHubAppClientId = oauthAppClientId
        };

        AssertEx.False(options.IsConfigured,
            "an OAuth App client ID must not configure the updater — StartAsync sends no `scope`, so its token would carry no permissions");
    }

    /// <summary>
    ///     The runtime gate and the packaging gate must agree on the client ID. If packaging accepted an ID the runtime
    ///     rejected, the artifact would ship with a silently inert updater; if the runtime accepted one packaging
    ///     rejected, a valid build could not be produced.
    /// </summary>
    [Test]
    [Arguments(LegacyClientId)]
    [Arguments(ModernClientId)]
    [Arguments("Iv23li0000000000")]
    [Arguments("Ov23liAbCdEfGhIjKlMn")]
    [Arguments("Iv23li000000000")]
    [Arguments("Iv1.test-client-id00")]
    [Arguments("1234567890123456")]
    [Arguments("REPLACE_MAIN_CLIENT_ID")]
    [Arguments("")]
    public void IsConfigured_ClientIdVerdict_MatchesThePackagingScriptAssertion(string clientId)
    {
        var packagingScriptAccepts = Regex.IsMatch(clientId,
            PackagingScriptClientIdPattern,
            RegexOptions.None,
            TimeSpan.FromSeconds(1));

        var options = new AppUpdateChannelOptions
        {
            Channel = "tester",
            GitHubRepositoryUrl = ValidRepositoryUrl,
            GitHubAppClientId = clientId
        };

        AssertEx.Equal(packagingScriptAccepts,
            options.IsConfigured,
            $"'{clientId}': the runtime gate and publish/package-tester-win.ps1 must reach the same verdict");
    }
}
