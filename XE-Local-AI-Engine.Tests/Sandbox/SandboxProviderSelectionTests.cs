namespace XE_Local_AI_Engine.Tests.Sandbox;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Fake;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Behavioural pin on per-feature provider selection. Two roles, two configuration keys, and a
///     deliberate asymmetry: the agent role cannot reach a container provider at all, while the Development role can —
///     and falls back to the agent role's choice when it is not configured, which is what makes the seam a runtime
///     no-op on a node that has never set the new key.
/// </summary>
public sealed class SandboxProviderSelectionTests
{
    [Test]
    public async Task ResolveAgent_WhenProviderIsFake_ReturnsFakeProvider()
    {
        await using var services = BuildServices("fake");

        var provider = SandboxProviderSelector.ResolveAgent(services);

        AssertEx.Equal(FakeSandboxRuntimeProvider.Name, provider.ProviderName);
        AssertEx.True(provider is FakeSandboxRuntimeProvider);
    }

    [Test]
    public async Task ResolveAgent_WhenProviderUnset_DefaultsToFakeProvider()
    {
        await using var services = BuildServices(agentProvider: null);

        var provider = SandboxProviderSelector.ResolveAgent(services);

        AssertEx.Equal(FakeSandboxRuntimeProvider.Name, provider.ProviderName);
    }

    [Test]
    public async Task ResolveAgent_WhenProviderIsProcess_ReturnsProcessProvider()
    {
        await using var services = BuildServices("process");

        var provider = SandboxProviderSelector.ResolveAgent(services);

        AssertEx.Equal(ProcessSandboxRuntimeProvider.Name, provider.ProviderName);
        AssertEx.True(provider is ProcessSandboxRuntimeProvider);
    }

    [Test]
    public async Task ResolveAgent_WhenProviderIsUnknown_ThrowsWithProviderName()
    {
        await using var services = BuildServices("does-not-exist");

        var exception = await AssertEx.ThrowsAsync<InvalidOperationException>(() =>
        {
            _ = SandboxProviderSelector.ResolveAgent(services);
            return Task.CompletedTask;
        });

        AssertEx.Contains(exception.Message, "does-not-exist");
    }

    /// <summary>
    ///     The no-op guarantee. A node that configures only <c>AgentHome:Sandbox:Provider</c> — i.e. every node that
    ///     existed before this seam — keeps executing Development Mode on that provider, and on the very same instance.
    /// </summary>
    [Test]
    public async Task ResolveDevelopment_WhenUnset_FallsBackToTheAgentProviderInstance()
    {
        await using var services = BuildServices("process");

        var agent = SandboxProviderSelector.ResolveAgent(services);
        var development = SandboxProviderSelector.ResolveDevelopment(services);

        AssertEx.Equal(ProcessSandboxRuntimeProvider.Name, development.ProviderName);
        AssertEx.True(ReferenceEquals(agent, development),
            "Both roles selected 'process', so they must share ONE instance: the process provider allocates its jail root "
            + "once per instance and Coder attaches to AgentHome's live sandbox by attach key.");
    }

    [Test]
    public async Task ResolveDevelopment_WhenSetToTheSameProviderAsTheAgentRole_SharesOneInstance()
    {
        await using var services = BuildServices("process", developmentProvider: "process");

        AssertEx.True(ReferenceEquals(SandboxProviderSelector.ResolveAgent(services), SandboxProviderSelector.ResolveDevelopment(services)));
    }

    [Test]
    public async Task ResolveDevelopment_WhenSetToADifferentProvider_OverridesTheAgentRoleWithoutChangingIt()
    {
        await using var services = BuildServices("process", developmentProvider: "fake");

        AssertEx.Equal(ProcessSandboxRuntimeProvider.Name, SandboxProviderSelector.ResolveAgent(services).ProviderName);
        AssertEx.Equal(FakeSandboxRuntimeProvider.Name, SandboxProviderSelector.ResolveDevelopment(services).ProviderName);
    }

    /// <summary>
    ///     Only the Development role may name the container provider. There is no matching agent-role test because
    ///     there is nothing to test: <c>DockerSandboxRuntimeProvider</c> does not implement
    ///     <c>IAgentSandboxRuntimeProvider</c>, so returning it from <c>ResolveAgent</c> would not compile.
    /// </summary>
    [Test]
    public async Task ResolveDevelopment_WhenSetToDocker_ReturnsTheContainerProvider()
    {
        await using var services = BuildServices("process", developmentProvider: "docker");

        var development = SandboxProviderSelector.ResolveDevelopment(services);

        AssertEx.Equal(DockerSandboxRuntimeProvider.Name, development.ProviderName);
        AssertEx.True(development is DockerSandboxRuntimeProvider);
    }

    [Test]
    public async Task ResolveDevelopment_WhenProviderIsUnknown_ThrowsWithProviderName()
    {
        await using var services = BuildServices("process", developmentProvider: "not-a-provider");

        var exception = await AssertEx.ThrowsAsync<InvalidOperationException>(() =>
        {
            _ = SandboxProviderSelector.ResolveDevelopment(services);
            return Task.CompletedTask;
        });

        AssertEx.Contains(exception.Message, "not-a-provider");
    }

    private static ServiceProvider BuildServices(string? agentProvider, string? developmentProvider = null)
    {
        var configurationValues = new Dictionary<string, string?>();
        if (agentProvider is not null)
        {
            configurationValues["AgentHome:Sandbox:Provider"] = agentProvider;
        }

        if (developmentProvider is not null)
        {
            configurationValues["Development:Sandbox:Provider"] = developmentProvider;
        }

        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(configurationValues)
                            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddLogging();
        services.AddOptions<SandboxOptions>().Bind(configuration.GetSection(SandboxOptions.SectionName));
        services.AddOptions<DevelopmentSandboxOptions>().Bind(configuration.GetSection(DevelopmentSandboxOptions.SectionName));
        // The process provider reuses the bound LocalContainerOptions for its per-file copy ceiling; constructing it
        // creates no process and opens no connection, so the bound options plus TimeProvider are all it needs.
        services.AddOptions<LocalContainerOptions>().Bind(configuration.GetSection(LocalContainerOptions.SectionName));
        services.AddOptions<ContainerSandboxOptions>().Bind(configuration.GetSection(ContainerSandboxOptions.SectionName));
        // Constructing the Docker provider only captures its dependencies — it contacts no daemon until a sandbox is
        // created — so selection can be pinned on a box with no Docker installed at all.
        services.AddSingleton(Substitute.For<IDockerRuntimeClientFactory>());
        // The container provider derives its install id from this; hashing a path touches no disk.
        services.AddSingleton<INodeDataDirectory>(new FakeNodeDataDirectory(Path.Combine(Path.GetTempPath(), "xe-selection-tests")));
        // Registered as concrete types, exactly as the real module does, because that is what makes two roles naming
        // the same provider resolve to one instance.
        services.AddSingleton<FakeSandboxRuntimeProvider>();
        services.AddSingleton<ProcessSandboxRuntimeProvider>();
        services.AddSingleton<DockerSandboxRuntimeProvider>();
        return services.BuildServiceProvider();
    }
}
