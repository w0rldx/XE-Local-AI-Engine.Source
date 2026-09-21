namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     An execution as a reader sees it. Content-free: <see cref="FailureCategory" /> is one of the ten closed
///     categories <see cref="IntegrationExecutionStatus" /> lists and <see cref="FailureSummary" /> is a short label,
///     never a message. <see cref="OutputBytes" /> counts <b>plaintext</b> UTF-8 bytes of the persisted
///     <c>external.output</c> payloads.
/// </summary>
public sealed record IntegrationExecutionSnapshot
{
    public required Guid Id { get; init; }

    public required Guid TriggerId { get; init; }

    public required Guid SessionId { get; init; }

    public required Guid PrincipalId { get; init; }

    public required Guid RequestId { get; init; }

    public required ReadOnlyMemory<byte> RequestFingerprint { get; init; }

    public required string KeyPrefix { get; init; }

    public required Guid InvocationId { get; init; }

    public required IntegrationExecutionStatus Status { get; init; }

    public required long ReceivedAtUtc { get; init; }

    public required long? StartedAtUtc { get; init; }

    public required long? EndedAtUtc { get; init; }

    public required long? StopRequestedAtUtc { get; init; }

    public required string? FailureCategory { get; init; }

    public required string? FailureSummary { get; init; }

    public required int OutputCount { get; init; }

    public required long OutputBytes { get; init; }

    public required long LastSequence { get; init; }

    public required long Version { get; init; }
}

/// <summary><c>DetailJson</c> is DECRYPTED text, not the stored <c>byte[]</c> — every consumer reads text.</summary>
public sealed class IntegrationExecutionEventSnapshot
{
    public required Guid Id { get; init; }

    public required Guid ExecutionId { get; init; }

    public required long Sequence { get; init; }

    public required string EventType { get; init; }

    public required string? DetailJson { get; init; }

    public required long OccurredAtUtc { get; init; }
}

/// <summary>
///     One event to append. <see cref="DetailJson" /> is PLAINTEXT text; the store encodes it to UTF-8 and the save
///     interceptor seals it. <see cref="Sequence" /> is minted by the coordinator's event buffer and never by the
///     store.
/// </summary>
public sealed record IntegrationEventAppend
{
    public required Guid EventId { get; init; }

    public required Guid ExecutionId { get; init; }

    public required long Sequence { get; init; }

    public required string EventType { get; init; }

    public required string? DetailJson { get; init; }

    public required long OccurredAtUtc { get; init; }
}

/// <summary>
///     Everything one admission writes.
/// </summary>
/// <remarks>
///     <see cref="SessionId" /> names the execution's session in <b>both</b> cases: <c>NewSession.SessionId</c> on a fresh session, the existing row otherwise.
///     <see cref="NewSession" /> is a nested record rather than four loose nullable fields, so the type enforces the all-or-nothing shape. <see cref="PrincipalId" />
///     is what every ownership and uniqueness question keys on, carried once because it lands on both rows; <see cref="KeyPrefix" /> rides along for <b>audit only</b>.
///     An accept sets no invocation id, start/end stamp, cancel marker, failure field or output counter: the store writes their defaults, and
///     <c>UpdateStatusAsync</c> moves them on the non-terminal transitions, <c>TryTerminalizeAsync</c> on the terminal one.
/// </remarks>
public sealed record IntegrationAcceptCommand
{
    public required IntegrationSessionCreate? NewSession { get; init; }

    public required Guid ExecutionId { get; init; }

    public required Guid TriggerId { get; init; }

    public required Guid SessionId { get; init; }

    public required Guid PrincipalId { get; init; }

    public required Guid RequestId { get; init; }

    public required ReadOnlyMemory<byte> RequestFingerprint { get; init; }

    public required string KeyPrefix { get; init; }

    public required long ReceivedAtUtc { get; init; }

    public required IntegrationEventAppend AcceptedEvent { get; init; }
}

/// <summary>
///     One NON-TERMINAL status compare-and-swap. Every optional field is "leave it alone" when null — never "clear it".
/// </summary>
/// <remarks>
///     <see cref="NewStatus" /> is required, so a caller that only wants to stamp <see cref="StopRequestedAtUtc" /> on
///     a running row asks for the Running-to-Running self-move. That is deliberate, and is why there is no second
///     marker-only method: the cancel marker and the status CAS contend on the same <c>Version</c>.
/// </remarks>
public sealed class IntegrationExecutionStatusUpdate
{
    public required Guid ExecutionId { get; init; }

    public required long ExpectedVersion { get; init; }

    public required IReadOnlySet<IntegrationExecutionStatus> ExpectedStatuses { get; init; }

    public required IntegrationExecutionStatus NewStatus { get; init; }

    public long? StartedAtUtc { get; init; }

    public long? EndedAtUtc { get; init; }

    public Guid? InvocationId { get; init; }

    public long? StopRequestedAtUtc { get; init; }

    public string? FailureCategory { get; init; }

    public string? FailureSummary { get; init; }
}

/// <summary>
///     The one TERMINAL transition: the status CAS, the sequence already RESERVED on the coordinator's buffer for the
///     terminal event, and the terminal columns.
/// </summary>
/// <remarks>
///     The store mints the event's Guid id, stamps <c>OccurredAtUtc</c> from <c>EndedAtUtc</c> and derives the event's
///     small <c>DetailJson</c> from the two failure fields; it still mints no sequence. <see cref="EventType" /> is
///     one of the three terminal stream-event types.
/// </remarks>
public sealed record IntegrationTerminalizeCommand
{
    public required Guid ExecutionId { get; init; }

    public required long ExpectedVersion { get; init; }

    public required IReadOnlySet<IntegrationExecutionStatus> ExpectedStatuses { get; init; }

    public required IntegrationExecutionStatus NewStatus { get; init; }

    public required long Sequence { get; init; }

    public required string EventType { get; init; }

    public required long EndedAtUtc { get; init; }

    public required string? FailureCategory { get; init; }

    public required string? FailureSummary { get; init; }

    /// <summary>
    ///     The terminal EVENT's detail, when the caller built one. The same JSON it publishes on the stream event, so
    ///     the poll route and the stream hand a caller the same envelope. Null falls back to the failure columns.
    /// </summary>
    public string? EventDetailJson { get; init; }

    /// <summary>
    ///     The ONE kind-3 audit row this execution owes, written by whoever wins the terminal compare-and-swap.
    /// </summary>
    /// <remarks>
    ///     It goes in the SAME transaction as the status and the terminal event, because a separate save after the
    ///     terminal commits loses the audit row permanently on a database failure between the two: every later
    ///     terminalization rejects an already-terminal row, so nothing could ever write it. A null means the caller
    ///     audits nothing — the queue-full accept path, which never had an invocation to audit.
    /// </remarks>
    public IntegrationInvocationAuditInput? Audit { get; init; }
}

/// <summary>
///     Paged filter for the admin executions list. The paging fields are <see cref="Limit" /> and <see cref="Offset" />
///     and no other names; a null filter field means "do not constrain on it".
/// </summary>
/// <remarks>
///     <see cref="Status" /> is a SET rather than one value, so the operator's Active/History chips are one
///     server-side query instead of one query per status. An empty set is treated exactly like a null one — "do not
///     constrain" — because an empty set can only ever match nothing, which no caller means by it.
/// </remarks>
public sealed class IntegrationExecutionFilter
{
    public required Guid? TriggerId { get; init; }

    public required Guid? SessionId { get; init; }

    public required IReadOnlySet<IntegrationExecutionStatus>? Status { get; init; }

    public required int Limit { get; init; }

    public required int Offset { get; init; }
}

/// <summary>
///     Admission refused because the node-wide or the per-principal active cap was already full. Nothing is written
///     when it is thrown.
/// </summary>
/// <remarks>
///     Both caps throw this one type because both answer 503 with a <c>Retry-After</c>; telling them apart would be a
///     message change, not a second type.
/// </remarks>
public sealed class IntegrationQueueFullException : InvalidOperationException
{
    public IntegrationQueueFullException(string message) : base(message)
    {
    }
}

/// <summary>
///     A continuation named a session that cannot host it: no such row, another principal's row, or one that is no
///     longer <c>Active</c>. Nothing is written — the admission transaction is abandoned.
/// </summary>
public sealed class IntegrationSessionUnavailableException : Exception
{
    public IntegrationSessionUnavailableException(string message) : base(message)
    {
    }
}

/// <summary>
///     Persistence boundary for integration executions and their event feed.
/// </summary>
/// <remarks>
///     There is no <c>CreateAsync</c> — an execution row is only ever born accepted — and no <c>CountActiveAsync</c>:
///     the count is folded into <see cref="AcceptAsync" />, where it is a hard bound rather than a racy read. There is
///     no <c>FailNonTerminalAsync</c> either: a bulk <c>UPDATE</c> cannot write the per-row terminal events, so the
///     restart sweep enumerates with <see cref="ListAsync" /> and terminalises row by row.
/// </remarks>
public interface IIntegrationExecutionStore
{
    /// <summary>
    ///     Reserves admission and writes the durable accept in ONE <c>BEGIN IMMEDIATE</c> transaction.
    /// </summary>
    /// <remarks>
    ///     <see langword="false" /> means the credential was revoked between authentication and admission; nothing is
    ///     written and the caller answers its generic 401. <see cref="IntegrationQueueFullException" /> means a cap is
    ///     full (503 with a <c>Retry-After</c>), and <see cref="IntegrationSessionUnavailableException" /> is the
    ///     race-free backstop on a continuation, not where a caller learns which of the three it was. What the
    ///     transaction writes, and what the caller owes afterwards: wiki 08 ("The integration admission transaction").
    /// </remarks>
    Task<bool> AcceptAsync(IntegrationAcceptCommand command,
        int maxActive,
        int maxActivePerPrincipal,
        CancellationToken cancellationToken = default);

    Task<IntegrationExecutionSnapshot?> GetByIdAsync(Guid executionId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The dedup lookup, by BOTH columns: scoped this way a replay sees only its own principal's rows, which is
    ///     what stops one integrator denying a request id to another.
    /// </summary>
    Task<IntegrationExecutionSnapshot?> GetByRequestIdAsync(Guid principalId, Guid requestId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Ordered <c>ReceivedAtUtc</c> then <c>Id</c> descending before paging.
    /// </summary>
    /// <remarks>
    ///     The id tie-break is not decoration: <c>ReceivedAtUtc</c> is a millisecond stamp, and two accepts in the
    ///     same millisecond would otherwise page non-deterministically, dropping or duplicating a row across pages.
    /// </remarks>
    Task<IReadOnlyList<IntegrationExecutionSnapshot>> ListAsync(IntegrationExecutionFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    ///     How many rows <paramref name="filter" /> matches, IGNORING its <c>Limit</c> and <c>Offset</c> — the total a
    ///     pager needs to know how far the history reaches.
    /// </summary>
    /// <remarks>
    ///     Shares the filter-application code with <see cref="ListAsync" /> rather than repeating the predicates, so
    ///     the count and the page it labels cannot disagree about which rows are in scope.
    /// </remarks>
    Task<int> CountAsync(IntegrationExecutionFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    ///     How many of ONE session's executions are <c>Accepted</c>, <c>Queued</c> or <c>Running</c>.
    /// </summary>
    /// <remarks>
    ///     Deliberately not the node-wide count folded into <see cref="AcceptAsync" />: that one is a hard admission
    ///     bound, while this answers "is this caller-managed session busy right now" for the 409 that a second
    ///     concurrent invoke and an operator delete both need. It is a READ, so its caller holds the per-session gate
    ///     across it AND the mutation that follows — checking outside the gate guards nothing.
    /// </remarks>
    Task<int> CountActiveBySessionAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The session's currently <c>Running</c> execution, or <see langword="null" />.
    /// </summary>
    /// <remarks>
    ///     How <c>emit_output</c> turns the ambient conversation id into the execution its payload belongs to: a
    ///     session runs at most one execution at a time, and the returned snapshot carries the <c>OutputBytes</c>
    ///     counter the tool's aggregate pre-check reads fresh on every call.
    /// </remarks>
    Task<IntegrationExecutionSnapshot?> FindActiveBySessionAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     <see cref="AppendEventAsync" />'s insert plus the three things an <c>external.output</c> row must share its
    ///     transaction with: the PLAINTEXT byte check-and-reserve and the <c>OutputBytes</c>/<c>OutputCount</c> bumps.
    /// </summary>
    /// <remarks>
    ///     The caller passes the CAP, never a byte count: the store counts the UTF-8 bytes of the detail itself, so the
    ///     number checked and the number added agree by construction, and returns <see langword="false" /> with NOTHING
    ///     written when the total would exceed the cap. That refusal is the authoritative bound; the tool's own
    ///     pre-check only keeps a refusal out of the event buffer. Nothing here reads the detail column's length: it is
    ///     an encrypted BLOB, so summing it would measure ciphertext against a plaintext cap.
    /// </remarks>
    Task<bool> AppendOutputEventAsync(IntegrationEventAppend append, long maxOutputBytesPerExecution, CancellationToken cancellationToken = default);

    /// <summary>
    ///     One <c>SaveChanges</c>. Returns <see langword="false" /> WITHOUT writing when the row is missing, the
    ///     version is stale, or the current status is outside the command's expected set.
    /// </summary>
    /// <remarks>
    ///     <b>NON-TERMINAL transitions only.</b> <c>NewStatus</c> must be <c>Accepted</c>, <c>Queued</c> or
    ///     <c>Running</c>: ending a run also has to write the terminal event, and only
    ///     <see cref="TryTerminalizeAsync" /> does both in one transaction. Because there is no legal move out of a
    ///     terminal status, <c>ExpectedStatuses</c> never contains one either. The store polices no transition, so a
    ///     terminal status slipped through here silently reproduces the split write that method exists to remove.
    /// </remarks>
    Task<bool> UpdateStatusAsync(IntegrationExecutionStatusUpdate command, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Terminal status CAS + terminal event insert + both watermarks, in ONE transaction. Publish to the stream
    ///     only after this returns <see langword="true" />.
    /// </summary>
    /// <remarks>
    ///     <see langword="false" /> means the CAS lost — stale version, current status outside the expected set, or no
    ///     row — and NOTHING is written, not even the event. The failure fields are <b>assigned, not merged</b> here,
    ///     the opposite of <see cref="UpdateStatusAsync" />'s "null means leave alone" rule and deliberately so: a
    ///     terminal write is the final word on why a run ended, and a <c>Completed</c> command carrying two nulls must
    ///     not inherit a stale category from an earlier attempt.
    /// </remarks>
    Task<bool> TryTerminalizeAsync(IntegrationTerminalizeCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Appends one already-sequenced event and moves both watermarks.
    /// </summary>
    /// <remarks>
    ///     The execution's <c>LastSequence</c> moves as a running MAXIMUM: an output row and a coordinator row can
    ///     persist out of order, and a plain assignment would let the slower writer move it backwards. The owning
    ///     session's is a plain assignment, because sequences restart at 1 per execution and a maximum would freeze at
    ///     the deepest old stream — it is the activity indicator the UI renders, not an ordering key. A duplicate
    ///     <c>(ExecutionId, Sequence)</c> surfaces as <c>DbUpdateException</c>: a bug, not a race to swallow.
    /// </remarks>
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
