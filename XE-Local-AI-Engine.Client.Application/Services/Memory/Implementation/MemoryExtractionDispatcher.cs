namespace XE_Local_AI_Engine.Client.Services.Memory.Implementation;

using System.Threading.Channels;
using Microsoft.Extensions.Options;

/// <summary>
///     Default <see cref="IMemoryExtractionDispatcher" />, owning the post-run extraction queue as a BOUNDED
///     single-reader <see cref="Channel{T}" /> whose singleton lifetime outlives any request scope.
/// </summary>
/// <remarks>
///     <see cref="Dispatch" /> must never delay the chat pump, so it TRY-writes and DROPS the newest job with a
///     text-free warning at capacity rather than blocking or growing: each job carries conversation content, so an
///     unbounded backlog would retain it in memory indefinitely. <see cref="MemoryExtractionWorker" /> drains the
///     queue under a bounded concurrency gate.
/// </remarks>
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

    /// <summary>
    ///     Completes the queue writer so no further job is accepted.
    /// </summary>
    /// <remarks>
    ///     After it, <see cref="Dispatch" /> takes the dropped-job path and the worker's read loop drains what is
    ///     buffered and ends on its own rather than being abandoned. It uses <c>TryComplete</c> so it stays
    ///     idempotent, because the worker completes it both at shutdown and from a stop-token callback.
    /// </remarks>
    public void CompleteWriter()
    {
        _ = _queue.Writer.TryComplete();
    }

    public void Dispatch(MemoryExtractionDispatchContext telemetry, MemoryExtractionRunInput run)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(run);

        // Non-blocking enqueue: never block or throw into the chat pump. A full queue drops this job (the run simply
        // does not contribute a memory) with a content-free warning so the backlog stays bounded.
        if (!_queue.Writer.TryWrite(new MemoryExtractionJob
            {
                Telemetry = telemetry,
                Run = run
            }))
        {
            _logger.LogWarning("Adaptive memory extraction queue is full; dropped a job for agent {AgentId}. The chat run is unaffected.",
                telemetry.AgentDefinitionId);
        }
    }
}

/// <summary>A queued extraction job: the metadata-only exec-log telemetry plus the content-bearing run input.</summary>
internal sealed class MemoryExtractionJob
{
    public required MemoryExtractionDispatchContext Telemetry { get; init; }

    public required MemoryExtractionRunInput Run { get; init; }
}
