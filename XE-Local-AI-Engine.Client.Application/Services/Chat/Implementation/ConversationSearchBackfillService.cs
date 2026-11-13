namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

/// <summary>
///     Fills the conversation search index from every chat conversation stored before this process started, in the
///     background, so start-up readiness never waits on it.
/// </summary>
/// <remarks>
///     It starts after migrations and the interrupted-turn recovery, which the host runs before any hosted service. A
///     failed pass, or one that could not read some conversations, is retried with capped exponential backoff until it
///     succeeds or the host stops; a retry skips conversations already indexed, so it only adds what was missed.
/// </remarks>
public sealed class ConversationSearchBackfillService : BackgroundService
{
    private const int BatchSize = 50;

    internal static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(5);

    internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);

    private readonly ILogger<ConversationSearchBackfillService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly NodeChatPersistenceWriter _writer;

    public ConversationSearchBackfillService(NodeChatPersistenceWriter writer, TimeProvider timeProvider, ILogger<ConversationSearchBackfillService> logger)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _writer = writer;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var retryDelay = FirstRetryDelay;
        while (true)
        {
            try
            {
                var skipped = await new NodeChatReadModel(_writer).BackfillSearchIndexAsync(BatchSize, stoppingToken);
                if (skipped.Count == 0)
                {
                    _logger.LogInformation("Conversation search index holds {ConversationCount} conversation(s) after the start-up backfill.", _writer.SearchIndex.Count);
                    return;
                }

                _logger.LogWarning("Conversation search backfill could not read conversation(s) {ConversationIds}; the rest are searchable. Retrying those in {RetryDelay}.",
                    string.Join(", ", skipped), retryDelay);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutdown mid-backfill: the index is memory only, so there is nothing to leave consistent.
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Conversation search backfill failed; searches report the index as still indexing. Retrying in {RetryDelay}.", retryDelay);
            }

            try
            {
                await Task.Delay(retryDelay, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            retryDelay = retryDelay * 2 < MaxRetryDelay ? retryDelay * 2 : MaxRetryDelay;
        }
    }
}
