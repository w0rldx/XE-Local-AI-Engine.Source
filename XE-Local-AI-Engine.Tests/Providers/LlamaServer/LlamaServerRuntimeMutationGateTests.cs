namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using NSubstitute;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class LlamaServerRuntimeMutationGateTests
{
    [Test]
    public async Task TryAcquireLease_WhenBlockedPredicateThrows_ReleasesTheExclusiveGate()
    {
        using var gate = new LlamaServerRuntimeMutationGate(typeof(LlamaServerRuntimeMutationGateTests), CancellationToken.None);

        await AssertEx.ThrowsAsync<InvalidOperationException>(() =>
            gate.AcquireLeaseOrNullAsync(() => throw new InvalidOperationException("probe failed"), raisesDemand: true, CancellationToken.None));

        AssertEx.False(gate.IsMutationActive, "A failed acquisition must not leave a mutation counted as active.");
        await AssertEx.CompletesAsync(gate.EnterSharedAsync(CancellationToken.None),
            TimeSpan.FromSeconds(5),
            "An ordinary ensure stayed blocked: the exclusive gate leaked when the predicate threw.");
        gate.ExitShared();
    }

    /// <summary>Each public entry raises demand exactly once per acquisition; the lease's exclusive wait goes through the private entry, so it never raises twice.</summary>
    [Test]
    [Arguments(true, 1)]
    [Arguments(false, 0)]
    public async Task AcquireLease_RaisesDemandOnceOrNotAtAll(bool raisesDemand, int expectedRequests)
    {
        var idlePreemption = Substitute.For<IGpuIdleHolderPreemption>();
        using var gate = new LlamaServerRuntimeMutationGate(typeof(LlamaServerRuntimeMutationGateTests), CancellationToken.None, idlePreemption);

        var lease = AssertEx.NotNull(await gate.AcquireLeaseOrNullAsync(static () => false, raisesDemand, CancellationToken.None));
        await lease.DisposeAsync();

        idlePreemption.Received(expectedRequests).RequestPreemption();
    }

    [Test]
    public async Task SharedAndExclusiveEntries_EachRaiseDemandOnce()
    {
        var idlePreemption = Substitute.For<IGpuIdleHolderPreemption>();
        using var gate = new LlamaServerRuntimeMutationGate(typeof(LlamaServerRuntimeMutationGateTests), CancellationToken.None, idlePreemption);

        await gate.EnterSharedAsync(CancellationToken.None);
        gate.ExitShared();
        idlePreemption.Received(1).RequestPreemption();

        await gate.EnterExclusiveAsync(CancellationToken.None);
        gate.ExitExclusive();
        idlePreemption.Received(2).RequestPreemption();
    }

    /// <summary>Demand is raised BEFORE the wait, so the holder sitting on the gate is asked to leave while the entry still waits for it.</summary>
    [Test]
    public async Task AWaitingSharedEntry_HasRaisedDemand_BeforeTheHolderLeaves()
    {
        var idlePreemption = Substitute.For<IGpuIdleHolderPreemption>();
        using var gate = new LlamaServerRuntimeMutationGate(typeof(LlamaServerRuntimeMutationGateTests), CancellationToken.None, idlePreemption);
        var held = AssertEx.NotNull(await gate.AcquireLeaseOrNullAsync(static () => false, raisesDemand: false, CancellationToken.None));

        var entering = gate.EnterSharedAsync(CancellationToken.None);

        idlePreemption.Received(1).RequestPreemption();
        await AssertEx.StaysIncompleteAsync(entering, "The entry waits while the lease is held.");
        await held.DisposeAsync();
        await entering.WaitAsync(TestBudgets.Contended);
        gate.ExitShared();
    }
}
