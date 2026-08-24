namespace XE_Local_AI_Engine.Client.Persistence.Tests.WorkSessions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.DependencyInjection.Modules;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;

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

    [Test]
    public async Task Reconciler_CollapsesInFlightSessionsOnlyWhenEnabled()
    {
        using var disabledFixture = new WorkSessionTestFixture();
        await using var disabledContext = await disabledFixture.CreateSchemaAsync().ConfigureAwait(false);
        var disabledStore = WorkSessionTestFixture.StoreFor(disabledContext);
        var disabledSessionId = await ArrangeRunningAsync(disabledStore).ConfigureAwait(false);
        await RunReconcilerAsync(disabledStore, new WorkSessionOptions()).ConfigureAwait(false);
        AssertEx.Equal(AgentWorkSessionStatus.Running,
            (await disabledStore.GetAsync(disabledSessionId).ConfigureAwait(false)).Status,
            "A disabled node must leave its session rows exactly as it found them.");

        using var enabledFixture = new WorkSessionTestFixture();
        await using var enabledContext = await enabledFixture.CreateSchemaAsync().ConfigureAwait(false);
        var enabledStore = WorkSessionTestFixture.StoreFor(enabledContext);
        var enabledSessionId = await ArrangeRunningAsync(enabledStore).ConfigureAwait(false);
        await RunReconcilerAsync(enabledStore,
                new WorkSessionOptions
                {
                    Enabled = true
                })
            .ConfigureAwait(false);
        AssertEx.Equal(AgentWorkSessionStatus.Interrupted, (await enabledStore.GetAsync(enabledSessionId).ConfigureAwait(false)).Status);
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
        var created = await store.CreateAsync(WorkSessionTestFixture.CreateSeed(sessionId)).ConfigureAwait(false);
        _ = await store.TransitionStatusAsync(new TransitionWorkSessionStatusCommand(sessionId, created.Version, AgentWorkSessionStatus.Running)).ConfigureAwait(false);
        return sessionId;
    }

    private static async Task RunReconcilerAsync(IAgentWorkSessionStore store, WorkSessionOptions options)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        await using var provider = services.BuildServiceProvider();
        var reconciler = new WorkSessionStartupReconciler(provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options),
            NullLogger<WorkSessionStartupReconciler>.Instance);

        await reconciler.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await reconciler.StopAsync(CancellationToken.None).ConfigureAwait(false);
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
