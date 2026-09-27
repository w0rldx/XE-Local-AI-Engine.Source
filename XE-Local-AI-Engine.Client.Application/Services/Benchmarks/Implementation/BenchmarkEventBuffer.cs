namespace XE_Local_AI_Engine.Client.Services.Benchmarks.Implementation;

using System.Text.Json;
using Microsoft.Extensions.Options;

public sealed class BenchmarkEventBuffer : IBenchmarkEventBuffer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Lock _gate = new();
    private readonly int _maxEventCount;
    private readonly int _maxUtf8Bytes;
    private readonly int _maxRetainedTerminalRuns;
    private readonly Dictionary<Guid, RunBuffer> _runs = [];

    /// <summary>Terminal runs in eviction order, so the oldest tombstone is the one dropped when the cap is reached.</summary>
    private readonly Queue<Guid> _evicted = new();

    public BenchmarkEventBuffer(IOptions<BenchmarkEventBufferOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _maxEventCount = options.Value.MaxEventCount;
        _maxUtf8Bytes = options.Value.MaxUtf8Bytes;
        _maxRetainedTerminalRuns = options.Value.MaxRetainedTerminalRuns;
        if (_maxEventCount <= 0 || _maxUtf8Bytes <= 0 || _maxRetainedTerminalRuns <= 0)
        {
            throw new InvalidOperationException("Benchmark event buffer limits must be positive.");
        }
    }

    /// <summary>How many runs the buffer still holds an entry for. Test-only seam.</summary>
    internal int TrackedRunCount
    {
        get
        {
            lock (_gate)
            {
                return _runs.Count;
            }
        }
    }

    public event EventHandler<BenchmarkRunStreamEventArgs>? EventPublished;

    public BenchmarkRunStreamEvent Append(Guid runId, BenchmarkRunStreamEventKind kind, BenchmarkRunStreamPayload payload)
    {
        var streamEvent = Reserve(runId, kind, payload);
        PublishReserved(streamEvent);
        return streamEvent;
    }

    public BenchmarkRunStreamEvent Reserve(Guid runId, BenchmarkRunStreamEventKind kind, BenchmarkRunStreamPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        lock (_gate)
        {
            var state = GetOrCreate(runId);
            return new BenchmarkRunStreamEvent
            {
                RunId = runId,
                Sequence = ++state.LatestSequence,
                Kind = kind,
                Payload = payload
            };
        }
    }

    public void PublishReserved(BenchmarkRunStreamEvent streamEvent)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);
        lock (_gate)
        {
            var state = GetOrCreate(streamEvent.RunId);
            if (streamEvent.Sequence <= state.LastPublishedSequence)
            {
                return;
            }

            if (streamEvent.Sequence > state.LatestSequence)
            {
                throw new InvalidOperationException("A benchmark stream event must be reserved before it is published.");
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(streamEvent, JsonOptions).Length;
            state.Events.AddLast(new BufferedEvent
            {
                Event = streamEvent,
                Utf8Bytes = bytes
            });
            state.Utf8Bytes += bytes;
            state.LastPublishedSequence = streamEvent.Sequence;
            Trim(state);
        }

        EventPublished?.Invoke(this, new BenchmarkRunStreamEventArgs(streamEvent));
    }

    public BenchmarkReplayResult Replay(Guid runId, long afterSequence, long runVersion)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out var state))
            {
                return new BenchmarkReplayResult
                {
                    Events = [],
                    ResetRequired = false,
                    LatestSequence = 0,
                    RunVersion = runVersion
                };
            }

            var firstRetained = state.Events.First?.Value.Event.Sequence;
            var reset = state.PlaintextEvicted
                        || firstRetained is { } first && afterSequence < first - 1
                        || firstRetained is null && state.HistoryTruncated && afterSequence < state.LatestSequence;
            if (reset)
            {
                return new BenchmarkReplayResult
                {
                    Events = [],
                    ResetRequired = true,
                    LatestSequence = state.LatestSequence,
                    RunVersion = runVersion
                };
            }

            var events = state.Events.Where(item => item.Event.Sequence > afterSequence).Select(item => item.Event).ToArray();
            return new BenchmarkReplayResult
            {
                Events = events,
                ResetRequired = false,
                LatestSequence = state.LatestSequence,
                RunVersion = runVersion
            };
        }
    }

    public void BeginActivePhase(Guid runId, long persistedSequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(persistedSequence);

        lock (_gate)
        {
            var state = GetOrCreate(runId);
            var hadHistory = state.LatestSequence > 0 || state.Events.Count > 0 || state.PlaintextEvicted;
            state.Events.Clear();
            state.Utf8Bytes = 0;
            state.LatestSequence = Math.Max(state.LatestSequence, persistedSequence);
            state.LastPublishedSequence = Math.Max(state.LastPublishedSequence, persistedSequence);
            state.HistoryTruncated = hadHistory || persistedSequence > 0;
            state.PlaintextEvicted = false;
        }
    }

    public void EvictPlaintext(Guid runId)
    {
        lock (_gate)
        {
            var state = GetOrCreate(runId);
            state.Events.Clear();
            state.Utf8Bytes = 0;

            // The entry survives eviction on purpose: emptied, it still turns a late subscriber's replay into a reset rather than silence. It is also the leak, so the tombstones are capped.
            // Queued is its OWN flag, not PlaintextEvicted: a run is evicted once per terminal PHASE and has two, and BeginActivePhase clears it between them — keying the queue off it halves the cap.
            state.PlaintextEvicted = true;
            if (!state.Queued)
            {
                state.Queued = true;
                _evicted.Enqueue(runId);
            }

            while (_evicted.Count > _maxRetainedTerminalRuns)
            {
                var oldest = _evicted.Dequeue();

                // Skipped when the run went active again (a judge phase after the primary): its entry belongs to a live stream, and dropping it would restart that stream's sequence numbering.
                // Either way the id leaves the queue, so a run that is spared here can be enqueued again by its next eviction.
                if (_runs.TryGetValue(oldest, out var stale))
                {
                    stale.Queued = false;
                    if (stale.PlaintextEvicted)
                    {
                        _ = _runs.Remove(oldest);
                    }
                }
            }
        }
    }

    private RunBuffer GetOrCreate(Guid runId)
    {
        if (runId == Guid.Empty)
        {
            throw new ArgumentException("Benchmark run id must be non-empty.", nameof(runId));
        }

        if (!_runs.TryGetValue(runId, out var state))
        {
            state = new RunBuffer();
            _runs.Add(runId, state);
        }

        return state;
    }

    private void Trim(RunBuffer state)
    {
        while (state.Events.Count > _maxEventCount || state.Utf8Bytes > _maxUtf8Bytes)
        {
            var first = state.Events.First;
            if (first is null)
            {
                break;
            }

            state.Utf8Bytes -= first.Value.Utf8Bytes;
            state.Events.RemoveFirst();
            state.HistoryTruncated = true;
        }
    }

    private sealed class RunBuffer
    {
        public long LatestSequence { get; set; }
        public long LastPublishedSequence { get; set; }
        public int Utf8Bytes { get; set; }
        public bool PlaintextEvicted { get; set; }

        /// <summary>Whether this run's id is currently in the tombstone queue. Owned by the queue, not by a phase.</summary>
        public bool Queued { get; set; }

        public bool HistoryTruncated { get; set; }
        public LinkedList<BufferedEvent> Events { get; } = [];
    }

    private sealed record BufferedEvent
    {
        public required BenchmarkRunStreamEvent Event { get; init; }

        public required int Utf8Bytes { get; init; }
    }
}
