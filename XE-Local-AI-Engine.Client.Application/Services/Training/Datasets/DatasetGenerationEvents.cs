namespace XE_Local_AI_Engine.Client.Services.Training.Datasets;

using Microsoft.Extensions.Options;

public enum DatasetGenerationEventKind
{
    State,
    Progress,
    SampleAdded,
    Rejected
}

public sealed record DatasetGenerationPayload(
    string? State = null,
    int? Completed = null,
    int? Total = null,
    string? Kind = null,
    string? Label = null,
    string? Reason = null,
    long? DatasetVersion = null);

public sealed record DatasetGenerationEvent(Guid DatasetId, long Sequence, DatasetGenerationEventKind Kind, DatasetGenerationPayload Payload);

public sealed class DatasetGenerationEventArgs(DatasetGenerationEvent generationEvent) : EventArgs
{
    public DatasetGenerationEvent Event { get; } = generationEvent ?? throw new ArgumentNullException(nameof(generationEvent));
}

public sealed record DatasetGenerationReplay(IReadOnlyList<DatasetGenerationEvent> Events, bool ResetRequired, long LatestSequence);

public interface IDatasetGenerationEventBuffer
{
    event EventHandler<DatasetGenerationEventArgs>? EventPublished;

    DatasetGenerationEvent Append(Guid datasetId, DatasetGenerationEventKind kind, DatasetGenerationPayload payload);

    DatasetGenerationReplay Replay(Guid datasetId, long afterSequence);

    /// <summary>Drops the retained plaintext for a dataset — called on every terminal path and on startup recovery.</summary>
    void EvictPlaintext(Guid datasetId);
}

public sealed class DatasetGenerationEventBufferOptions
{
    public const int DefaultMaxEventCount = 512;

    public int MaxEventCount { get; init; } = DefaultMaxEventCount;
}

/// <summary>
///     Bounded per-dataset replay ring, the <c>BenchmarkEventBuffer</c> pattern at the size this stream needs. Dataset
///     generation publishes coarse progress rather than token deltas, so there is no reserve/publish split here: an event
///     is only ever raised after the sample it describes is already durable.
/// </summary>
public sealed class DatasetGenerationEventBuffer : IDatasetGenerationEventBuffer
{
    private readonly Dictionary<Guid, DatasetBuffer> _datasets = [];
    private readonly Lock _gate = new();
    private readonly int _maxEventCount;

    public DatasetGenerationEventBuffer(IOptions<DatasetGenerationEventBufferOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _maxEventCount = options.Value.MaxEventCount;
        if (_maxEventCount <= 0)
        {
            throw new InvalidOperationException("The dataset generation event buffer bound must be positive.");
        }
    }

    public event EventHandler<DatasetGenerationEventArgs>? EventPublished;

    public DatasetGenerationEvent Append(Guid datasetId, DatasetGenerationEventKind kind, DatasetGenerationPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        DatasetGenerationEvent generationEvent;
        lock (_gate)
        {
            var state = GetOrCreate(datasetId);
            generationEvent = new DatasetGenerationEvent(datasetId, ++state.LatestSequence, kind, payload);
            state.Events.AddLast(generationEvent);
            while (state.Events.Count > _maxEventCount)
            {
                state.Events.RemoveFirst();
                state.HistoryTruncated = true;
            }
        }

        EventPublished?.Invoke(this, new DatasetGenerationEventArgs(generationEvent));
        return generationEvent;
    }

    public DatasetGenerationReplay Replay(Guid datasetId, long afterSequence)
    {
        lock (_gate)
        {
            if (!_datasets.TryGetValue(datasetId, out var state))
            {
                return new DatasetGenerationReplay([], ResetRequired: false, LatestSequence: 0);
            }

            var firstRetained = state.Events.First?.Value.Sequence;
            var reset = state.PlaintextEvicted
                        || (firstRetained is { } first && afterSequence < first - 1)
                        || (firstRetained is null && state.HistoryTruncated && afterSequence < state.LatestSequence);
            return reset
                ? new DatasetGenerationReplay([], ResetRequired: true, state.LatestSequence)
                : new DatasetGenerationReplay(state.Events.Where(item => item.Sequence > afterSequence).ToArray(),
                    ResetRequired: false,
                    state.LatestSequence);
        }
    }

    public void EvictPlaintext(Guid datasetId)
    {
        lock (_gate)
        {
            var state = GetOrCreate(datasetId);
            state.Events.Clear();
            state.PlaintextEvicted = true;
        }
    }

    private DatasetBuffer GetOrCreate(Guid datasetId)
    {
        if (datasetId == Guid.Empty)
        {
            throw new ArgumentException("The dataset id must be non-empty.", nameof(datasetId));
        }

        if (!_datasets.TryGetValue(datasetId, out var state))
        {
            state = new DatasetBuffer();
            _datasets.Add(datasetId, state);
        }

        return state;
    }

    private sealed class DatasetBuffer
    {
        public long LatestSequence { get; set; }
        public bool PlaintextEvicted { get; set; }
        public bool HistoryTruncated { get; set; }
        public LinkedList<DatasetGenerationEvent> Events { get; } = [];
    }
}

public interface IDatasetGenerationQueueSignal
{
    void Wake();

    /// <summary>Waits for a wake or the timeout. True means a wake was consumed, false that the poll interval elapsed.</summary>
    Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Coalescing wake-up for the single generation consumer: a pending wake is sufficient, so a second is dropped.</summary>
public sealed class DatasetGenerationQueueSignal : IDatasetGenerationQueueSignal, IDisposable
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

public sealed class DatasetGenerationQueueOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
}
