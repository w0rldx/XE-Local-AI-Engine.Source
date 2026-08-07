namespace XE_Local_AI_Engine.Tests.AppUpdate;

using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>Locks the public GitHub update-source policy independently from Velopack's OS package channel.</summary>
public sealed class AppUpdateChannelOptionsTests
{
    private const string ValidRepositoryUrl = "https://github.com/example/public-repo";

    [Test]
    [Arguments(ValidRepositoryUrl)]
    [Arguments("https://github.com/example/public-repo/")]
    [Arguments("https://GitHub.com/example/public-repo")]
    [Arguments("https://github.com/w0rldx/XE-Local-AI-Engine.Source")]
    public void IsConfigured_WhenPublicRepositoryUrlIsValid_DoesNotRequireAuthentication(string repositoryUrl)
    {
        var options = new AppUpdateChannelOptions { GitHubRepositoryUrl = repositoryUrl };

        AssertEx.True(options.IsConfigured);
        AssertEx.NotNull(options.SourcePolicy);
    }

    [Test]
    [Arguments("https://github.com/REPLACE_OWNER/REPLACE_REPO")]
    [Arguments("https://github.com/CHANGE_ME/CHANGE_ME")]
    [Arguments("https://github.com/TODO_OWNER/TODO_REPO")]
    [Arguments("https://example.com/example/public-repo")]
    [Arguments("http://github.com/example/public-repo")]
    [Arguments("https://github.com/example")]
    [Arguments("https://github.com/example/public-repo/releases")]
    [Arguments("https://github.com/example/public-repo?tab=readme")]
    [Arguments("github.com/example/public-repo")]
    [Arguments("")]
    public void IsConfigured_WhenRepositoryUrlIsInvalid_ReturnsFalse(string repositoryUrl)
    {
        var options = new AppUpdateChannelOptions { GitHubRepositoryUrl = repositoryUrl };

        AssertEx.False(options.IsConfigured);
        AssertEx.Null(options.SourcePolicy);
    }

    [Test]
    public void SourcePolicy_StableTrack_ExcludesPrereleases()
    {
        var options = new AppUpdateChannelOptions
        {
            GitHubRepositoryUrl = ValidRepositoryUrl,
            ReleaseTrack = AppUpdateReleaseTrack.Stable
        };

        AssertEx.False(AssertEx.NotNull(options.SourcePolicy).IncludePrereleases);
    }

    [Test]
    public void SourcePolicy_RcTrack_IncludesPrereleases()
    {
        var options = new AppUpdateChannelOptions
        {
            GitHubRepositoryUrl = ValidRepositoryUrl,
            ReleaseTrack = AppUpdateReleaseTrack.Rc
        };

        AssertEx.True(AssertEx.NotNull(options.SourcePolicy).IncludePrereleases);
    }
}
