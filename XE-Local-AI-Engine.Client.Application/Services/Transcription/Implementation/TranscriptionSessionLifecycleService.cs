namespace XE_Local_AI_Engine.Client.Services.Transcription.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Startup fails every row a dead process left <see cref="TranscriptionSessionStatus.Transcribing" />; a graceful stop
///     ends live sessions while their rows can still be written.
/// </summary>
/// <remarks>
///     Mirrors <c>ImageJobStartupReconciler</c>: one pass, a literal content-free reason, never auto-resumed — the audio
///     is gone after a restart. The stop half exists because the registry's own disposal runs after the service provider
///     is disposed. A graceful stop ends live sessions only; a batch upload the process dies inside is failed as interrupted on the next boot.
/// </remarks>
public sealed class TranscriptionSessionLifecycleService : IHostedService
{
    /// <summary>The error code stamped on a row a dead process left transcribing.</summary>
    public const string InterruptedErrorCode = "interrupted";

    /// <summary>Display-safe reason stamped on interrupted rows; never the title or a transcript line.</summary>
    public const string InterruptedReason = "Interrupted by an application shutdown; the transcript up to that point is kept.";

    private const int PageSize = 200;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILiveTranscriptionSessionRegistry _liveRegistry;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TranscriptionSessionLifecycleService> _logger;

    public TranscriptionSessionLifecycleService(IServiceScopeFactory scopeFactory,
        ILiveTranscriptionSessionRegistry liveRegistry,
        TimeProvider timeProvider,
        ILogger<TranscriptionSessionLifecycleService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _liveRegistry = liveRegistry ?? throw new ArgumentNullException(nameof(liveRegistry));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var reconciled = 0;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<ITranscriptionSessionStore>();
            var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

            // Pages the whole list (titles decrypted): the store has no status query. Add one if session counts grow large.
            // Ids are collected first, so a write can never shift a row between the pages still to be read.
            var interrupted = new List<Guid>();
            var offset = 0;
            var pageCount = PageSize;
            while (pageCount == PageSize)
            {
                var page = await store.ListAsync(PageSize, offset, cancellationToken);
                pageCount = page.Count;
                offset += PageSize;
                interrupted.AddRange(page.Where(session => session.Status == TranscriptionSessionStatus.Transcribing).Select(session => session.Id));
            }

            foreach (var sessionId in interrupted)
            {
                reconciled += await store.FailAsync(sessionId, InterruptedErrorCode, InterruptedReason, now, cancellationToken) ? 1 : 0;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Best-effort, as for image jobs: reconciliation must never block node startup.
            _logger.LogError(exception, "Could not reconcile interrupted transcription sessions at startup.");
            return;
        }

        if (reconciled > 0)
        {
            _logger.LogWarning("Reconciled {Count} interrupted transcription session(s) to Failed at startup.", reconciled);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Idempotent: the container's own disposal later finds nothing left to end.
        if (_liveRegistry is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync();
        }
    }
}
