namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class LlamaServerRuntimeMutationGateTests
{
    [Test]
    public async Task TryAcquireLease_WhenBlockedPredicateThrows_ReleasesTheExclusiveGate()
    {
        using var gate = new LlamaServerRuntimeMutationGate(typeof(LlamaServerRuntimeMutationGateTests), CancellationToken.None);

        await AssertEx.ThrowsAsync<InvalidOperationException>(() => gate.TryAcquireLeaseAsync(() => throw new InvalidOperationException("probe failed"), CancellationToken.None));

        AssertEx.False(gate.IsMutationActive, "A failed acquisition must not leave a mutation counted as active.");
        await AssertEx.CompletesAsync(gate.EnterSharedAsync(CancellationToken.None),
            TimeSpan.FromSeconds(5),
            "An ordinary ensure stayed blocked: the exclusive gate leaked when the predicate threw.");
        gate.ExitShared();
    }
}
