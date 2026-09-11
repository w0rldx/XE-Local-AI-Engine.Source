namespace XE_Local_AI_Engine.Tests.ExternalApps;

using Microsoft.Extensions.Configuration;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The two halves of the shipped backend default, pinned separately because they must disagree: the shipped
///     <c>appsettings.json</c> turns the feature ON, while the code default leaves it OFF so a node whose
///     configuration is missing or unreadable fails closed rather than starting a container surface nobody asked
///     for. A single assertion over either half alone would pass while the other silently moved.
///     <para>
///         The third assertion pins the distribution decision: no catalog refresh URL ships, so a node serves the
///         embedded seed until an operator configures one. A URL that appeared here would make every install of this
///         product fetch from it.
///     </para>
/// </summary>
public sealed class ExternalAppsShippedConfigurationTests
{
    private const string AppSettingsFileName = "appsettings.json";

    [Test]
    public void ShippedAppSettings_TurnTheFeatureOn()
    {
        var options = BindExternalApps(BuildShippedConfiguration());

        AssertEx.True(options.Enabled,
            $"'{ExternalAppsOptions.SectionName}:{nameof(ExternalAppsOptions.Enabled)}' must be true in the shipped {AppSettingsFileName}.");
    }

    [Test]
    public void AnEmptyConfiguration_LeavesTheFeatureOff()
    {
        var options = BindExternalApps(new ConfigurationBuilder().Build());

        AssertEx.False(options.Enabled,
            "A node with no External Apps configuration must fail closed, so the code default has to stay false.");
    }

    [Test]
    public void ShippedAppSettings_ConfigureNoCatalogRefreshUrl()
    {
        var configuration = BuildShippedConfiguration();

        var refreshUrl = configuration.GetSection(ExternalAppCatalogOptions.SectionName)[nameof(ExternalAppCatalogOptions.RefreshUrl)];

        AssertEx.True(string.IsNullOrWhiteSpace(refreshUrl),
            $"No catalog refresh URL may ship; the bundled seed is the default source. Found '{refreshUrl}'.");
    }

    private static ExternalAppsOptions BindExternalApps(IConfiguration configuration)
    {
        return configuration.GetSection(ExternalAppsOptions.SectionName).Get<ExternalAppsOptions>() ?? new ExternalAppsOptions();
    }

    private static IConfigurationRoot BuildShippedConfiguration()
    {
        var path = RepositoryPaths.ClientProject(AppSettingsFileName);
        AssertEx.True(File.Exists(path), $"Expected Client app settings at '{path}'.");

        return new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();
    }
}
