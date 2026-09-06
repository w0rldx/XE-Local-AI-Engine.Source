namespace XE_Local_AI_Engine.Client.Services.Integrations.Implementation;

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Options;

/// <summary>
///     Default <see cref="IIntegrationExecutionEventBuffer" />. Ring shape copied from <c>BenchmarkEventBuffer</c>: one
///     lock over the whole dictionary, a per-execution <see cref="LinkedList{T}" /> whose entries carry their own
///     serialized byte length, and a FIFO of terminal ids for eviction.
/// </summary>
/// <remarks>
///     ponytail: one node-wide lock over the whole dictionary rather than a lock per execution. MaxTrackedExecutions is
///     64 and appends run at human-answer rate; per-execution locks if a profile ever shows contention here.
/// </remarks>
internal sealed class IntegrationExecutionEventBuffer : IIntegrationExecutionEventBuffer, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The three types that make an entry evictable once PUBLISHED. Reserving one is not enough (ruling R4-2).</summary>
    private static readonly IReadOnlySet<string> TerminalTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        IntegrationStreamEventTypes.ExecutionCompleted,
        IntegrationStreamEventTypes.ExecutionFailed,
        IntegrationStreamEventTypes.ExecutionCancelled
    };

    private readonly int _capacity;
    private readonly Dictionary<Guid, ExecutionBuffer> _entries = [];
    private readonly Lock _gate = new();
    private readonly int _maxBytes;
    private readonly int _maxTracked;
    private readonly CancellationTokenSource _sweepCancellation = new();
    private readonly Task _sweepLoop;
    private readonly PeriodicTimer _sweepTimer;

    /// <summary>Terminal entries in eviction order, so the OLDEST terminal one is the one dropped under pressure.</summary>
    private readonly Queue<Guid> _terminal = new();

    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _ttl;

    public IntegrationExecutionEventBuffer(IOptions<IntegrationOptions> options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        var value = options.Value;
        _capacity = value.EventBufferCapacity;
        _maxBytes = value.EventBufferMaxBytes;
        _maxTracked = value.MaxTrackedExecutions;
        _ttl = value.EventBufferTtlAfterTerminal;

        // The sweep lives HERE rather than in a hosted service: it needs this lock and nothing else, and a hosted
        // service would add a registration and a lifetime to reason about for no gain. Sweeping at most once a minute,
        // and never slower than the TTL itself, keeps a short configured TTL honest.
        _sweepTimer = new PeriodicTimer(_ttl < TimeSpan.FromMinutes(1) ? _ttl : TimeSpan.FromMinutes(1), _timeProvider);
        _sweepLoop = RunSweepLoopAsync();
    }

    /// <summary>How many ids the eviction FIFO holds. Test-only seam.</summary>
    internal int EvictionQueueDepth
    {
        get
        {
            lock (_gate)
            {
                return _terminal.Count;
            }
        }
    }

    /// <summary>How many executions hold an entry. Test-only seam.</summary>
    internal int TrackedCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public bool TryCreate(Guid executionId, long initialSequence = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialSequence);

        lock (_gate)
        {
            if (_entries.ContainsKey(executionId))
            {
                return true;
            }

            while (_entries.Count >= _maxTracked)
            {
                if (!TryEvictOldestTerminal())
                {
                    // Every tracked execution is live or still resolving a reservation. A live one is never evicted
                    // under pressure — a reader attached to it would get a gap for a run that is still producing.
                    return false;
                }
            }

            _entries.Add(executionId,
                new ExecutionBuffer
                {
                    LatestSequence = initialSequence,
                    LastAppendAtUtc = NowUnixMilliseconds()
                });
            return true;
        }
    }

    public void Remove(Guid executionId)
    {
        lock (_gate)
        {
            if (_entries.Remove(executionId, out var entry))
            {
                entry.Queued = false;
                DropFromEvictionQueue(executionId);
                // A parked reader waits on this entry's source and nothing else. Dropping the entry without completing
                // it would leave that reader waiting on a source no writer can ever reach: it must wake, find the entry
                // gone and answer the gap.
                Wake(entry);
            }
        }
    }

    public IntegrationStreamEvent Append(Guid executionId, Guid sessionId, string type, string? contentType, JsonElement? payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        // Clone OUTSIDE the lock. A caller may hand over an element whose parent JsonDocument is disposed the moment
        // its request ends, after which reading it throws from inside the stream writer — a failure that surfaces in a
        // different thread, slices later, in someone else's code.
        var owned = payload?.Clone();

        lock (_gate)
        {
            var entry = Require(executionId);
            var streamEvent = new IntegrationStreamEvent(type,
                ++entry.LatestSequence,
                executionId,
                sessionId,
                NowUnixMilliseconds(),
                contentType,
                owned);
            Insert(entry, streamEvent);
            return streamEvent;
        }
    }

    public long Reserve(Guid executionId)
    {
        lock (_gate)
        {
            var entry = Require(executionId);
            var sequence = ++entry.LatestSequence;
            _ = entry.Pending.Add(sequence);
            return sequence;
        }
    }

    public void Publish(IntegrationStreamEvent streamEvent)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);

        var owned = streamEvent.Payload?.Clone();

        lock (_gate)
        {
            var entry = Require(streamEvent.ExecutionId);
            if (!entry.Pending.Remove(streamEvent.Sequence))
            {
                // Never reserved, reserved for another execution, or already resolved. All three are wiring bugs, and
                // a silent accept here would put an event on the stream that no reservation is holding a reader for.
                throw new InvalidOperationException($"Integration event sequence {streamEvent.Sequence} for execution {streamEvent.ExecutionId} was not reserved, or was already resolved.");
            }

            Insert(entry, streamEvent with
            {
                Payload = owned
            });
        }
    }

    public void Abandon(Guid executionId, long sequence)
    {
        lock (_gate)
        {
            var entry = Require(executionId);
            if (!entry.Pending.Remove(sequence))
            {
                throw new InvalidOperationException($"Integration event sequence {sequence} for execution {executionId} was not reserved, or was already resolved.");
            }

            // Wake readers even though nothing became readable: a reader parked at this barrier must learn the hole is
            // permanent and move on, or the stream stalls until the entry is evicted.
            Wake(entry);
        }
    }

    public long LowestPendingReservation(Guid executionId)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(executionId, out var entry) && entry.Pending.Count > 0 ? entry.Pending.Min : long.MaxValue;
        }
    }

    public bool IsTracked(Guid executionId)
    {
        lock (_gate)
        {
            return _entries.ContainsKey(executionId);
        }
    }

    public long LastSequence(Guid executionId)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(executionId, out var entry) ? entry.LatestSequence : 0;
        }
    }

    public long Floor(Guid executionId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(executionId, out var entry))
            {
                return 0;
            }

            // The DROPPED watermark, never just the surviving head: a single event larger than the byte cap empties the
            // list outright, and reading the floor off an empty list reports 0 — which a reader's
            // `sinceSequence + 1 < Floor` precheck takes for "no gap" and then silently misses every dropped event.
            return entry.Events.First is { } head ? Math.Max(entry.Floor, head.Value.Event.Sequence) : entry.Floor;
        }
    }

    public async IAsyncEnumerable<IntegrationStreamEvent> ReadAsync(Guid executionId,
        long sinceSequence,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sinceSequence);

        var cursor = sinceSequence;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<IntegrationStreamEvent>? batch = null;
            Task appended;
            lock (_gate)
            {
                if (!_entries.TryGetValue(executionId, out var entry))
                {
                    // Never tracked, TTL-swept, or evicted under pressure. Same answer for all three: the ring cannot
                    // serve this position and the caller must fall back to the persisted rows.
                    throw new IntegrationEventGapException(executionId, cursor);
                }

                var floor = entry.Events.First is { } head ? Math.Max(entry.Floor, head.Value.Event.Sequence) : entry.Floor;
                if (cursor < floor - 1 || cursor > entry.LatestSequence)
                {
                    // Below the retained window the reader would silently miss events; above the head it would park on
                    // a cursor no append can ever reach, holding an open 200 that never speaks.
                    throw new IntegrationEventGapException(executionId, cursor);
                }

                // The R5-3 barrier. A reserved-but-unresolved sequence is about to be committed, and yielding anything
                // above it would advance the cursor PAST it — the late publish would then fall below the cursor and be
                // lost to this reader forever, with no gap to report it. The snapshot therefore stops short of it, and
                // both Publish and Abandon complete the very source captured below.
                var barrier = entry.Pending.Count > 0 ? entry.Pending.Min : long.MaxValue;
                // The list is kept in sequence order, so skip-then-take is the whole selection: no index arithmetic, no
                // contiguity assertion, and a hole simply is not there to take.
                var selected = entry.Events.Select(static buffered => buffered.Event)
                                    .SkipWhile(streamEvent => streamEvent.Sequence <= cursor)
                                    .TakeWhile(streamEvent => streamEvent.Sequence < barrier)
                                    .ToList();
                batch = selected.Count > 0 ? selected : null;

                // Captured in the SAME acquisition as the snapshot: reading it after releasing the lock would let an
                // append swap the source in between, leaving the reader waiting on the successor and missing what is
                // already in the ring.
                appended = entry.Appended.Task;
            }

            if (batch is not null)
            {
                foreach (var streamEvent in batch)
                {
                    yield return streamEvent;
                    // Never cursor + 1: sequences are compared, never counted, because holes are legal.
                    cursor = streamEvent.Sequence;

                    if (TerminalTypes.Contains(streamEvent.Type))
                    {
                        // Completion is decided by the TYPE yielded, never by cursor == LastSequence: the head may name
                        // a reservation that is still pending, and a terminal whose commit failed is followed by
                        // another one.
                        yield break;
                    }
                }

                // Re-snapshot rather than wait: anything appended during the yield is already in the ring.
                continue;
            }

            await appended.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _sweepCancellation.Cancel();
        _sweepTimer.Dispose();

        try
        {
            _sweepLoop.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // The loop's own shutdown signal.
        }

        _sweepCancellation.Dispose();

        // Host shutdown. A parked reader waits on its entry's source and nothing else, so dropping the entries without
        // completing those sources would leave every reader waiting on one no writer can reach — waiting out its own
        // request token instead of ending here. Wake first, then clear: the reader finds the entry gone and answers the
        // gap, exactly as it does for Remove.
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                entry.Queued = false;
                Wake(entry);
            }

            _entries.Clear();
            _terminal.Clear();
        }
    }

    /// <summary>
    ///     Drops terminal entries whose last append is older than the TTL. ONLY terminal ones, and never one holding a
    ///     pending reservation: a live execution that is queued, slow or loading a cold model must not be evicted out
    ///     from under a reader. Internal so a test can drive it without waiting on the timer.
    /// </summary>
    internal int Sweep()
    {
        var cutoff = NowUnixMilliseconds() - (long)_ttl.TotalMilliseconds;

        lock (_gate)
        {
            var expired = _entries.Where(pair => pair.Value.TerminalSequence is not null
                                                 && pair.Value.Pending.Count == 0
                                                 && pair.Value.LastAppendAtUtc <= cutoff)
                                  .Select(static pair => pair.Key)
                                  .ToArray();

            foreach (var executionId in expired)
            {
                if (_entries.Remove(executionId, out var entry))
                {
                    entry.Queued = false;
                    DropFromEvictionQueue(executionId);
                    Wake(entry);
                }
            }

            return expired.Length;
        }
    }

    private async Task RunSweepLoopAsync()
    {
        try
        {
            while (await _sweepTimer.WaitForNextTickAsync(_sweepCancellation.Token).ConfigureAwait(false))
            {
                _ = Sweep();
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal.
        }
    }

    private ExecutionBuffer Require(Guid executionId)
    {
        if (!_entries.TryGetValue(executionId, out var entry))
        {
            throw new InvalidOperationException($"Integration execution {executionId} has no event buffer entry. Call TryCreate before minting a sequence.");
        }

        return entry;
    }

    /// <summary>Stores an event in sequence order, updates the caps and the terminal bookkeeping, then wakes readers.</summary>
    private void Insert(ExecutionBuffer entry, IntegrationStreamEvent streamEvent)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(streamEvent, JsonOptions).Length;
        var buffered = new BufferedEvent(streamEvent, bytes);

        // Publish may land BELOW a sequence an Append minted while its commit was in flight, so the list is kept
        // ordered by insertion position rather than by append order.
        var node = entry.Events.Last;
        while (node is not null && node.Value.Event.Sequence > streamEvent.Sequence)
        {
            node = node.Previous;
        }

        if (node is null)
        {
            _ = entry.Events.AddFirst(buffered);
        }
        else
        {
            _ = entry.Events.AddAfter(node, buffered);
        }

        entry.Utf8Bytes += bytes;
        entry.LastAppendAtUtc = NowUnixMilliseconds();

        if (TerminalTypes.Contains(streamEvent.Type))
        {
            entry.TerminalSequence = streamEvent.Sequence;
            if (!entry.Queued)
            {
                entry.Queued = true;
                _terminal.Enqueue(streamEvent.ExecutionId);
            }
        }

        Trim(entry);
        Wake(entry);
    }

    /// <summary>Drops the oldest events until both caps hold. Trimming moves the entry's <c>Floor</c>, which is what a reader's replay compares against.</summary>
    private void Trim(ExecutionBuffer entry)
    {
        while (entry.Events.Count > 0 && (entry.Events.Count > _capacity || entry.Utf8Bytes > _maxBytes))
        {
            var first = entry.Events.First!;
            entry.Utf8Bytes -= first.Value.Utf8Bytes;
            entry.Floor = Math.Max(entry.Floor, first.Value.Event.Sequence + 1);
            entry.Events.RemoveFirst();
        }
    }

    /// <summary>
    ///     Discards an id the eviction FIFO still names after its entry was removed. Without it the queue grows without
    ///     bound on a node that never reaches <c>MaxTrackedExecutions</c>, because only an eviction scan drops stale ids.
    /// </summary>
    private void DropFromEvictionQueue(Guid executionId)
    {
        for (var remaining = _terminal.Count; remaining > 0; remaining--)
        {
            var candidate = _terminal.Dequeue();
            if (candidate != executionId)
            {
                _terminal.Enqueue(candidate);
            }
        }
    }

    /// <summary>
    ///     Completes and REPLACES the wakeup source. The source is built with
    ///     <see cref="TaskCreationOptions.RunContinuationsAsynchronously" /> always, because completing it while the
    ///     lock is held would otherwise run a continuation inline, and a continuation that re-entered this class would
    ///     deadlock.
    /// </summary>
    private static void Wake(ExecutionBuffer entry)
    {
        var previous = entry.Appended;
        entry.Appended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = previous.TrySetResult();
    }

    private bool TryEvictOldestTerminal()
    {
        // Bounded by the queue's current length so an entry that is skipped and re-enqueued cannot spin.
        for (var scanned = _terminal.Count; scanned > 0; scanned--)
        {
            var candidate = _terminal.Dequeue();
            if (!_entries.TryGetValue(candidate, out var entry))
            {
                continue;
            }

            if (entry.TerminalSequence is null || entry.Pending.Count > 0)
            {
                // A pending reservation PINS the entry: evicting it would strand a reader parked at the barrier and
                // let the Publish that follows land on a vanished entry.
                _terminal.Enqueue(candidate);
                continue;
            }

            entry.Queued = false;
            _ = _entries.Remove(candidate);
            Wake(entry);
            return true;
        }

        return false;
    }

    private long NowUnixMilliseconds() =>
        _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>The wakeup source a reader awaits. Test-only seam; S2's reader reads the field directly under the lock.</summary>
    internal Task AppendedTask(Guid executionId)
    {
        lock (_gate)
        {
            return Require(executionId).Appended.Task;
        }
    }

    private readonly record struct BufferedEvent(IntegrationStreamEvent Event, int Utf8Bytes);

    private sealed class ExecutionBuffer
    {
        public TaskCompletionSource Appended { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LinkedList<BufferedEvent> Events { get; } = [];

        /// <summary>The highest sequence <see cref="Events" /> has dropped, plus one. Zero while nothing has been dropped.</summary>
        public long Floor { get; set; }

        public long LastAppendAtUtc { get; set; }

        public long LatestSequence { get; set; }

        /// <summary>Reserved but neither published nor abandoned. Ordered, because the reader barrier is the minimum.</summary>
        public SortedSet<long> Pending { get; } = [];

        /// <summary>Whether this id currently sits in the eviction queue, so it is enqueued once rather than per terminal event.</summary>
        public bool Queued { get; set; }

        public long? TerminalSequence { get; set; }

        public long Utf8Bytes { get; set; }
    }
}
