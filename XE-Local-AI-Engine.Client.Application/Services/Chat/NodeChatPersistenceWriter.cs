namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Serializes node chat persistence writes under a per-conversation lock hierarchy, so contiguous unique message
///     sequences and delete atomicity hold under concurrent sends, regenerations and deletes.
/// </summary>
/// <remarks>
///     Each conversation has one reader-writer lock with three uses, described in <c>docs/wiki/05-chat.md</c> under
///     "The persistence lock hierarchy": exclusive for sequence allocation, the conversation lifecycle and delete;
///     shared for read-only queries; and, for a payload UPDATE, shared plus a per-message exclusive lock. The nesting
///     order is always conversation lock then message lock and the exclusive side never takes a message lock, so no
///     cross-lock deadlock is reachable. Gates are reference-counted and dropped the moment they fall idle.
/// </remarks>
public sealed class NodeChatPersistenceWriter
{
    private readonly Dictionary<Guid, ConversationGate> _gates = new();
    private readonly Lock _gatesSync = new();
    private readonly ILogger<NodeChatPersistenceWriter>? _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public NodeChatPersistenceWriter(IServiceScopeFactory scopeFactory, ILogger<NodeChatPersistenceWriter>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    ///     Runs <paramref name="persistenceOperation" /> under the conversation's exclusive (writer) lock. Use for
    ///     sequence allocation + message insert, conversation-lifecycle writes, and delete/purge.
    /// </summary>
    public async Task<TResult> ExecuteConversationExclusiveAsync<TResult>(Guid conversationId,
        Func<NodeChatDbContext, CancellationToken, Task<TResult>> persistenceOperation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(persistenceOperation);

        var gate = RentGate(conversationId);
        try
        {
            await gate.Lock.EnterWriteAsync(cancellationToken);
            try
            {
                return await RunScopedAsync(persistenceOperation, cancellationToken);
            }
            finally
            {
                gate.Lock.ExitWrite();
            }
        }
        finally
        {
            ReturnGate(conversationId, gate);
        }
    }

    /// <summary>
    ///     Runs <paramref name="persistenceOperation" /> under the conversation's shared (reader) lock. Use for read-only
    ///     conversation/message queries: parallel with other reads, excluded against a conversation-exclusive op.
    /// </summary>
    public async Task<TResult> ExecuteConversationSharedAsync<TResult>(Guid conversationId,
        Func<NodeChatDbContext, CancellationToken, Task<TResult>> persistenceOperation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(persistenceOperation);

        var gate = RentGate(conversationId);
        try
        {
            await gate.Lock.EnterReadAsync(cancellationToken);
            try
            {
                return await RunScopedAsync(persistenceOperation, cancellationToken);
            }
            finally
            {
                gate.Lock.ExitRead();
            }
        }
        finally
        {
            ReturnGate(conversationId, gate);
        }
    }

    /// <summary>
    ///     Runs <paramref name="persistenceOperation" /> as a payload UPDATE of an already-allocated message row,
    ///     under the conversation's shared lock plus a per-message exclusive lock.
    /// </summary>
    /// <remarks>
    ///     Updates to different messages run in parallel, updates to the same message serialize, and all are excluded
    ///     against a conversation delete.
    /// </remarks>
    public async Task<TResult> ExecuteMessageUpdateAsync<TResult>(Guid conversationId,
        Guid messageId,
        Func<NodeChatDbContext, CancellationToken, Task<TResult>> persistenceOperation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(persistenceOperation);

        var gate = RentGate(conversationId);
        try
        {
            await gate.Lock.EnterReadAsync(cancellationToken);
            try
            {
                var messageLock = gate.RentMessageLock(messageId);
                try
                {
                    await messageLock.WaitAsync(cancellationToken);
                    try
                    {
                        return await RunScopedAsync(persistenceOperation, cancellationToken);
                    }
                    finally
                    {
                        messageLock.Release();
                    }
                }
                finally
                {
                    gate.ReturnMessageLock(messageId);
                }
            }
            finally
            {
                gate.Lock.ExitRead();
            }
        }
        finally
        {
            ReturnGate(conversationId, gate);
        }
    }

    /// <summary>
    ///     Number of live per-conversation gates. Test-only seam (internal + <c>InternalsVisibleTo</c>) proving the lock
    ///     map is bounded by concurrently-active conversations, not by conversations/messages ever seen.
    /// </summary>
    internal int ActiveConversationLockCount
    {
        get
        {
            lock (_gatesSync)
            {
                return _gates.Count;
            }
        }
    }

    private async Task<TResult> RunScopedAsync<TResult>(Func<NodeChatDbContext, CancellationToken, Task<TResult>> persistenceOperation, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        try
        {
            return await persistenceOperation(dbContext, cancellationToken);
        }
        catch (Exception exception)
        {
            // Account raw-path SQLite write contention (SQLITE_BUSY/LOCKED surfacing past busy_timeout). No-op
            // for any other failure. Reads never contend under WAL, so in practice this observes the write paths only.
            NodeSqliteContention.Record("raw", exception, _logger);
            throw;
        }
    }

    private ConversationGate RentGate(Guid conversationId)
    {
        lock (_gatesSync)
        {
            if (!_gates.TryGetValue(conversationId, out var gate))
            {
                gate = new ConversationGate();
                _gates[conversationId] = gate;
            }

            gate.RefCount++;
            return gate;
        }
    }

    private void ReturnGate(Guid conversationId, ConversationGate gate)
    {
        lock (_gatesSync)
        {
            gate.RefCount--;
            if (gate.RefCount == 0)
            {
                _gates.Remove(conversationId);
            }
        }
    }

    /// <summary>
    ///     A single conversation's reader-writer lock plus its bounded set of per-message locks. Reference-counted by the
    ///     owning writer so it is discarded the moment the conversation falls idle.
    /// </summary>
    private sealed class ConversationGate
    {
        private readonly Dictionary<Guid, MessageLock> _messageLocks = new();
        private readonly Lock _messageLocksSync = new();

        public int RefCount;

        public AsyncReaderWriterLock Lock { get; } = new();

        public SemaphoreSlim RentMessageLock(Guid messageId)
        {
            lock (_messageLocksSync)
            {
                if (!_messageLocks.TryGetValue(messageId, out var messageLock))
                {
                    messageLock = new MessageLock();
                    _messageLocks[messageId] = messageLock;
                }

                messageLock.RefCount++;
                return messageLock.Semaphore;
            }
        }

        public void ReturnMessageLock(Guid messageId)
        {
            lock (_messageLocksSync)
            {
                if (!_messageLocks.TryGetValue(messageId, out var messageLock))
                {
                    return;
                }

                messageLock.RefCount--;
                if (messageLock.RefCount == 0)
                {
                    _messageLocks.Remove(messageId);
                    messageLock.Semaphore.Dispose();
                }
            }
        }

        private sealed class MessageLock
        {
            public int RefCount;

            public SemaphoreSlim Semaphore { get; } = new(initialCount: 1, maxCount: 1);
        }
    }
}
