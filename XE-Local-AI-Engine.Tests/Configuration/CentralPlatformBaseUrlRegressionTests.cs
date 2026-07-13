namespace XE_Local_AI_Engine.Tests.Configuration;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class CentralPlatformBaseUrlRegressionTests
{
    private const int CentralPlatformApiPort = 7003;

    private readonly CentralPlatformOptionsValidator _validator = new();

    [Test]
    public void DevelopmentAppSettings_BaseUrl_TargetsCentralPlatformApiPortOverHttps()
    {
        var options = BindCentralPlatform("appsettings.Development.json");

        var baseUri = new Uri(options.BaseUrl, UriKind.Absolute);
        AssertEx.Equal(CentralPlatformApiPort, baseUri.Port);
        AssertEx.Equal(Uri.UriSchemeHttps, baseUri.Scheme);
    }

    [Test]
    public void BaseAppSettings_BaseUrl_TargetsCentralPlatformApiPort()
    {
        var options = BindCentralPlatform("appsettings.json");

        var baseUri = new Uri(options.BaseUrl, UriKind.Absolute);
        AssertEx.Equal(CentralPlatformApiPort, baseUri.Port);
    }

    [Test]
    public void Validate_WhenBaseUrlNotAbsolute_ReturnsFailure()
    {
        var options = CreateValidOptions();
        options.BaseUrl = "central-platform/api";

        var result = _validator.Validate(name: null, options);

        AssertFailureContains(result, "BaseUrl");
    }

    [Test]
    public void Validate_WhenHubPathMissingLeadingSlash_ReturnsFailure()
    {
        var options = CreateValidOptions();
        options.HubPath = "hub/worker";

        var result = _validator.Validate(name: null, options);

        AssertFailureContains(result, "HubPath");
    }

    [Test]
    public void Validate_WhenHubPathContainsScheme_ReturnsFailure()
    {
        var options = CreateValidOptions();
        options.HubPath = "https://evil.example.com/hub/worker";

        var result = _validator.Validate(name: null, options);

        AssertFailureContains(result, "HubPath");
    }

    [Test]
    public void Validate_WhenHubPathStartsWithDoubleSlash_ReturnsFailure()
    {
        var options = CreateValidOptions();
        options.HubPath = "//evil.example.com/hub/worker";

        var result = _validator.Validate(name: null, options);

        AssertFailureContains(result, "HubPath");
    }

    private static CentralPlatformOptions BindCentralPlatform(string appSettingsFileName)
    {
        var path = GetClientAppSettingsPath(appSettingsFileName);
        AssertEx.True(File.Exists(path), $"Expected Client app settings at '{path}'.");

        var configuration = new ConfigurationBuilder()
                            .AddJsonFile(path, optional: false)
                            .Build();

        var options = new CentralPlatformOptions
        {
            BaseUrl = string.Empty
        };
        configuration.GetSection(CentralPlatformOptions.SectionName).Bind(options);

        AssertEx.NotNullOrEmpty(options.BaseUrl);
        return options;
    }

    private static string GetClientAppSettingsPath(string appSettingsFileName)
    {
        return RepositoryPaths.ClientProject(appSettingsFileName);
    }

    private static CentralPlatformOptions CreateValidOptions()
    {
        return new CentralPlatformOptions
        {
            BaseUrl = "https://localhost:7003"
        };
    }

    private static void AssertFailureContains(ValidateOptionsResult result, string expectedText)
    {
        AssertEx.False(result.Succeeded);
        AssertEx.NotEmpty(result.Failures);
        AssertEx.Contains(result.Failures, failure => failure.Contains(expectedText, StringComparison.Ordinal));
    }
}
