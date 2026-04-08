namespace XE_Local_AI_Engine.Services.DeadLetter
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Logging;
    using XE_Local_AI_Engine.Services.Connection;

    public sealed class DeadLetterFlushService
    {
        private readonly IDeadLetterStore _deadLetterStore;
        private readonly Lazy<IHubMessageSender> _hubMessageSender;
        private readonly ILogger<DeadLetterFlushService> _logger;

        public DeadLetterFlushService(
            IDeadLetterStore deadLetterStore,
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

            foreach (var pendingEntry in pendingEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await _hubMessageSender.Value
                        .SendInvocationFailedAsync(pendingEntry, cancellationToken)
                        .ConfigureAwait(false);

                    await _deadLetterStore.RemoveAsync(pendingEntry.InvocationId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        exception,
                        "Dead letter flush stopped after failing to resend invocation failure for {InvocationId}.",
                        pendingEntry.InvocationId);

                    return;
                }
            }

            _logger.LogInformation("Dead letter flush completed successfully.");
        }
    }
}
