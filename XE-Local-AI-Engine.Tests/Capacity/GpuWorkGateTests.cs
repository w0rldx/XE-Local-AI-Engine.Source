namespace XE_Local_AI_Engine.Tests.Capacity;

using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Capacity.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The node's GPU admission gate. Exclusive work owns the whole node; shared work coexists with shared work and
///     with nothing else.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class GpuWorkGateTests
{
    [Test]
    public void Gate_ExclusiveIsRefusedWhileSharedIsHeld_AndTheReverse()
    {
        var gate = new GpuWorkGate();

        AssertEx.Succeeded(gate.TryBeginShared(GpuWorkKind.Benchmark, out var shared), "A free gate admits shared work.");
        using (shared)
        {
            AssertEx.False(gate.TryBeginExclusive(GpuWorkKind.TrainingRun, out _), "A run must not admit beside executing benchmark work.");
            AssertEx.Null(gate.ExclusiveKind, "A refused exclusive acquisition must not record a holder.");
        }

        AssertEx.Succeeded(gate.TryBeginExclusive(GpuWorkKind.TrainingRun, out var exclusive), "Releasing the shared hold frees the gate.");
        using var exclusiveScope = exclusive;
        AssertEx.Equal(GpuWorkKind.TrainingRun, gate.ExclusiveKind);
        AssertEx.False(gate.TryBeginShared(GpuWorkKind.ImageJob, out _), "Shared work must not admit beside an exclusive holder.");
        AssertEx.False(gate.TryBeginExclusive(GpuWorkKind.Export, out _), "Nor may a second exclusive holder.");
    }

    [Test]
    public void Gate_SharedHoldersCoexist_AndTheLastReleaseFreesTheGate()
    {
        var gate = new GpuWorkGate();

        AssertEx.Succeeded(gate.TryBeginShared(GpuWorkKind.Benchmark, out var benchmark));
        AssertEx.Succeeded(gate.TryBeginShared(GpuWorkKind.DatasetGeneration, out var generation));
        AssertEx.Succeeded(gate.TryBeginShared(GpuWorkKind.ImageJob, out var image));

        benchmark.Dispose();
        generation.Dispose();
        AssertEx.False(gate.TryBeginExclusive(GpuWorkKind.Export, out _), "One shared holder is still enough to refuse an export.");

        image.Dispose();
        image.Dispose();
        AssertEx.Succeeded(gate.TryBeginExclusive(GpuWorkKind.Export, out var exclusive), "The last release frees the gate.");
        using var exclusiveScope = exclusive;
        AssertEx.Equal(GpuWorkKind.Export, gate.ExclusiveKind);
    }

    [Test]
    public void Gate_DisposingAnExclusiveHandleTwiceDoesNotReleaseSomebodyElsesHold()
    {
        var gate = new GpuWorkGate();
        AssertEx.Succeeded(gate.TryBeginExclusive(GpuWorkKind.EvaluationRun, out var first));
        first.Dispose();

        AssertEx.Succeeded(gate.TryBeginExclusive(GpuWorkKind.TrainingRun, out var second));
        using var secondScope = second;
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
                if (!(exclusive ? gate.TryBeginExclusive(kind, out var held) : gate.TryBeginShared(kind, out held)))
                {
                    continue;
                }

                using var heldScope = held;

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
        AssertEx.Succeeded(gate.TryBeginExclusive(GpuWorkKind.TrainingRun, out var free), "A drained gate must admit again.");
        using var freeScope = free;
    }
}
