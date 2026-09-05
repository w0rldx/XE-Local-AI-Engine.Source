namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the four invariants the supervisor's runtime gate depends on: shared holders run concurrently, an
///     exclusive holder excludes them in both directions, a pending exclusive acquire is not starved by later shared
///     acquires, and an abandoned (cancelled) exclusive acquire leaves the gate usable.
/// </summary>
public sealed class AsyncSharedExclusiveGateTests
{
    [Test]
    public async Task SharedHolders_DoNotExcludeEachOther()
    {
        using var gate = new AsyncSharedExclusiveGate();
        await gate.EnterSharedAsync(CancellationToken.None);

        // The point of the whole type: the first holder's (arbitrarily long) work does not gate the second.
        await gate.EnterSharedAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

        gate.ExitShared();
        gate.ExitShared();
    }

    [Test]
    public async Task ExclusiveAcquire_WaitsForSharedHoldersAlreadyInside()
    {
        using var gate = new AsyncSharedExclusiveGate();
        await gate.EnterSharedAsync(CancellationToken.None);

        var exclusive = gate.EnterExclusiveAsync(CancellationToken.None);
        await AssertEx.StaysIncompleteAsync(exclusive, "An exclusive acquire must not be admitted while a shared holder is inside.");

        gate.ExitShared();
        await exclusive.WaitAsync(TimeSpan.FromSeconds(3));
        gate.ExitExclusive();
    }

    [Test]
    public async Task SharedAcquire_WaitsWhileExclusiveIsHeld()
    {
        using var gate = new AsyncSharedExclusiveGate();
        await gate.EnterExclusiveAsync(CancellationToken.None);

        var shared = gate.EnterSharedAsync(CancellationToken.None);
        await AssertEx.StaysIncompleteAsync(shared, "No shared holder may be admitted while the exclusive holder runs.");

        gate.ExitExclusive();
        await shared.WaitAsync(TimeSpan.FromSeconds(3));
        gate.ExitShared();
    }

    [Test]
    public async Task PendingExclusiveAcquire_IsNotStarvedByLaterSharedAcquires()
    {
        using var gate = new AsyncSharedExclusiveGate();
        await gate.EnterSharedAsync(CancellationToken.None);
        var exclusive = gate.EnterExclusiveAsync(CancellationToken.None);
        await AssertEx.StaysIncompleteAsync(exclusive, "The exclusive acquire must be parked on the shared drain before the late shared acquire arrives.");

        // Queued behind the exclusive waiter. Without FIFO admission a steady stream of these (every inference request
        // takes the gate shared) would postpone an operator runtime mutation indefinitely.
        var lateShared = gate.EnterSharedAsync(CancellationToken.None);
        await AssertEx.StaysIncompleteAsync(lateShared, "A shared acquire arriving after an exclusive waiter must queue behind it.");

        gate.ExitShared();
        await exclusive.WaitAsync(TimeSpan.FromSeconds(3));
        AssertEx.False(lateShared.IsCompleted, "The late shared acquire must still wait for the exclusive holder to exit.");

        gate.ExitExclusive();
        await lateShared.WaitAsync(TimeSpan.FromSeconds(3));
        gate.ExitShared();
    }

    [Test]
    public async Task CancelledExclusiveAcquire_DoesNotLeaveTheGateClosed()
    {
        using var gate = new AsyncSharedExclusiveGate();
        await gate.EnterSharedAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var abandoned = gate.EnterExclusiveAsync(cancellation.Token);
        await AssertEx.StaysIncompleteAsync(abandoned, "The abandoned acquire must reach its drain wait before it is cancelled.");

        await cancellation.CancelAsync();
        await AssertEx.ThrowsAsync<OperationCanceledException>(() => abandoned);
        gate.ExitShared();

        // The abandoned acquire had already taken the underlying semaphore before parking on the drain wait; leaving it
        // taken would wedge every later acquire, shared and exclusive alike.
        await gate.EnterExclusiveAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        gate.ExitExclusive();
        await gate.EnterSharedAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        gate.ExitShared();
    }
}
