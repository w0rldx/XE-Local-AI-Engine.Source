namespace XE_Local_AI_Engine.Tests.Sandbox;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Fake;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class SandboxProviderSelectionTests
{
    [Test]
    public void Resolve_WhenProviderIsFake_ReturnsFakeProvider()
    {
        using var services = BuildServices("fake");

        var provider = SandboxProviderSelector.Resolve(services);

        AssertEx.Equal(FakeSandboxRuntimeProvider.Name, provider.ProviderName);
        AssertEx.True(provider is FakeSandboxRuntimeProvider);
    }

    [Test]
    public void Resolve_WhenProviderUnset_DefaultsToFakeProvider()
    {
        using var services = BuildServices(provider: null);

        var provider = SandboxProviderSelector.Resolve(services);

        AssertEx.Equal(FakeSandboxRuntimeProvider.Name, provider.ProviderName);
    }

    [Test]
    public async Task Resolve_WhenProviderIsLocalContainer_ThrowsNotAvailable()
    {
        using var services = BuildServices("local-container");

        var exception = await AssertEx.ThrowsAsync<InvalidOperationException>(() =>
        {
            SandboxProviderSelector.Resolve(services);
            return Task.CompletedTask;
        });

        AssertEx.Contains(exception.Message, "local-container");
        AssertEx.Contains(exception.Message, "Marker J-local");
    }

    [Test]
    public async Task Resolve_WhenProviderIsUnknown_ThrowsWithProviderName()
    {
        using var services = BuildServices("does-not-exist");

        var exception = await AssertEx.ThrowsAsync<InvalidOperationException>(() =>
        {
            SandboxProviderSelector.Resolve(services);
            return Task.CompletedTask;
        });

        AssertEx.Contains(exception.Message, "does-not-exist");
    }

    private static ServiceProvider BuildServices(string? provider)
    {
        var configurationValues = new Dictionary<string, string?>();
        if (provider is not null)
        {
            configurationValues["AgentHome:Sandbox:Provider"] = provider;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddOptions<SandboxOptions>().Bind(configuration.GetSection(SandboxOptions.SectionName));
        return services.BuildServiceProvider();
    }
}
