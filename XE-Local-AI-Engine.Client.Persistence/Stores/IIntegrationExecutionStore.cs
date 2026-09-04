namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     An execution as a reader sees it. Content-free: <see cref="FailureCategory" /> is one of the ten closed
///     categories <see cref="IntegrationExecutionStatus" /> lists and <see cref="FailureSummary" /> is a short label,
///     never a message. <see cref="OutputBytes" /> counts <b>plaintext</b> UTF-8 bytes of the persisted
///     <c>external.output</c> payloads.
/// </summary>
public sealed record IntegrationExecutionSnapshot(
    Guid Id,
    Guid TriggerId,
    Guid SessionId,
    Guid PrincipalId,
    Guid RequestId,
    ReadOnlyMemory<byte> RequestFingerprint,
    string KeyPrefix,
    Guid InvocationId,
    IntegrationExecutionStatus Status,
    long ReceivedAtUtc,
    long? StartedAtUtc,
    long? EndedAtUtc,
    long? StopRequestedAtUtc,
    string? FailureCategory,
    string? FailureSummary,
    int OutputCount,
    long OutputBytes,
    long LastSequence,
    long Version);

/// <summary><c>DetailJson</c> is DECRYPTED text, not the stored <c>byte[]</c> — every consumer reads text.</summary>
public sealed record IntegrationExecutionEventSnapshot(
    Guid Id,
    Guid ExecutionId,
    long Sequence,
    string EventType,
    string? DetailJson,
    long OccurredAtUtc);

/// <summary>
///     One event to append. <see cref="DetailJson" /> is PLAINTEXT text; the store encodes it to UTF-8 and the save
///     interceptor seals it. <see cref="Sequence" /> is minted by the coordinator's event buffer and never by the
///     store.
/// </summary>
public sealed record IntegrationEventAppend(
    Guid EventId,
    Guid ExecutionId,
    long Sequence,
    string EventType,
    string? DetailJson,
    long OccurredAtUtc);

/// <summary>
///     Everything one admission writes.
///     <para>
///         <see cref="SessionId" /> names the session the execution belongs to in <b>both</b> cases: it equals
///         <c>NewSession.SessionId</c> on a fresh session and names the existing row otherwise. <see cref="NewSession" />
///         is a nested record rather than four loose nullable fields so the all-or-nothing shape is enforced by the
///         type.
///     </para>
///     <para>
///         <see cref="PrincipalId" /> is the identity every ownership and uniqueness question keys on; it lands on the
///         execution row and, when <see cref="NewSession" /> is non-null, on the session row too, which is why the
///         command carries it once rather than twice. <see cref="KeyPrefix" /> rides along for <b>audit only</b>.
///     </para>
///     <para>
///         Fields an accept never sets — invocation id, start/end stamps, the cancel marker, the failure fields and both
///         output counters — are absent by design: the store writes their zero/null defaults, and afterwards
///         <c>UpdateStatusAsync</c> moves them on the non-terminal transitions and <c>TryTerminalizeAsync</c> on the one
///         terminal transition.
///     </para>
/// </summary>
public sealed record IntegrationAcceptCommand(
    IntegrationSessionCreate? NewSession,
    Guid ExecutionId,
    Guid TriggerId,
    Guid SessionId,
    Guid PrincipalId,
    Guid RequestId,
    ReadOnlyMemory<byte> RequestFingerprint,
    string KeyPrefix,
    long ReceivedAtUtc,
    IntegrationEventAppend AcceptedEvent);

/// <summary>
///     One NON-TERMINAL status compare-and-swap. Every optional field is "leave it alone" when null — never "clear it".
///     <see cref="NewStatus" /> is required, so a caller that only wants to stamp <see cref="StopRequestedAtUtc" /> on a
///     running row passes <c>ExpectedStatuses = { Running }</c> and <c>NewStatus = Running</c>; that self-move is
///     deliberate and is why there is no second marker-only method — the cancel marker and the status CAS contend on the
///     same <c>Version</c>.
/// </summary>
public sealed record IntegrationExecutionStatusUpdate(
    Guid ExecutionId,
    long ExpectedVersion,
    IReadOnlySet<IntegrationExecutionStatus> ExpectedStatuses,
    IntegrationExecutionStatus NewStatus,
    long? StartedAtUtc = null,
    long? EndedAtUtc = null,
    Guid? InvocationId = null,
    long? StopRequestedAtUtc = null,
    string? FailureCategory = null,
    string? FailureSummary = null);

/// <summary>
///     The one TERMINAL transition. Carries the status CAS, the sequence already RESERVED on the coordinator's buffer
///     for the terminal event, and the terminal columns. The store mints the event's Guid id, stamps
///     <c>OccurredAtUtc = EndedAtUtc</c>, and derives the event's small <c>DetailJson</c> from the two failure fields;
///     it still mints no sequence. <see cref="EventType" /> is one of the three terminal stream-event types.
/// </summary>
public sealed record IntegrationTerminalizeCommand(
    Guid ExecutionId,
    long ExpectedVersion,
    IReadOnlySet<IntegrationExecutionStatus> ExpectedStatuses,
    IntegrationExecutionStatus NewStatus,
    long Sequence,
    string EventType,
    long EndedAtUtc,
    string? FailureCategory,
    string? FailureSummary,
    /// <summary>
    ///     The terminal EVENT's detail, when the caller built one. The same JSON it publishes on the stream event, so
    ///     the poll route and the stream hand a caller the same envelope. Null falls back to the failure columns.
    /// </summary>
    string? EventDetailJson = null,
    /// <summary>
    ///     The ONE kind-3 audit row this execution owes, written by whoever wins the terminal compare-and-swap and in
    ///     the SAME transaction as the status and the terminal event. It used to be a separate <c>SaveChanges</c> after
    ///     the terminal committed, so a database failure between the two lost a required audit row permanently: every
    ///     later terminalization rejects an already-terminal row, so nothing could ever write it. A null means the
    ///     caller audits nothing (the queue-full accept path, which never had an invocation to audit).
    /// </summary>
    IntegrationInvocationAuditInput? Audit = null);

/// <summary>
///     Paged filter for the admin executions list. The paging fields are <see cref="Limit" /> and <see cref="Offset" />
///     and no other names; a null filter field means "do not constrain on it".
/// </summary>
public sealed record IntegrationExecutionFilter(
    Guid? TriggerId,
    Guid? SessionId,
    IntegrationExecutionStatus? Status,
    int Limit,
    int Offset);

/// <summary>
///     Admission refused because the node-wide or the per-principal active cap was already full. Both caps throw this
///     one type because both answer 503 with a <c>Retry-After</c>; telling them apart would be a message change, not a
///     second type. Nothing is written when it is thrown.
/// </summary>
public sealed class IntegrationQueueFullException(string message) : InvalidOperationException(message);

/// <summary>
///     A continuation named a session that cannot host it: no such row, another principal's row, or one that is no
///     longer <c>Active</c>. Nothing is written when it is thrown — the admission transaction is abandoned.
/// </summary>
public sealed class IntegrationSessionUnavailableException(string message) : Exception(message);

/// <summary>
///     Persistence boundary for integration executions and their event feed.
///     <para>
///         There is no <c>CreateAsync</c> — an execution row is only ever born accepted — and no <c>CountActiveAsync</c>:
///         the count is folded into <see cref="AcceptAsync" />, where it is a hard bound rather than a racy read. There
///         is no <c>FailNonTerminalAsync</c> either: a bulk <c>UPDATE</c> cannot write the per-row terminal events, so
///         the restart sweep enumerates with <see cref="ListAsync" /> and terminalises row by row.
///     </para>
/// </summary>
public interface IIntegrationExecutionStore
{
    /// <summary>
    ///     Reserves admission and writes the durable accept in ONE <c>BEGIN IMMEDIATE</c> transaction: re-read the key
    ///     row for revocation, count the node's active executions, count the principal's, insert the session (or bump
    ///     the existing one's counters), insert the execution, insert the <c>execution.accepted</c> event, commit.
    ///     <para>
    ///         Returns <see langword="false" /> when the credential was revoked between authentication and admission:
    ///         nothing is written and the caller answers the same generic 401 it uses for any other invalid credential.
    ///         Throws <see cref="IntegrationQueueFullException" /> when either cap is full: nothing is written, and the
    ///         caller answers 503 with a <c>Retry-After</c>.
    ///     </para>
    ///     <para>
    ///         Both caps are parameters rather than command fields because they are policy numbers the caller reads from
    ///         <c>IntegrationOptions</c>, not part of the row being written.
    ///     </para>
    ///     <para>
    ///         On a continuation (<c>NewSession</c> null) the session bump is scoped to the caller's own <c>Active</c>
    ///         session and throws <see cref="IntegrationSessionUnavailableException" /> when it matches no row. The
    ///         caller (S3) has already pre-checked ownership and status under its per-session semaphore and answers the
    ///         proper 404/409 there; this is the RACE-FREE BACKSTOP for the window between that check and this
    ///         transaction, not the place a caller learns which of the three it was.
    ///     </para>
    ///     <para>
    ///         <b>After this returns</b> the caller creates the owned <c>NodeConversation</c> at the pre-minted
    ///         <c>NewSession.ConversationId</c> and writes the seed message. A failure there terminalises the execution
    ///         <c>Failed</c> / <c>internal-failure</c> through the coordinator's ordinary path. Because the conversation
    ///         is created after the durable rows, no orphan conversation can exist and the feature carries no orphan
    ///         sweep.
    ///     </para>
    /// </summary>
    Task<bool> AcceptAsync(IntegrationAcceptCommand command,
        int maxActive,
        int maxActivePerPrincipal,
        CancellationToken cancellationToken = default);

    Task<IntegrationExecutionSnapshot?> GetByIdAsync(Guid executionId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The dedup lookup, by BOTH columns. Scoped this way a replay sees only its own principal's rows, which is what
    ///     stops one integrator denying a request id to another.
    /// </summary>
    Task<IntegrationExecutionSnapshot?> GetByRequestIdAsync(Guid principalId, Guid requestId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Ordered <c>ReceivedAtUtc</c> then <c>Id</c> descending before paging. The id tie-break is not decoration:
    ///     <c>ReceivedAtUtc</c> is a millisecond stamp, and two accepts in the same millisecond would otherwise page
    ///     non-deterministically, dropping or duplicating a row across pages.
    /// </summary>
    Task<IReadOnlyList<IntegrationExecutionSnapshot>> ListAsync(IntegrationExecutionFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    ///     How many of ONE session's executions are <c>Accepted</c>, <c>Queued</c> or <c>Running</c>. Deliberately not
    ///     the node-wide count folded into <see cref="AcceptAsync" />: that one is a hard admission bound, while this
    ///     answers "is this caller-managed session busy right now" for the 409 that a second concurrent invoke and an
    ///     operator delete both need. It is a READ, so its caller holds the per-session gate across it AND the mutation
    ///     that follows — checking outside the gate guards nothing.
    /// </summary>
    Task<int> CountActiveBySessionAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The session's currently <c>Running</c> execution, or <see langword="null" />. This is how <c>emit_output</c>
    ///     turns the ambient conversation id into the execution its payload belongs to: a session runs at most one
    ///     execution at a time, and the returned snapshot carries the <c>OutputBytes</c> counter the tool's aggregate
    ///     pre-check reads fresh on every call.
    /// </summary>
    Task<IntegrationExecutionSnapshot?> FindActiveBySessionAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     <see cref="AppendEventAsync" />'s insert plus the three things an <c>external.output</c> row must share its
    ///     transaction with: the PLAINTEXT byte check-and-reserve, the <c>OutputBytes</c> increment, and the
    ///     <c>OutputCount</c> increment.
    ///     <para>
    ///         The caller passes the CAP, never a byte count: the store computes
    ///         <c>Encoding.UTF8.GetByteCount(append.DetailJson)</c> itself, so the number checked and the number added
    ///         agree by construction. Returns <see langword="false" /> with NOTHING written when
    ///         <c>OutputBytes + length</c> would exceed the cap. That refusal is the authoritative bound; the tool's own
    ///         pre-check exists only to keep a refusal from ever reaching the event buffer.
    ///     </para>
    ///     <para>
    ///         Nothing here reads <c>length(detail_json)</c>. That column is an encrypted BLOB, so a <c>SUM</c> over it
    ///         would measure ciphertext against a plaintext cap — which is why the counter is a column rather than a
    ///         query.
    ///     </para>
    /// </summary>
    Task<bool> AppendOutputEventAsync(IntegrationEventAppend append, long maxOutputBytesPerExecution, CancellationToken cancellationToken = default);

    /// <summary>
    ///     One <c>SaveChanges</c>. Returns <see langword="false" /> WITHOUT writing when the row is missing, the version
    ///     is stale, or the current status is outside the command's expected set.
    ///     <para>
    ///         <b>This method is for NON-TERMINAL transitions only.</b> <c>NewStatus</c> must be <c>Accepted</c>,
    ///         <c>Queued</c> or <c>Running</c>: ending a run also has to write the terminal event, and only
    ///         <see cref="TryTerminalizeAsync" /> does both in one transaction. And because there is no legal move out of
    ///         a terminal status, <c>ExpectedStatuses</c> never contains one either. The store does not police it — it
    ///         polices no transition — so a terminal status slipped through here would silently reproduce the split
    ///         write that method exists to remove.
    ///     </para>
    /// </summary>
    Task<bool> UpdateStatusAsync(IntegrationExecutionStatusUpdate command, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Terminal status CAS + terminal event insert + both watermarks, in ONE transaction. Returns
    ///     <see langword="false" /> when the CAS loses — stale version, current status outside the expected set, or no
    ///     row — and NOTHING is written, not even the event. Publish to the stream only after this returns
    ///     <see langword="true" />.
    ///     <para>
    ///         The failure fields are <b>assigned, not merged</b> here — the opposite of
    ///         <see cref="UpdateStatusAsync" />'s "null means leave alone" rule, and deliberately so: a terminal write is
    ///         the final word on why a run ended, and a <c>Completed</c> command carrying two nulls must not inherit a
    ///         stale category from an earlier attempt.
    ///     </para>
    /// </summary>
    Task<bool> TryTerminalizeAsync(IntegrationTerminalizeCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Appends one already-sequenced event and moves both watermarks: the execution's <c>LastSequence</c> as a
    ///     running MAXIMUM (an output row and a coordinator row can persist out of order, and a plain assignment would
    ///     let the slower writer move it backwards), and the owning session's as a plain assignment (sequences restart
    ///     at 1 per execution, so a maximum across a session's executions would freeze at the deepest old stream and
    ///     never move again — it is the activity indicator the UI renders, not an ordering key).
    ///     <para>
    ///         A duplicate <c>(ExecutionId, Sequence)</c> surfaces as <c>DbUpdateException</c>: it means a caller minted
    ///         a sequence it never reserved, which is a bug and not a race to swallow.
    ///     </para>
    /// </summary>
    Task AppendEventAsync(IntegrationEventAppend command, CancellationToken cancellationToken = default);

    /// <summary>
    ///     A page of an execution's persisted events, ordered by sequence, with <c>DetailJson</c> DECRYPTED to text.
    ///     A non-positive <paramref name="limit" /> throws <see cref="ArgumentOutOfRangeException" />.
    /// </summary>
    Task<IReadOnlyList<IntegrationExecutionEventSnapshot>> ListEventsAsync(Guid executionId,
        long sinceSequence,
        int limit,
        CancellationToken cancellationToken = default);
}
