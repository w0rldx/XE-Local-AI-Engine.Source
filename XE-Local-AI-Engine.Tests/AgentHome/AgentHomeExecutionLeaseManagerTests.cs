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
        AssertEx.Succeeded(manager.TryAcquire(KeyA, out var first));
        using var firstScope = first;

        Task<bool> contender;
        using (ExecutionContext.SuppressFlow())
        {
            contender = Task.Run(() => manager.TryAcquire(KeyA, out _));
        }

        AssertEx.False(await contender);
    }

    [Test]
    public void TryAcquire_SameAmbientKey_ReturnsBorrowedLeaseWithoutReleasingOwner()
    {
        var manager = new AgentHomeExecutionLeaseManager();
        AssertEx.Succeeded(manager.TryAcquire(KeyA, out var owner));
        using var ownerScope = owner;
        AssertEx.Succeeded(manager.TryAcquire(KeyA, out var borrowed));
        using var borrowedScope = borrowed;

        AssertEx.True(borrowed.IsBorrowed);
    }

    [Test]
    public void TryAcquire_DifferentKeyWhileAmbientRootExists_IsRejected()
    {
        var manager = new AgentHomeExecutionLeaseManager();
        AssertEx.Succeeded(manager.TryAcquire(KeyA, out var first));
        using var firstScope = first;

        AssertEx.False(manager.TryAcquire(KeyB, out _));
    }

    [Test]
    public async Task Dispose_OwnerLease_AllowsLaterUnrelatedAcquisition()
    {
        var manager = new AgentHomeExecutionLeaseManager();
        AssertEx.Succeeded(manager.TryAcquire(KeyA, out var first));
        first.Dispose();

        Task<IAgentHomeExecutionLease?> later;
        using (ExecutionContext.SuppressFlow())
        {
            later = Task.Run(() => manager.TryAcquire(KeyA, out var lease) ? lease : null);
        }

        using var acquired = await later;
        AssertEx.NotNull(acquired);
    }

    [Test]
    public async Task EnterAmbientScope_TransfersBorrowingIntoDetachedInvocationContext()
    {
        var manager = new AgentHomeExecutionLeaseManager();
        using var owner = await AcquireAfterYieldAsync(manager);
        AssertEx.False(manager.TryAcquire(KeyA, out _));

        using (owner.EnterAmbientScope())
        {
            AssertEx.Succeeded(manager.TryAcquire(KeyA, out var borrowed));
            using (borrowed)
            {
                AssertEx.True(borrowed.IsBorrowed);
            }
        }
    }

    private static async Task<IAgentHomeExecutionLease> AcquireAfterYieldAsync(IAgentHomeExecutionLeaseManager manager)
    {
        await Task.Yield();
        AssertEx.Succeeded(manager.TryAcquire(KeyA, out var lease));
        return lease;
    }

    [Test]
    public void TryAcquire_WhenPoisoned_RefusesNormalButAllowsRecovery()
    {
        var manager = new AgentHomeExecutionLeaseManager();
        manager.MarkPoisoned(KeyA);

        AssertEx.False(manager.TryAcquire(KeyA, out _));
        AssertEx.Succeeded(manager.TryAcquireForRecovery(KeyA, out var recovery));
        using var recoveryScope = recovery;
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

        AssertEx.Succeeded(manager.TryAcquire(KeyA, out var lease));
        AssertEx.True(manager.IsHeld(KeyA), "a held lease must read as held, or the sweep would delete a live run.");
        AssertEx.False(manager.IsHeld(KeyB), "the peek is per key.");

        // The peek must not have consumed the gate: an unrelated context still finds the key busy.
        Task<bool> contender;
        using (ExecutionContext.SuppressFlow())
        {
            contender = Task.Run(() => manager.TryAcquire(KeyA, out _));
        }

        AssertEx.False(await contender, "IsHeld must be a read; a peek that acquired would have released the owner's gate.");

        lease.Dispose();
        AssertEx.False(manager.IsHeld(KeyA), "a released lease reads as free again.");
    }

    /// <summary>
    ///     The race the peek cannot close, pinned as the behavior it IS: a lease taken after the read still reads as
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
#pragma warning disable CA2000 // Disposed on another context below: that out-of-order disposal is what this test pins.
        AssertEx.Succeeded(manager.TryAcquire(KeyA, out var owner));
#pragma warning restore CA2000
        using var activation = owner.EnterAmbientScope();
        await Task.Run(owner.Dispose);

        AssertEx.Succeeded(manager.TryAcquire(KeyA, out var replacement));
        using var replacementScope = replacement;
        AssertEx.False(replacement.IsBorrowed);
    }
}
