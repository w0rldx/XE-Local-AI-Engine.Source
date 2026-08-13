namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using Microsoft.Extensions.Options;

/// <summary>
///     Low-frequency background reconciliation for embedding model/policy changes. The catalog resets only confidently
///     stale Indexed rows, and the existing bounded ingestion dispatcher performs the actual local work.
/// </summary>
public sealed class KnowledgeScheduledModelReindexWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IKnowledgeIngestionDispatcher _dispatcher;
    private readonly TimeSpan _interval;
    private readonly bool _enabled;
    private readonly ILogger<KnowledgeScheduledModelReindexWorker> _logger;

    public KnowledgeScheduledModelReindexWorker(IServiceScopeFactory scopeFactory,
        IKnowledgeIngestionDispatcher dispatcher,
        IOptions<KnowledgeBaseOptions> options,
        ILogger<KnowledgeScheduledModelReindexWorker> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        ArgumentNullException.ThrowIfNull(options);
        _enabled = options.Value.ScheduledModelReindexEnabled;
        _interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.ScheduledModelReindexIntervalMinutes));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await ReconcileOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning("Scheduled knowledge reindex reconciliation failed ({ExceptionType}).", exception.GetType().Name);
            }
        }
    }

    /// <summary>One deterministic reconciliation tick; internal so queue-pressure and stale selection are testable without a wall-clock timer.</summary>
    internal async Task ReconcileOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IKnowledgeDocumentCatalogService>();
        var stale = await catalog.ResetStaleDocumentsToPendingAsync(cancellationToken).ConfigureAwait(false);
        foreach (var documentId in stale)
        {
            if (await _dispatcher.EnqueueAsync(documentId, cancellationToken).ConfigureAwait(false)
                == KnowledgeIngestionEnqueueResult.QueueFull)
            {
                break;
            }
        }
    }
}
