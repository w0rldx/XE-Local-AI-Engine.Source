namespace XE_Local_AI_Engine.Tests.Auth;

using XE_Local_AI_Engine.Tests.Testing;

public sealed class ClientHostCleanupTests
{
    [Test]
    public async Task LegacyUiArtifacts_AreRemovedFromClientHost()
    {
        var clientRoot = GetClientPath();
        var componentsRoot = Path.Combine(clientRoot, "Components");
        var projectFile = await File.ReadAllTextAsync(Path.Combine(clientRoot, "XE-Local-AI-Engine.Client.csproj"));
        var configureServices = await File.ReadAllTextAsync(Path.Combine(clientRoot, "ConfigureServices.cs"));
        var razorFiles = Directory.Exists(componentsRoot)
            ? Directory.EnumerateFiles(componentsRoot, "*.razor", SearchOption.AllDirectories)
            : [];

        AssertEx.False(razorFiles.Any());
        AssertEx.False(File.Exists(Path.Combine(clientRoot, "_Imports.razor")));
        AssertEx.False(projectFile.Contains(string.Concat("Mud", "Blazor"), StringComparison.Ordinal));
        AssertEx.False(configureServices.Contains("AddRazorComponents", StringComparison.Ordinal));
        AssertEx.False(configureServices.Contains("AddMudServices", StringComparison.Ordinal));
    }

    [Test]
    public async Task ProductionLoggingConfiguration_SuppressesSignalRAccessTokenRequestLogs()
    {
        var appSettings = await File.ReadAllTextAsync(GetClientPath("appsettings.json"));
        var developmentAppSettings = await File.ReadAllTextAsync(GetClientPath("appsettings.Development.json"));
        var startupLogger = await File.ReadAllTextAsync(GetClientPath("Common", "Extensions", "LoggerExtensions.cs"));

        AssertLoggingOverrides(appSettings);
        AssertLoggingOverrides(developmentAppSettings);
        AssertEx.True(startupLogger.Contains("MinimumLevel.Override(\"Microsoft.AspNetCore.Hosting\", LogEventLevel.Warning)", StringComparison.Ordinal));
        AssertEx.True(startupLogger.Contains("MinimumLevel.Override(\"Microsoft.AspNetCore.SignalR\", LogEventLevel.Warning)", StringComparison.Ordinal));
    }

    private static void AssertLoggingOverrides(string appSettings)
    {
        AssertEx.True(appSettings.Contains("\"Microsoft.AspNetCore.Hosting\": \"Warning\"", StringComparison.Ordinal));
        AssertEx.True(appSettings.Contains("\"Microsoft.AspNetCore.Http.Connections\": \"Warning\"", StringComparison.Ordinal));
        AssertEx.True(appSettings.Contains("\"Microsoft.AspNetCore.SignalR\": \"Warning\"", StringComparison.Ordinal));
    }

    private static string GetClientPath(params string[] segments)
    {
        return RepositoryPaths.ClientProject(segments);
    }
}
