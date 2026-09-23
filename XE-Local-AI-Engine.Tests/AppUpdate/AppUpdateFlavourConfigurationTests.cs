namespace XE_Local_AI_Engine.Tests.AppUpdate;

using Microsoft.Extensions.Configuration;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Binds the three baked flavour files the way the host does, so a flavour whose packaging <c>Channel</c> and
///     user-facing <c>DefaultChannel</c> disagree fails here rather than shipping the wrong update visibility.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class AppUpdateFlavourConfigurationTests
{
    [Test]
    [Arguments("main", AppUpdateChannel.Stable)]
    [Arguments("tester", AppUpdateChannel.Preview)]
    [Arguments("dev", AppUpdateChannel.Development)]
    public void EveryFlavourFile_BakesItsOwnChannelAndDefault(string flavour, AppUpdateChannel expectedDefault)
    {
        var options = BindFlavour(flavour);

        AssertEx.Equal(flavour, options.Channel);
        AssertEx.Equal(expectedDefault, options.DefaultChannel);
    }

    [Test]
    [Arguments("main")]
    [Arguments("tester")]
    [Arguments("dev")]
    public void EveryFlavour_IsConfiguredAgainstThePublicRepository(string flavour)
    {
        var options = BindFlavour(flavour);

        AssertEx.True(options.IsConfigured);
        AssertEx.Equal("https://github.com/w0rldx/XE-Local-AI-Engine.Source",
            AssertEx.NotNull(options.SourcePolicy).GitHubRepositoryUrl);
    }

    private static AppUpdateChannelOptions BindFlavour(string flavour)
    {
        var path = LocateFlavourFile($"appsettings.AppUpdate.{flavour}.json");
        var options = new ConfigurationBuilder().AddJsonFile(path).Build()
                                                .GetSection(AppUpdateChannelOptions.SectionName)
                                                .Get<AppUpdateChannelOptions>();
        return AssertEx.NotNull(options, $"'{path}' has no '{AppUpdateChannelOptions.SectionName}' section.");
    }

    private static string LocateFlavourFile(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "XE-Local-AI-Engine.Client", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate flavour configuration {fileName}.");
    }
}
