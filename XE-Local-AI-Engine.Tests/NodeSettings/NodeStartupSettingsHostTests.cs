namespace XE_Local_AI_Engine.Tests.NodeSettings;

using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Endpoints.Development;
using XE_Local_AI_Engine.Client.Hubs;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using EndpointDefinition = FastEndpoints.EndpointDefinition;

/// <summary>
///     A stored <c>developmentEnabled</c> in <c>node-settings.json</c> decides, at host build, whether the Development
///     endpoints and hub exist, and it beats the <c>Development:Enabled</c> appsettings seed.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class NodeStartupSettingsHostTests
{
    [Test]
    public async Task StoredDevelopmentOff_RemovesTheDevelopmentEndpointsAndHub()
    {
        await using var factory = new TestServerWebAppFactory();
        WriteStoredSettings(factory, """{ "developmentEnabled": false }""");

        var routes = RouteEndpoints(factory);

        AssertEx.NotEmpty(HubRoutes(routes, typeof(LocalChatHub)), "The route table is not trustworthy without an unconditional hub.");
        AssertEx.Empty(HubRoutes(routes, typeof(DevelopmentAttemptHub)));
        AssertEx.Empty(DevelopmentEndpointRoutes(routes));
    }

    [Test]
    public async Task StoredDevelopmentOn_BeatsTheConfigurationSeed()
    {
        await using var factory = new TestServerWebAppFactory
        {
            EnableDevelopmentMode = false
        };
        WriteStoredSettings(factory, """{ "developmentEnabled": true }""");

        var routes = RouteEndpoints(factory);

        AssertEx.NotEmpty(HubRoutes(routes, typeof(DevelopmentAttemptHub)));
        AssertEx.NotEmpty(DevelopmentEndpointRoutes(routes));
    }

    /// <summary>
    ///     Every structural switch off at once, from the stored file, still builds a validated container: a service outside a
    ///     switched-off module must not need one of that module's skipped registrations.
    /// </summary>
    [Test]
    public async Task StoredStructuralSwitchesOff_HostBootsWithScopeAndBuildValidation()
    {
        await using var factory = new TestServerWebAppFactory
        {
            ValidateServiceProvider = true
        };
        WriteStoredSettings(factory, """{ "developmentEnabled": false, "schedulerEnabled": false, "externalAppsEnabled": false }""");

        AssertBootedWithTheStructuralSwitchesOff(factory);
    }

    /// <summary>The same three switches off through the appsettings seed, with no stored file.</summary>
    [Test]
    public async Task ConfiguredStructuralSwitchesOff_HostBootsWithScopeAndBuildValidation()
    {
        await using var factory = new TestServerWebAppFactory
        {
            ValidateServiceProvider = true,
            AdditionalConfiguration = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Development:Enabled"] = "false",
                ["Scheduler:Enabled"] = "false",
                ["ExternalApps:Enabled"] = "false"
            }
        };

        AssertBootedWithTheStructuralSwitchesOff(factory);
    }

    private static void AssertBootedWithTheStructuralSwitchesOff(TestServerWebAppFactory factory)
    {
        var startupSettings = factory.Services.GetRequiredService<NodeStartupSettings>();
        AssertEx.False(startupSettings.DevelopmentEnabled);
        AssertEx.False(startupSettings.SchedulerEnabled);
        AssertEx.False(startupSettings.ExternalAppsEnabled);
        AssertEx.Empty(DevelopmentEndpointRoutes(RouteEndpoints(factory)));
    }

    private static void WriteStoredSettings(TestServerWebAppFactory factory, string json)
    {
        Directory.CreateDirectory(factory.NodeDataDirectoryPath);
        File.WriteAllText(Path.Combine(factory.NodeDataDirectoryPath, "node-settings.json"), json);
    }

    private static List<RouteEndpoint> RouteEndpoints(TestServerWebAppFactory factory) =>
        [.. factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()];

    private static List<RouteEndpoint> HubRoutes(List<RouteEndpoint> routes, Type hubType) =>
        [.. routes.Where(endpoint => endpoint.Metadata.GetMetadata<HubMetadata>()?.HubType == hubType)];

    private static List<RouteEndpoint> DevelopmentEndpointRoutes(List<RouteEndpoint> routes) =>
        [.. routes.Where(static endpoint => endpoint.Metadata.GetMetadata<EndpointDefinition>()?.EndpointType is { } type
                                            && typeof(IDevelopmentEndpoint).IsAssignableFrom(type))];
}
