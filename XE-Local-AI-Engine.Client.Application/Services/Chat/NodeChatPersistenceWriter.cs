namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Serializes node chat persistence writes under a per-conversation lock hierarchy so conversation-wide invariants
///     (contiguous, unique message sequences; delete/purge atomicity) hold under concurrent sends, regenerations, and
///     deletes.
///
///     <para><b>Lock hierarchy.</b> Each conversation has one reader-writer lock:</para>
///     <list type="bullet">
///         <item>
///             <b>Conversation-exclusive</b> (writer) — sequence allocation + message insert, conversation lifecycle
///             (create/ensure/rename/pin/archive/select-path), and delete/purge. Holds the writer side, so nothing else
///             on the same conversation runs concurrently. This is what makes <c>MAX(sequence)+1</c> allocation race-free
///             and stops a delete from interleaving with a message write.
///         </item>
///         <item>
///             <b>Conversation-shared</b> (reader) — read-only conversation/message queries. Multiple run in parallel;
///             all are excluded against a conversation-exclusive op (so a read never observes a half-applied delete).
///         </item>
///         <item>
///             <b>Message-update</b> — a payload UPDATE to an ALREADY-allocated row (streaming flush / queued / streaming
///             / terminalize / cancel / feedback). Holds the conversation lock in <em>shared</em> mode plus a
///             per-<c>(conversation, message)</c> exclusive lock: updates to different messages run in parallel, updates
///             to the same message serialize, and every update is mutually excluded against a conversation delete via the
///             shared/exclusive relationship. The nesting order is always conversation-lock first, then message-lock, and
///             the exclusive side never takes a message lock, so no cross-lock deadlock is reachable.
///         </item>
///     </list>
///
///     <para><b>Bounded lock map.</b> Gates (and the per-message locks inside them) are reference-counted and removed the
///     moment they fall idle, so the map is bounded by the number of <em>concurrently active</em> conversations, not by
///     the number of conversations or messages ever seen.</para>
/// </summary>
public sealed class NodeChatPersistenceWriter(IServiceScopeFactory scopeFactory, ILogger<NodeChatPersistenceWriter>? logger = null)
{
    private readonly Dictionary<Guid, ConversationGate> _gates = new();
    private readonly Lock _gatesSync = new();
    private readonly ILogger<NodeChatPersistenceWriter>? _logger = logger;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

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
            await gate.Lock.EnterWriteAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await RunScopedAsync(persistenceOperation, cancellationToken).ConfigureAwait(false);
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
            await gate.Lock.EnterReadAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await RunScopedAsync(persistenceOperation, cancellationToken).ConfigureAwait(false);
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
    ///     Runs <paramref name="persistenceOperation" /> as a payload UPDATE of an already-allocated message row: the
    ///     conversation's shared (reader) lock plus a per-message exclusive lock. Updates to different messages run in
    ///     parallel; updates to the same message serialize; all are excluded against a conversation delete.
    /// </summary>
    public async Task<TResult> ExecuteMessageUpdateAsync<TResult>(Guid conversationId,
        Guid messageId,
        Func<NodeChatDbContext, CancellationToken, Task<TResult>> persistenceOperation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(persistenceOperation);

        var gate = RentGate(conversationId);
        try
        {
            await gate.Lock.EnterReadAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var messageLock = gate.RentMessageLock(messageId);
                try
                {
                    await messageLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        return await RunScopedAsync(persistenceOperation, cancellationToken).ConfigureAwait(false);
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
            return await persistenceOperation(dbContext, cancellationToken).ConfigureAwait(false);
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
