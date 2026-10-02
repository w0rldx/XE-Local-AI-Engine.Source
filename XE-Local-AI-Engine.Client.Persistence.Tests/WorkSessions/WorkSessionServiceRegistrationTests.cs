namespace XE_Local_AI_Engine.Client.Persistence.Tests.WorkSessions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.DependencyInjection.Modules;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;

[Category(TestCategories.Integration)]
public sealed class WorkSessionServiceRegistrationTests
{
    [Test]
    public void AddNodeWorkSessions_RegistersTheStoreBlobStoreAndExactlyOneReconciler()
    {
        var builder = Host.CreateApplicationBuilder();
        _ = builder.AddNodeWorkSessions(new ConfigurationBuilder().Build());

        AssertEx.True(builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IAgentWorkSessionStore)));
        AssertEx.True(builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IWorkSessionArtifactBlobStore)));
        AssertEx.Equal(expected: 1,
            builder.Services.Count(descriptor => descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType == typeof(WorkSessionStartupReconciler)),
            "Two reconciler registrations would collapse every in-flight session twice.");
    }

    [Test]
    public void AddNodeWorkSessions_WhenDisabled_StillRegistersEverything()
    {
        var builder = Host.CreateApplicationBuilder();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WorkSessions:Enabled"] = "false"
        }).Build();
        _ = builder.AddNodeWorkSessions(configuration);

        // The kill switch gates behaviour, not the container: the REST surface and the hub are mapped unconditionally,
        // so an empty container would answer 500 where a disabled node has to answer legibly.
        AssertEx.True(builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IAgentWorkSessionStore)));
        AssertEx.True(builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IWorkSessionArtifactBlobStore)));
        AssertEx.True(builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType == typeof(WorkSessionStartupReconciler)));
    }

    /// <summary>
    ///     The switch is live, so recovery cannot wait for it: a session stranded while the feature was off would stay
    ///     Running once it is turned on, and BeginAsync refuses to resume a Running session.
    /// </summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Reconciler_CollapsesInFlightSessionsWhateverTheFeatureSwitchSays(bool enabled)
    {
        using var fixture = new WorkSessionTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = WorkSessionTestFixture.StoreFor(context);
        var sessionId = await ArrangeRunningAsync(store);

        await RunReconcilerAsync(store, enabled);

        AssertEx.Equal(AgentWorkSessionStatus.Interrupted, (await store.GetAsync(sessionId)).Status);
    }

    [Test]
    public void CompositionRoot_InvokesTheModuleAfterChat()
    {
        // Hosted services start in registration order, so the chat restart recovery has to terminalize rows orphaned by
        // a crash before this reconciler collapses those sessions to Interrupted. The invariant is a property of the
        // composition root's call order, which is why it is read from the source rather than from a container.
        var source = File.ReadAllText(CompositionRootPath());
        var chatIndex = source.IndexOf("AddNodeChat(configuration)", StringComparison.Ordinal);
        var workSessionIndex = source.IndexOf("AddNodeWorkSessions(configuration)", StringComparison.Ordinal);

        AssertEx.True(chatIndex >= 0, "The composition root must still call AddNodeChat.");
        AssertEx.True(workSessionIndex > chatIndex, "AddNodeWorkSessions must be invoked after AddNodeChat in the composition root.");
    }

    private static async Task<Guid> ArrangeRunningAsync(IAgentWorkSessionStore store)
    {
        var sessionId = Guid.NewGuid();
        var created = await store.CreateAsync(WorkSessionTestFixture.CreateSeed(sessionId));
        _ = await store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand
        {
            SessionId = sessionId,
            ExpectedVersion = created.Version,
            TargetStatus = AgentWorkSessionStatus.Running
        });
        return sessionId;
    }

    private static async Task RunReconcilerAsync(IAgentWorkSessionStore store, bool enabled)
    {
        var runtimeSettings = Substitute.For<INodeRuntimeSettings>();
        runtimeSettings.GetWorkSessionsEnabledAsync(Arg.Any<CancellationToken>()).Returns(enabled);
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(runtimeSettings);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        await using var provider = services.BuildServiceProvider();

        // Built the way the host builds it, so a reconciler that came to read the switch would read this one.
        var reconciler = ActivatorUtilities.CreateInstance<WorkSessionStartupReconciler>(provider);

        await reconciler.StartAsync(CancellationToken.None);
        await reconciler.StopAsync(CancellationToken.None);
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
