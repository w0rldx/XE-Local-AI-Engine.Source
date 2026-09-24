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

    Error,

    /// <summary>
    ///     An evaluation created from this run changed status. Evaluations ride the run's own group rather than a
    ///     group of their own — the kind is what separates the two streams.
    /// </summary>
    EvaluationState,

    /// <summary>One more hold-out sample carries a verdict. <c>Step</c>/<c>TotalSteps</c> are scored/total.</summary>
    EvaluationProgress,

    /// <summary>
    ///     An export step moved. Carries the pipeline phase (merging, converting, quantizing, inspecting, smoke) and,
    ///     on a terminal phase, the reason.
    /// </summary>
    /// <remarks>
    ///     The run's own status never moves for an export — the artifact row is the durable record — so this stream
    ///     is how the operator watches one happen.
    /// </remarks>
    Export
}

public sealed class TrainingRunPayload
{
    public string? State { get; init; }

    public string? Phase { get; init; }

    public int? Step { get; init; }

    public int? TotalSteps { get; init; }

    public double? Epoch { get; init; }

    public double? Loss { get; init; }

    public double? LearningRate { get; init; }

    public long? VramBytes { get; init; }

    public string? Message { get; init; }

    public long? RunVersion { get; init; }

    /// <summary>Set on the evaluation kinds only — which evaluation of this run the event describes.</summary>
    public Guid? EvaluationId { get; init; }

    /// <summary>Evaluation kinds only: how many of the scored samples passed.</summary>
    public int? PassedCount { get; init; }
}

public sealed class TrainingRunEvent
{
    public required Guid RunId { get; init; }

    public required long Sequence { get; init; }

    public required TrainingRunEventKind Kind { get; init; }

    public required TrainingRunPayload Payload { get; init; }
}

public sealed class TrainingRunEventArgs : EventArgs
{
    public TrainingRunEventArgs(TrainingRunEvent runEvent)
    {
        ArgumentNullException.ThrowIfNull(runEvent);
        Event = runEvent;
    }

    public TrainingRunEvent Event { get; }
}

public sealed class TrainingRunReplay
{
    public required IReadOnlyList<TrainingRunEvent> Events { get; init; }

    public required bool ResetRequired { get; init; }

    public required long LatestSequence { get; init; }
}

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

/// <summary>Bounded per-run replay ring, the dataset-generation buffer at the size this stream needs.</summary>
/// <remarks>
///     A run publishes coarse progress rather than token deltas, so there is no reserve/publish split: an event is
///     only raised once the state it describes is already durable.
/// </remarks>
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
            runEvent = new TrainingRunEvent
            {
                RunId = runId,
                Sequence = ++state.LatestSequence,
                Kind = kind,
                Payload = payload
            };
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
                return new TrainingRunReplay
                {
                    Events = [],
                    ResetRequired = false,
                    LatestSequence = 0
                };
            }

            var firstRetained = state.Events.First?.Value.Sequence;
            var reset = state.PlaintextEvicted
                        || (firstRetained is { } first && afterSequence < first - 1)
                        || (firstRetained is null && state.HistoryTruncated && afterSequence < state.LatestSequence);
            return reset
                ? new TrainingRunReplay
                {
                    Events = [],
                    ResetRequired = true,
                    LatestSequence = state.LatestSequence
                }
                : new TrainingRunReplay
                {
                    Events = state.Events.Where(item => item.Sequence > afterSequence).ToArray(),
                    ResetRequired = false,
                    LatestSequence = state.LatestSequence
                };
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

    /// <summary>
    ///     How long a trainer that has already closed its output may take to actually exit before its process group
    ///     is killed.
    /// </summary>
    /// <remarks>
    ///     A bound of its own rather than a reuse of <see cref="InactivityTimeout" />: silence <i>during</i> a run can be a
    ///     wedged CUDA call and is worth minutes, but a closed stream means the process is already tearing down, with only the
    ///     CUDA context release and the kernel reaping it and any child holding the pipe left to wait for — seconds, not
    ///     minutes. Keeping them separate also means an operator who shortens the silence tolerance for a chatty trainer does
    ///     not thereby start killing a slow one mid-teardown, and it lets the two be told apart when either fires.
    /// </remarks>
    public TimeSpan ExitGracePeriod { get; init; } = TimeSpan.FromSeconds(30);
}
