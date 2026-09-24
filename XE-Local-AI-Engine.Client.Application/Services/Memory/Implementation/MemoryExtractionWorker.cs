namespace XE_Local_AI_Engine.Client.Services.Memory.Implementation;

using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Background worker draining the <see cref="MemoryExtractionDispatcher" /> queue, running each job in its own
///     scope on the drain-deadline token rather than the chat send token.
/// </summary>
/// <remarks>
///     Concurrency is bounded by <see cref="MemoryExtractionOptions.MaxConcurrentExtractions" />, and the
///     drain-deadline token means neither a client-side cancel nor a disposed request scope loses a completed run's
///     memory. Shutdown drains QUEUED as well as in-flight work, since the queue is in-memory and a dropped job is
///     lost for good; a job ignoring cancellation past the grace is ABANDONED, counted and metered. Every failure
///     logs the exception TYPE NAME only. Shutdown contract: <see cref="StopAsync" />.
/// </remarks>
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

    // Cancelled ONLY when the shutdown drain window elapses, and both the jobs and the read loop run on it: ordinary
    // operation never cancels it, so no completed run's memory write is lost, but disposal can still proceed.
    private readonly CancellationTokenSource _drainDeadline = new();

    // A brief grace bounding how long StopAsync waits for the read loop and the cancelled stragglers to unwind before
    // Dispose runs. A job that observes the token finishes well inside it; the cap only binds one that ignores it.
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
        // A host stop completes the writer so the read loop drains and exits on its own, the safety net for a stop
        // that trips before StopAsync. The loop reads the DRAIN token, so no stop abandons a buffered job.
        await using var stopRegistration = stoppingToken.Register(static state => ((MemoryExtractionDispatcher)state!).CompleteWriter(),
            _dispatcher);

        try
        {
            // Acquire the concurrency slot BEFORE the destructive read, so no job leaves the channel without one: a
            // dequeue-then-await-slot ordering loses a job cancelled mid-wait from BOTH counters.
            while (await _dispatcher.Reader.WaitToReadAsync(_drainDeadline.Token))
            {
                // Gate on the concurrency budget before starting the next job so at most MaxConcurrentExtractions run.
                await _concurrency.WaitAsync(_drainDeadline.Token);

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
                await readLoop.WaitAsync(window.Token);
            }

            var pending = _inFlight.Keys.ToArray();
            if (pending.Length > 0)
            {
                _logger.LogInformation("Memory extraction worker draining {InFlightCount} in-flight extraction(s) before shutdown.", pending.Length);
                await Task.WhenAll(pending).WaitAsync(window.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // 3. The window (or the host's own shutdown deadline) elapsed before the drain finished. Cancel the drain
            //    token, wait briefly for the read loop and stragglers to unwind, and account for the rest as dropped.
            await AbandonAfterDeadlineAsync();
        }

        // 4. Let the base observe ExecuteAsync's completion (it has already returned) and signal the stopping token.
        await base.StopAsync(cancellationToken);
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
            await WriteExecutionLogAsync(scope.ServiceProvider, job.Telemetry, cancellationToken);

            var extractionService = scope.ServiceProvider.GetRequiredService<IMemoryExtractionService>();
            _ = await extractionService.ExtractAsync(job.Run, cancellationToken);
        }
        catch (OperationCanceledException) when (_drainDeadline.IsCancellationRequested)
        {
            // The shutdown drain window elapsed before this job finished; it is dropped (the run simply does not
            // contribute a memory). Not a fault.
            _logger.LogInformation("Background memory extraction for agent {AgentId} was interrupted by shutdown.", job.Telemetry.AgentDefinitionId);
        }
        catch (Exception exception)
        {
            // Catch-all: a background memory job must NEVER affect the run path nor log conversation content, so only
            // the exception TYPE NAME is logged — its Message and stack could carry conversation text.
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

        _ = await executionLogStore.AddAsync(new AgentExecutionLogInput
            {
                AgentDefinitionId = telemetry.AgentDefinitionId,
                ConversationId = telemetry.ConversationId,
                MessageId = telemetry.MessageId,
                ModelName = telemetry.ModelName,
                ConfigHash = telemetry.ConfigHash,
                LatencyMs = telemetry.LatencyMs,
                Success = telemetry.Success,
                PromptTokens = telemetry.PromptTokens,
                CompletionTokens = telemetry.CompletionTokens,
                ErrorClass = telemetry.ErrorClass
            },
            cancellationToken);
    }

    private async Task AbandonAfterDeadlineAsync()
    {
        // Cancel the shared drain token so the read loop returns and every in-flight job unwinds. Awaiting it BEFORE
        // returning is what keeps Dispose from tearing down the CTS and semaphore under a still-running job.
        await _drainDeadline.CancelAsync();

        // A brief bounded grace for the read loop and the stragglers to unwind. The cap only matters for a job that
        // ignores the token, for which the ObjectDisposedException net in ReleaseConcurrency is the last-resort guard.
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
                await Task.WhenAll(toAwait).WaitAsync(grace.Token);
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

        // A job still in-flight after the grace ignored cancellation and is now ABANDONED: Dispose is about to tear
        // down the CTS and semaphore beneath it. The count is snapshotted so the deliberate trade is never silent.
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
