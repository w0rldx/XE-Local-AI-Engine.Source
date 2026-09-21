namespace XE_Local_AI_Engine.Tests.AgentHome;

using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
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

    /// <summary>
    ///     The non-acquiring peek the run-retention sweep reads: it must answer for a key nobody ever took, become
    ///     true while a lease is held, and go false again on disposal — all without ever taking the gate itself.
    /// </summary>
    [Test]
    public async Task IsHeld_TracksTheGateWithoutTakingIt()
    {
        var manager = new AgentHomeExecutionLeaseManager();

        AssertEx.False(manager.IsHeld(KeyA), "a key nobody ever acquired has no gate, so it cannot be held.");

        var lease = AssertEx.NotNull(manager.TryAcquire(KeyA));
        AssertEx.True(manager.IsHeld(KeyA), "a held lease must read as held, or the sweep would delete a live run.");
        AssertEx.False(manager.IsHeld(KeyB), "the peek is per key.");

        // The peek must not have consumed the gate: an unrelated context still finds the key busy.
        Task<IAgentHomeExecutionLease?> contender;
        using (ExecutionContext.SuppressFlow())
        {
            contender = Task.Run(() => manager.TryAcquire(KeyA));
        }

        AssertEx.Null(await contender, "IsHeld must be a read; a peek that acquired would have released the owner's gate.");

        lease.Dispose();
        AssertEx.False(manager.IsHeld(KeyA), "a released lease reads as free again.");
    }

    /// <summary>
    ///     The race the peek cannot close, pinned as the behaviour it IS: a lease taken after the read still reads as
    ///     free to the caller holding that stale answer. Callers treat "held" as a refusal, never "not held" as
    ///     exclusivity.
    /// </summary>
    [Test]
    public async Task IsHeld_ReadBeforeAnAcquisition_IsAStaleAnswerNotALock()
    {
        var manager = new AgentHomeExecutionLeaseManager();
        var beforeAcquire = manager.IsHeld(KeyA);

        using var lease = await AcquireAfterYieldAsync(manager);

        AssertEx.False(beforeAcquire, "the snapshot taken before the acquisition is free, and stays free.");
        AssertEx.True(manager.IsHeld(KeyA), "a fresh read sees the lease the stale one could not.");
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
