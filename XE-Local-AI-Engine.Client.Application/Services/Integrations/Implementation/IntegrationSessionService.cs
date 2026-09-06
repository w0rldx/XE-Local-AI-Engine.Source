namespace XE_Local_AI_Engine.Client.Services.Integrations.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Everything about an integration session that is not a write to its execution rows: the invocation gate that
///     decides whether a caller's <c>sessionId</c> may host the next execution, the operator's read and delete
///     surfaces, and the integrator's own principal-scoped status read.
///     <para>
///         <b>It persists no conversation and no seed.</b> The gate returns a DECISION; the accept path performs every
///         write, in ruling R4-1's order — the admission transaction commits first, and only then are the conversation
///         (new session only) and the seed written. There is no create path here, no compensating delete anywhere, and
///         no orphan sweep: because nothing exists before a durable execution row, an orphan conversation cannot.
///     </para>
///     <para>
///         A <c>public sealed class</c> with no interface, registered and injected as itself: one implementation, and
///         the endpoints that consume it live in another assembly that this one friends only for tests. Only the
///         invocation gate stays <c>internal</c>, because its single caller is the accept path in this assembly.
///     </para>
/// </summary>
public sealed class IntegrationSessionService
{
    /// <summary>The one message every masked case answers with, so no branch can make itself distinguishable.</summary>
    private const string SessionNotFoundMessage = "No such session.";

    private const string SessionClosedMessage = "That session is closed and accepts no further executions.";

    private const string SessionBusyMessage = "An execution is still running on that session. Cancel it before starting another.";

    private const string BusyDeleteMessage = "Cancel the session's execution before deleting it; one is still running.";

    private readonly IntegrationExternalAccess _access;
    private readonly IIntegrationExecutionStore _executions;
    private readonly IntegrationSessionGate _gate;
    private readonly ILogger<IntegrationSessionService> _logger;
    private readonly INodeChatPersistenceService _persistence;
    private readonly IIntegrationSessionStore _sessions;
    private readonly TimeProvider _timeProvider;
    private readonly IIntegrationTriggerStore _triggers;

    /// <summary>
    ///     INTERNAL, because the class is public (its endpoints live in another assembly) but takes the internal
    ///     per-session gate, which a public constructor cannot express. The DI module constructs it — it lives in this
    ///     assembly and can — rather than widening a collaborator that nothing outside this assembly should reach.
    /// </summary>
    internal IntegrationSessionService(IIntegrationSessionStore sessions,
        IIntegrationExecutionStore executions,
        IIntegrationTriggerStore triggers,
        IntegrationExternalAccess access,
        INodeChatPersistenceService persistence,
        IntegrationSessionGate gate,
        TimeProvider timeProvider,
        ILogger<IntegrationSessionService> logger)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _executions = executions ?? throw new ArgumentNullException(nameof(executions));
        _triggers = triggers ?? throw new ArgumentNullException(nameof(triggers));
        _access = access ?? throw new ArgumentNullException(nameof(access));
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    ///     Decides whether this invocation may proceed and, for a continuation, which session it joins. Never writes.
    ///     <para>
    ///         The caller holds the per-session gate around this call AND the accept transaction that follows it, so
    ///         the busy read below is inside the same critical section as the write it authorises.
    ///     </para>
    ///     <list type="table">
    ///         <item><c>PerInvocation</c> + no session id — a new session per invocation, unchanged.</item>
    ///         <item><c>PerInvocation</c> + a session id — 404: such a trigger has no addressable sessions, and
    ///         "unknown" is the same answer an unknown id gets.</item>
    ///         <item><c>CallerManaged</c> + no session id — a new session that stays Active after the run.</item>
    ///         <item><c>CallerManaged</c> + a session id — the gate below.</item>
    ///     </list>
    /// </summary>
    internal async Task<IntegrationSessionGateResult> ResolveForInvocationAsync(Guid? sessionId,
        IntegrationTriggerSnapshot trigger,
        IntegrationCallerIdentity caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(caller);

        if (trigger.SessionPolicy != IntegrationSessionPolicy.CallerManaged)
        {
            // A per-invocation trigger has no addressable sessions, so naming one is the same 404 an unknown id gets —
            // never a distinct code that would confirm the policy of a trigger the caller cannot otherwise inspect.
            return sessionId is null
                ? Accepted(existing: null)
                : await MaskAsync(sessionId.Value, cancellationToken).ConfigureAwait(false);
        }

        if (sessionId is not { } id)
        {
            return Accepted(existing: null);
        }

        // The first three masked cases are ONE call to the shared helper — principal ownership AND the current key's
        // trigger allowlist — because two routes composing the same rule separately is exactly how the execution
        // family lost its per-key allowlist. The helper re-reads the key row per request, so narrowing a key takes
        // effect on its next call.
        var access = await _access.ResolveSessionAsync(id, caller, cancellationToken).ConfigureAwait(false);
        if (access.Outcome != IntegrationAccessOutcome.Allowed || access.Session is not { } session)
        {
            return await MaskAsync(id, cancellationToken).ConfigureAwait(false);
        }

        // Another trigger's session is masked too: confirming it exists would let a caller enumerate sessions across
        // the triggers it can reach.
        if (session.TriggerId != trigger.Id)
        {
            return Masked;
        }

        if (session.Status != IntegrationSessionStatus.Active)
        {
            return new IntegrationSessionGateResult(IntegrationAcceptOutcome.SessionClosed, Existing: null, SessionClosedMessage);
        }

        // Inside the caller's gate, so no second accept can read this count and then write a second seed into the same
        // conversation while the first is still being written.
        var active = await _executions.CountActiveBySessionAsync(id, cancellationToken).ConfigureAwait(false);
        return active == 0
            ? Accepted(session)
            : new IntegrationSessionGateResult(IntegrationAcceptOutcome.SessionBusy, Existing: null, SessionBusyMessage);
    }

    /// <summary>One session for the operator, unscoped: an operator is not acting as an integrator.</summary>
    public async Task<IntegrationSessionDto?> GetAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var session = await _sessions.GetByIdAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return session is null ? null : await ToDtoAsync(session, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The operator's page, in the store's order. Nothing is re-sorted or filtered here.</summary>
    public async Task<IReadOnlyList<IntegrationSessionDto>> ListAsync(IntegrationSessionFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var sessions = await _sessions.ListAsync(filter.TriggerId, filter.Status, filter.Limit, filter.Offset, cancellationToken).ConfigureAwait(false);
        if (sessions.Count == 0)
        {
            return [];
        }

        // One read for every name rather than one per row: the trigger list is node-scoped and small, and a per-row
        // lookup would turn a page of 50 into 51 queries.
        var names = (await _triggers.ListAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(static trigger => trigger.Id, static trigger => trigger.Name);
        return [.. sessions.Select(session => ToDto(session, names.GetValueOrDefault(session.TriggerId, string.Empty)))];
    }

    /// <summary>The total the operator's pager labels a page with: the same filter, without its window.</summary>
    public Task<int> CountAsync(IntegrationSessionFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        return _sessions.CountAsync(filter.TriggerId, filter.Status, cancellationToken);
    }

    /// <summary>
    ///     The integrator's own read, and its ENTIRE authorisation decision is the shared helper: principal ownership
    ///     AND the current key's trigger allowlist. Returns <see langword="null" /> for every masked case — unknown,
    ///     foreign principal, allowlist-excluded — which the route maps to ONE 404. There is no unscoped read on this
    ///     path and no masking assembled endpoint-side, because separate <c>if</c>s in an endpoint are separate chances
    ///     to return a distinguishable body.
    /// </summary>
    public async Task<IntegrationSessionDto?> GetForExternalCallerAsync(Guid sessionId,
        IntegrationCallerIdentity caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var access = await _access.ResolveSessionAsync(sessionId, caller, cancellationToken).ConfigureAwait(false);
        return access.Outcome != IntegrationAccessOutcome.Allowed || access.Session is not { } session
            ? null
            : await ToDtoAsync(session, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Closes a session so nothing further may join it. Idempotent, and deliberately WITHOUT a busy refusal: its
    ///     only callers close a <c>PerInvocation</c> session whose execution has just terminalized, and the startup
    ///     sweep which closes sessions for rows it has already failed. Refusing there would leave such a session Active
    ///     forever. There is no operator close route — an operator deletes.
    /// </summary>
    public async Task<bool> CloseAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        using var lease = await _gate.EnterAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var closed = await _sessions.CloseAsync(sessionId, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken).ConfigureAwait(false);
        _gate.Forget(sessionId);
        return closed;
    }

    /// <summary>
    ///     Deletes a session by purging its OWNED CONVERSATION, which is the whole delete: the conversation footprint
    ///     purge takes the session row, its executions and their events with it, because a session's executions carry
    ///     conversation-derived content and that purge is the node's privacy single source of truth. Only the
    ///     content-free audit rows survive, and they do so because their <c>ConversationId</c> is null.
    ///     <para>
    ///         The busy check and the mutation are inside ONE critical section. Checking outside it would let a delete
    ///         that read "not busy" purge the conversation out from under an accept sitting between its own read and
    ///         the admission transaction.
    ///     </para>
    /// </summary>
    public async Task<IntegrationSessionDeleteOutcome> DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        using var lease = await _gate.EnterAsync(sessionId, cancellationToken).ConfigureAwait(false);

        var session = await _sessions.GetByIdAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            // Same reason as the invoke path: this call minted the entry, no row justifies keeping it, and the read
            // that proved absence is the one inside this section.
            _gate.Forget(sessionId);
            return IntegrationSessionDeleteOutcome.NotFound;
        }

        if (await _executions.CountActiveBySessionAsync(sessionId, cancellationToken).ConfigureAwait(false) > 0)
        {
            return IntegrationSessionDeleteOutcome.Busy;
        }

        await DeleteConversationAsync(session.ConversationId).ConfigureAwait(false);

        // The backstop, not the mechanism: the purge above already cascaded this row away, so this ordinarily deletes
        // nothing. It matters only when the purge could not run — an operator must not be left with a session whose
        // conversation is gone.
        _ = await _sessions.DeleteAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        _gate.Forget(sessionId);
        return IntegrationSessionDeleteOutcome.Deleted;
    }

    /// <summary>The sentence a busy delete answers with, kept beside the outcome it belongs to.</summary>
    public static string BusyMessage => BusyDeleteMessage;

    private static IntegrationSessionGateResult Accepted(IntegrationSessionSnapshot? existing) =>
        new(IntegrationAcceptOutcome.Accepted, existing, "Accepted.");

    /// <summary>Unknown, foreign-principal, allowlist-excluded and another trigger's session are ONE answer.</summary>
    private static IntegrationSessionGateResult Masked => new(IntegrationAcceptOutcome.SessionNotFound, Existing: null, SessionNotFoundMessage);

    /// <summary>
    ///     The masked answer, and the gate entry the accept path minted for an id with NO row behind it. Without this an
    ///     authenticated integrator looping invoke with random GUIDs adds one <c>SemaphoreSlim</c> per call, permanently
    ///     — the per-principal limiter bounds the rate, not the total.
    ///     <para>
    ///         ONLY when the row is absent, and the read that proves it runs inside the caller's own critical section.
    ///         Dropping the entry of a session that merely belongs to someone ELSE would let its owner's next accept
    ///         mint a second semaphore and enter while a first accept is still inside — which is the cross-request
    ///         contamination the gate exists to prevent.
    ///     </para>
    /// </summary>
    private async Task<IntegrationSessionGateResult> MaskAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (await _sessions.GetByIdAsync(sessionId, cancellationToken).ConfigureAwait(false) is null)
        {
            _gate.Forget(sessionId);
        }

        return Masked;
    }

    private async Task<IntegrationSessionDto> ToDtoAsync(IntegrationSessionSnapshot session, CancellationToken cancellationToken)
    {
        var trigger = await _triggers.GetByIdAsync(session.TriggerId, cancellationToken).ConfigureAwait(false);
        return ToDto(session, trigger?.Name ?? string.Empty);
    }

    private static IntegrationSessionDto ToDto(IntegrationSessionSnapshot session, string triggerName) =>
        new(session.Id,
            session.TriggerId,
            triggerName,
            session.PrincipalId,
            session.AgentDefinitionId,
            session.Status,
            session.CreatedAtUtc,
            session.LastActivityUtc,
            session.ExecutionCount);

    /// <summary>
    ///     Best effort, exactly as the work-session delete is: the rows are already gone or about to be, and a failed
    ///     purge is a warning rather than a refusal the operator cannot act on.
    /// </summary>
    private async Task DeleteConversationAsync(Guid conversationId)
    {
        try
        {
            _ = await _persistence.DeleteConversationAsync(new NodeChatDeleteConversationRequest(conversationId,
                                          _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                                          PurgeImmediately: true),
                                      CancellationToken.None)
                                  .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException)
        {
            _logger.LogWarning(exception, "Could not delete the conversation {ConversationId} an integration session owned.", conversationId);
        }
    }
}
