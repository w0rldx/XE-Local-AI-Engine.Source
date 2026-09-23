namespace XE_Local_AI_Engine.Client.Services.Chat.Compaction;

using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

/// <summary>
///     Default <see cref="IConversationMaintenanceDispatcher" />: a bounded single-reader queue drained by
///     <see cref="ConversationMaintenanceWorker" />, with per-conversation coalescing.
/// </summary>
/// <remarks>
///     Same shape as <c>MemoryExtractionDispatcher</c>: <c>FullMode.Wait</c> plus <c>TryWrite</c> drops the newest job
///     with a warning instead of blocking the chat pump. A job stays in the pending set until the worker finishes it,
///     so a second trigger for the same conversation while one is queued or running is a no-op.
/// </remarks>
internal sealed class ConversationMaintenanceDispatcher : IConversationMaintenanceDispatcher
{
    private readonly Channel<ConversationMaintenanceJob> _queue;
    private readonly ConcurrentDictionary<(Guid ConversationId, ConversationMaintenanceKind Kind), byte> _pending = new();
    private readonly ILogger<ConversationMaintenanceDispatcher> _logger;

    public ConversationMaintenanceDispatcher(IOptions<ConversationCompactionOptions> options, ILogger<ConversationMaintenanceDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _queue = Channel.CreateBounded<ConversationMaintenanceJob>(new BoundedChannelOptions(Math.Max(1, options.Value.MaintenanceQueueCapacity))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    /// <summary>The queue reader the background worker drains.</summary>
    public ChannelReader<ConversationMaintenanceJob> Reader => _queue.Reader;

    public void Dispatch(ConversationMaintenanceJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        var key = (job.ConversationId, job.Kind);
        if (!_pending.TryAdd(key, 0))
        {
            _logger.LogDebug("Conversation {ConversationId} already has a queued or running {Kind} job; the new one is coalesced into it.", job.ConversationId, job.Kind);
            return;
        }

        if (!_queue.Writer.TryWrite(job))
        {
            _ = _pending.TryRemove(key, out _);
            _logger.LogWarning("Conversation maintenance queue is full or stopped; dropped a {Kind} job for conversation {ConversationId}. The chat turn is unaffected.",
                job.Kind,
                job.ConversationId);
        }
    }

    /// <summary>Releases the coalescing slot once the worker has finished (or dropped) <paramref name="job" />.</summary>
    public void Complete(ConversationMaintenanceJob job)
    {
        _ = _pending.TryRemove((job.ConversationId, job.Kind), out _);
    }

    /// <summary>Stops accepting jobs; idempotent. The worker's read loop then drains what is buffered and ends.</summary>
    public void CompleteWriter()
    {
        _ = _queue.Writer.TryComplete();
    }
}
