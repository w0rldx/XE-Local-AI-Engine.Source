namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

/// <summary>
///     Fills the conversation search index from every chat conversation stored before this process started, in the
///     background, so start-up readiness never waits on it.
/// </summary>
/// <remarks>
///     It starts after migrations and the interrupted-turn recovery, which the host runs before any hosted service. A
///     failed pass is retried with capped exponential backoff until one succeeds or the host stops. A conversation a
///     pass could not read is retried the same way, but only for <see cref="MaxReadAttempts" /> passes: after that it is
///     logged once and left out, and a later write to it marks it stale, so the next search reads it again. A retry
///     skips conversations already indexed, so it only adds what was missed.
/// </remarks>
public sealed class ConversationSearchBackfillService : BackgroundService
{
    private const int BatchSize = 50;

    // How many ids one log line names; the count says how many there are in all.
    private const int MaxLoggedIds = 10;

    /// <summary>Passes that may fail to read one conversation before the backfill stops retrying it: one corrupt row must not warn for the life of the process.</summary>
    internal const int MaxReadAttempts = 3;

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
        var failedReads = new Dictionary<Guid, int>();
        while (true)
        {
            try
            {
                var skipped = await new NodeChatReadModel(_writer).BackfillSearchIndexAsync(BatchSize, stoppingToken);
                var retrying = new List<Guid>();
                var givenUp = new List<Guid>();
                foreach (var conversationId in skipped)
                {
                    var attempts = failedReads[conversationId] = failedReads.GetValueOrDefault(conversationId) + 1;
                    if (attempts < MaxReadAttempts)
                    {
                        retrying.Add(conversationId);
                    }
                    else if (attempts == MaxReadAttempts)
                    {
                        // Logged by the one pass that reaches the limit; later passes still read it while others retry.
                        givenUp.Add(conversationId);
                    }
                }

                if (givenUp.Count > 0)
                {
                    _logger.LogWarning(
                        "Conversation search backfill could not read {ConversationCount} conversation(s) in {Attempts} passes and stops retrying them: {ConversationIds}. A write to one marks it for the next search.",
                        givenUp.Count, MaxReadAttempts, string.Join(", ", givenUp.Take(MaxLoggedIds)));
                }

                if (retrying.Count == 0)
                {
                    _logger.LogInformation("Conversation search index holds {ConversationCount} conversation(s) after the start-up backfill.", _writer.SearchIndex.Count);
                    return;
                }

                _logger.LogWarning("Conversation search backfill could not read {ConversationCount} conversation(s), {ConversationIds}; the rest are searchable. Retrying those in {RetryDelay}.",
                    retrying.Count, string.Join(", ", retrying.Take(MaxLoggedIds)), retryDelay);
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
