namespace XE_Local_AI_Engine.Client.Services.Images.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Startup reconciliation for the image-job registry: terminalizes jobs a dead process left
///     <see cref="ImageJobStatus.Queued" /> or <see cref="ImageJobStatus.Generating" /> and pushes a status event so a
///     connected UI updates.
/// </summary>
/// <remarks>
///     The coordinator's in-memory registry is gone after a restart, so nothing would ever transition those rows
///     again and they would show as stuck forever. Interrupted jobs are NOT auto-retried — generation is expensive
///     and nondeterministic, so they are marked <see cref="ImageJobStatus.Failed" /> with a content-free reason and
///     the operator resubmits, mirroring the scheduler's stale-run reconciliation in <c>Program</c>. Reconciliation
///     always completes before a new job could race it. See docs/wiki/14-image-generation.md ("Restart and recovery").
/// </remarks>
public sealed class ImageJobStartupReconciler : IHostedService
{
    /// <summary>Display-safe reason stamped on interrupted jobs. Content-free by design — never the prompt or a path.</summary>
    public const string InterruptedReason = "Interrupted by an application shutdown; submit the job again to retry.";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IImageJobEventPublisher _eventPublisher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ImageJobStartupReconciler> _logger;

    public ImageJobStartupReconciler(
        IServiceScopeFactory scopeFactory,
        IImageJobEventPublisher eventPublisher,
        TimeProvider timeProvider,
        ILogger<ImageJobStartupReconciler> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(eventPublisher);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _scopeFactory = scopeFactory;
        _eventPublisher = eventPublisher;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> interrupted;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IImageJobStore>();
            interrupted = await store.MarkInterruptedFailedAsync(InterruptedReason, NowUnixMs(), cancellationToken);
        }
        catch (Exception exception)
        {
            // Best-effort: reconciliation must never block node startup. The status endpoint still serves whatever state
            // is stored — the same swallow-and-warn posture the coordinator takes for its own store writes.
            _logger.LogError(exception, "Could not reconcile interrupted image jobs at startup.");
            return;
        }

        if (interrupted.Count == 0)
        {
            return;
        }

        _logger.LogWarning("Reconciled {Count} interrupted image job(s) to Failed at startup.", interrupted.Count);

        foreach (var jobId in interrupted)
        {
            await PublishFailedAsync(jobId, cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private async Task PublishFailedAsync(Guid jobId, CancellationToken cancellationToken)
    {
        // Seq 0: the per-job replay log did not survive the restart, so this is the first event of the new process.
        var payload = new ImageJobStatusHubEvent
        {
            JobId = jobId,
            Phase = ImageJobStatus.Failed.ToString(),
            QueuePosition = null,
            ElapsedMs = null,
            ImageId = null,
            SanitizedError = InterruptedReason,
            OccurredAtUtc = NowUnixMs(),
            Seq = 0
        };

        try
        {
            await _eventPublisher.PublishStatusAsync(payload, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not push reconciled image-job status for {JobId}; the status endpoint still serves it.", jobId);
        }
    }

    private long NowUnixMs()
    {
        return _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
    }
}
