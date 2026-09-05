namespace XE_Local_AI_Engine.Tests.Knowledge;

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Lifecycle-safety tests for <see cref="KnowledgeIngestionWorker" />: startup recovery re-dispatches documents left
///     non-terminal by a previous run, shutdown drains in-flight documents so a terminal write lands, a hung document is
///     abandoned within the bounded drain window rather than blocking shutdown, and a document that completes after the
///     semaphore is disposed does not fault on <see cref="ObjectDisposedException" />.
/// </summary>
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
        var dispatcher = new KnowledgeIngestionDispatcher();
        await using var provider = BuildProvider(ingestion, catalog);
        using var worker = CreateWorker(provider, dispatcher);

        await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);

        // Recovery resets the interrupted rows to Pending, re-dispatches their ids, and the worker drains them — so the
        // ingestion state machine runs for BOTH documents without any new upload.
        await AssertEx.EventuallyAsync(() => ingestion.Started.Count == 2,
            PollTimeout,
            "Startup recovery should have re-dispatched both interrupted documents.").ConfigureAwait(false);
        AssertEx.Contains(ingestion.Started, first);
        AssertEx.Contains(ingestion.Started, second);

        await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    [Test]
    public async Task StopAsync_WaitsForAnInFlightDocument_AndItsTerminalWriteLands()
    {
        var documentId = Guid.NewGuid();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ingestion = new FakeIngestionService((_, _) => gate.Task);
        var catalog = NoRecovery();
        var dispatcher = new KnowledgeIngestionDispatcher();
        // A generous drain window so the wait is decided by the document finishing, not by the timeout.
        await using var provider = BuildProvider(ingestion, catalog);
        using var worker = CreateWorker(provider, dispatcher, drainTimeoutSeconds: 30);

        await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await dispatcher.EnqueueAsync(documentId, CancellationToken.None).ConfigureAwait(false);
        await AssertEx.EventuallyAsync(() => !ingestion.Started.IsEmpty,
            PollTimeout,
            "The worker should have started ingesting the enqueued document.").ConfigureAwait(false);

        // Begin shutdown while the document is still mid-run (blocked on the gate). StopAsync must not complete yet.
        var stop = worker.StopAsync(CancellationToken.None);
        await AssertEx.StaysIncompleteAsync(stop, "StopAsync must wait for the in-flight document to finish draining.").ConfigureAwait(false);
        AssertEx.True(ingestion.Completed.IsEmpty, "The document must not have completed while still gated.");

        // Release the document; StopAsync completes only after its terminal write lands.
        gate.SetResult();
        await stop.ConfigureAwait(false);
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
        var dispatcher = new KnowledgeIngestionDispatcher();
        await using var provider = BuildProvider(ingestion, catalog);
        using var worker = CreateWorker(provider, dispatcher, drainTimeoutSeconds: 1);

        await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await dispatcher.EnqueueAsync(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false);
        await AssertEx.EventuallyAsync(() => !ingestion.Started.IsEmpty,
            PollTimeout,
            "The worker should have started ingesting the enqueued document.").ConfigureAwait(false);

        // The document ignores cancellation and would run for 10s; StopAsync must return within the ~1s drain window and
        // must not throw, abandoning the hung document (it stays non-terminal and recovers on the next start).
        var stopwatch = Stopwatch.StartNew();
        await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
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
            // worker is disposed — so its finally releases an already-disposed semaphore. The guarded Release must swallow
            // the ObjectDisposedException so the background task completes cleanly instead of faulting.
            // real-timer: as above, the document's runtime is the input — it has to outlast the 1 s drain window so its
            // finally runs after disposal. A gate the test releases would not reproduce "completes after we stopped looking".
            var ingestion = new FakeIngestionService((_, _) => Task.Delay(PostDisposalDocumentRuntime, CancellationToken.None));
            var catalog = NoRecovery();
            var dispatcher = new KnowledgeIngestionDispatcher();
            await using (var provider = BuildProvider(ingestion, catalog))
            {
                var worker = CreateWorker(provider, dispatcher, drainTimeoutSeconds: 1);
                try
                {
                    await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
                    await dispatcher.EnqueueAsync(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false);
                    await AssertEx.EventuallyAsync(() => !ingestion.Started.IsEmpty,
                        PollTimeout,
                        "The worker should have started ingesting the enqueued document.").ConfigureAwait(false);

                    await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    worker.Dispose();
                }

                // Let the abandoned document finish so its finally runs Release() on the now-disposed semaphore.
                await AssertEx.EventuallyAsync(() => !ingestion.Completed.IsEmpty,
                    PollTimeout,
                    "The abandoned document should have finished after disposal.").ConfigureAwait(false);

                // The guarded Release runs in the abandoned task's finally, one continuation after the completion above.
                await AssertEx.SettleAsync().ConfigureAwait(false);
            }

#pragma warning disable S1215 // Deterministic finalizer flush is the only way to surface UnobservedTaskException in-test.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
#pragma warning restore S1215

            AssertEx.Empty(captured.Where(exception => exception is ObjectDisposedException),
                "Releasing the disposed semaphore on a post-disposal document completion must not fault the background task.");
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

        var dispatcher = new KnowledgeIngestionDispatcher();
        await using var provider = BuildProvider(ingestion, catalog);
        using var worker = CreateWorker(provider, dispatcher, drainTimeoutSeconds: 30);

        await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        // One normal document primes the pump; its completion drains the queue to empty and fires the sweep that admits the
        // two stranded documents the full-queue path never enqueued — the "drain → enqueued → ingested" recovery.
        _ = await dispatcher.EnqueueAsync(trigger, CancellationToken.None).ConfigureAwait(false);

        await AssertEx.EventuallyAsync(() => ingestion.Completed.Contains(trigger) && ingestion.Completed.Contains(strandedA) && ingestion.Completed.Contains(strandedB),
            PollTimeout,
            "The drain-sweep should have admitted and ingested both stranded Pending documents.").ConfigureAwait(false);

        await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static IKnowledgeDocumentCatalogService NoRecovery()
    {
        var catalog = Substitute.For<IKnowledgeDocumentCatalogService>();
        catalog.ResetNonTerminalToPendingAsync(Arg.Any<CancellationToken>())
               .Returns((IReadOnlyList<Guid>)[]);
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
        int drainTimeoutSeconds = 30)
    {
        var options = Options.Create(new KnowledgeBaseOptions
        {
            MaxConcurrentIngestions = 1,
            ShutdownDrainTimeoutSeconds = drainTimeoutSeconds
        });
        return new KnowledgeIngestionWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            dispatcher,
            options,
            NullLogger<KnowledgeIngestionWorker>.Instance);
    }

    private sealed class FakeIngestionService(Func<Guid, CancellationToken, Task> behavior) : IKnowledgeIngestionService
    {
        public ConcurrentBag<Guid> Started { get; } = [];

        public ConcurrentBag<Guid> Completed { get; } = [];

        public async Task RunAsync(Guid documentId, CancellationToken cancellationToken)
        {
            Started.Add(documentId);
            await behavior(documentId, cancellationToken).ConfigureAwait(false);
            Completed.Add(documentId);
        }
    }
}
