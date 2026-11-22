namespace XE_Local_AI_Engine.Tests.Knowledge;

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Knowledge.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Lifecycle-safety tests for <see cref="KnowledgeIngestionWorker" />: startup recovery re-dispatches documents left
///     non-terminal by a previous run, shutdown drains in-flight documents so a terminal write lands, a hung document is
///     abandoned within the bounded drain window rather than blocking shutdown, and a document that completes after the
///     worker is disposed does not fault on <see cref="ObjectDisposedException" />.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class KnowledgeIngestionWorkerTests
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(5);

    /// <summary>An ingestion that ignores cancellation and outruns any drain window the worker is configured with.</summary>
    private static readonly TimeSpan HungDocumentRuntime = TimeSpan.FromSeconds(10);

    /// <summary>Long enough to land after the 1 s drain window and the disposal that follows it, short enough to observe.</summary>
    private static readonly TimeSpan PostDisposalDocumentRuntime = TimeSpan.FromMilliseconds(1500);

    [Test]
    public async Task ExecuteAsync_OnStart_ReDispatchesNonTerminalDocumentsFromAPreviousRun()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var catalog = Substitute.For<IKnowledgeDocumentCatalogService>();
        catalog.ResetNonTerminalToPendingAsync(Arg.Any<CancellationToken>())
               .Returns((IReadOnlyList<Guid>)[first, second]);
        var ingestion = new FakeIngestionService((_, _) => Task.CompletedTask);
        var dispatcher = new KnowledgeIngestionDispatcher(new NullKnowledgeIndexingNotifier());
        await using var provider = BuildProvider(ingestion, catalog);
        using var worker = CreateWorker(provider, dispatcher);

        await worker.StartAsync(CancellationToken.None);

        // Recovery resets the interrupted rows to Pending, re-dispatches their ids, and the worker drains them — so the
        // ingestion state machine runs for BOTH documents without any new upload.
        await AssertEx.EventuallyAsync(() => ingestion.Started.Count == 2,
            PollTimeout,
            "Startup recovery should have re-dispatched both interrupted documents.");
        AssertEx.Contains(ingestion.Started, first);
        AssertEx.Contains(ingestion.Started, second);

        await worker.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task StopAsync_WaitsForAnInFlightDocument_AndItsTerminalWriteLands()
    {
        var documentId = Guid.NewGuid();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ingestion = new FakeIngestionService((_, _) => gate.Task);
        var catalog = NoRecovery();
        var dispatcher = new KnowledgeIngestionDispatcher(new NullKnowledgeIndexingNotifier());
        // A generous drain window so the wait is decided by the document finishing, not by the timeout.
        await using var provider = BuildProvider(ingestion, catalog);
        using var worker = CreateWorker(provider, dispatcher, drainTimeoutSeconds: 30);

        await worker.StartAsync(CancellationToken.None);
        await dispatcher.EnqueueAsync(documentId, CancellationToken.None);
        await AssertEx.EventuallyAsync(() => !ingestion.Started.IsEmpty,
            PollTimeout,
            "The worker should have started ingesting the enqueued document.");

        // Begin shutdown while the document is still mid-run (blocked on the gate). StopAsync must not complete yet.
        var stop = worker.StopAsync(CancellationToken.None);
        await AssertEx.StaysIncompleteAsync(stop, "StopAsync must wait for the in-flight document to finish draining.");
        AssertEx.True(ingestion.Completed.IsEmpty, "The document must not have completed while still gated.");

        // Release the document; StopAsync completes only after its terminal write lands.
        gate.SetResult();
        await stop;
        AssertEx.Contains(ingestion.Completed, documentId);
    }

    [Test]
    public async Task StopAsync_WhenADocumentHangsPastTheDrainWindow_AbandonsItWithoutThrowingOrBlocking()
    {
        // real-timer: the hung document IS the subject's input — an ingestion that ignores cancellation and outlives
        // the drain window. Nothing in IKnowledgeIngestionService exposes a "still running" seam the worker could be
        // driven against, and the assertion below is on the worker's elapsed drain, not on this duration.
        var ingestion = new FakeIngestionService((_, _) => Task.Delay(HungDocumentRuntime, CancellationToken.None));
        var catalog = NoRecovery();
        var dispatcher = new KnowledgeIngestionDispatcher(new NullKnowledgeIndexingNotifier());
        await using var provider = BuildProvider(ingestion, catalog);
        using var worker = CreateWorker(provider, dispatcher, drainTimeoutSeconds: 1);

        await worker.StartAsync(CancellationToken.None);
        await dispatcher.EnqueueAsync(Guid.NewGuid(), CancellationToken.None);
        await AssertEx.EventuallyAsync(() => !ingestion.Started.IsEmpty,
            PollTimeout,
            "The worker should have started ingesting the enqueued document.");

        // The document ignores cancellation and would run for 10s; StopAsync must return within the ~1s drain window and
        // must not throw, abandoning the hung document (it stays non-terminal and recovers on the next start).
        var stopwatch = Stopwatch.StartNew();
        await worker.StopAsync(CancellationToken.None);
        stopwatch.Stop();

        AssertEx.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"StopAsync should have abandoned the hung document within the drain window, but took {stopwatch.Elapsed.TotalSeconds:F1}s.");
        AssertEx.True(ingestion.Completed.IsEmpty, "The hung document must not have completed within the drain window.");
    }

    [Test]
    public async Task ShutdownRace_WhenADocumentCompletesAfterDisposal_DoesNotFaultOnSemaphoreObjectDisposed()
    {
        var captured = new ConcurrentBag<Exception>();

        void Handler(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            foreach (var inner in args.Exception.Flatten().InnerExceptions)
            {
                captured.Add(inner);
            }

            args.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            // The document ignores cancellation and finishes ~500ms after the 1s drain window elapses — i.e. after the
            // worker is disposed — so its finally releases the concurrency slot on a disposed worker, and the background task
            // must complete cleanly instead of faulting.
            // real-timer: as above, the document's runtime is the input — it has to outlast the 1 s drain window so its
            // finally runs after disposal. A gate the test releases would not reproduce "completes after we stopped looking".
            var ingestion = new FakeIngestionService((_, _) => Task.Delay(PostDisposalDocumentRuntime, CancellationToken.None));
            var catalog = NoRecovery();
            var dispatcher = new KnowledgeIngestionDispatcher(new NullKnowledgeIndexingNotifier());
            await using (var provider = BuildProvider(ingestion, catalog))
            {
                var worker = CreateWorker(provider, dispatcher, drainTimeoutSeconds: 1);
                try
                {
                    await worker.StartAsync(CancellationToken.None);
                    await dispatcher.EnqueueAsync(Guid.NewGuid(), CancellationToken.None);
                    await AssertEx.EventuallyAsync(() => !ingestion.Started.IsEmpty,
                        PollTimeout,
                        "The worker should have started ingesting the enqueued document.");

                    await worker.StopAsync(CancellationToken.None);
                }
                finally
                {
                    worker.Dispose();
                }

                // Let the abandoned document finish so its finally runs Release() on the now-disposed worker.
                await AssertEx.EventuallyAsync(() => !ingestion.Completed.IsEmpty,
                    PollTimeout,
                    "The abandoned document should have finished after disposal.");

                // The guarded Release runs in the abandoned task's finally, one continuation after the completion above.
                await AssertEx.SettleAsync();
            }

#pragma warning disable S1215 // Deterministic finalizer flush is the only way to surface UnobservedTaskException in-test.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
#pragma warning restore S1215

            AssertEx.Empty(captured.Where(exception => exception is ObjectDisposedException),
                "Releasing the slot on a post-disposal document completion must not fault the background task.");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }
    }

    [Test]
    public async Task DrainSweep_AdmitsAndIngestsStrandedPendingDocumentsOnceTheQueueEmpties()
    {
        var strandedA = Guid.NewGuid();
        var strandedB = Guid.NewGuid();
        var trigger = Guid.NewGuid();

        var ingestion = new FakeIngestionService((_, _) => Task.CompletedTask);
        var catalog = Substitute.For<IKnowledgeDocumentCatalogService>();
        catalog.ResetNonTerminalToPendingAsync(Arg.Any<CancellationToken>()).Returns((IReadOnlyList<Guid>)[]);
        // The two stranded documents (persisted but never admitted — the full-queue 503 path) stay Pending until ingestion
        // starts, which flips them out of Pending; the sweep source stops returning a document once the worker begins it,
        // mirroring the real status transition and letting the sweep terminate.
        catalog.ListPendingDocumentIdsAsync(Arg.Any<CancellationToken>())
               .Returns(_ => (IReadOnlyList<Guid>)new[]
               {
                   strandedA,
                   strandedB
               }.Where(id => !ingestion.Started.Contains(id)).ToList());

        var dispatcher = new KnowledgeIngestionDispatcher(new NullKnowledgeIndexingNotifier());
        await using var provider = BuildProvider(ingestion, catalog);
        using var worker = CreateWorker(provider, dispatcher, drainTimeoutSeconds: 30);

        await worker.StartAsync(CancellationToken.None);
        // One normal document primes the pump; its completion drains the queue to empty and fires the sweep that admits the
        // two stranded documents the full-queue path never enqueued — the "drain → enqueued → ingested" recovery.
        _ = await dispatcher.EnqueueAsync(trigger, CancellationToken.None);

        await AssertEx.EventuallyAsync(() => ingestion.Completed.Contains(trigger) && ingestion.Completed.Contains(strandedA) && ingestion.Completed.Contains(strandedB),
            PollTimeout,
            "The drain-sweep should have admitted and ingested both stranded Pending documents.");

        // FIFO with one slot: a follow-up run the sweep scheduled for any of them would run before this barrier does.
        var barrier = Guid.NewGuid();
        _ = await dispatcher.EnqueueAsync(barrier, CancellationToken.None);
        await AssertEx.EventuallyAsync(() => ingestion.Completed.Contains(barrier), PollTimeout, "the barrier document ran");
        foreach (var documentId in new[]
                 {
                     trigger,
                     strandedA,
                     strandedB
                 })
        {
            AssertEx.Equal(expected: 1, ingestion.Started.Count(id => id == documentId), "each document is ingested exactly once");
        }

        await worker.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task IngestionWorker_TwoQueuedDocs_EachStartedExactlyOnce()
    {
        // The sweep merged into a document already dequeued and waiting for the slot, so that document ran a second time.
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leftPending = new ConcurrentBag<Guid>();
        // A row leaves Pending only once its run writes Extracting; the second run waits until the sweep has listed it, the
        // window in which the real sweep reads the row while that document is dequeued but not yet started.
        var ingestion = new FakeIngestionService(async (id, _) =>
        {
            if (id == first)
            {
                await release.Task;
            }
            else if (id == second)
            {
                await listed.Task;
            }

            leftPending.Add(id);
        });
        // The sweep's scope is disposed only after its admit loop, so releasing the second run there (not at the listing)
        // means the admit always sees that document still in flight, as production does.
        var catalog = Substitute.For<IKnowledgeDocumentCatalogService>();
        var sweptSecond = 0;
        catalog.ResetNonTerminalToPendingAsync(Arg.Any<CancellationToken>()).Returns((IReadOnlyList<Guid>)[]);
        catalog.ListPendingDocumentIdsAsync(Arg.Any<CancellationToken>())
               .Returns(_ =>
               {
                   IReadOnlyList<Guid> pending =
                   [
                       .. new[]
                       {
                           first,
                           second
                       }.Where(id => !leftPending.Contains(id))
                   ];
                   if (pending.Contains(second))
                   {
                       Volatile.Write(ref sweptSecond, 1);
                   }

                   return pending;
               });
        var dispatcher = new KnowledgeIngestionDispatcher(new NullKnowledgeIndexingNotifier());
        var services = new ServiceCollection();
        services.AddScoped<IKnowledgeIngestionService>(_ => ingestion);
        services.AddScoped(_ => new ScopeEndProbe(() =>
        {
            if (Volatile.Read(ref sweptSecond) == 1)
            {
                listed.TrySetResult();
            }
        }));
        services.AddScoped(scoped =>
        {
            _ = scoped.GetRequiredService<ScopeEndProbe>();
            return catalog;
        });
        await using var provider = services.BuildServiceProvider();
        using var worker = CreateWorker(provider, dispatcher);
        await worker.StartAsync(CancellationToken.None);

        _ = await dispatcher.EnqueueAsync(first, CancellationToken.None);
        _ = await dispatcher.EnqueueAsync(second, CancellationToken.None);
        await AssertEx.EventuallyAsync(() => ingestion.Started.Contains(first) && dispatcher.PendingCount == 0, PollTimeout,
            "the second document is dequeued and waiting for the slot, still Pending");
        release.SetResult();
        await AssertEx.EventuallyAsync(() => ingestion.Completed.Contains(second), PollTimeout, "the second document ran");
        var barrier = Guid.NewGuid();
        _ = await dispatcher.EnqueueAsync(barrier, CancellationToken.None);
        await AssertEx.EventuallyAsync(() => ingestion.Completed.Contains(barrier), PollTimeout, "the barrier document ran");

        AssertEx.Equal(expected: 1, ingestion.Started.Count(id => id == first));
        AssertEx.Equal(expected: 1, ingestion.Started.Count(id => id == second), "a sweep must not schedule a second run of a dequeued document");
        await worker.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task Wake_WhileTheEmbedderStaysEjected_DoesNoDocumentWorkAndDoublesOneArmedWindowToTheCap()
    {
        var time = new ManualTimeProvider();
        var checks = 0;
        var embedder = Substitute.For<IKnowledgeChunkEmbedder>();
        embedder.IsEmbeddingModelBlockedAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Interlocked.Increment(ref checks);
            return Task.FromResult(true);
        });
        var ingestion = new FakeIngestionService((_, _) => Task.CompletedTask);
        var catalog = NoRecovery();
        await using var provider = BuildProvider(ingestion, catalog);
        using var worker = CreateWorker(provider, new KnowledgeIngestionDispatcher(new NullKnowledgeIndexingNotifier()), embedder: embedder, timeProvider: time);
        await worker.StartAsync(CancellationToken.None);

        var expectedChecks = 0;
        foreach (var seconds in new[]
                 {
                     60,
                     120,
                     240,
                     300,
                     300
                 })
        {
            await AssertEx.EventuallyAsync(() => Volatile.Read(ref checks) == expectedChecks && time.ArmedTimerCount == 1, PollTimeout, "exactly one wake is armed");
            time.Advance(TimeSpan.FromSeconds(seconds - 1));

            // An early fire either leaves no timer armed yet or has already checked again: both conditions catch it.
            AssertEx.Equal(1, time.ArmedTimerCount, $"the {seconds} s window must not have elapsed one second early");
            AssertEx.Equal(expectedChecks, Volatile.Read(ref checks), $"the {seconds} s window must not have checked one second early");
            time.Advance(TimeSpan.FromSeconds(1));
            expectedChecks++;
        }

        await AssertEx.EventuallyAsync(() => Volatile.Read(ref checks) == expectedChecks && time.ArmedTimerCount == 1, PollTimeout, "the capped window re-arms");
        AssertEx.Empty(ingestion.Started);
        await catalog.DidNotReceive().ClearEmbeddingModelWaitAsync(Arg.Any<CancellationToken>());
        await catalog.DidNotReceive().ListPendingDocumentIdsAsync(Arg.Any<CancellationToken>());
        await worker.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task Wake_OnceTheEmbedderIsBack_ClearsTheWaitRefreshesOnceAndSweepsTheWaitingRowIn()
    {
        var time = new ManualTimeProvider();
        var waiting = Guid.NewGuid();
        var notifier = Substitute.For<IKnowledgeIndexingNotifier>();
        var embedder = Substitute.For<IKnowledgeChunkEmbedder>();
        embedder.IsEmbeddingModelBlockedAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(true), Task.FromResult(false));
        var runs = 0;
        var ingestion = new FakeIngestionService((_, _) => Task.CompletedTask)
        {
            Outcome = id => id == waiting && Interlocked.Increment(ref runs) == 1 ? KnowledgeIngestionRunOutcome.Deferred : KnowledgeIngestionRunOutcome.Completed
        };
        var catalog = WaitingRowCatalog(ingestion, waiting);
        var dispatcher = new KnowledgeIngestionDispatcher(new NullKnowledgeIndexingNotifier());
        await using var provider = BuildProvider(ingestion, catalog);
        using var worker = CreateWorker(provider, dispatcher, embedder: embedder, timeProvider: time, notifier: notifier);
        await worker.StartAsync(CancellationToken.None);

        _ = await dispatcher.EnqueueAsync(waiting, CancellationToken.None);
        await AssertEx.EventuallyAsync(() => ingestion.Completed.Contains(waiting) && time.ArmedTimerCount == 1, PollTimeout, "the deferred row leaves one wake armed");

        time.Advance(KnowledgeIngestionWorker.FirstWakeWindow);
        await AssertEx.EventuallyAsync(() => embedder.ReceivedCalls().Count() == 1 && time.ArmedTimerCount == 1, PollTimeout, "still blocked: the next window is armed");
        AssertEx.Equal(1, ingestion.Started.Count(id => id == waiting), "a blocked wake does no document work");
        await notifier.DidNotReceiveWithAnyArgs().NotifyDocumentsChangedAsync(default);

        time.Advance(KnowledgeIngestionWorker.FirstWakeWindow * 2);
        await AssertEx.EventuallyAsync(() => ingestion.Started.Count(id => id == waiting) == 2, PollTimeout, "the unblocked wake sweeps the cleared row in");
        await worker.StopAsync(CancellationToken.None);
        await catalog.Received(1).ClearEmbeddingModelWaitAsync(Arg.Any<CancellationToken>());
        await notifier.Received(1).NotifyDocumentsChangedAsync(Arg.Any<CancellationToken>());
        await notifier.DidNotReceiveWithAnyArgs().NotifyDocumentChangedAsync(Guid.Empty, default, default);
    }

    [Test]
    public async Task Wake_WhenAClearedRowWasIndexedMeanwhile_RefreshesOnceAndPublishesNoPendingForIt()
    {
        var time = new ManualTimeProvider();
        var indexed = Guid.NewGuid();
        var notifier = Substitute.For<IKnowledgeIndexingNotifier>();
        var logger = new RecordingLogger<KnowledgeIngestionWorker>();
        var ingestion = new FakeIngestionService((_, _) => Task.CompletedTask);
        var catalog = NoRecovery();
        catalog.ClearEmbeddingModelWaitAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Guid>>([indexed]));

        // The row left Pending between the clear and the sweep, so a Pending push for it would stick on clients.
        catalog.ListPendingDocumentIdsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Guid>>([]));
        await using var provider = BuildProvider(ingestion, catalog);
        using var worker = CreateWorker(provider, new KnowledgeIngestionDispatcher(notifier), timeProvider: time, notifier: notifier, logger: logger);
        await worker.StartAsync(CancellationToken.None);

        await AssertEx.EventuallyAsync(() => time.ArmedTimerCount == 1, PollTimeout, "the startup wake is armed");
        time.Advance(KnowledgeIngestionWorker.FirstWakeWindow);
        await AssertEx.EventuallyAsync(() => catalog.ReceivedCalls().Any(call => call.GetMethodInfo().Name == nameof(IKnowledgeDocumentCatalogService.ListPendingDocumentIdsAsync)),
            PollTimeout,
            "the unblocked wake swept");
        await worker.StopAsync(CancellationToken.None);

        await notifier.DidNotReceiveWithAnyArgs().NotifyDocumentChangedAsync(Guid.Empty, default, default);
        await notifier.Received(1).NotifyDocumentsChangedAsync(Arg.Any<CancellationToken>());
        AssertEx.True(logger.HasEntry(LogLevel.Information, "cleared 1 embedding-wait flag"), "the wake logs how many wait flags it cleared");
        AssertEx.Empty(ingestion.Started);
    }

    [Test]
    public async Task Wake_WhenTheSweepFailsAfterTheClear_ReArmsAndRunsTheClearedRowAtTheNextWindow()
    {
        var time = new ManualTimeProvider();
        var cleared = Guid.NewGuid();
        var lists = 0;
        var ingestion = new FakeIngestionService((_, _) => Task.CompletedTask);
        var catalog = NoRecovery();
        catalog.ClearEmbeddingModelWaitAsync(Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<IReadOnlyList<Guid>>([cleared]), Task.FromResult<IReadOnlyList<Guid>>([]));

        // After the clear no row carries the waiting reason, so the retry wake must sweep without that reason to find.
        catalog.HasEmbeddingModelWaitAsync(Arg.Any<CancellationToken>()).Returns(true, false);

        // The first listing fails after the clear landed, so the row is plain Pending with no waiting reason left.
        catalog.ListPendingDocumentIdsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (Interlocked.Increment(ref lists) == 1)
            {
                return Task.FromException<IReadOnlyList<Guid>>(new InvalidOperationException("The database is not available."));
            }

            return Task.FromResult<IReadOnlyList<Guid>>(ingestion.Started.Contains(cleared) ? [] : [cleared]);
        });
        await using var provider = BuildProvider(ingestion, catalog);
        using var worker = CreateWorker(provider, new KnowledgeIngestionDispatcher(new NullKnowledgeIndexingNotifier()), timeProvider: time);
        await worker.StartAsync(CancellationToken.None);

        await AssertEx.EventuallyAsync(() => time.ArmedTimerCount == 1, PollTimeout, "the startup wake is armed");
        time.Advance(KnowledgeIngestionWorker.FirstWakeWindow);
        await AssertEx.EventuallyAsync(() => Volatile.Read(ref lists) == 1 && time.ArmedTimerCount == 1, PollTimeout, "a failed sweep re-arms the wake");
        AssertEx.Empty(ingestion.Started);

        time.Advance(KnowledgeIngestionWorker.FirstWakeWindow * 2);
        await AssertEx.EventuallyAsync(() => ingestion.Completed.Contains(cleared), PollTimeout, "the next wake sweeps the cleared row in");
        await worker.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task Wake_WhenNoRowWaits_NeverProbesStaysUnarmedAndTheNextDeferralArmsAgain()
    {
        var time = new ManualTimeProvider();
        var embedder = Substitute.For<IKnowledgeChunkEmbedder>();
        embedder.IsEmbeddingModelBlockedAsync(Arg.Any<CancellationToken>()).Returns(true);
        var ingestion = new FakeIngestionService((_, _) => Task.CompletedTask)
        {
            Outcome = _ => KnowledgeIngestionRunOutcome.Deferred
        };
        var catalog = NoRecovery();
        catalog.HasEmbeddingModelWaitAsync(Arg.Any<CancellationToken>()).Returns(false);
        var dispatcher = new KnowledgeIngestionDispatcher(new NullKnowledgeIndexingNotifier());
        await using var provider = BuildProvider(ingestion, catalog);
        using var worker = CreateWorker(provider, dispatcher, embedder: embedder, timeProvider: time);
        await worker.StartAsync(CancellationToken.None);

        await AssertEx.EventuallyAsync(() => time.ArmedTimerCount == 1, PollTimeout, "the startup wake is armed");
        time.Advance(KnowledgeIngestionWorker.FirstWakeWindow);
        await AssertEx.EventuallyAsync(() => catalog.ReceivedCalls().Any(call => call.GetMethodInfo().Name == nameof(IKnowledgeDocumentCatalogService.HasEmbeddingModelWaitAsync)),
            PollTimeout,
            "the wake asked whether any row waits");
        await AssertEx.SettleAsync();
        AssertEx.Equal(0, time.ArmedTimerCount, "a wake with nothing waiting must not re-arm");
        _ = await embedder.DidNotReceive().IsEmbeddingModelBlockedAsync(Arg.Any<CancellationToken>());

        _ = await dispatcher.EnqueueAsync(Guid.NewGuid(), CancellationToken.None);
        await AssertEx.EventuallyAsync(() => time.ArmedTimerCount == 1, PollTimeout, "a later deferral arms the wake again");
        await worker.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task StopAsync_WhileTheWakeSweepReadsTheCatalog_CancelsTheReadAndCompletesPromptly()
    {
        var time = new ManualTimeProvider();
        var listEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<IReadOnlyList<Guid>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = NoRecovery();
        catalog.ListPendingDocumentIdsAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            listEntered.TrySetResult();
            return never.Task.WaitAsync(call.Arg<CancellationToken>());
        });
        await using var provider = BuildProvider(new FakeIngestionService((_, _) => Task.CompletedTask), catalog);
        using var worker = CreateWorker(provider, new KnowledgeIngestionDispatcher(new NullKnowledgeIndexingNotifier()), timeProvider: time);
        await worker.StartAsync(CancellationToken.None);

        await AssertEx.EventuallyAsync(() => time.ArmedTimerCount == 1, PollTimeout, "the startup wake is armed");
        time.Advance(KnowledgeIngestionWorker.FirstWakeWindow);
        await listEntered.Task.WaitAsync(PollTimeout);

        await AssertEx.CompletesAsync(worker.StopAsync(CancellationToken.None), PollTimeout, "the stop cancels the wake's catalog read");
    }

    [Test]
    public async Task Dispose_WhileTheWakeLoopWaitsForItsNextSignal_EndsTheWakeLoop()
    {
        var time = new ManualTimeProvider();
        var catalog = NoRecovery();
        await using var provider = BuildProvider(new FakeIngestionService((_, _) => Task.CompletedTask), catalog);
        var worker = CreateWorker(provider, new KnowledgeIngestionDispatcher(new NullKnowledgeIndexingNotifier()), timeProvider: time);
        try
        {
            await worker.StartAsync(CancellationToken.None);
            await AssertEx.EventuallyAsync(() => time.ArmedTimerCount == 1, PollTimeout, "the startup wake is armed");
            time.Advance(KnowledgeIngestionWorker.FirstWakeWindow);

            // The unblocked wake sweeps, then the loop goes back to waiting for a signal nobody sends.
            await AssertEx.EventuallyAsync(() => catalog.ReceivedCalls().Any(call => call.GetMethodInfo().Name == nameof(IKnowledgeDocumentCatalogService.ListPendingDocumentIdsAsync)),
                PollTimeout,
                "the first wake swept");
            await AssertEx.SettleAsync();
        }
        finally
        {
            // A host whose stop timed out disposes the worker without StopAsync.
            worker.Dispose();
        }

        await AssertEx.CompletesAsync(AssertEx.NotNull(worker.ExecuteTask), PollTimeout, "the idle wake loop ends once the worker is disposed");
    }

    [Test]
    public async Task Dispose_WhileAWakeIsChecking_EndsTheWakeLoopWithoutFaulting()
    {
        var time = new ManualTimeProvider();
        var checkEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var embedder = Substitute.For<IKnowledgeChunkEmbedder>();
        embedder.IsEmbeddingModelBlockedAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            checkEntered.TrySetResult();
            return checkResult.Task;
        });
        await using var provider = BuildProvider(new FakeIngestionService((_, _) => Task.CompletedTask), NoRecovery());
        var worker = CreateWorker(provider, new KnowledgeIngestionDispatcher(new NullKnowledgeIndexingNotifier()), embedder: embedder, timeProvider: time);
        try
        {
            await worker.StartAsync(CancellationToken.None);
            await AssertEx.EventuallyAsync(() => time.ArmedTimerCount == 1, PollTimeout, "the startup wake is armed");
            time.Advance(KnowledgeIngestionWorker.FirstWakeWindow);
            await checkEntered.Task.WaitAsync(PollTimeout);
        }
        finally
        {
            // A host whose stop timed out disposes the worker while a wake is still out.
            worker.Dispose();
        }

        // The blocked answer would re-arm the wake on the disposed worker.
        checkResult.SetResult(true);

        await AssertEx.CompletesAsync(AssertEx.NotNull(worker.ExecuteTask), PollTimeout, "the wake loop ends once the worker is disposed");
    }

    [Test]
    public async Task Dispose_WhileTheReadLoopWaitsForAConcurrencySlot_EndsTheReadLoop()
    {
        var first = Guid.NewGuid();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ingestion = new FakeIngestionService((id, _) => id == first ? gate.Task : Task.CompletedTask);
        var dispatcher = new KnowledgeIngestionDispatcher(new NullKnowledgeIndexingNotifier());
        await using var provider = BuildProvider(ingestion, NoRecovery());
        var worker = CreateWorker(provider, dispatcher);
        try
        {
            await worker.StartAsync(CancellationToken.None);
            _ = await dispatcher.EnqueueAsync(first, CancellationToken.None);
            _ = await dispatcher.EnqueueAsync(Guid.NewGuid(), CancellationToken.None);

            // The only slot is held by the gated document, so the dequeued second one parks the read loop on the slot.
            await AssertEx.EventuallyAsync(() => ingestion.Started.Contains(first) && dispatcher.PendingCount == 0, PollTimeout,
                "the read loop dequeued the second document and waits for the slot");
        }
        finally
        {
            // A host whose stop timed out disposes the worker without StopAsync.
            worker.Dispose();
        }

        await AssertEx.CompletesAsync(AssertEx.NotNull(worker.ExecuteTask), PollTimeout, "the read loop ends once the worker is disposed");
        AssertEx.True(worker.ExecuteTask!.IsCompletedSuccessfully, "the read loop ends without faulting");
        gate.SetResult();
        await AssertEx.EventuallyAsync(() => ingestion.Completed.Contains(first), PollTimeout, "the gated document finished");
    }

    [Test]
    public async Task Wake_WhenTheCheckThrows_RetriesAtTheNextWindowAndThenSweeps()
    {
        var time = new ManualTimeProvider();
        var embedder = Substitute.For<IKnowledgeChunkEmbedder>();
        embedder.IsEmbeddingModelBlockedAsync(Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromException<bool>(new InvalidOperationException("The embedding provider is not registered.")), _ => Task.FromResult(false));
        var ingestion = new FakeIngestionService((_, _) => Task.CompletedTask);
        var catalog = NoRecovery();
        await using var provider = BuildProvider(ingestion, catalog);
        using var worker = CreateWorker(provider, new KnowledgeIngestionDispatcher(new NullKnowledgeIndexingNotifier()), embedder: embedder, timeProvider: time);
        await worker.StartAsync(CancellationToken.None);

        await AssertEx.EventuallyAsync(() => time.ArmedTimerCount == 1, PollTimeout, "the startup wake is armed");
        time.Advance(KnowledgeIngestionWorker.FirstWakeWindow);
        await AssertEx.EventuallyAsync(() => embedder.ReceivedCalls().Count() == 1 && time.ArmedTimerCount == 1, PollTimeout, "a failed check re-arms");
        await catalog.DidNotReceive().ClearEmbeddingModelWaitAsync(Arg.Any<CancellationToken>());

        time.Advance(KnowledgeIngestionWorker.FirstWakeWindow * 2);
        await AssertEx.EventuallyAsync(() => catalog.ReceivedCalls().Any(call => call.GetMethodInfo().Name == nameof(IKnowledgeDocumentCatalogService.ClearEmbeddingModelWaitAsync)),
            PollTimeout,
            "the next window clears the wait");
        await worker.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task Wake_WhenARowDefersInsideTheUnblockedBranch_ArmsASuccessor()
    {
        var time = new ManualTimeProvider();
        var deferred = Guid.NewGuid();
        var clearEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clearRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ingestion = new FakeIngestionService((_, _) => Task.CompletedTask)
        {
            Outcome = _ => KnowledgeIngestionRunOutcome.Deferred
        };
        var catalog = NoRecovery();
        catalog.ClearEmbeddingModelWaitAsync(Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            clearEntered.TrySetResult();
            await clearRelease.Task;
            return (IReadOnlyList<Guid>)[];
        });
        var notifier = Substitute.For<IKnowledgeIndexingNotifier>();
        var dispatcher = new KnowledgeIngestionDispatcher(new NullKnowledgeIndexingNotifier());
        await using var provider = BuildProvider(ingestion, catalog);
        using var worker = CreateWorker(provider, dispatcher, timeProvider: time, notifier: notifier);
        await worker.StartAsync(CancellationToken.None);

        await AssertEx.EventuallyAsync(() => time.ArmedTimerCount == 1, PollTimeout, "the startup wake is armed");
        time.Advance(KnowledgeIngestionWorker.FirstWakeWindow);
        await clearEntered.Task.WaitAsync(PollTimeout);

        // The wake is past its armed flag and inside the clear: this deferral must arm the next wake itself.
        _ = await dispatcher.EnqueueAsync(deferred, CancellationToken.None);
        await AssertEx.EventuallyAsync(() => ingestion.Completed.Contains(deferred), PollTimeout, "the deferring run finished");
        clearRelease.SetResult();

        await AssertEx.EventuallyAsync(() => time.ArmedTimerCount == 1, PollTimeout, "a waiting row always has an armed wake");
        await worker.StopAsync(CancellationToken.None);
        await notifier.DidNotReceiveWithAnyArgs().NotifyDocumentsChangedAsync(default);
    }

    [Test]
    public async Task Wake_AfterAFailedStartupRecovery_StillSweepsAPersistedWaitingRowIn()
    {
        var time = new ManualTimeProvider();
        var waiting = Guid.NewGuid();
        var ingestion = new FakeIngestionService((_, _) => Task.CompletedTask);
        var catalog = WaitingRowCatalog(ingestion, waiting);
        catalog.ResetNonTerminalToPendingAsync(Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromException<IReadOnlyList<Guid>>(new InvalidOperationException("The database is not available.")));
        await using var provider = BuildProvider(ingestion, catalog);
        using var worker = CreateWorker(provider, new KnowledgeIngestionDispatcher(new NullKnowledgeIndexingNotifier()), timeProvider: time);
        await worker.StartAsync(CancellationToken.None);

        await AssertEx.EventuallyAsync(() => time.ArmedTimerCount == 1, PollTimeout, "the startup wake is armed even after a failed recovery");
        time.Advance(KnowledgeIngestionWorker.FirstWakeWindow);

        await AssertEx.EventuallyAsync(() => ingestion.Completed.Contains(waiting), PollTimeout, "the persisted waiting row was swept in");
        await worker.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task Wake_ExplicitReEnqueueOfAWaitingRow_RunsWithoutWaitingForTheWake()
    {
        var time = new ManualTimeProvider();
        var waiting = Guid.NewGuid();
        var ingestion = new FakeIngestionService((_, _) => Task.CompletedTask)
        {
            Outcome = _ => KnowledgeIngestionRunOutcome.Deferred
        };
        var dispatcher = new KnowledgeIngestionDispatcher(new NullKnowledgeIndexingNotifier());
        await using var provider = BuildProvider(ingestion, NoRecovery());
        using var worker = CreateWorker(provider, dispatcher, timeProvider: time);
        await worker.StartAsync(CancellationToken.None);

        _ = await dispatcher.EnqueueAsync(waiting, CancellationToken.None);
        await AssertEx.EventuallyAsync(() => ingestion.Completed.Contains(waiting), PollTimeout, "the first run deferred");
        _ = await dispatcher.EnqueueAsync(waiting, CancellationToken.None);

        await AssertEx.EventuallyAsync(() => ingestion.Completed.Count(id => id == waiting) == 2, PollTimeout, "an upload or retry runs at once");
        AssertEx.Equal(1, time.ArmedTimerCount, "both deferrals share the one armed wake");
        await worker.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task StopAsync_WhenIdle_CompletesWithoutTheClockAndLeavesNothingArmed()
    {
        var time = new ManualTimeProvider();
        var embedder = Substitute.For<IKnowledgeChunkEmbedder>();
        await using var provider = BuildProvider(new FakeIngestionService((_, _) => Task.CompletedTask), NoRecovery());
        using var worker = CreateWorker(provider, new KnowledgeIngestionDispatcher(new NullKnowledgeIndexingNotifier()), embedder: embedder, timeProvider: time);
        await worker.StartAsync(CancellationToken.None);
        await AssertEx.EventuallyAsync(() => time.ArmedTimerCount == 1, PollTimeout, "the startup wake is armed");

        await worker.StopAsync(CancellationToken.None).WaitAsync(PollTimeout);

        AssertEx.Equal(0, time.ArmedTimerCount);
        _ = await embedder.DidNotReceive().IsEmbeddingModelBlockedAsync(Arg.Any<CancellationToken>());
    }

    // A catalog whose one waiting row is listed only once its wait was cleared, and only until its next run starts.
    private static IKnowledgeDocumentCatalogService WaitingRowCatalog(FakeIngestionService ingestion, Guid waiting)
    {
        var catalog = NoRecovery();
        var cleared = 0;
        var startsAtClear = 0;
        catalog.ClearEmbeddingModelWaitAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Volatile.Write(ref startsAtClear, ingestion.Started.Count(id => id == waiting));
            Volatile.Write(ref cleared, 1);
            return Task.FromResult<IReadOnlyList<Guid>>([waiting]);
        });
        catalog.ListPendingDocumentIdsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            Task.FromResult<IReadOnlyList<Guid>>(Volatile.Read(ref cleared) == 1 && ingestion.Started.Count(id => id == waiting) == Volatile.Read(ref startsAtClear)
                ? [waiting]
                : []));
        return catalog;
    }

    // Nothing to recover; a waiting row exists unless a test says otherwise, so every wake probes the embedder.
    private static IKnowledgeDocumentCatalogService NoRecovery()
    {
        var catalog = Substitute.For<IKnowledgeDocumentCatalogService>();
        catalog.ResetNonTerminalToPendingAsync(Arg.Any<CancellationToken>())
               .Returns((IReadOnlyList<Guid>)[]);
        catalog.HasEmbeddingModelWaitAsync(Arg.Any<CancellationToken>()).Returns(true);
        return catalog;
    }

    private static ServiceProvider BuildProvider(IKnowledgeIngestionService ingestion, IKnowledgeDocumentCatalogService catalog)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => ingestion);
        services.AddScoped(_ => catalog);
        return services.BuildServiceProvider();
    }

    private static KnowledgeIngestionWorker CreateWorker(ServiceProvider provider,
        KnowledgeIngestionDispatcher dispatcher,
        int drainTimeoutSeconds = 30,
        IKnowledgeChunkEmbedder? embedder = null,
        TimeProvider? timeProvider = null,
        IKnowledgeIndexingNotifier? notifier = null,
        ILogger<KnowledgeIngestionWorker>? logger = null)
    {
        var options = Options.Create(new KnowledgeBaseOptions
        {
            MaxConcurrentIngestions = 1,
            ShutdownDrainTimeoutSeconds = drainTimeoutSeconds
        });

        // A manual clock nobody advances keeps the startup wake armed and silent in every test that is not about it.
        return new KnowledgeIngestionWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            dispatcher,
            embedder ?? Substitute.For<IKnowledgeChunkEmbedder>(),
            notifier ?? new NullKnowledgeIndexingNotifier(),
            options,
            timeProvider ?? new ManualTimeProvider(),
            logger ?? NullLogger<KnowledgeIngestionWorker>.Instance);
    }

    /// <summary>Runs its callback when the DI scope that resolved it is disposed.</summary>
    private sealed class ScopeEndProbe : IDisposable
    {
        private readonly Action _onDispose;

        public ScopeEndProbe(Action onDispose)
        {
            _onDispose = onDispose;
        }

        public void Dispose()
        {
            _onDispose();
        }
    }

    private sealed class FakeIngestionService : IKnowledgeIngestionService
    {
        private readonly Func<Guid, CancellationToken, Task> _behavior;

        public FakeIngestionService(Func<Guid, CancellationToken, Task> behavior)
        {
            _behavior = behavior;
        }

        public ConcurrentBag<Guid> Started { get; } = [];

        public ConcurrentBag<Guid> Completed { get; } = [];

        /// <summary>How each run ends; every run completes unless a test defers it.</summary>
        public Func<Guid, KnowledgeIngestionRunOutcome> Outcome { get; init; } = static _ => KnowledgeIngestionRunOutcome.Completed;

        public async Task<KnowledgeIngestionRunOutcome> RunAsync(Guid documentId, CancellationToken cancellationToken)
        {
            Started.Add(documentId);
            await _behavior(documentId, cancellationToken);
            Completed.Add(documentId);
            return Outcome(documentId);
        }
    }
}
