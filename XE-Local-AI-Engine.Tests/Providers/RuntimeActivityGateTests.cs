namespace XE_Local_AI_Engine.Tests.Providers;

using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The lease algebra behind <c>409 runtime-busy</c>. The real gate is exercised here, never a substitute: it is the
///     thing under test, and a mocked gate would prove only that the test calls the mock. Every case runs against both
///     runtimes' gates.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class RuntimeActivityGateTests
{
    [Test]
    [Arguments(Runtime.Transcription)]
    [Arguments(Runtime.Image)]
    public void Mutation_RefusedWhileAJobLeaseIsHeld(Runtime runtime)
    {
        var gate = Create(runtime);
        AssertEx.Succeeded(gate.TryAcquireJobLease(out var job));
        using var jobScope = job;

        AssertEx.False(gate.TryAcquireMutationReservation(out _),
            "A managed-runtime mutation must not replace bytes out from under an in-flight job.");
        AssertEx.True(gate.GetSnapshot().IsBusy);
        AssertEx.Equal(expected: 1, gate.GetSnapshot().ActiveJobCount);
    }

    [Test]
    [Arguments(Runtime.Transcription)]
    [Arguments(Runtime.Image)]
    public void Mutation_RefusedWhileAProcessIsResident(Runtime runtime)
    {
        // A resident child still has the binary and the model file open, so replacing them is not safe even with
        // nothing in flight. This is the one thing mutation requires that eviction does not.
        var gate = Create(runtime);
        AssertEx.Succeeded(gate.TryAcquireResidentProcessLease(out var resident));
        using var residentScope = resident;

        AssertEx.False(gate.TryAcquireMutationReservation(out _));
        AssertEx.True(gate.TryAcquireEvictionReservation(out _));
    }

    [Test]
    [Arguments(Runtime.Transcription)]
    [Arguments(Runtime.Image)]
    public void Eviction_RefusedWhileASpawnReadinessLeaseIsHeld(Runtime runtime)
    {
        var gate = Create(runtime);
        AssertEx.Succeeded(gate.TryAcquireSpawnReadinessLease(out var spawn));
        using var spawnScope = spawn;

        AssertEx.False(gate.TryAcquireEvictionReservation(out _),
            "An eject must not race a spawn that is still becoming ready.");
    }

    [Test]
    [Arguments(Runtime.Transcription)]
    [Arguments(Runtime.Image)]
    public void Eviction_RefusedWhileAJobLeaseIsHeld(Runtime runtime)
    {
        var gate = Create(runtime);
        AssertEx.Succeeded(gate.TryAcquireJobLease(out var job));
        using var jobScope = job;

        AssertEx.False(gate.TryAcquireEvictionReservation(out _));
    }

    [Test]
    [Arguments(Runtime.Transcription)]
    [Arguments(Runtime.Image)]
    public void JobAndSpawn_RefusedWhileAReservationIsHeld(Runtime runtime)
    {
        var gate = Create(runtime);
        AssertEx.Succeeded(gate.TryAcquireMutationReservation(out var mutation));
        using var mutationScope = mutation;

        AssertEx.False(gate.TryAcquireJobLease(out _), "A reservation must close the gate to new work.");
        AssertEx.False(gate.TryAcquireSpawnReadinessLease(out _));
        AssertEx.False(gate.TryAcquireResidentProcessLease(out _));
    }

    [Test]
    [Arguments(Runtime.Transcription)]
    [Arguments(Runtime.Image)]
    public void Reservation_ReleasedOnDispose_ReopensTheGate(Runtime runtime)
    {
        var gate = Create(runtime);

        AssertEx.Succeeded(gate.TryAcquireEvictionReservation(out var reservation));
        using (reservation)
        {
            AssertEx.False(gate.TryAcquireJobLease(out _));
        }

        AssertEx.True(gate.TryAcquireJobLease(out _));
        AssertEx.False(gate.GetSnapshot().EvictionReserved);
    }

    [Test]
    [Arguments(Runtime.Transcription)]
    [Arguments(Runtime.Image)]
    public void Leases_ReleaseExactlyOnceOnDoubleDispose(Runtime runtime)
    {
        // A double dispose that decremented twice would let a mutation in while real work was still in flight.
        var gate = Create(runtime);
        AssertEx.Succeeded(gate.TryAcquireJobLease(out var first));
        AssertEx.Succeeded(gate.TryAcquireJobLease(out var second));

        first.Dispose();
        first.Dispose();

        AssertEx.Equal(expected: 1, gate.GetSnapshot().ActiveJobCount,
            "A second dispose of the same lease must not release the other one.");
        AssertEx.False(gate.TryAcquireMutationReservation(out _));

        second.Dispose();
        AssertEx.Equal(expected: 0, gate.GetSnapshot().ActiveJobCount);
        AssertEx.True(gate.TryAcquireMutationReservation(out _));
    }

    [Test]
    [Arguments(Runtime.Transcription)]
    [Arguments(Runtime.Image)]
    public async Task Leases_ReleaseExactlyOnceOnAsyncDispose(Runtime runtime)
    {
        var gate = Create(runtime);
        AssertEx.Succeeded(gate.TryAcquireJobLease(out var lease));

        await lease.DisposeAsync();
        await lease.DisposeAsync();

        AssertEx.Equal(expected: 0, gate.GetSnapshot().ActiveJobCount);
    }

    [Test]
    [Arguments(Runtime.Transcription)]
    [Arguments(Runtime.Image)]
    public void EveryGrantAndRelease_TicksResidency_ARefusalDoesNot(Runtime runtime)
    {
        // The residents read reports these counts, so the header must hear about each one; a refusal changed nothing.
        var notifier = new RecordingResidencyChangeNotifier();
        var gate = Create(runtime, notifier);
        AssertEx.Succeeded(gate.TryAcquireJobLease(out var job));
        AssertEx.Succeeded(gate.TryAcquireSpawnReadinessLease(out var spawn));
        AssertEx.Succeeded(gate.TryAcquireResidentProcessLease(out var resident));
        var leases = new[] { job, spawn, resident };
        AssertEx.Equal(expected: 3, notifier.Count);

        AssertEx.False(gate.TryAcquireMutationReservation(out _));
        AssertEx.Equal(expected: 3, notifier.Count, "A refused reservation must not tick.");

        foreach (var lease in leases)
        {
            lease.Dispose();
        }

        AssertEx.Equal(expected: 6, notifier.Count);
        AssertEx.Succeeded(gate.TryAcquireEvictionReservation(out var reservation));
        using (reservation)
        {
            AssertEx.Equal(expected: 7, notifier.Count);
        }

        AssertEx.Equal(expected: 8, notifier.Count);
    }

    [Test]
    [Arguments(Runtime.Transcription)]
    [Arguments(Runtime.Image)]
    public void Snapshot_WithNothingHeld_IsNotBusy(Runtime runtime)
    {
        var snapshot = Create(runtime).GetSnapshot();

        AssertEx.False(snapshot.IsBusy);
        AssertEx.Equal(expected: 0, snapshot.ActiveJobCount);
        AssertEx.Equal(expected: 0, snapshot.SpawnReadinessCount);
        AssertEx.Equal(expected: 0, snapshot.ResidentProcessCount);
        AssertEx.False(snapshot.MutationReserved);
        AssertEx.False(snapshot.EvictionReserved);
    }

    [Test]
    public void ProviderRegistrations_GiveEachRuntimeItsOwnGate()
    {
        // The two runtimes share one gate class, never one instance: a transcription in flight must not block an image
        // runtime mutation, and the reverse.
        var services = new ServiceCollection();
        services.AddWhisperCppRuntime();
        services.AddStableDiffusionCppImageProvider();
        using var provider = services.BuildServiceProvider();
        var whisper = provider.GetRequiredService<IWhisperRuntimeActivityGate>();
        var image = provider.GetRequiredService<IImageRuntimeActivityGate>();

        AssertEx.False(ReferenceEquals(whisper, image));
        AssertEx.Succeeded(whisper.TryAcquireJobLease(out var transcription));
        using var transcriptionScope = transcription;
        AssertEx.Succeeded(image.TryAcquireMutationReservation(out var reservation));
        using (reservation)
        {
            AssertEx.Equal(expected: 0, image.GetSnapshot().ActiveJobCount);
            AssertEx.False(whisper.GetSnapshot().MutationReserved);
        }

        AssertEx.Succeeded(image.TryAcquireJobLease(out var generation));
        using var generationScope = generation;
        AssertEx.Equal(expected: 1, whisper.GetSnapshot().ActiveJobCount);
        AssertEx.Equal(expected: 1, image.GetSnapshot().ActiveJobCount);
    }

    public enum Runtime
    {
        Transcription,
        Image
    }

    private static RuntimeActivityGate Create(Runtime runtime, IRuntimeResidencyChangeNotifier? notifier = null) =>
        runtime == Runtime.Transcription ? new WhisperRuntimeActivityGate(notifier) : new ImageRuntimeActivityGate(notifier);
}
