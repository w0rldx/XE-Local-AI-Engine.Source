namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Services.Events;

/// <inheritdoc cref="IInvocationAttachmentTracker" />
public sealed class InvocationAttachmentTracker : IInvocationAttachmentTracker
{
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    private readonly Lazy<IWorkerEventDispatcher> _eventDispatcher;
    private readonly TimeProvider _timeProvider;
    private int _subscribed;

    // The dispatcher arrives LAZY and is deliberately not touched here: WorkerEventDispatcher depends on
    // IInvocationRunner, which now depends on this tracker, so resolving it in the constructor closes a DI cycle the
    // container rejects at validate-on-build. InvocationRunner breaks the same cycle the same way.
    public InvocationAttachmentTracker(Lazy<IWorkerEventDispatcher> eventDispatcher, TimeProvider timeProvider)
    {
        _eventDispatcher = eventDispatcher ?? throw new ArgumentNullException(nameof(eventDispatcher));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public event EventHandler<InvocationAttachmentChangedEventArgs>? AttachmentChanged;

    public IDisposable Attach(Guid invocationId)
    {
        EnsureSubscribed();
        var entry = _entries.GetOrAdd(invocationId, static _ => new Entry());

        var firstConsumer = entry.Increment(out var wasCountedDetached);
        if (wasCountedDetached)
        {
            NodeMetrics.ChatStreamDetachedInvocations.Add(-1);
        }

        // Raised OUTSIDE the entry lock: the runner's handler takes its own _syncRoot, and holding both would invert
        // the lock order against SetInvocationDeadline, which reads IsDetached while holding _syncRoot.
        if (firstConsumer)
        {
            AttachmentChanged?.Invoke(this, new InvocationAttachmentChangedEventArgs(invocationId, attached: true));
        }

        return new AttachmentHandle(this, invocationId, entry);
    }

    public bool IsDetached(Guid invocationId)
    {
        return _entries.TryGetValue(invocationId, out var entry) && entry.IsDetached;
    }

    public IReadOnlyCollection<DetachedInvocation> ListDetached()
    {
        List<DetachedInvocation>? detached = null;

        foreach (var (invocationId, entry) in _entries)
        {
            if (entry.TryGetDetachedAt(out var detachedAtUtc))
            {
                (detached ??= []).Add(new DetachedInvocation(invocationId, detachedAtUtc));
            }
        }

        return detached ?? (IReadOnlyCollection<DetachedInvocation>)[];
    }

    // Subscribes on the FIRST attach rather than at construction. By then the container has finished building, so
    // resolving the dispatcher is safe; and before the first attach there are no entries, so the terminal events this
    // would have missed had nothing to remove. Subscribes exactly once for the process lifetime — both are singletons,
    // so there is no unsubscribe path (mirrors InvocationResumeRegistry's subscription to the same dispatcher).
    private void EnsureSubscribed()
    {
        if (Interlocked.Exchange(ref _subscribed, value: 1) == 0)
        {
            _eventDispatcher.Value.InvocationStateChanged += OnInvocationStateChanged;
        }
    }

    private void Release(Guid invocationId, Entry entry)
    {
        if (entry.Decrement(_timeProvider.GetUtcNow()))
        {
            NodeMetrics.ChatStreamDetachedInvocations.Add(1);
            AttachmentChanged?.Invoke(this, new InvocationAttachmentChangedEventArgs(invocationId, attached: false));
        }
    }

    // A terminal invocation can never be reaped or re-attached, so drop its entry — otherwise every completed turn
    // that was ever watched would linger in the dictionary (and in ListDetached) for the process lifetime.
    private void OnInvocationStateChanged(object? sender, InvocationStateChangedEventArgs args)
    {
        if (args.State.Status is InvocationStatus.Completed or InvocationStatus.Cancelled or InvocationStatus.Failed
            && _entries.TryRemove(args.State.InvocationId, out var entry)
            && entry.Retire())
        {
            // The run terminalized while still detached, so the gauge's other exit: it must fall back to zero whether
            // the client came back or the turn simply ended.
            NodeMetrics.ChatStreamDetachedInvocations.Add(-1);
        }
    }

    /// <summary>
    ///     One invocation's consumer count. <c>_counted</c> is the single authority for whether this entry currently
    ///     contributes 1 to the <c>chat_stream_detached_invocations</c> gauge: every transition into and out of it is
    ///     claimed exactly once under this lock, so the gauge cannot drift however the callers interleave.
    /// </summary>
    private sealed class Entry
    {
        private readonly Lock _syncRoot = new();
        private int _count;
        private bool _counted;
        private DateTimeOffset? _detachedAtUtc;
        private bool _retired;

        public bool IsDetached
        {
            get
            {
                lock (_syncRoot)
                {
                    return _detachedAtUtc is not null;
                }
            }
        }

        /// <summary>
        ///     Returns <see langword="true" /> when this was the FIRST consumer (a zero-to-one transition).
        ///     <paramref name="wasCountedDetached" /> is <see langword="true" /> when the caller must decrement the gauge —
        ///     i.e. this attach ended a counted detachment, as opposed to being the entry's first ever consumer.
        /// </summary>
        public bool Increment(out bool wasCountedDetached)
        {
            lock (_syncRoot)
            {
                wasCountedDetached = _counted;
                _counted = false;
                _detachedAtUtc = null;
                return ++_count == 1;
            }
        }

        /// <summary>
        ///     Returns <see langword="true" /> when this was the LAST consumer (a one-to-zero transition) and the caller
        ///     must increment the gauge. A RETIRED entry returns <see langword="false" />: the hub's <c>finally</c>
        ///     routinely disposes its attachment after the terminal state has already removed the entry, and counting
        ///     that would strand the gauge permanently above zero on the ordinary completion path.
        /// </summary>
        public bool Decrement(DateTimeOffset nowUtc)
        {
            lock (_syncRoot)
            {
                if (_retired || _count == 0 || --_count > 0)
                {
                    return false;
                }

                _detachedAtUtc = nowUtc;
                _counted = true;
                return true;
            }
        }

        /// <summary>
        ///     Marks the entry dead (the invocation terminalized) and returns <see langword="true" /> when it was still
        ///     counted, meaning the caller must decrement the gauge.
        /// </summary>
        public bool Retire()
        {
            lock (_syncRoot)
            {
                _retired = true;
                var wasCounted = _counted;
                _counted = false;
                _detachedAtUtc = null;
                return wasCounted;
            }
        }

        public bool TryGetDetachedAt(out DateTimeOffset detachedAtUtc)
        {
            lock (_syncRoot)
            {
                detachedAtUtc = _detachedAtUtc.GetValueOrDefault();
                return _detachedAtUtc is not null;
            }
        }
    }

    // Idempotent: the hub disposes in a finally that a faulted enumerator can reach more than once, and a double
    // release would drop the count below the number of live consumers.
    private sealed class AttachmentHandle(InvocationAttachmentTracker tracker, Guid invocationId, Entry entry) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, value: 1) == 0)
            {
                tracker.Release(invocationId, entry);
            }
        }
    }
}
