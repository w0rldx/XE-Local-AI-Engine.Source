namespace XE_Local_AI_Engine.Client.Services.Training.Runs;

using Microsoft.Extensions.Options;

public enum TrainingRunEventKind
{
    /// <summary>The run's own status moved (Preparing, Training, Succeeded, …).</summary>
    State,

    /// <summary>The trainer's internal phase moved (loading, tokenizing, training, saving).</summary>
    Phase,

    Progress,

    /// <summary>An artifact was registered against the run.</summary>
    Artifact,

    /// <summary>
    ///     An export step moved. Carries the pipeline phase (merging, converting, quantizing, inspecting, smoke) and,
    ///     on a terminal phase, the reason. The run's own status never moves for an export — the artifact row is the
    ///     durable record — so this stream is how the operator watches one happen.
    /// </summary>
    Export,

    Error
}

public sealed record TrainingRunPayload(
    string? State = null,
    string? Phase = null,
    int? Step = null,
    int? TotalSteps = null,
    double? Epoch = null,
    double? Loss = null,
    double? LearningRate = null,
    long? VramBytes = null,
    string? Message = null,
    long? RunVersion = null);

public sealed record TrainingRunEvent(Guid RunId, long Sequence, TrainingRunEventKind Kind, TrainingRunPayload Payload);

public sealed class TrainingRunEventArgs(TrainingRunEvent runEvent) : EventArgs
{
    public TrainingRunEvent Event { get; } = runEvent ?? throw new ArgumentNullException(nameof(runEvent));
}

public sealed record TrainingRunReplay(IReadOnlyList<TrainingRunEvent> Events, bool ResetRequired, long LatestSequence);

public interface ITrainingRunEventBuffer
{
    event EventHandler<TrainingRunEventArgs>? EventPublished;

    TrainingRunEvent Append(Guid runId, TrainingRunEventKind kind, TrainingRunPayload payload);

    TrainingRunReplay Replay(Guid runId, long afterSequence);

    /// <summary>Drops the retained events for a run — every terminal path and startup recovery.</summary>
    void EvictPlaintext(Guid runId);
}

public sealed class TrainingRunEventBufferOptions
{
    public const int DefaultMaxEventCount = 512;

    public int MaxEventCount { get; init; } = DefaultMaxEventCount;
}

/// <summary>
///     Bounded per-run replay ring, the dataset-generation buffer at the size this stream needs. A run publishes
///     coarse progress rather than token deltas, so there is no reserve/publish split: an event is only raised once the
///     state it describes is already durable.
/// </summary>
public sealed class TrainingRunEventBuffer : ITrainingRunEventBuffer
{
    private readonly Dictionary<Guid, RunBuffer> _runs = [];
    private readonly Lock _gate = new();
    private readonly int _maxEventCount;

    public TrainingRunEventBuffer(IOptions<TrainingRunEventBufferOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _maxEventCount = options.Value.MaxEventCount;
        if (_maxEventCount <= 0)
        {
            throw new InvalidOperationException("The training run event buffer bound must be positive.");
        }
    }

    public event EventHandler<TrainingRunEventArgs>? EventPublished;

    public TrainingRunEvent Append(Guid runId, TrainingRunEventKind kind, TrainingRunPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        TrainingRunEvent runEvent;
        lock (_gate)
        {
            var state = GetOrCreate(runId);
            runEvent = new TrainingRunEvent(runId, ++state.LatestSequence, kind, payload);
            state.Events.AddLast(runEvent);
            while (state.Events.Count > _maxEventCount)
            {
                state.Events.RemoveFirst();
                state.HistoryTruncated = true;
            }
        }

        EventPublished?.Invoke(this, new TrainingRunEventArgs(runEvent));
        return runEvent;
    }

    public TrainingRunReplay Replay(Guid runId, long afterSequence)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out var state))
            {
                return new TrainingRunReplay([], ResetRequired: false, LatestSequence: 0);
            }

            var firstRetained = state.Events.First?.Value.Sequence;
            var reset = state.PlaintextEvicted
                        || (firstRetained is { } first && afterSequence < first - 1)
                        || (firstRetained is null && state.HistoryTruncated && afterSequence < state.LatestSequence);
            return reset
                ? new TrainingRunReplay([], ResetRequired: true, state.LatestSequence)
                : new TrainingRunReplay(state.Events.Where(item => item.Sequence > afterSequence).ToArray(),
                    ResetRequired: false,
                    state.LatestSequence);
        }
    }

    public void EvictPlaintext(Guid runId)
    {
        lock (_gate)
        {
            var state = GetOrCreate(runId);
            state.Events.Clear();
            state.PlaintextEvicted = true;
        }
    }

    private RunBuffer GetOrCreate(Guid runId)
    {
        if (runId == Guid.Empty)
        {
            throw new ArgumentException("The run id must be non-empty.", nameof(runId));
        }

        if (!_runs.TryGetValue(runId, out var state))
        {
            state = new RunBuffer();
            _runs.Add(runId, state);
        }

        return state;
    }

    private sealed class RunBuffer
    {
        public long LatestSequence { get; set; }
        public bool PlaintextEvicted { get; set; }
        public bool HistoryTruncated { get; set; }
        public LinkedList<TrainingRunEvent> Events { get; } = [];
    }
}

public interface ITrainingRunQueueSignal
{
    void Wake();

    /// <summary>True when a wake was consumed, false when the poll interval elapsed.</summary>
    Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Coalescing wake-up for the single run consumer: one pending wake is sufficient, so a second is dropped.</summary>
public sealed class TrainingRunQueueSignal : ITrainingRunQueueSignal, IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public void Dispose() =>
        _semaphore.Dispose();

    public void Wake()
    {
        try
        {
            _ = _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake is already pending; that is sufficient.
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _semaphore.WaitAsync(timeout, cancellationToken);
}

public sealed class TrainingRunQueueOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     How long the trainer may emit nothing parseable before the run is killed. A silent trainer is either wedged
    ///     on a CUDA call or has died without closing its pipes; both need the GPU back.
    /// </summary>
    public TimeSpan InactivityTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Absolute ceiling on one run, so a pathological configuration cannot hold the GPU forever.</summary>
    public TimeSpan MaxRunDuration { get; init; } = TimeSpan.FromHours(24);
}
