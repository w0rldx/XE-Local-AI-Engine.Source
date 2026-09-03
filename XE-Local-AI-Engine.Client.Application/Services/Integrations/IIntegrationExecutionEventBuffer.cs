namespace XE_Local_AI_Engine.Client.Services.Integrations;

using System.Text.Json;

/// <summary>
///     The per-execution in-memory event ring, and <b>the only thing in this feature that mints a
///     <see cref="IntegrationStreamEvent.Sequence" /></b>. Nothing else — not a store, not the accept path, not the
///     coordinator — may compute one: two counters cannot coexist without duplicating sequence numbers and breaking
///     <c>Last-Event-ID</c> replay.
///     <para>
///         <b>Append versus Reserve/Publish.</b> <see cref="Append" /> mints and publishes in one step, for events a
///         caller can lose without harm (the phase boundaries and tool lifecycle). The durable-before-visible events —
///         the three terminal ones, and <c>external.output</c> — instead <see cref="Reserve" /> a sequence, commit the
///         row with it, and only then <see cref="Publish" />, so an external caller never acts on an event the
///         database does not hold. A commit that fails calls <see cref="Abandon" />.
///     </para>
///     <para>
///         <b>Holes are legal; stalls are not.</b> An abandoned reservation leaves a gap, and readers must tolerate one
///         — <c>Last-Event-ID</c> is a watermark, not a contiguity claim. What a reader must NOT do is run past an
///         UNRESOLVED reservation: <see cref="LowestPendingReservation" /> is the barrier, because a sequence published
///         late would otherwise fall below the reader's cursor and be lost to every live consumer forever.
///     </para>
/// </summary>
public interface IIntegrationExecutionEventBuffer
{
    /// <summary>
    ///     Reserves a tracked entry. <paramref name="initialSequence" /> seeds the counter: 0 for a fresh accept, so
    ///     the first <see cref="Append" /> mints 1, and the row's <c>LastSequence</c> for the startup sweep, so a
    ///     recovered execution's terminal event continues its own numbering. Returns <see langword="false" /> only when
    ///     the tracked-execution cap is full of entries that cannot be evicted, which the caller answers 503.
    ///     Idempotent: an id that is already tracked returns <see langword="true" /> and keeps its counter.
    /// </summary>
    bool TryCreate(Guid executionId, long initialSequence = 0);

    /// <summary>Drops an entry — the accept path releases its reservation this way when admission fails.</summary>
    void Remove(Guid executionId);

    /// <summary>
    ///     Mints the next sequence, stores the event and wakes every reader. Throws
    ///     <see cref="InvalidOperationException" /> for an untracked id: silently creating an entry would restart the
    ///     counter at 1 and collide with sequences already persisted for that execution.
    /// </summary>
    IntegrationStreamEvent Append(Guid executionId, Guid sessionId, string type, string? contentType, JsonElement? payload);

    /// <summary>
    ///     Mints a sequence and publishes NOTHING. The caller commits the event with this number and then calls
    ///     <see cref="Publish" />, or <see cref="Abandon" /> if the commit failed. Throws for an untracked id.
    /// </summary>
    long Reserve(Guid executionId);

    /// <summary>
    ///     Makes a reserved event readable, in sequence order — a concurrent <see cref="Append" /> may have minted a
    ///     higher number while the commit was in flight. Does not advance the mint watermark. Throws when the sequence
    ///     was never reserved, was reserved for another execution, or was already published.
    /// </summary>
    void Publish(IntegrationStreamEvent streamEvent);

    /// <summary>
    ///     Resolves a reservation whose commit failed. The sequence is never published and the hole stays; readers
    ///     proceed past it. Throws when the sequence was never reserved or is already resolved.
    /// </summary>
    void Abandon(Guid executionId, long sequence);

    /// <summary>
    ///     The lowest sequence reserved but neither published nor abandoned, or <see cref="long.MaxValue" /> when there
    ///     is none and for an untracked id — the sentinel reads as "no barrier", so no caller special-cases the empty
    ///     set. A reader never yields a sequence at or above this value.
    /// </summary>
    long LowestPendingReservation(Guid executionId);

    /// <summary>
    ///     Whether an entry exists at all. Not redundant with the <c>0</c> sentinels: an untracked id and a tracked one
    ///     whose caller asked from sequence 0 look identical on the numbers alone, and the stream writer has to tell a
    ///     404 from a 410 before it writes a status line.
    /// </summary>
    bool IsTracked(Guid executionId);

    /// <summary>The highest sequence MINTED — which may not be readable yet, because a reservation can be outstanding. 0 when untracked.</summary>
    long LastSequence(Guid executionId);

    /// <summary>The oldest sequence still retained, which trimming moves. 0 when untracked or when nothing is retained.</summary>
    long Floor(Guid executionId);

    /// <summary>
    ///     Replays everything above <paramref name="sinceSequence" /> and then stays live, ending when it yields a
    ///     terminal event. <paramref name="sinceSequence" /> is EXCLUSIVE and is a watermark, not a counter: resuming
    ///     at a hole's own sequence is as correct as resuming at a published one.
    ///     <para>
    ///         Selection is by comparison only — <c>Sequence &gt; cursor</c>, bounded above by
    ///         <see cref="LowestPendingReservation" /> — so a hole is skipped silently and a pending reservation is a
    ///         WAIT. Throws <see cref="IntegrationEventGapException" /> for exactly three positions: below the retained
    ///         floor, above the minted head, and an untracked execution.
    ///     </para>
    /// </summary>
    IAsyncEnumerable<IntegrationStreamEvent> ReadAsync(Guid executionId, long sinceSequence, CancellationToken cancellationToken = default);
}

/// <summary>
///     A reader asked for a position the ring cannot serve: below what it still retains, above what it has minted, or
///     on an execution it no longer tracks at all. Never raised for a HOLE — an abandoned reservation is a legal skip —
///     and never for a PENDING one, which is a wait.
///     <para>
///         Raised before the first yield, it is the stream writer's 410. Raised mid-enumeration, the writer ends the
///         response cleanly: the status is already on the wire and the caller recovers by re-attaching, which then
///         answers 410 with the poll route.
///     </para>
/// </summary>
public sealed class IntegrationEventGapException : Exception
{
    public IntegrationEventGapException(Guid executionId, long sinceSequence)
        : base($"Integration execution {executionId} cannot be replayed from sequence {sinceSequence}.")
    {
        ExecutionId = executionId;
        SinceSequence = sinceSequence;
    }

    public IntegrationEventGapException()
    {
    }

    public IntegrationEventGapException(string message)
        : base(message)
    {
    }

    public IntegrationEventGapException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public Guid ExecutionId { get; }

    public long SinceSequence { get; }
}
