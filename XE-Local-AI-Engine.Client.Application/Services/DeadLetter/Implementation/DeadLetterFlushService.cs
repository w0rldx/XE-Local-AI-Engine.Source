namespace XE_Local_AI_Engine.Client.Services.DeadLetter.Implementation;

using XE_Local_AI_Engine.Client.Services.Connection;

public sealed class DeadLetterFlushService
{
    private readonly IDeadLetterStore _deadLetterStore;
    private readonly Lazy<IHubMessageSender> _hubMessageSender;
    private readonly ILogger<DeadLetterFlushService> _logger;

    public DeadLetterFlushService(IDeadLetterStore deadLetterStore,
        Lazy<IHubMessageSender> hubMessageSender,
        ILogger<DeadLetterFlushService> logger)
    {
        _deadLetterStore = deadLetterStore ?? throw new ArgumentNullException(nameof(deadLetterStore));
        _hubMessageSender = hubMessageSender ?? throw new ArgumentNullException(nameof(hubMessageSender));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var pendingEntries = await _deadLetterStore.GetPendingAsync(cancellationToken).ConfigureAwait(false);
        if (pendingEntries.Count == 0)
        {
            _logger.LogDebug("Dead letter flush skipped because no pending entries were found.");
            return;
        }

        _logger.LogInformation("Flushing {PendingEntryCount} dead letter entries.", pendingEntries.Count);

        var flushed = 0;
        foreach (var pendingEntry in pendingEntries)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                LogDeadlineDrop(flushed, pendingEntries.Count);
                cancellationToken.ThrowIfCancellationRequested();
            }

            try
            {
                await _hubMessageSender.Value
                                       .SendInvocationFailedAsync(pendingEntry, cancellationToken)
                                       .ConfigureAwait(false);

                await _deadLetterStore.RemoveAsync(pendingEntry.InvocationId, cancellationToken).ConfigureAwait(false);
                flushed++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The shutdown deadline fired mid-send. Record how many entries stay queued (they are never removed
                // until resent) and propagate so the drain records the stage as deadline-exceeded rather than complete.
                LogDeadlineDrop(flushed, pendingEntries.Count);
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception,
                    "Dead letter flush stopped after failing to resend invocation failure for {InvocationId}.",
                    pendingEntry.InvocationId);

                return;
            }
        }

        _logger.LogInformation("Dead letter flush completed successfully.");
    }

    private void LogDeadlineDrop(int flushed, int total)
    {
        _logger.LogWarning("Dead letter flush stopped at the shutdown deadline: {FlushedCount} flushed, {DroppedCount} left pending for a later run.",
            flushed,
            total - flushed);
    }
}
