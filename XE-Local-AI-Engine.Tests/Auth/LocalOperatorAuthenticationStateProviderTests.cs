namespace XE_Local_AI_Engine.Tests.Auth;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class LocalOperatorAuthenticationStateProviderTests
{
    [Test]
    public async Task GetAuthenticationStateAsync_ReturnsAuthenticatedOperatorRole()
    {
        var provider = new LocalOperatorAuthenticationStateProvider();

        var state = await provider.GetAuthenticationStateAsync();

        AssertEx.True(state.User.Identity?.IsAuthenticated ?? false);
        AssertEx.Equal(LocalOperatorAuthorization.UserName, state.User.Identity?.Name);
        AssertEx.True(state.User.IsInRole(LocalOperatorAuthorization.OperatorRole));
    }

    [Test]
    public async Task AuthenticationStateProvider_IsRegisteredInApplicationHost()
    {
        await using var factory = new TestingWebAppFactory();
        using var scope = factory.Services.CreateScope();

        var provider = scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>();

        AssertEx.Equal(typeof(LocalOperatorAuthenticationStateProvider), provider.GetType());
    }

    [Test]
    public async Task ManagerAuthorizationArtifacts_UseOperatorRoleAndAuthorizeRouteView()
    {
        var root = GetClientPath("Components");
        var managerPage = await File.ReadAllTextAsync(Path.Combine(root, "Pages", "Manager", "ManagerOverview.razor"));
        var routes = await File.ReadAllTextAsync(Path.Combine(root, "Routes.razor"));
        var layout = await File.ReadAllTextAsync(Path.Combine(root, "Layout", "MainLayout.razor"));

        AssertEx.Contains(managerPage, "@attribute [Authorize(Roles = LocalOperatorAuthorization.OperatorRole)]");
        AssertEx.Contains(routes, "<AuthorizeRouteView");
        AssertEx.Contains(layout, "<AuthorizeView Roles=\"@LocalOperatorAuthorization.OperatorRole\"");
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
