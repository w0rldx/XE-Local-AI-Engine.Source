namespace XE_Local_AI_Engine.Tests.AgentHome;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Fake;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class AgentHomeWorkspaceRevocationPreparationTests
{
    [Test]
    public async Task PrepareAsync_ClearsLiveSelectedRootBeforeReturning()
    {
        var provider = new FakeSandboxRuntimeProvider(TimeProvider.System);
        var handle = await provider.CreateOrAttachAsync(new SandboxCreateRequest
        {
            AttachKey = AttachKey(),
            RuntimeProfile = "dotnet-agent-home"
        });
        provider.WriteHostFile("stale", "secret");
        await provider.CopyIntoAsync(handle, new SandboxCopyRequest
        {
            SourcePath = "stale",
            DestinationPath = AgentHomeGit.WorkspaceSelectedRoot + "/project/stale.txt"
        });
        var service = CreateService(provider, new AgentHomeExecutionLeaseManager());

        await using var session = await service.PrepareAsync(Folder());

        AssertEx.Empty(provider.SnapshotSandboxPaths(handle));
    }

    [Test]
    public async Task PrepareAsync_WhenSameOwnerNodeIsBusy_FailsBeforeClearing()
    {
        var provider = new FakeSandboxRuntimeProvider(TimeProvider.System);
        var leases = new AgentHomeExecutionLeaseManager();
        using var held = leases.TryAcquire(new AgentHomeExecutionLeaseKey("owner", "node"));
        var service = CreateService(provider, leases);

        Task action;
        using (ExecutionContext.SuppressFlow())
        {
            action = Task.Run(() => service.PrepareAsync(Folder()));
        }

        await AssertEx.ThrowsAsync<WorkspaceRevocationBusyException>(() => action);
    }

    [Test]
    public async Task PrepareAsync_RetainsLeaseUntilReturnedSessionIsDisposed()
    {
        var provider = new FakeSandboxRuntimeProvider(TimeProvider.System);
        var leases = new AgentHomeExecutionLeaseManager();
        var service = CreateService(provider, leases);
        var session = await service.PrepareAsync(Folder());

        Task<IAgentHomeExecutionLease?> contender;
        using (ExecutionContext.SuppressFlow())
        {
            contender = Task.Run(() => leases.TryAcquire(new AgentHomeExecutionLeaseKey("owner", "node")));
        }

        AssertEx.Null(await contender);
        await session.DisposeAsync();
        using var acquired = leases.TryAcquire(new AgentHomeExecutionLeaseKey("owner", "node"));
        AssertEx.NotNull(acquired);
    }

    [Test]
    public async Task PrepareAsync_WhenOwnerNodeIsPoisoned_RecoveryClearsPoisonAndReturnsSession()
    {
        var provider = new FakeSandboxRuntimeProvider(TimeProvider.System);
        var leases = new AgentHomeExecutionLeaseManager();
        var key = new AgentHomeExecutionLeaseKey("owner", "node");
        leases.MarkPoisoned(key);
        var service = CreateService(provider, leases);

        await using var session = await service.PrepareAsync(Folder());

        AssertEx.False(leases.IsPoisoned(key));
    }

    private static AgentHomeWorkspaceRevocationPreparation CreateService(FakeSandboxRuntimeProvider provider,
        IAgentHomeExecutionLeaseManager leases)
    {
        var isolation = new AgentHomeWorkspaceIsolation(provider, leases, NullLogger<AgentHomeWorkspaceIsolation>.Instance);
        return new AgentHomeWorkspaceRevocationPreparation(new IdentityProvider(),
            leases,
            isolation,
            provider,
            Options.Create(new AgentHomeOptions()));
    }

    private static SandboxAttachKey AttachKey() => new()
    {
        OwnerUserId = "owner",
        NodeId = "node",
        ProviderName = FakeSandboxRuntimeProvider.Name,
        RuntimeProfile = "dotnet-agent-home",
        ManifestVersion = AgentHomeManifest.CurrentVersion
    };

    private static ResolvedSelectedFolder Folder() =>
        new(Guid.NewGuid(), "project", "/opaque/not/read", SelectedFolderMode.Copy);

    private sealed class IdentityProvider : IAgentHomeIdentityProvider
    {
        public Task<AgentHomeOwnerIdentity> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentHomeOwnerIdentity("owner", "node"));
    }
}
