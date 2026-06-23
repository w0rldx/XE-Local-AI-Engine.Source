namespace XE_Local_AI_Engine.Client.Services.Events.Implementation;

#pragma warning disable CA1812 // Instantiated by DI container.
internal sealed class InvocationHistory : IInvocationHistory
#pragma warning restore CA1812
{
    private const int DefaultCapacity = 50;

    private readonly LinkedList<InvocationHistoryEntry> _entries = new();
    private readonly ILogger<InvocationHistory> _logger;
    private readonly HashSet<Guid> _recordedInvocationIds = [];
    private readonly Lock _syncRoot = new();

    public InvocationHistory(ILogger<InvocationHistory> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public event EventHandler<InvocationHistoryEntryAddedEventArgs>? EntryAdded;

    public int Capacity => DefaultCapacity;

    public IReadOnlyList<InvocationHistoryEntry> Snapshot()
    {
        lock (_syncRoot)
        {
            return [.. _entries];
        }
    }

    public void Record(InvocationState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.Status is not (InvocationStatus.Completed or InvocationStatus.Failed or InvocationStatus.Cancelled))
        {
            return;
        }

        var entry = new InvocationHistoryEntry(state.InvocationId,
            state.ConversationId,
            state.Status,
            state.ModelUsed,
            state.StartedAt,
            state.CompletedAt ?? DateTimeOffset.UtcNow,
            state.Error,
            state.FailureCategory,
            state.StreamedChunkCount,
            state.StreamedThinkingChunkCount);

        lock (_syncRoot)
        {
            if (!_recordedInvocationIds.Add(entry.InvocationId))
            {
                return;
            }

            _entries.AddFirst(entry);

            while (_entries.Count > DefaultCapacity)
            {
                var oldest = _entries.Last!;
                _entries.RemoveLast();
                _ = _recordedInvocationIds.Remove(oldest.Value.InvocationId);
            }
        }

        _logger.LogInformation(
            "Invocation completed. InvocationId={InvocationId} ConversationId={ConversationId} Status={Status} Model={Model} DurationMs={DurationMs} Chunks={Chunks} ThinkingChunks={ThinkingChunks} FailureCategory={FailureCategory} Error={Error}",
            entry.InvocationId,
            entry.ConversationId,
            entry.Status,
            entry.ModelUsed,
            (long)entry.Duration.TotalMilliseconds,
            entry.StreamedChunkCount,
            entry.StreamedThinkingChunkCount,
            entry.FailureCategory,
            entry.Error);

        Volatile.Read(ref EntryAdded)?.Invoke(this, new InvocationHistoryEntryAddedEventArgs(entry));
    }
}
