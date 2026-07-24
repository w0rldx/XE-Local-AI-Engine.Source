namespace XE_Local_AI_Engine.Tests.AppUpdate;

using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class AppUpdateChannelOptionsTests
{
    [Test]
    public void IsConfigured_WhenRepositoryAndGitHubAppClientIdAreValid_ReturnsTrue()
    {
        var options = new AppUpdateChannelOptions
        {
            Channel = "tester",
            GitHubRepositoryUrl = "https://github.com/example/tester-repo",
            GitHubAppClientId = "Iv1.testclientid0000"
        };

        AssertEx.True(options.IsConfigured);
    }

    [Test]
    [Arguments("https://github.com/REPLACE_OWNER/REPLACE_REPO", "Iv1.testclientid0000")]
    [Arguments("https://github.com/example/tester-repo", "")]
    [Arguments("https://example.com/example/tester-repo", "Iv1.testclientid0000")]
    [Arguments("https://github.com/example/tester-repo", "123456")]
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
}
