namespace XE_Local_AI_Engine.Tests.Chat;

using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The cancellation paths of the per-conversation chat persistence lock: a regression here hangs every read and
///     write of one conversation until restart, with no error.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class AsyncReaderWriterLockTests
{
    [Test]
    public async Task CancellingAQueuedWriter_ReleasesTheReadersQueuedBehindIt()
    {
        var sut = new AsyncReaderWriterLock();
        await sut.EnterReadAsync(CancellationToken.None);
        using var writerCancellation = new CancellationTokenSource();
        var writer = sut.EnterWriteAsync(writerCancellation.Token);
        var queuedReader = sut.EnterReadAsync(CancellationToken.None);
        await AssertEx.StaysIncompleteAsync(queuedReader, "Writer preference must queue a new reader behind the waiting writer.");

        await writerCancellation.CancelAsync();

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => writer);
        await AssertEx.CompletesAsync(queuedReader, TestBudgets.Contended, "The reader queued behind the cancelled writer was never released.");
        sut.ExitRead();
        sut.ExitRead();
        await AssertEx.CompletesAsync(sut.EnterWriteAsync(CancellationToken.None),
            TestBudgets.Contended,
            "The lock did not return to idle after the cancelled writer and both readers left.");
    }

    [Test]
    public async Task GrantRacingCancel_LeavesTheLockConsistent()
    {
        var sut = new AsyncReaderWriterLock();
        for (var round = 0; round < 200; round++)
        {
            await sut.EnterWriteAsync(CancellationToken.None);
            using var readerCancellation = new CancellationTokenSource();
            var reader = sut.EnterReadAsync(readerCancellation.Token);
            using var barrier = new Barrier(participantCount: 2);

            var releasing = Task.Run(() =>
            {
                barrier.SignalAndWait();
                sut.ExitWrite();
            });
            var cancelling = Task.Run(() =>
            {
                barrier.SignalAndWait();
                readerCancellation.Cancel();
            });
            await Task.WhenAll(releasing, cancelling);

            // Exactly one side wins: a granted reader holds the lock and must leave; a cancelled one never held it.
            try
            {
                await reader.WaitAsync(TestBudgets.Contended);
                sut.ExitRead();
            }
            catch (OperationCanceledException)
            {
                // The cancel won the race.
            }

            await AssertEx.CompletesAsync(sut.EnterWriteAsync(CancellationToken.None),
                TestBudgets.Contended,
                $"Round {round}: the lock leaked a grant or a waiter when the grant raced the cancellation.");
            sut.ExitWrite();
        }
    }
}
