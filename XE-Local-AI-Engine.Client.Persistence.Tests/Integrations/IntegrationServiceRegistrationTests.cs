namespace XE_Local_AI_Engine.Client.Persistence.Tests.Integrations;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.DependencyInjection.Modules;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Integrations;

public sealed class IntegrationServiceRegistrationTests
{
    [Test]
    public void AddNodeIntegrations_RegistersTheFourStoresAsScoped()
    {
        var builder = Host.CreateApplicationBuilder();
        _ = builder.AddNodeIntegrations(new ConfigurationBuilder().Build());

        foreach (var serviceType in new[]
                 {
                     typeof(IIntegrationTriggerStore),
                     typeof(IIntegrationApiKeyStore),
                     typeof(IIntegrationSessionStore),
                     typeof(IIntegrationExecutionStore)
                 })
        {
            var descriptor = AssertEx.NotNull(builder.Services.SingleOrDefault(candidate => candidate.ServiceType == serviceType),
                $"{serviceType.Name} must be registered exactly once.");
            AssertEx.Equal(ServiceLifetime.Scoped, descriptor.Lifetime, "The stores take the scoped DbContext, so a singleton would capture a disposed one.");
        }
    }

    [Test]
    public void AddNodeIntegrations_BindsTheOptionsAndValidatesThemOnStart()
    {
        var builder = Host.CreateApplicationBuilder();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Integrations:MaxQueuedExecutions"] = "16",
            ["Integrations:EventBufferTtlAfterTerminal"] = "00:05:00"
        }).Build();
        _ = builder.AddNodeIntegrations(configuration);

        using var provider = builder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<IntegrationOptions>>().Value;

        AssertEx.Equal(expected: 16, options.MaxQueuedExecutions);
        AssertEx.Equal(TimeSpan.FromMinutes(5), options.EventBufferTtlAfterTerminal);
        AssertEx.Equal(expected: 2, options.MaxQueuedExecutionsPerPrincipal, "An unset member keeps its compiled default, which is the shipped posture.");

        // ValidateOnStart registers a startup validator; without it a hostile configuration would only be discovered on
        // first resolution, deep into a running node.
        AssertEx.True(builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IStartupValidator)));
    }

    [Test]
    public void AddNodeIntegrations_RegistersNoValidateOptionsClass()
    {
        var builder = Host.CreateApplicationBuilder();
        _ = builder.AddNodeIntegrations(new ConfigurationBuilder().Build());

        // The options class has no cross-section invariant, so it validates itself through IValidatableObject rather
        // than earning a registered validator type — one implementation of a one-purpose interface is the abstraction
        // this deliberately does not add.
        AssertEx.Empty(builder.Services.Where(descriptor => descriptor.ServiceType == typeof(IValidateOptions<IntegrationOptions>)
                                                            && descriptor.ImplementationType is not null));
    }

    [Test]
    public void CompositionRoot_InvokesTheModuleAfterTheChatModule()
    {
        // Hosted services start in registration order, so the coordinator this module gains in the next slice must
        // start after chat restart recovery has terminalized rows orphaned by a crash. The invariant is a property of
        // the composition root's call order, which is why it is read from the source rather than from a container.
        var source = File.ReadAllText(CompositionRootPath());
        var chatIndex = source.IndexOf("AddNodeChat(configuration)", StringComparison.Ordinal);
        var integrationsIndex = source.IndexOf("AddNodeIntegrations(configuration)", StringComparison.Ordinal);

        AssertEx.True(chatIndex >= 0, "The composition root must still call AddNodeChat.");
        AssertEx.True(integrationsIndex > chatIndex, "AddNodeIntegrations must be invoked after AddNodeChat in the composition root.");
    }

    private static string CompositionRootPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "XE-Local-AI-Engine.slnx")))
        {
            directory = directory.Parent;
        }

        var root = AssertEx.NotNull(directory, "The repository root must be reachable from the test output directory.");
        return Path.Combine(root.FullName, "XE-Local-AI-Engine.Client.Application", "DependencyInjection", "NodeApplicationServiceCollectionExtensions.cs");
    }
}
