namespace XE_Local_AI_Engine.Client.Services.Memory.Implementation;

using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Background worker that drains the <see cref="MemoryExtractionDispatcher" /> queue and runs post-run adaptive-memory
///     extraction per job, bounded to <see cref="MemoryExtractionOptions.MaxConcurrentExtractions" /> concurrent jobs by a
///     <see cref="SemaphoreSlim" /> (so a burst of terminal turns cannot spin up unbounded concurrent model calls). Each
///     job runs in its own <c>CreateAsyncScope()</c> with the drain-deadline token — never the chat send token — so a
///     completed run's memory is never lost to a client-side cancellation, and the request scope being disposed cannot
///     fault extraction with an <see cref="ObjectDisposedException" />. Replaces the prior unbounded fire-and-forget
///     <c>Task.Run</c> dispatch; mirrors <c>KnowledgeIngestionWorker</c>.
///     <para>
///         Shutdown awareness — a BOUNDED, honest contract. Each job carries conversation content and the queue is purely
///         in-memory (no persistence recovery), so a job dropped at shutdown is lost for good — shutdown therefore drains
///         QUEUED work as well as in-flight work. <see cref="StopAsync" /> (1) completes the dispatcher's writer FIRST, so
///         no new job is accepted (<see cref="MemoryExtractionDispatcher.Dispatch" /> then takes its content-free
///         dropped-job path) and the read loop can drain the buffered jobs and end on its own; (2) drains the read loop
///         (which launches every remaining buffered job) AND the in-flight jobs under a single bounded window
///         (<see cref="MemoryExtractionOptions.ShutdownDrainTimeoutSeconds" />); (3) if that window elapses, cancels
///         <see cref="_drainDeadline" /> so the read loop stops launching and every straggler observes cancellation, then
///         awaits their unwinding under a brief FIXED grace (<see cref="PostDeadlineGrace" />) and accounts for any
///         still-queued jobs as dropped (content-free) before returning. The read loop reads on
///         <see cref="_drainDeadline" /> (NOT the host stopping token), so a stop cannot abandon jobs already buffered on
///         the channel; that token is cancelled ONLY when the drain window elapses, so ordinary operation never cancels a
///         job mid-write.
///     </para>
///     <para>
///         The deliberate trade against unbounded shutdown. Shutdown is capped at the drain window PLUS the fixed grace —
///         it never waits indefinitely for a job to finish. A job that cooperates with cancellation always unwinds inside
///         the grace and so never observes a disposed scope factory or semaphore. A job that IGNORES cancellation past the
///         grace is deliberately ABANDONED: <see cref="StopAsync" /> returns and <see cref="Dispose" /> disposes the
///         <see cref="_drainDeadline" /> CTS and the <see cref="_concurrency" /> semaphore while that job is still running,
///         so when it finally resumes it MAY observe disposed host services and its failure is swallowed (the
///         <see cref="ObjectDisposedException" /> net in <see cref="ReleaseConcurrency" /> and the per-job catch-all are
///         the last-resort guards for exactly this). Abandonment is not hidden: each abandoned job is counted, logged
///         content-free, and reported on <see cref="NodeMetrics.MemoryExtractionAbandonedTotal" />. This is the accepted
///         cost of a bounded shutdown — at most a small, counted number of cancellation-ignoring jobs may lose their
///         memory write and race disposal, in exchange for a shutdown that always completes promptly.
///     </para>
///     All failures are handled inside the job's catch-all, which logs the exception TYPE NAME only — never conversation
///     content, mirroring the extraction service's text-free discipline.
/// </summary>
public sealed class MemoryExtractionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MemoryExtractionDispatcher _dispatcher;
    private readonly SemaphoreSlim _concurrency;
    private readonly TimeSpan _drainTimeout;
    private readonly ILogger<MemoryExtractionWorker> _logger;

    // Every in-flight ProcessJobAsync task, so shutdown can await them before the scope factory and the semaphore are
    // disposed. Each task removes itself on completion via a synchronous continuation, so the set only holds running work.
    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();

    // Cancelled ONLY when the shutdown drain window elapses. Per-job work AND the read loop run on this token: during
    // normal operation it is never cancelled (a completed run's memory write is never lost, and the read loop blocks on an
    // empty queue), but a job or read that outlives the drain window is cancelled so it stops and disposal can proceed.
    private readonly CancellationTokenSource _drainDeadline = new();

    // After the drain window elapses and the drain token is cancelled, this brief grace bounds how long StopAsync waits
    // for the read loop and the (now-cancelled) stragglers to unwind before it returns and Dispose runs. Cancellation is
    // cooperative, so a job that observes the token finishes well inside this; it caps a job that ignores the token.
    private static readonly TimeSpan PostDeadlineGrace = TimeSpan.FromSeconds(2);

    internal MemoryExtractionWorker(IServiceScopeFactory scopeFactory,
        MemoryExtractionDispatcher dispatcher,
        IOptions<MemoryExtractionOptions> options,
        ILogger<MemoryExtractionWorker> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var maxConcurrency = Math.Max(1, options.Value.MaxConcurrentExtractions);
        _concurrency = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _drainTimeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.ShutdownDrainTimeoutSeconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Respond to a host stop by completing the writer so the read loop below drains the buffered jobs and then exits
        // on its own. StopAsync also completes the writer (idempotently) and bounds the drain; this registration is the
        // safety net for a stop that trips the token before StopAsync runs. The loop deliberately reads on the drain token,
        // NOT the stopping token, so a stop can never abandon jobs already buffered on the channel — they are drained.
        using var stopRegistration = stoppingToken.Register(static state => ((MemoryExtractionDispatcher)state!).CompleteWriter(),
            _dispatcher);

        try
        {
            // Acquire the concurrency slot BEFORE the destructive read, never after. The dispatcher is SingleReader, so a
            // WaitToReadAsync/TryRead pair is a safe stand-in for the absent multi-reader peek: a job is never removed from
            // the channel until a slot is in hand. This closes the accounting gap of a dequeue-then-await-slot ordering,
            // where a job cancelled while awaiting the slot had already left the channel (so the queued-drain misses it) yet
            // never reached TrackInFlight (so _inFlight misses it) — escaping BOTH the dropped and abandoned counters. Now a
            // job the drain window cancels stays buffered and is accounted as dropped in StopAsync.
            while (await _dispatcher.Reader.WaitToReadAsync(_drainDeadline.Token).ConfigureAwait(false))
            {
                // Gate on the concurrency budget before starting the next job so at most MaxConcurrentExtractions run.
                await _concurrency.WaitAsync(_drainDeadline.Token).ConfigureAwait(false);

                if (_dispatcher.Reader.TryRead(out var job))
                {
                    TrackInFlight(ProcessJobAsync(job));
                }
                else
                {
                    // The reader signalled readiness but nothing was there (the writer completed between the wait and the
                    // read). Release the slot just taken so it is not leaked, then loop to re-observe writer completion.
                    ReleaseConcurrency();
                }
            }
        }
        catch (OperationCanceledException) when (_drainDeadline.IsCancellationRequested)
        {
            // The shutdown drain window elapsed: stop launching. Jobs still buffered are accounted as dropped in StopAsync.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // 1. Complete the writer FIRST. No work is accepted after this: Dispatch's TryWrite returns false and takes its
        //    content-free dropped-job path, and the read loop drains the buffered jobs then returns on its own.
        _dispatcher.CompleteWriter();

        // 2. Drain the read loop (which launches every remaining buffered job) AND the in-flight jobs under a SINGLE
        //    bounded window. Nothing is cancelled inside the window, so a near-complete memory write still lands.
        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(_drainTimeout);

        try
        {
            var readLoop = ExecuteTask;
            if (readLoop is not null)
            {
                await readLoop.WaitAsync(window.Token).ConfigureAwait(false);
            }

            var pending = _inFlight.Keys.ToArray();
            if (pending.Length > 0)
            {
                _logger.LogInformation("Memory extraction worker draining {InFlightCount} in-flight extraction(s) before shutdown.", pending.Length);
                await Task.WhenAll(pending).WaitAsync(window.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 3. The window (or the host's own shutdown deadline) elapsed before the drain finished. Cancel the drain
            //    token, wait briefly for the read loop and stragglers to unwind, and account for the rest as dropped.
            await AbandonAfterDeadlineAsync().ConfigureAwait(false);
        }

        // 4. Let the base observe ExecuteAsync's completion (it has already returned) and signal the stopping token.
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessJobAsync(MemoryExtractionJob job)
    {
        var cancellationToken = _drainDeadline.Token;

        try
        {
            // Own scope + own DbContext: the request/pump scope that produced the terminal may already be disposed, so
            // resolving the scoped stores/services from a fresh scope avoids an ObjectDisposedException on the context.
            await using var scope = _scopeFactory.CreateAsyncScope();

            // Execution-log row FIRST (metadata only — no message content): it is the diagnostic record of the run and
            // must be written even if extraction is a no-op (temp chat / no model / no lesson).
            await WriteExecutionLogAsync(scope.ServiceProvider, job.Telemetry, cancellationToken).ConfigureAwait(false);

            var extractionService = scope.ServiceProvider.GetRequiredService<IMemoryExtractionService>();
            _ = await extractionService.ExtractAsync(job.Run, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_drainDeadline.IsCancellationRequested)
        {
            // The shutdown drain window elapsed before this job finished; it is dropped (the run simply does not
            // contribute a memory). Not a fault.
            _logger.LogInformation("Background memory extraction for agent {AgentId} was interrupted by shutdown.", job.Telemetry.AgentDefinitionId);
        }
        catch (Exception exception)
        {
            // Catch-all: a background memory job must NEVER affect the run path, and must NEVER log conversation content.
            // Log the exception TYPE NAME only — never the exception object, whose Message/stack could carry conversation
            // text or model output (same text-free discipline as the exec-log ErrorClass field).
            _logger.LogWarning("Background memory extraction failed ({ErrorClass}) for agent {AgentId}; the chat run is unaffected.",
                exception.GetType().Name,
                job.Telemetry.AgentDefinitionId);
        }
        finally
        {
            ReleaseConcurrency();
        }
    }

    private static async Task WriteExecutionLogAsync(IServiceProvider serviceProvider,
        MemoryExtractionDispatchContext telemetry,
        CancellationToken cancellationToken)
    {
        var executionLogStore = serviceProvider.GetRequiredService<IAgentExecutionLogStore>();

        _ = await executionLogStore.AddAsync(new AgentExecutionLogInput(telemetry.AgentDefinitionId,
                telemetry.ConversationId,
                telemetry.MessageId,
                telemetry.ModelName,
                telemetry.ConfigHash,
                telemetry.LatencyMs,
                telemetry.Success,
                telemetry.PromptTokens,
                telemetry.CompletionTokens,
                telemetry.ErrorClass),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task AbandonAfterDeadlineAsync()
    {
        // Cancel the shared drain token: the read loop's ReadAllAsync/WaitAsync throw so it returns, and every in-flight
        // job observes cancellation and unwinds. Awaiting this BEFORE returning is what keeps Dispose from tearing down the
        // CTS/semaphore under a still-running job.
        await _drainDeadline.CancelAsync().ConfigureAwait(false);

        // Give the read loop and the stragglers a brief, bounded grace to unwind. Cancellation is cooperative, so a job
        // that observes the token finishes well inside this; the cap only matters for one that ignores it (for which the
        // ObjectDisposedException net in ReleaseConcurrency remains the last-resort guard).
        var toAwait = new List<Task>(_inFlight.Keys);
        if (ExecuteTask is { } readLoop)
        {
            toAwait.Add(readLoop);
        }

        if (toAwait.Count > 0)
        {
            using var grace = new CancellationTokenSource(PostDeadlineGrace);
            try
            {
                await Task.WhenAll(toAwait).WaitAsync(grace.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Even the brief grace elapsed for a job that ignores cancellation; this path should be vanishingly rare.
            }
        }

        // The read loop has stopped reading, so account for whatever it never got to as dropped rather than losing it
        // silently — content-free, mirroring the dispatcher's full-queue drop.
        var dropped = 0;
        while (_dispatcher.Reader.TryRead(out _))
        {
            dropped++;
        }

        // Any job still in-flight after the grace ignored cooperative cancellation and is now being ABANDONED: StopAsync is
        // about to return and Dispose will tear down the CTS/semaphore beneath it (the ObjectDisposedException net in
        // ReleaseConcurrency is the last-resort guard). Snapshot the count so the deliberate trade is observable, never silent.
        var abandoned = _inFlight.Count;

        _logger.LogWarning("Memory extraction worker shutdown drain exceeded {DrainSeconds:F0}s; abandoned {Abandoned} in-flight and dropped {Dropped} queued extraction(s).",
            _drainTimeout.TotalSeconds,
            abandoned,
            dropped);

        if (abandoned > 0)
        {
            // Dedicated, content-free record of the abandoned jobs: they ignored cancellation within the grace, may observe
            // disposed host services when they resume, and have their failures swallowed — the accepted bounded-shutdown cost.
            _logger.LogWarning(
                "Abandoned {Abandoned} memory-extraction job(s) that ignored cancellation within the shutdown grace; they may observe disposed host services and their failures are swallowed.",
                abandoned);
            NodeMetrics.MemoryExtractionAbandonedTotal.Add(abandoned);
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
            // A job abandoned past the drain window can complete after Dispose has run; releasing a disposed semaphore is
            // a no-op we deliberately swallow — there is no longer anything to gate.
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
