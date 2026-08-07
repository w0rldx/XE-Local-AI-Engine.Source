namespace XE_Local_AI_Engine.Tests.AgentHome;

using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class AgentHomeExecutionLeaseManagerTests
{
    private static readonly AgentHomeExecutionLeaseKey KeyA = new("owner", "node-a");
    private static readonly AgentHomeExecutionLeaseKey KeyB = new("owner", "node-b");

    [Test]
    public async Task TryAcquire_SameKeyFromUnrelatedContext_IsBusyAndDoesNotQueue()
    {
        var manager = new AgentHomeExecutionLeaseManager();
        using var first = manager.TryAcquire(KeyA);
        AssertEx.NotNull(first);

        Task<IAgentHomeExecutionLease?> contender;
        using (ExecutionContext.SuppressFlow())
        {
            contender = Task.Run(() => manager.TryAcquire(KeyA));
        }

        AssertEx.Null(await contender);
    }

    [Test]
    public void TryAcquire_SameAmbientKey_ReturnsBorrowedLeaseWithoutReleasingOwner()
    {
        var manager = new AgentHomeExecutionLeaseManager();
        using var owner = manager.TryAcquire(KeyA);
        using var borrowed = manager.TryAcquire(KeyA);

        AssertEx.NotNull(owner);
        AssertEx.NotNull(borrowed);
        AssertEx.True(borrowed!.IsBorrowed);
    }

    [Test]
    public void TryAcquire_DifferentKeyWhileAmbientRootExists_IsRejected()
    {
        var manager = new AgentHomeExecutionLeaseManager();
        using var first = manager.TryAcquire(KeyA);
        using var second = manager.TryAcquire(KeyB);

        AssertEx.NotNull(first);
        AssertEx.Null(second);
    }

    [Test]
    public async Task Dispose_OwnerLease_AllowsLaterUnrelatedAcquisition()
    {
        var manager = new AgentHomeExecutionLeaseManager();
        manager.TryAcquire(KeyA)!.Dispose();

        Task<IAgentHomeExecutionLease?> later;
        using (ExecutionContext.SuppressFlow())
        {
            later = Task.Run(() => manager.TryAcquire(KeyA));
        }

        using var acquired = await later;
        AssertEx.NotNull(acquired);
    }

    [Test]
    public async Task EnterAmbientScope_TransfersBorrowingIntoDetachedInvocationContext()
    {
        var manager = new AgentHomeExecutionLeaseManager();
        using var owner = await AcquireAfterYieldAsync(manager);
        AssertEx.Null(manager.TryAcquire(KeyA));

        using (owner.EnterAmbientScope())
        using (var borrowed = manager.TryAcquire(KeyA))
        {
            AssertEx.NotNull(borrowed);
            AssertEx.True(borrowed!.IsBorrowed);
        }
    }

    private static async Task<IAgentHomeExecutionLease> AcquireAfterYieldAsync(IAgentHomeExecutionLeaseManager manager)
    {
        await Task.Yield();
        return AssertEx.NotNull(manager.TryAcquire(KeyA));
    }

    [Test]
    public void TryAcquire_WhenPoisoned_RefusesNormalButAllowsRecovery()
    {
        var manager = new AgentHomeExecutionLeaseManager();
        manager.MarkPoisoned(KeyA);

        AssertEx.Null(manager.TryAcquire(KeyA));
        using var recovery = manager.TryAcquireForRecovery(KeyA);
        AssertEx.NotNull(recovery);
        manager.ClearPoison(KeyA);
        AssertEx.False(manager.IsPoisoned(KeyA));
    }

    [Test]
    public async Task OutOfOrderCrossContextDisposal_DoesNotResurrectDisposedAmbientScope()
    {
        var manager = new AgentHomeExecutionLeaseManager();
        var owner = AssertEx.NotNull(manager.TryAcquire(KeyA));
        using var activation = owner.EnterAmbientScope();
        await Task.Run(owner.Dispose);

        using var replacement = manager.TryAcquire(KeyA);
        AssertEx.NotNull(replacement);
        AssertEx.False(replacement!.IsBorrowed);
    }
}
