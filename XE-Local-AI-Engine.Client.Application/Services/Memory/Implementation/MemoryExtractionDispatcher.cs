namespace XE_Local_AI_Engine.Client.Services.Memory.Implementation;

using System.Threading.Channels;
using Microsoft.Extensions.Options;

/// <summary>
///     Default <see cref="IMemoryExtractionDispatcher" />. Owns the post-run extraction queue as a BOUNDED single-reader
///     <see cref="Channel{T}" /> of <see cref="MemoryExtractionJob" />s. Singleton: the queue outlives any request scope.
///     <para>
///         <see cref="Dispatch" /> is non-blocking (it must never delay the chat pump), so it TRY-writes and, when the
///         queue is at capacity, DROPS the newest job with a text-free warning rather than blocking or growing without
///         limit — each job carries conversation content, so an unbounded backlog would retain that content in memory
///         indefinitely. The <see cref="MemoryExtractionWorker" /> drains this queue under a bounded concurrency gate.
///     </para>
/// </summary>
internal sealed class MemoryExtractionDispatcher : IMemoryExtractionDispatcher
{
    private readonly Channel<MemoryExtractionJob> _queue;
    private readonly ILogger<MemoryExtractionDispatcher> _logger;

    public MemoryExtractionDispatcher(IOptions<MemoryExtractionOptions> options, ILogger<MemoryExtractionDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var capacity = Math.Max(1, options.Value.QueueCapacity);
        _queue = Channel.CreateBounded<MemoryExtractionJob>(new BoundedChannelOptions(capacity)
        {
            // FullMode.Wait so a full-queue TryWrite returns false (rather than silently dropping the OLDEST); we then
            // log the dropped newest job explicitly. SingleReader — the one background worker drains it.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    /// <summary>The queue reader the background worker drains.</summary>
    public ChannelReader<MemoryExtractionJob> Reader => _queue.Reader;

    public void Dispatch(MemoryExtractionDispatchContext telemetry, MemoryExtractionRunInput run)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(run);

        // Non-blocking enqueue: never block or throw into the chat pump. A full queue drops this job (the run simply
        // does not contribute a memory) with a content-free warning so the backlog stays bounded.
        if (!_queue.Writer.TryWrite(new MemoryExtractionJob(telemetry, run)))
        {
            _logger.LogWarning("Adaptive memory extraction queue is full; dropped a job for agent {AgentId}. The chat run is unaffected.",
                telemetry.AgentDefinitionId);
        }
    }
}

/// <summary>A queued extraction job: the metadata-only exec-log telemetry plus the content-bearing run input.</summary>
internal sealed record MemoryExtractionJob(MemoryExtractionDispatchContext Telemetry, MemoryExtractionRunInput Run);
