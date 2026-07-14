namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     The authoritative message-status transition table. Each writer intent (cancel, partial flush, terminalize, and
///     restart/replay recovery) declares the set of source statuses it may transition FROM. Every correlated UPDATE
///     enforces its set atomically inside the SQL predicate, so a write from a disallowed source status is an atomic
///     no-op rather than a read-then-write race. Terminal rows (completed / failed / interrupted) are never a legal
///     source, with a single deliberate exception: the pump's authoritative terminalize may supersede a Cancelled row
///     (see <see cref="TerminalizeSources" />).
/// </summary>
public static class NodeChatMessageTransitions
{
    // The non-terminal lifecycle states; identical to the cancellable set. A cancel, a partial flush, and restart
    // recovery may all fire only from these — none may mutate a row that has already reached a terminal status.
    private static IReadOnlySet<string> NonTerminal => NodeChatMessageStatusValues.Cancellable;

    // The non-terminal states plus Cancelled: the source set for the pump's true-outcome terminals. Cancelled is
    // whitelisted so an authoritative completion/failure supersedes an optimistic HTTP-cancel marker, and a cancel
    // terminalize over an already-cancelled row is the idempotent final-content write.
    private static readonly IReadOnlySet<string> NonTerminalOrCancelled = new HashSet<string>(StringComparer.Ordinal)
    {
        NodeChatMessageStatusValues.Pending,
        NodeChatMessageStatusValues.Queued,
        NodeChatMessageStatusValues.Streaming,
        NodeChatMessageStatusValues.Cancelled
    };

    // Streaming's legitimate predecessors: Queued on the local send/regen path (pending -> queued -> streaming) and
    // Pending on the platform path (the worker coordinator marks streaming straight off the pending placeholder, with no
    // queued step). Terminal rows are excluded, so a stream can never resurrect a cancelled/finished message.
    private static readonly IReadOnlySet<string> PendingOrQueued = new HashSet<string>(StringComparer.Ordinal)
    {
        NodeChatMessageStatusValues.Pending,
        NodeChatMessageStatusValues.Queued
    };

    private static readonly IReadOnlySet<string> PendingOnly = new HashSet<string>(StringComparer.Ordinal)
    {
        NodeChatMessageStatusValues.Pending
    };

    /// <summary>Source statuses a cancel may fire from: the non-terminal lifecycle states only.</summary>
    public static IReadOnlySet<string> CancelSources => NonTerminal;

    /// <summary>Source statuses the queued mark may fire from: only its Pending predecessor, so a cancel that raced ahead of the run cannot be overwritten back to Queued.</summary>
    public static IReadOnlySet<string> QueuedSources => PendingOnly;

    /// <summary>Source statuses the streaming mark may fire from: Pending (platform path) or Queued (local send/regen path); never a terminal row.</summary>
    public static IReadOnlySet<string> StreamingSources => PendingOrQueued;

    /// <summary>Source statuses a partial/delta flush may fire from: non-terminal only. A late flush against a terminal row is rejected.</summary>
    public static IReadOnlySet<string> FlushSources => NonTerminal;

    /// <summary>Source statuses restart/replay recovery (interruption) may fire from: non-terminal only, so it can never downgrade a terminal row.</summary>
    public static IReadOnlySet<string> RecoverySources => NonTerminal;

    /// <summary>
    ///     Source statuses the pump's authoritative terminalize may fire from when writing <paramref name="terminalStatus" />.
    ///     Interrupted (stream loss) may only overwrite a non-terminal row, so it can never downgrade a user Cancelled. The
    ///     true-outcome terminals (completed / failed / cancelled) additionally whitelist Cancelled as a source: the pump
    ///     derives its status from the real run outcome and emits the single SSE terminal, so an authoritative completion is
    ///     allowed to supersede an optimistic HTTP-cancel marker. Every terminal source other than that whitelisted
    ///     Cancelled (i.e. completed / failed / interrupted) is rejected.
    /// </summary>
    public static IReadOnlySet<string> TerminalizeSources(string terminalStatus)
    {
        ArgumentNullException.ThrowIfNull(terminalStatus);
        return string.Equals(terminalStatus, NodeChatMessageStatusValues.Interrupted, StringComparison.Ordinal)
            ? NonTerminal
            : NonTerminalOrCancelled;
    }
}
