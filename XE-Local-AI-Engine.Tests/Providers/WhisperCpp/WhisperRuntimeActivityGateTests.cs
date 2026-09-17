namespace XE_Local_AI_Engine.Tests.Providers.WhisperCpp;

using XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The lease algebra behind <c>409 runtime-busy</c>. The real gate is exercised here, never a substitute: it is the
///     thing under test, and a mocked gate would prove only that the test calls the mock.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class WhisperRuntimeActivityGateTests
{
    [Test]
    public void Mutation_RefusedWhileATranscriptionLeaseIsHeld()
    {
        var gate = new WhisperRuntimeActivityGate();
        using var transcription = AssertEx.NotNull(gate.TryAcquireTranscriptionLease());

        AssertEx.Null(gate.TryAcquireMutationReservation(),
            "A managed-runtime mutation must not replace bytes out from under an in-flight transcription.");
        AssertEx.True(gate.GetSnapshot().IsBusy);
        AssertEx.Equal(expected: 1, gate.GetSnapshot().ActiveTranscriptionCount);
    }

    [Test]
    public void Mutation_RefusedWhileAProcessIsResident()
    {
        // A resident child still has the binary and the model file open, so replacing them is not safe even with
        // nothing in flight. This is the one thing mutation requires that eviction does not.
        var gate = new WhisperRuntimeActivityGate();
        using var resident = AssertEx.NotNull(gate.TryAcquireResidentProcessLease());

        AssertEx.Null(gate.TryAcquireMutationReservation());
        AssertEx.NotNull(gate.TryAcquireEvictionReservation());
    }

    [Test]
    public void Eviction_RefusedWhileASpawnReadinessLeaseIsHeld()
    {
        var gate = new WhisperRuntimeActivityGate();
        using var spawn = AssertEx.NotNull(gate.TryAcquireSpawnReadinessLease());

        AssertEx.Null(gate.TryAcquireEvictionReservation(),
            "An eject must not race a spawn that is still becoming ready.");
    }

    [Test]
    public void Eviction_RefusedWhileATranscriptionLeaseIsHeld()
    {
        var gate = new WhisperRuntimeActivityGate();
        using var transcription = AssertEx.NotNull(gate.TryAcquireTranscriptionLease());

        AssertEx.Null(gate.TryAcquireEvictionReservation());
    }

    [Test]
    public void TranscriptionAndSpawn_RefusedWhileAReservationIsHeld()
    {
        var gate = new WhisperRuntimeActivityGate();
        using var mutation = AssertEx.NotNull(gate.TryAcquireMutationReservation());

        AssertEx.Null(gate.TryAcquireTranscriptionLease(), "A reservation must close the gate to new work.");
        AssertEx.Null(gate.TryAcquireSpawnReadinessLease());
        AssertEx.Null(gate.TryAcquireResidentProcessLease());
    }

    [Test]
    public void Reservation_ReleasedOnDispose_ReopensTheGate()
    {
        var gate = new WhisperRuntimeActivityGate();

        using (AssertEx.NotNull(gate.TryAcquireEvictionReservation()))
        {
            AssertEx.Null(gate.TryAcquireTranscriptionLease());
        }

        AssertEx.NotNull(gate.TryAcquireTranscriptionLease());
        AssertEx.False(gate.GetSnapshot().EvictionReserved);
    }

    [Test]
    public void Leases_ReleaseExactlyOnceOnDoubleDispose()
    {
        // A double dispose that decremented twice would let a mutation in while real work was still in flight.
        var gate = new WhisperRuntimeActivityGate();
        var first = AssertEx.NotNull(gate.TryAcquireTranscriptionLease());
        var second = AssertEx.NotNull(gate.TryAcquireTranscriptionLease());

        first.Dispose();
        first.Dispose();

        AssertEx.Equal(expected: 1, gate.GetSnapshot().ActiveTranscriptionCount,
            "A second dispose of the same lease must not release the other one.");
        AssertEx.Null(gate.TryAcquireMutationReservation());

        second.Dispose();
        AssertEx.Equal(expected: 0, gate.GetSnapshot().ActiveTranscriptionCount);
        AssertEx.NotNull(gate.TryAcquireMutationReservation());
    }

    [Test]
    public async Task Leases_ReleaseExactlyOnceOnAsyncDispose()
    {
        var gate = new WhisperRuntimeActivityGate();
        var lease = AssertEx.NotNull(gate.TryAcquireTranscriptionLease());

        await lease.DisposeAsync();
        await lease.DisposeAsync();

        AssertEx.Equal(expected: 0, gate.GetSnapshot().ActiveTranscriptionCount);
    }

    [Test]
    public void Snapshot_WithNothingHeld_IsNotBusy()
    {
        var snapshot = new WhisperRuntimeActivityGate().GetSnapshot();

        AssertEx.False(snapshot.IsBusy);
        AssertEx.Equal(expected: 0, snapshot.ActiveTranscriptionCount);
        AssertEx.Equal(expected: 0, snapshot.SpawnReadinessCount);
        AssertEx.Equal(expected: 0, snapshot.ResidentProcessCount);
        AssertEx.False(snapshot.MutationReserved);
        AssertEx.False(snapshot.EvictionReserved);
    }
}
