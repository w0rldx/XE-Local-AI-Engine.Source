namespace XE_Local_AI_Engine.Tests.DocumentIngestion;

using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using XE_Local_AI_Engine.Tests.Testing;

// The lease handles are disposed explicitly on the paths under test; CA2000 cannot track the out-var + conditional flow.
#pragma warning disable CA2000
public sealed class DocumentExtractionAdmissionGateTests
{
    [Test]
    public void TryAcquire_WhenAtCapacity_RejectsUntilALeaseIsReleased()
    {
        using var gate = new DocumentExtractionAdmissionGate(maxConcurrentExtractions: 1);

        AssertEx.True(gate.TryAcquire(out var first), "the first extraction is admitted.");
        AssertEx.False(gate.TryAcquire(out var blocked), "a second extraction is rejected while the gate is full.");
        AssertEx.Null(blocked);

        // Releasing the first lease frees the single slot for the next extraction.
        first!.Dispose();

        AssertEx.True(gate.TryAcquire(out var third), "a slot frees up once the prior lease is released.");
        third!.Dispose();
    }

    [Test]
    public void TryAcquire_LeaseDispose_IsIdempotent()
    {
        using var gate = new DocumentExtractionAdmissionGate(maxConcurrentExtractions: 1);

        AssertEx.True(gate.TryAcquire(out var lease));
        lease!.Dispose();
        lease.Dispose(); // double dispose must not over-release the semaphore.

        // Only one slot should exist: acquire it, and a second acquire must still be rejected.
        AssertEx.True(gate.TryAcquire(out var again), "the single slot is acquirable after a double-dispose.");
        AssertEx.False(gate.TryAcquire(out _), "a double-dispose must not have leaked an extra slot.");
        again!.Dispose();
    }

    [Test]
    public async Task Constructor_WhenMaxConcurrentIsNotPositive_Throws()
    {
        await AssertEx.ThrowsAsync<ArgumentOutOfRangeException>(() =>
        {
            using var gate = new DocumentExtractionAdmissionGate(maxConcurrentExtractions: 0);
            return Task.CompletedTask;
        });
    }
}
#pragma warning restore CA2000
