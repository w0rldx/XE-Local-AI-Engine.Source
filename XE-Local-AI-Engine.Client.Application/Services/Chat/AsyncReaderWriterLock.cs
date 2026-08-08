namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     A small, allocation-light async reader-writer lock used by <see cref="NodeChatPersistenceWriter" /> to model the
///     per-conversation lock hierarchy. Multiple readers run concurrently; a writer runs alone. It is
///     <b>writer-preferring</b>: once a writer is waiting, new readers queue behind it, so the frequent writer op
///     (message-sequence allocation on every turn) cannot be starved by a burst of reads. Waits honor cancellation.
///
///     <para>Not reentrant — callers must not re-enter while holding either side. The node chat persistence paths never
///     nest a same-conversation acquire, so reentrancy cannot arise.</para>
/// </summary>
internal sealed class AsyncReaderWriterLock
{
    private readonly LinkedList<Waiter> _waitingReaders = new();
    private readonly LinkedList<Waiter> _waitingWriters = new();
    private readonly Lock _sync = new();

    private int _activeReaders;
    private bool _writerActive;

    public Task EnterReadAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        Waiter waiter;
        lock (_sync)
        {
            // Writer-preference: a waiting writer blocks new readers so it cannot be starved.
            if (!_writerActive && _waitingWriters.Count == 0)
            {
                _activeReaders++;
                return Task.CompletedTask;
            }

            waiter = new Waiter(isReader: true);
            waiter.Node = _waitingReaders.AddLast(waiter);
        }

        return WaitAsync(waiter, cancellationToken);
    }

    public Task EnterWriteAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        Waiter waiter;
        lock (_sync)
        {
            // Grant immediately only when fully idle AND no other writer is already queued (preserve writer FIFO).
            if (!_writerActive && _activeReaders == 0 && _waitingWriters.Count == 0)
            {
                _writerActive = true;
                return Task.CompletedTask;
            }

            waiter = new Waiter(isReader: false);
            waiter.Node = _waitingWriters.AddLast(waiter);
        }

        return WaitAsync(waiter, cancellationToken);
    }

    public void ExitRead()
    {
        List<Waiter>? granted;
        lock (_sync)
        {
            _activeReaders--;
            granted = _activeReaders == 0 ? GrantNext() : null;
        }

        Signal(granted);
    }

    public void ExitWrite()
    {
        List<Waiter>? granted;
        lock (_sync)
        {
            _writerActive = false;
            granted = GrantNext();
        }

        Signal(granted);
    }

    private async Task WaitAsync(Waiter waiter, CancellationToken cancellationToken)
    {
        await using var registration = cancellationToken.Register(() => OnWaiterCancelled(waiter, cancellationToken)).ConfigureAwait(false);
        await waiter.Task.ConfigureAwait(false);
    }

    private void OnWaiterCancelled(Waiter waiter, CancellationToken cancellationToken)
    {
        List<Waiter>? granted;
        lock (_sync)
        {
            if (waiter.Claimed)
            {
                // Already granted; the grant path owns completion.
                return;
            }

            waiter.Claimed = true;
            if (waiter.Node is { } node)
            {
                (waiter.IsReader ? _waitingReaders : _waitingWriters).Remove(node);
                waiter.Node = null;
            }

            // Removing a queued writer may now let waiting readers proceed.
            granted = GrantNext();
        }

        waiter.CompleteCancelled(cancellationToken);
        Signal(granted);
    }

    /// <summary>
    ///     Grants as many queued waiters as the current state permits, mutating the reader/writer counters and returning
    ///     the waiters whose completion source must be signaled (outside the lock). Must be called under <see cref="_sync" />.
    /// </summary>
    private List<Waiter>? GrantNext()
    {
        if (_writerActive)
        {
            return null;
        }

        if (_activeReaders > 0)
        {
            // Readers hold the lock: only more readers may join, and only when no writer is waiting.
            return _waitingWriters.Count == 0 ? DrainWaitingReaders() : null;
        }

        // Fully idle: writer-preference grants a single queued writer first.
        if (_waitingWriters.Count > 0)
        {
            var writer = DequeueFirstLiveWaiter(_waitingWriters);
            if (writer is null)
            {
                return null;
            }

            _writerActive = true;
            return [writer];
        }

        return DrainWaitingReaders();
    }

    private List<Waiter>? DrainWaitingReaders()
    {
        List<Waiter>? granted = null;
        while (DequeueFirstLiveWaiter(_waitingReaders) is { } reader)
        {
            _activeReaders++;
            (granted ??= []).Add(reader);
        }

        return granted;
    }

    private static Waiter? DequeueFirstLiveWaiter(LinkedList<Waiter> queue)
    {
        while (queue.First is { } node)
        {
            queue.RemoveFirst();
            var waiter = node.Value;
            // A cancelled waiter is already claimed + completed by the cancellation path; skip it.
            if (waiter.Claimed)
            {
                continue;
            }

            waiter.Claimed = true;
            waiter.Node = null;
            return waiter;
        }

        return null;
    }

    private static void Signal(List<Waiter>? granted)
    {
        if (granted is null)
        {
            return;
        }

        foreach (var waiter in granted)
        {
            waiter.CompleteGranted();
        }
    }

    private sealed class Waiter(bool isReader)
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsReader { get; } = isReader;

        // Both Claimed and Node are only ever read/written under the owning lock's _sync.
        public bool Claimed { get; set; }

        public LinkedListNode<Waiter>? Node { get; set; }

        public Task Task => _completion.Task;

        public void CompleteGranted()
        {
            _completion.TrySetResult();
        }

        public void CompleteCancelled(CancellationToken cancellationToken)
        {
            _completion.TrySetCanceled(cancellationToken);
        }
    }
}
