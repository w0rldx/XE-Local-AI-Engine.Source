namespace XE_Local_AI_Engine.Tests.Auth;

using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class LocalOperatorAuthorizationTests
{
    [Test]
    public async Task CreatePrincipal_ReturnsAuthenticatedOperatorRole()
    {
        await Task.CompletedTask;

        var principal = LocalOperatorAuthorization.CreatePrincipal();

        AssertEx.True(principal.Identity?.IsAuthenticated ?? false);
        AssertEx.Equal(LocalOperatorAuthorization.UserName, principal.Identity?.Name);
        AssertEx.True(principal.IsInRole(LocalOperatorAuthorization.OperatorRole));
    }

    [Test]
    public async Task BlazorAndMudBlazorArtifacts_AreRemovedFromClientHost()
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
        AssertEx.False(projectFile.Contains("MudBlazor", StringComparison.Ordinal));
        AssertEx.False(configureServices.Contains("AddRazorComponents", StringComparison.Ordinal));
        AssertEx.False(configureServices.Contains("AddMudServices", StringComparison.Ordinal));
    }

    private static string GetClientPath(params string[] segments)
    {
        var root = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(root) && !Directory.Exists(Path.Combine(root, "Apps")))
        {
            root = Directory.GetParent(root)?.FullName ?? string.Empty;
        }

        if (string.IsNullOrEmpty(root))
        {
            throw new DirectoryNotFoundException("Could not locate repository root from test output directory.");
        }

        return Path.Combine([root, "Apps", "XE-Local-AI-Engine", "XE-Local-AI-Engine.Client", .. segments]);
    }
}
