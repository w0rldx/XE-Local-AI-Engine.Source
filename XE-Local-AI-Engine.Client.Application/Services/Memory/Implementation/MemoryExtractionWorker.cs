namespace XE_Local_AI_Engine.Client.Services.Memory.Implementation;

using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
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
///         Shutdown awareness: in-flight job tasks are tracked; <see cref="StopAsync" /> stops reading the queue and then
///         awaits them within a bounded drain window (<see cref="MemoryExtractionOptions.ShutdownDrainTimeoutSeconds" />)
///         before disposal, so the scope factory and semaphore are never disposed under a running job. Per-job work runs
///         on <see cref="_drainDeadline" />, cancelled ONLY when that window elapses, so ordinary operation never cancels
///         a job mid-write yet a hung job cannot block shutdown forever.
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

    // Cancelled ONLY when the shutdown drain window elapses. Per-job work runs on this token: during normal operation it
    // is never cancelled (a completed run's memory write is never lost), but a job that outlives the drain window is
    // cancelled so it stops and disposal can proceed.
    private readonly CancellationTokenSource _drainDeadline = new();

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
        try
        {
            await foreach (var job in _dispatcher.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                // Gate on the concurrency budget before starting the next job so at most MaxConcurrentExtractions run.
                await _concurrency.WaitAsync(stoppingToken).ConfigureAwait(false);
                TrackInFlight(ProcessJobAsync(job));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown requested: stop reading the queue. StopAsync awaits the in-flight jobs before disposal.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // base.StopAsync cancels the stopping token and awaits ExecuteAsync's return, so the queue read stops and no new
        // jobs are launched. Only then do we drain the jobs already in flight.
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await DrainInFlightAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task DrainInFlightAsync(CancellationToken cancellationToken)
    {
        var pending = _inFlight.Keys.ToArray();
        if (pending.Length == 0)
        {
            return;
        }

        _logger.LogInformation("Memory extraction worker draining {InFlightCount} in-flight extraction(s) before shutdown.", pending.Length);

        // Bound the wait. During the window jobs keep running uncancelled so a near-complete write still lands; if the
        // window (or the host's own shutdown deadline) elapses first, cancel the shared drain token so stragglers stop.
        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(_drainTimeout);

        try
        {
            await Task.WhenAll(pending).WaitAsync(window.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await _drainDeadline.CancelAsync().ConfigureAwait(false);
            _logger.LogWarning("Memory extraction worker abandoned in-flight extraction(s) not drained within {DrainSeconds:F0}s.", _drainTimeout.TotalSeconds);
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
