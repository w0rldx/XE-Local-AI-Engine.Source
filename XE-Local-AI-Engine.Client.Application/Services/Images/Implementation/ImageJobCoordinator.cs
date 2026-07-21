namespace XE_Local_AI_Engine.Client.Services.Images.Implementation;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Providers.Abstractions.Image;

/// <summary>
///     Default <see cref="IImageJobCoordinator" />. Mirrors the GGUF download coordinator: a per-job in-flight
///     <see cref="CancellationTokenSource" /> registry, throttled coarse status push, and detached run tasks. Generation
///     is serialized through a single-slot <see cref="SemaphoreSlim" /> so at most one job is handed to the runtime at a
///     time; extra jobs wait in their run task holding <see cref="ImageJobStatus.Queued" /> (never submitted to the
///     runtime until the slot frees), bounding the blast radius of a kill+restart cancel to one job (§4A/§7.5).
///     <para>
///         <b>Singleton.</b> The registry outlives the request that started a job (generation runs detached). Job state is
///         persisted to <c>image_jobs</c> through a fresh DI scope per operation; on success the image is persisted
///         encrypted-at-rest BEFORE the job is marked succeeded. Progress is coarse status only — never the prompt, a
///         path, or step/percent (§4A/§10).
///     </para>
///     <para>
///         <b>Shutdown/restart.</b> <see cref="DisposeAsync" /> cancels every in-flight job and drains the run tasks for
///         a short bound so terminal states can be persisted; any job that still dies non-terminal (hard crash, drain
///         timeout) is terminalized by <see cref="ImageJobStartupReconciler" /> on the next boot (no auto-retry).
///     </para>
/// </summary>
public sealed class ImageJobCoordinator : IImageJobCoordinator, IDisposable, IAsyncDisposable
{
    // Minimum gap between two pushed non-terminal progress updates for the same job — protects the socket from a
    // high-frequency runtime callback. The initial Queued/Generating push and every terminal push bypass the throttle.
    private static readonly TimeSpan ProgressPushInterval = TimeSpan.FromSeconds(1);

    // How long DisposeAsync waits for cancelled run tasks to persist their terminal state before letting go. Anything
    // that outlives the drain is terminalized by ImageJobStartupReconciler on the next boot.
    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(3);

    // Per-job replay buffer cap and how long a terminal job's log lingers for a late subscriber before eviction.
    private const int MaxBufferedEventsPerJob = 128;
    private static readonly TimeSpan ReplayRetention = TimeSpan.FromMinutes(5);

    private readonly IImageRuntime _runtime;
    private readonly IGeneratedImageStore _imageStore;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IImageJobEventPublisher _eventPublisher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ImageJobCoordinator> _logger;

    // Serializes generation to one running job; extra jobs wait here (still Queued) until the slot frees.
    private readonly SemaphoreSlim _generationSlot = new(initialCount: 1, maxCount: 1);

    // Keyed by job id. An in-flight job owns a live CTS; Cancel signals it via the registry.
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _inFlight = new();

    // Detached run tasks by job id so DisposeAsync can drain them (bounded) after cancelling — giving each run task a
    // chance to persist its terminal state before the process exits.
    private readonly ConcurrentDictionary<Guid, Task> _runTasks = new();

    // Last instant a non-terminal progress push went out per job, so runtime callbacks are throttled.
    private readonly ConcurrentDictionary<Guid, long> _lastProgressPushTicks = new();

    // Per-job ordered replay log for late subscribers; outlives the run and is evicted after ReplayRetention.
    private readonly ConcurrentDictionary<Guid, JobEventLog> _eventLogs = new();

    // Periodic eviction so terminal replay logs are released even when no further job ever starts. Without it the
    // eviction in EnqueueAsync was the ONLY trigger, so the last jobs' logs lingered on an idle node indefinitely
    // (parity with PreviewWorkflowIdleSweeper's cadence-driven sweep).
    private readonly ITimer _evictionTimer;

    public ImageJobCoordinator(IImageRuntime runtime,
        IGeneratedImageStore imageStore,
        IServiceScopeFactory scopeFactory,
        IImageJobEventPublisher eventPublisher,
        TimeProvider timeProvider,
        ILogger<ImageJobCoordinator> logger)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _imageStore = imageStore ?? throw new ArgumentNullException(nameof(imageStore));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // The callback only walks concurrent dictionaries (safe against a racing Dispose) and never throws.
        _evictionTimer = _timeProvider.CreateTimer(static state => ((ImageJobCoordinator)state!).EvictExpiredEventLogs(),
            this,
            ReplayRetention,
            ReplayRetention);
    }

    public async Task<Guid> EnqueueAsync(CreateImageJobInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateInput(input);

        var jobId = Guid.NewGuid();
        var createdAt = NowUnixMs();

        // Persist the queued row first (encrypts the prompt at rest). If this throws, nothing is registered.
        await CreateQueuedAsync(jobId, input, createdAt, cancellationToken).ConfigureAwait(false);

        var cts = new CancellationTokenSource();
        _inFlight[jobId] = cts;

        // Create the replay log BEFORE the first push so the initial Queued event is buffered as seq 0.
        _ = GetOrCreateEventLog(jobId);
        PushStatus(jobId, ImageJobStatus.Queued, queuePosition: null, elapsedMs: null, imageId: null, sanitizedError: null, isInitialOrTerminal: true);

        EvictExpiredEventLogs();

        // Detached run task owns the CTS lifetime: it captures only the token (a struct) and disposes the CTS via the
        // registry in Cleanup — so no IDisposable instance is passed into an un-awaited task (CA2025), while Cancel can
        // still signal it via the registry until then.
        var request = ToRequest(input);
        var runTask = RunJobAsync(jobId, request, cts.Token);

        // Track the run task for the shutdown drain. If the task already completed (its Cleanup ran before this add),
        // remove it again so a finished job never lingers in the registry.
        _runTasks[jobId] = runTask;
        if (runTask.IsCompleted)
        {
            _ = _runTasks.TryRemove(jobId, out _);
        }

        return jobId;
    }

    public async Task<bool> CancelAsync(Guid jobId, CancellationToken cancellationToken)
    {
        if (!_inFlight.TryGetValue(jobId, out var cts))
        {
            return false;
        }

        try
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The job finished and disposed its CTS between the lookup and the cancel — nothing to cancel.
            return false;
        }

        // Record the request time best-effort; the run task drives the actual terminal transition.
        await RunStoreAsync(store => store.MarkCancellationRequestedAsync(jobId, NowUnixMs(), cancellationToken), jobId, "record cancellation request")
            .ConfigureAwait(false);
        return true;
    }

    public async Task<ImageJobView?> GetAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IImageJobStore>();
        return await store.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ImageJobView>> ListAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IImageJobStore>();
        return await store.ListAsync(cancellationToken).ConfigureAwait(false);
    }

    public IReadOnlyList<ImageJobBufferedEvent> SnapshotBufferedEvents(Guid jobId)
    {
        return _eventLogs.TryGetValue(jobId, out var log) ? log.Snapshot() : [];
    }

    public void Dispose()
    {
        // Synchronous teardown: cancel and release without blocking on the run tasks (never sync-over-async). Any job
        // that dies before persisting a terminal state is terminalized by ImageJobStartupReconciler on the next boot.
        CancelAllInFlight();
        ReleaseDisposables();
    }

    public async ValueTask DisposeAsync()
    {
        // Graceful shutdown (the DI container prefers this path): cancel every in-flight job, then drain the run tasks
        // for a short bound so they can persist their terminal state (Cancelled) before the process exits. Anything that
        // outlives the drain is terminalized by ImageJobStartupReconciler on the next boot.
        CancelAllInFlight();

        try
        {
            await Task.WhenAll(_runTasks.Values.ToArray()).WaitAsync(ShutdownDrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A wedged runtime call outlived the drain window; startup reconciliation catches the job on the next boot.
        }

        ReleaseDisposables();
    }

    // Signals every in-flight job so any run task waiting on the generation slot or inside the runtime unwinds.
    // Disposal of each CTS belongs to ReleaseDisposables (or the run task's own Cleanup, whichever gets there first).
    private void CancelAllInFlight()
    {
        foreach (var cts in _inFlight.Values)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed by its own run task; nothing to cancel.
            }
        }
    }

    private void ReleaseDisposables()
    {
        foreach (var cts in _inFlight.Values)
        {
            cts.Dispose();
        }

        _inFlight.Clear();
        _runTasks.Clear();
        _generationSlot.Dispose();
        _evictionTimer.Dispose();
    }

    private async Task RunJobAsync(Guid jobId, ImageGenerationRequest request, CancellationToken token)
    {
        var acquired = false;
        try
        {
            // Serialize: wait for the single generation slot. A cancel while still queued throws here — the job is
            // dropped to Cancelled WITHOUT ever calling the runtime.
            await _generationSlot.WaitAsync(token).ConfigureAwait(false);
            acquired = true;
        }
        catch (OperationCanceledException)
        {
            await RunStoreAsync(store => store.MarkCancelledAsync(jobId, NowUnixMs(), CancellationToken.None), jobId, "mark cancelled").ConfigureAwait(false);
            PushStatus(jobId, ImageJobStatus.Cancelled, queuePosition: null, elapsedMs: null, imageId: null, sanitizedError: null, isInitialOrTerminal: true);
            _logger.LogInformation("Operator cancelled queued image job {JobId} before generation started.", jobId);
            Cleanup(jobId);
            return;
        }
        catch (ObjectDisposedException)
        {
            // The coordinator was disposed (node shutdown) while this job was still waiting for the slot — nothing to
            // clean up beyond the registry; the job simply never ran.
            Cleanup(jobId);
            return;
        }

        try
        {
            token.ThrowIfCancellationRequested();

            var startedAt = NowUnixMs();
            await RunStoreAsync(store => store.MarkGeneratingAsync(jobId, startedAt, CancellationToken.None), jobId, "mark generating").ConfigureAwait(false);
            PushStatus(jobId, ImageJobStatus.Generating, queuePosition: null, elapsedMs: 0, imageId: null, sanitizedError: null, isInitialOrTerminal: true);

            var progress = new Progress<ImageGenProgress>(update => OnRuntimeProgress(jobId, update));
            var result = await _runtime.GenerateAsync(request, progress, token).ConfigureAwait(false);

            // Persist the image encrypted-at-rest BEFORE marking the job succeeded.
            var imageId = Guid.NewGuid();
            await _imageStore.AddAsync(jobId,
                                 imageId,
                                 result.ImageBytes,
                                 new GeneratedImageMetadata
                                 {
                                     Width = result.Width,
                                     Height = result.Height
                                 },
                                 CancellationToken.None)
                             .ConfigureAwait(false);

            var durationMs = (long)result.Duration.TotalMilliseconds;
            await RunStoreAsync(store => store.MarkSucceededAsync(jobId, imageId, NowUnixMs(), durationMs, CancellationToken.None), jobId, "mark succeeded").ConfigureAwait(false);
            PushStatus(jobId, ImageJobStatus.Succeeded, queuePosition: null, elapsedMs: durationMs, imageId: imageId, sanitizedError: null, isInitialOrTerminal: true);
        }
        catch (OperationCanceledException)
        {
            await RunStoreAsync(store => store.MarkCancelledAsync(jobId, NowUnixMs(), CancellationToken.None), jobId, "mark cancelled").ConfigureAwait(false);
            PushStatus(jobId, ImageJobStatus.Cancelled, queuePosition: null, elapsedMs: null, imageId: null, sanitizedError: null, isInitialOrTerminal: true);
            _logger.LogInformation("Operator cancelled image job {JobId} during generation.", jobId);
        }
        catch (Exception exception)
        {
            // Sanitized: never surface the raw message (it may carry internal/model detail) and never log the prompt.
            const string sanitizedError = "Image generation failed.";
            await RunStoreAsync(store => store.MarkFailedAsync(jobId, sanitizedError, NowUnixMs(), CancellationToken.None), jobId, "mark failed").ConfigureAwait(false);
            PushStatus(jobId, ImageJobStatus.Failed, queuePosition: null, elapsedMs: null, imageId: null, sanitizedError: sanitizedError, isInitialOrTerminal: true);
            _logger.LogWarning(exception, "Image job {JobId} failed during generation.", jobId);
        }
        finally
        {
            if (acquired)
            {
                try
                {
                    _ = _generationSlot.Release();
                }
                catch (ObjectDisposedException)
                {
                    // The coordinator was disposed (node shutdown) while this job held the slot — nothing to release.
                }
            }

            Cleanup(jobId);
        }
    }

    private void OnRuntimeProgress(Guid jobId, ImageGenProgress update)
    {
        // Terminal phases are driven by the run task after persistence; only coarse Queued/Generating transitions flow
        // through the throttled progress push (elapsed forwarded — no step/percent exists over HTTP, §4A).
        if (update.Phase is ImageGenPhase.Completed or ImageGenPhase.Failed or ImageGenPhase.Cancelled)
        {
            return;
        }

        var status = update.Phase == ImageGenPhase.Queued ? ImageJobStatus.Queued : ImageJobStatus.Generating;
        var elapsedMs = (long)update.Elapsed.TotalMilliseconds;
        PushStatus(jobId, status, update.QueuePosition, elapsedMs, imageId: null, sanitizedError: null, isInitialOrTerminal: false);
    }

    private async Task CreateQueuedAsync(Guid jobId, CreateImageJobInput input, long createdAtUtc, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IImageJobStore>();
        await store.CreateQueuedAsync(new ImageJobCreate
                   {
                       Id = jobId,
                       ModelName = input.ModelName,
                       Prompt = input.Prompt,
                       NegativePrompt = input.NegativePrompt,
                       Seed = input.Seed,
                       Width = input.Width,
                       Height = input.Height,
                       Steps = input.Steps,
                       Sampler = input.Sampler ?? string.Empty,
                       CfgScale = input.CfgScale,
                       CreatedAtUtc = createdAtUtc
                   }, cancellationToken)
                   .ConfigureAwait(false);
    }

    // Runs a persistence action in a fresh scope, swallowing failures with a warning so a detached run task never faults
    // on a best-effort status write (the status endpoint remains the authoritative hydrate either way).
    private async Task RunStoreAsync(Func<IImageJobStore, Task> action, Guid jobId, string operation)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IImageJobStore>();
            await action(store).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not {Operation} for image job {JobId}.", operation, jobId);
        }
    }

    // Records the coarse status in the per-job replay log and broadcasts it. Non-terminal pushes are throttled to at most
    // one per ProgressPushInterval per job; the initial Queued/Generating push and every terminal push bypass the throttle.
    private void PushStatus(Guid jobId,
        ImageJobStatus status,
        int? queuePosition,
        long? elapsedMs,
        Guid? imageId,
        string? sanitizedError,
        bool isInitialOrTerminal)
    {
        if (isInitialOrTerminal)
        {
            if (status is ImageJobStatus.Queued or ImageJobStatus.Generating)
            {
                _lastProgressPushTicks[jobId] = _timeProvider.GetUtcNow().UtcTicks;
            }
            else
            {
                _lastProgressPushTicks.TryRemove(jobId, out _);
            }

            BroadcastStatus(jobId, status, queuePosition, elapsedMs, imageId, sanitizedError);
            return;
        }

        var now = _timeProvider.GetUtcNow().UtcTicks;
        var last = _lastProgressPushTicks.TryGetValue(jobId, out var ticks) ? ticks : 0L;
        if (now - last < ProgressPushInterval.Ticks)
        {
            return;
        }

        _lastProgressPushTicks[jobId] = now;
        BroadcastStatus(jobId, status, queuePosition, elapsedMs, imageId, sanitizedError);
    }

    private void BroadcastStatus(Guid jobId,
        ImageJobStatus status,
        int? queuePosition,
        long? elapsedMs,
        Guid? imageId,
        string? sanitizedError)
    {
        var log = GetOrCreateEventLog(jobId);
        var nowUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var isTerminal = IsTerminalStatus(status);

        var payload = (ImageJobStatusHubEvent)log.Append(ImageJobHubEvents.StatusChanged,
            seq => new ImageJobStatusHubEvent(jobId, status.ToString(), queuePosition, elapsedMs, imageId, sanitizedError, nowUnixMs, seq),
            isTerminal,
            nowUnixMs,
            out var truncated);

        if (truncated)
        {
            _logger.LogDebug("Image job {JobId} replay buffer exceeded {Cap}; dropping oldest events.", jobId, MaxBufferedEventsPerJob);
        }

        _ = PublishStatusAsync(jobId, payload);
    }

    private async Task PublishStatusAsync(Guid jobId, ImageJobStatusHubEvent payload)
    {
        try
        {
            await _eventPublisher.PublishStatusAsync(payload).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not push image-job status for {JobId}; the status endpoint still serves it.", jobId);
        }
    }

    private void Cleanup(Guid jobId)
    {
        if (_inFlight.TryRemove(jobId, out var registeredCts))
        {
            registeredCts.Dispose();
        }

        _ = _runTasks.TryRemove(jobId, out _);
        _lastProgressPushTicks.TryRemove(jobId, out _);
    }

    private JobEventLog GetOrCreateEventLog(Guid jobId)
    {
        return _eventLogs.GetOrAdd(jobId, _ => new JobEventLog(MaxBufferedEventsPerJob, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()));
    }

    private void EvictExpiredEventLogs()
    {
        var nowUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var retentionMs = (long)ReplayRetention.TotalMilliseconds;

        foreach (var (jobId, log) in _eventLogs)
        {
            if (log.TerminalAtUnixMs is { } terminal && nowUnixMs - terminal >= retentionMs && !_inFlight.ContainsKey(jobId))
            {
                _ = _eventLogs.TryRemove(jobId, out _);
            }
        }
    }

    private long NowUnixMs()
    {
        return _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
    }

    private static bool IsTerminalStatus(ImageJobStatus status)
    {
        return status is ImageJobStatus.Succeeded or ImageJobStatus.Failed or ImageJobStatus.Cancelled;
    }

    private static ImageGenerationRequest ToRequest(CreateImageJobInput input)
    {
        return new ImageGenerationRequest
        {
            ModelName = input.ModelName,
            Prompt = input.Prompt,
            NegativePrompt = input.NegativePrompt,
            Seed = input.Seed,
            Width = input.Width,
            Height = input.Height,
            Steps = input.Steps,
            Sampler = input.Sampler,
            CfgScale = input.CfgScale,
            BatchCount = 1
        };
    }

    private static void ValidateInput(CreateImageJobInput input)
    {
        if (string.IsNullOrWhiteSpace(input.ModelName))
        {
            throw new ArgumentException("An image job requires a model name.", nameof(input));
        }

        if (string.IsNullOrWhiteSpace(input.Prompt))
        {
            throw new ArgumentException("An image job requires a prompt.", nameof(input));
        }
    }

    /// <summary>One buffered event in a job's replay log: the SignalR method name, the seq-stamped payload, and its seq.</summary>
    private sealed record BufferedEvent(string MethodName, object Payload, long Seq);

    /// <summary>
    ///     A per-job ordered, bounded event log for late-subscriber replay. Seq assignment + append are atomic under a lock
    ///     so concurrent publishes never collide on a seq or append out of order; <see cref="Snapshot" /> copies under the
    ///     same lock for a consistent ordered view.
    /// </summary>
    private sealed class JobEventLog(int maxEvents, long createdAtUnixMs)
    {
        private readonly List<BufferedEvent> _events = [];
        private readonly Lock _gate = new();

        private long _nextSeq;

        public long CreatedAtUnixMs { get; } = createdAtUnixMs;

        /// <summary>Set to the terminal event's timestamp once a terminal status is buffered; drives eviction.</summary>
        public long? TerminalAtUnixMs { get; private set; }

        public object Append(string methodName,
            Func<long, object> payloadFactory,
            bool isTerminal,
            long terminalAtUnixMs,
            out bool truncated)
        {
            lock (_gate)
            {
                var seq = _nextSeq++;
                var payload = payloadFactory(seq);
                _events.Add(new BufferedEvent(methodName, payload, seq));

                truncated = false;
                if (_events.Count > maxEvents)
                {
                    _events.RemoveAt(index: 0);
                    truncated = true;
                }

                if (isTerminal)
                {
                    TerminalAtUnixMs = terminalAtUnixMs;
                }

                return payload;
            }
        }

        public IReadOnlyList<ImageJobBufferedEvent> Snapshot()
        {
            lock (_gate)
            {
                var copy = new List<ImageJobBufferedEvent>(_events.Count);
                foreach (var buffered in _events)
                {
                    copy.Add(new ImageJobBufferedEvent(buffered.MethodName, buffered.Payload));
                }

                return copy;
            }
        }
    }
}
