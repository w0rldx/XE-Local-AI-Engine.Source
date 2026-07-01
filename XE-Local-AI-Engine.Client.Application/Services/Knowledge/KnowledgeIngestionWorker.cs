namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using Microsoft.Extensions.Options;

/// <summary>
///     Background worker that drains the <see cref="KnowledgeIngestionDispatcher" /> queue and runs the ingestion state
///     machine per document, bounded to <see cref="KnowledgeBaseOptions.MaxConcurrentIngestions" /> concurrent documents
///     by a <see cref="SemaphoreSlim" /> (M2 — so N uploads cannot spin up N unbounded embedding pipelines). Each document
///     runs in its own <c>CreateAsyncScope()</c> with a fresh <see cref="CancellationToken.None" />, mirroring the memory
///     extraction dispatcher: a completed document's index write is never lost to a client-side cancellation. Failures are
///     handled inside the state machine; the worker's own catch-all logs the exception type only.
/// </summary>
public sealed class KnowledgeIngestionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly KnowledgeIngestionDispatcher _dispatcher;
    private readonly SemaphoreSlim _concurrency;
    private readonly ILogger<KnowledgeIngestionWorker> _logger;

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
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var documentId in _dispatcher.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            // Gate on the concurrency budget before starting the next document so at most MaxConcurrentIngestions run.
            await _concurrency.WaitAsync(stoppingToken).ConfigureAwait(false);
            _ = ProcessDocumentAsync(documentId);
        }
    }

    private async Task ProcessDocumentAsync(Guid documentId)
    {
        try
        {
            // Fresh scope + fresh DbContext per document; a fresh token so a shutdown/cancel never loses a completed write.
            await using var scope = _scopeFactory.CreateAsyncScope();
            var ingestionService = scope.ServiceProvider.GetRequiredService<IKnowledgeIngestionService>();
            await ingestionService.RunAsync(documentId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The state machine already records step failures; this is a last-resort guard for an unexpected fault such as
            // scope creation. Log the exception type only — never any document content.
            _logger.LogWarning("Background knowledge ingestion faulted for document {DocumentId} ({ErrorClass}).", documentId, exception.GetType().Name);
        }
        finally
        {
            _ = _concurrency.Release();
        }
    }

    public override void Dispose()
    {
        _concurrency.Dispose();
        base.Dispose();
    }
}
