namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Threading.Channels;

/// <summary>
///     Default <see cref="IKnowledgeIngestionDispatcher" />. Owns the background ingestion queue as an unbounded
///     single-reader <see cref="Channel{T}" /> of document ids. Singleton: the queue outlives any request scope. The
///     actual M2 concurrency bound is enforced by the worker's <c>SemaphoreSlim</c> (see <see cref="KnowledgeIngestionWorker" />),
///     not by the queue capacity — the queue only holds tiny pending ids, so it never applies backpressure to the upload
///     endpoint.
/// </summary>
public sealed class KnowledgeIngestionDispatcher : IKnowledgeIngestionDispatcher
{
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    /// <summary>The queue reader the background worker drains.</summary>
    public ChannelReader<Guid> Reader => _queue.Reader;

    public ValueTask EnqueueAsync(Guid documentId, CancellationToken cancellationToken)
    {
        if (documentId == Guid.Empty)
        {
            throw new ArgumentException("A document id is required to enqueue ingestion.", nameof(documentId));
        }

        return _queue.Writer.WriteAsync(documentId, cancellationToken);
    }
}
