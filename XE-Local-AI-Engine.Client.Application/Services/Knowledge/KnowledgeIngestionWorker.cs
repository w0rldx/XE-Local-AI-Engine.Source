namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

/// <summary>
///     Background worker that drains the <see cref="KnowledgeIngestionDispatcher" /> queue and runs the ingestion state
///     machine per document, bounded to <see cref="KnowledgeBaseOptions.MaxConcurrentIngestions" /> concurrent documents
///     by a <see cref="SemaphoreSlim" /> (M2 — so N uploads cannot spin up N unbounded embedding pipelines). Each document
///     runs in its own <c>CreateAsyncScope()</c>, mirroring the memory extraction dispatcher: a completed document's index
///     write is never lost to a client-side cancellation.
///     <para>
///         Lifecycle safety. (1) STARTUP RECOVERY — the queue is an in-memory channel, so a document left mid-pipeline by
///         a crash or hard stop would be stuck non-terminal forever; on start the worker resets every non-terminal document
///         to Pending and re-dispatches it before draining new work. (2) SHUTDOWN AWARENESS — in-flight document tasks are
///         tracked; <see cref="StopAsync" /> stops reading the queue and then awaits them within a bounded drain window
///         (<see cref="KnowledgeBaseOptions.ShutdownDrainTimeoutSeconds" />) before disposal proceeds, so the scope factory
///         and the semaphore are never disposed under a running document. Per-document work runs on
///         <see cref="_drainDeadline" />, a token cancelled ONLY when that window elapses — so ordinary operation never
///         cancels a document mid-write, yet a hung document cannot block shutdown forever.
///     </para>
///     Failures are handled inside the state machine; the worker's own catch-all logs the exception type only.
/// </summary>
public sealed class KnowledgeIngestionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly KnowledgeIngestionDispatcher _dispatcher;
    private readonly SemaphoreSlim _concurrency;
    private readonly TimeSpan _drainTimeout;
    private readonly ILogger<KnowledgeIngestionWorker> _logger;

    // Every in-flight ProcessDocumentAsync task, so shutdown can await them before the scope factory and the semaphore are
    // disposed. Each task removes itself on completion via a synchronous continuation, so the set only holds running work.
    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();

    // Cancelled ONLY when the shutdown drain window elapses. Per-document work runs on this token: during normal operation
    // it is never cancelled (a completed document's index write is never lost), but a document that outlives the drain
    // window is cancelled so it stops and disposal can proceed.
    private readonly CancellationTokenSource _drainDeadline = new();

    public KnowledgeIngestionWorker(IServiceScopeFactory scopeFactory,
        KnowledgeIngestionDispatcher dispatcher,
        IOptions<KnowledgeBaseOptions> options,
        ILogger<KnowledgeIngestionWorker> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var maxConcurrency = Math.Max(1, options.Value.MaxConcurrentIngestions);
        _concurrency = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _drainTimeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.ShutdownDrainTimeoutSeconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Startup recovery first: re-dispatch any document a previous run left non-terminal (its queue entry was lost with
        // the in-memory channel) before draining new uploads. A failure here must never take the worker down.
        await RecoverInterruptedDocumentsAsync(stoppingToken).ConfigureAwait(false);

        try
        {
            await foreach (var documentId in _dispatcher.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                // Gate on the concurrency budget before starting the next document so at most MaxConcurrentIngestions run.
                await _concurrency.WaitAsync(stoppingToken).ConfigureAwait(false);
                TrackInFlight(ProcessDocumentAsync(documentId));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown requested: stop reading the queue. StopAsync awaits the in-flight documents before disposal.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // base.StopAsync cancels the stopping token and awaits ExecuteAsync's return, so the queue read stops and no new
        // documents are launched. Only then do we drain the documents already in flight.
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await DrainInFlightAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RecoverInterruptedDocumentsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<IKnowledgeDocumentCatalogService>();
            var interrupted = await catalog.ResetNonTerminalToPendingAsync(cancellationToken).ConfigureAwait(false);
            if (interrupted.Count == 0)
            {
                return;
            }

            foreach (var documentId in interrupted)
            {
                await _dispatcher.EnqueueAsync(documentId, cancellationToken).ConfigureAwait(false);
            }

            // Content-free count only — never any document identity or name.
            _logger.LogInformation("Re-queued {InterruptedCount} interrupted ingestion(s) from a previous run.", interrupted.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down during startup recovery — the documents stay non-terminal and recover on the next start.
        }
        catch (Exception exception)
        {
            // A recovery failure (e.g. a transient database open) must never take the worker down; new uploads must still
            // ingest, and the interrupted documents recover on a later start. Log the exception type only.
            _logger.LogWarning("Knowledge ingestion startup recovery failed ({ErrorClass}); interrupted documents remain for the next start.", exception.GetType().Name);
        }
    }

    private async Task ProcessDocumentAsync(Guid documentId)
    {
        try
        {
            // Fresh scope + fresh DbContext per document. The drain-deadline token is uncancelled during normal operation
            // (so a shutdown/cancel never loses a completed write) and is cancelled only if the shutdown drain window is
            // exceeded, so a hung document does not block shutdown forever.
            await using var scope = _scopeFactory.CreateAsyncScope();
            var ingestionService = scope.ServiceProvider.GetRequiredService<IKnowledgeIngestionService>();
            await ingestionService.RunAsync(documentId, _drainDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_drainDeadline.IsCancellationRequested)
        {
            // The shutdown drain window elapsed before this document finished. It stays non-terminal and is re-dispatched
            // on the next start — not a fault.
            _logger.LogInformation("Background knowledge ingestion for document {DocumentId} was interrupted by shutdown; it resumes on next start.", documentId);
        }
        catch (Exception exception)
        {
            // The state machine already records step failures; this is a last-resort guard for an unexpected fault such as
            // scope creation. Log the exception type only — never any document content.
            _logger.LogWarning("Background knowledge ingestion faulted for document {DocumentId} ({ErrorClass}).", documentId, exception.GetType().Name);
        }
        finally
        {
            ReleaseConcurrency();
        }
    }

    private async Task DrainInFlightAsync(CancellationToken cancellationToken)
    {
        var pending = _inFlight.Keys.ToArray();
        if (pending.Length == 0)
        {
            return;
        }

        _logger.LogInformation("Knowledge ingestion worker draining {InFlightCount} in-flight ingestion(s) before shutdown.", pending.Length);

        // Bound the wait. During the window the documents keep running uncancelled so a near-complete index write still
        // lands; if the window (or the host's own shutdown deadline) elapses first, cancel the shared drain token so the
        // stragglers stop and are re-queued on the next start — a hung document must never block shutdown forever.
        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(_drainTimeout);

        try
        {
            await Task.WhenAll(pending).WaitAsync(window.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await _drainDeadline.CancelAsync().ConfigureAwait(false);
            _logger.LogWarning("Knowledge ingestion worker abandoned in-flight ingestion(s) not drained within {DrainSeconds:F0}s; they resume on next start.", _drainTimeout.TotalSeconds);
        }
    }

    private void TrackInFlight(Task task)
    {
        _inFlight[task] = 0;
        // Self-removing continuation, run synchronously on completion so the set only ever holds genuinely running tasks.
        _ = task.ContinueWith(static (completed, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(completed, out _),
            _inFlight,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ReleaseConcurrency()
    {
        try
        {
            _ = _concurrency.Release();
        }
        catch (ObjectDisposedException)
        {
            // A document abandoned past the drain window can complete after Dispose has run; releasing a disposed semaphore
            // is a no-op we deliberately swallow — there is no longer anything to gate.
        }
        catch (SemaphoreFullException)
        {
            // Defensive: a release-count anomaly must never escape the finally and fault the background task.
        }
    }

    public override void Dispose()
    {
        _drainDeadline.Dispose();
        _concurrency.Dispose();
        base.Dispose();
    }
}
