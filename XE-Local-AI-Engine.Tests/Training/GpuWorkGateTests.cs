namespace XE_Local_AI_Engine.Tests.Training;

using XE_Local_AI_Engine.Client.Services.Training;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The node's GPU admission gate. Exclusive work owns the whole node; shared work coexists with shared work and
///     with nothing else.
/// </summary>
public sealed class GpuWorkGateTests
{
    [Test]
    public void Gate_ExclusiveIsRefusedWhileSharedIsHeld_AndTheReverse()
    {
        var gate = new GpuWorkGate();

        using (var shared = AssertEx.NotNull(gate.TryBeginShared(GpuWorkKind.Benchmark), "A free gate admits shared work."))
        {
            AssertEx.Null(gate.TryBeginExclusive(GpuWorkKind.TrainingRun), "A run must not admit beside executing benchmark work.");
            AssertEx.Null(gate.ExclusiveKind, "A refused exclusive acquisition must not record a holder.");
        }

        using var exclusive = AssertEx.NotNull(gate.TryBeginExclusive(GpuWorkKind.TrainingRun), "Releasing the shared hold frees the gate.");
        AssertEx.Equal(GpuWorkKind.TrainingRun, gate.ExclusiveKind);
        AssertEx.Null(gate.TryBeginShared(GpuWorkKind.ImageJob), "Shared work must not admit beside an exclusive holder.");
        AssertEx.Null(gate.TryBeginExclusive(GpuWorkKind.Export), "Nor may a second exclusive holder.");
    }

    [Test]
    public void Gate_SharedHoldersCoexist_AndTheLastReleaseFreesTheGate()
    {
        var gate = new GpuWorkGate();

        var benchmark = AssertEx.NotNull(gate.TryBeginShared(GpuWorkKind.Benchmark));
        var generation = AssertEx.NotNull(gate.TryBeginShared(GpuWorkKind.DatasetGeneration));
        var image = AssertEx.NotNull(gate.TryBeginShared(GpuWorkKind.ImageJob));

        benchmark.Dispose();
        generation.Dispose();
        AssertEx.Null(gate.TryBeginExclusive(GpuWorkKind.Export), "One shared holder is still enough to refuse an export.");

        image.Dispose();
        image.Dispose();
        using var exclusive = AssertEx.NotNull(gate.TryBeginExclusive(GpuWorkKind.Export), "The last release frees the gate.");
        AssertEx.Equal(GpuWorkKind.Export, gate.ExclusiveKind);
    }

    [Test]
    public void Gate_DisposingAnExclusiveHandleTwiceDoesNotReleaseSomebodyElsesHold()
    {
        var gate = new GpuWorkGate();
        var first = AssertEx.NotNull(gate.TryBeginExclusive(GpuWorkKind.EvaluationRun));
        first.Dispose();

        using var second = AssertEx.NotNull(gate.TryBeginExclusive(GpuWorkKind.TrainingRun));
        first.Dispose();

        AssertEx.Equal(GpuWorkKind.TrainingRun, gate.ExclusiveKind, "A stale handle must not release the current holder.");
    }

    /// <summary>
    ///     The invariant under contention, checked from inside every held window: an exclusive holder never coexists
    ///     with any other holder. Counters rather than timing — a violation is a wrong count, not a slow test.
    /// </summary>
    [Test]
    public async Task Gate_UnderContention_AnExclusiveHolderNeverCoexistsWithAnother()
    {
        var gate = new GpuWorkGate();
        var exclusiveHolders = 0;
        var sharedHolders = 0;
        var violations = 0;
        var kinds = Enum.GetValues<GpuWorkKind>();

        var workers = Enumerable.Range(0, 8).Select(seed => Task.Run(() =>
        {
            var random = new Random(seed);
            for (var iteration = 0; iteration < 2000; iteration++)
            {
                var kind = kinds[random.Next(kinds.Length)];
                var exclusive = random.Next(2) == 0;
                using var held = exclusive ? gate.TryBeginExclusive(kind) : gate.TryBeginShared(kind);
                if (held is null)
                {
                    continue;
                }

                if (exclusive)
                {
                    _ = Interlocked.Increment(ref exclusiveHolders);
                    if (Volatile.Read(ref exclusiveHolders) != 1 || Volatile.Read(ref sharedHolders) != 0)
                    {
                        _ = Interlocked.Increment(ref violations);
                    }

                    _ = Interlocked.Decrement(ref exclusiveHolders);
                }
                else
                {
                    _ = Interlocked.Increment(ref sharedHolders);
                    if (Volatile.Read(ref exclusiveHolders) != 0)
                    {
                        _ = Interlocked.Increment(ref violations);
                    }

                    _ = Interlocked.Decrement(ref sharedHolders);
                }
            }
        })).ToArray();

        await Task.WhenAll(workers);

        AssertEx.Equal(expected: 0, violations, "An exclusive holder coexisted with another holder.");
        AssertEx.Null(gate.ExclusiveKind, "Every hold was disposed, so the gate must be free.");
        using var free = AssertEx.NotNull(gate.TryBeginExclusive(GpuWorkKind.TrainingRun), "A drained gate must admit again.");
    }
}
