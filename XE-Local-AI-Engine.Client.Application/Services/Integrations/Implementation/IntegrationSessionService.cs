namespace XE_Local_AI_Engine.Client.Services.Integrations.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Everything about an integration session that is not a write to its execution rows: the invocation gate, the
///     operator's read and delete surfaces, and the integrator's own principal-scoped status read.
/// </summary>
/// <remarks>
///     It persists NO conversation and no seed: the gate returns a DECISION, and the accept path performs every write in
///     ruling R4-1's order. There is no create path here, no compensating delete and no orphan sweep — nothing exists
///     before a durable execution row, so an orphan conversation cannot. A <c>public sealed class</c> with no interface,
///     registered and injected as itself, because the endpoints that consume it live in another assembly; only the
///     invocation gate stays <c>internal</c>, its single caller being the accept path in this assembly.
/// </remarks>
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
    ///     INTERNAL, because this public class takes the internal per-session gate, which a public constructor cannot
    ///     express.
    /// </summary>
    /// <remarks>
    ///     The DI module constructs it — it lives in this assembly and can — rather than widening a collaborator that
    ///     nothing outside this assembly should reach.
    /// </remarks>
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
    /// </summary>
    /// <remarks>
    ///     The caller holds the per-session gate around this call AND the accept transaction that follows it, so the busy
    ///     read below is inside the same critical section as the write it authorises. <c>PerInvocation</c> takes a new
    ///     session per invocation and answers a named one with the masked 404 — such a trigger has no addressable
    ///     sessions, and "unknown" is the answer an unknown id gets; <c>CallerManaged</c> takes a new session that stays
    ///     Active after the run, or joins the named one through the gate below.
    /// </remarks>
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
                : await MaskAsync(sessionId.Value, cancellationToken);
        }

        if (sessionId is not { } id)
        {
            return Accepted(existing: null);
        }

        // The first three masked cases are ONE call to the shared helper — principal ownership AND the current key's trigger allowlist — because two routes
        // composing the same rule separately is how the execution family lost its per-key allowlist. The helper re-reads the key row on every request.
        var access = await _access.ResolveSessionAsync(id, caller, cancellationToken);
        if (access.Outcome != IntegrationAccessOutcome.Allowed || access.Session is not { } session)
        {
            return await MaskAsync(id, cancellationToken);
        }

        // Another trigger's session is masked too: confirming it exists would let a caller enumerate sessions across
        // the triggers it can reach.
        if (session.TriggerId != trigger.Id)
        {
            return Masked;
        }

        if (session.Status != IntegrationSessionStatus.Active)
        {
            return new IntegrationSessionGateResult
            {
                Outcome = IntegrationAcceptOutcome.SessionClosed,
                Existing = null,
                Message = SessionClosedMessage
            };
        }

        // Inside the caller's gate, so no second accept can read this count and then write a second seed into the same
        // conversation while the first is still being written.
        var active = await _executions.CountActiveBySessionAsync(id, cancellationToken);
        return active == 0
            ? Accepted(session)
            : new IntegrationSessionGateResult
            {
                Outcome = IntegrationAcceptOutcome.SessionBusy,
                Existing = null,
                Message = SessionBusyMessage
            };
    }

    /// <summary>One session for the operator, unscoped: an operator is not acting as an integrator.</summary>
    public async Task<IntegrationSessionDto?> GetAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var session = await _sessions.GetByIdAsync(sessionId, cancellationToken);
        return session is null ? null : await ToDtoAsync(session, cancellationToken);
    }

    /// <summary>The operator's page, in the store's order. Nothing is re-sorted or filtered here.</summary>
    public async Task<IReadOnlyList<IntegrationSessionDto>> ListAsync(IntegrationSessionFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var sessions = await _sessions.ListAsync(filter.TriggerId, filter.Status, filter.Limit, filter.Offset, cancellationToken);
        if (sessions.Count == 0)
        {
            return [];
        }

        // One read for every name rather than one per row: the trigger list is node-scoped and small, and a per-row
        // lookup would turn a page of 50 into 51 queries.
        var names = (await _triggers.ListAsync(cancellationToken)).ToDictionary(static trigger => trigger.Id, static trigger => trigger.Name);
        return [.. sessions.Select(session => ToDto(session, names.GetValueOrDefault(session.TriggerId, string.Empty)))];
    }

    /// <summary>The total the operator's pager labels a page with: the same filter, without its window.</summary>
    public Task<int> CountAsync(IntegrationSessionFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        return _sessions.CountAsync(filter.TriggerId, filter.Status, cancellationToken);
    }

    /// <summary>
    ///     The integrator's own read, whose ENTIRE authorisation decision is the shared helper: principal ownership AND
    ///     the current key's trigger allowlist.
    /// </summary>
    /// <remarks>
    ///     Returns <see langword="null" /> for every masked case — unknown, foreign principal, allowlist-excluded —
    ///     which the route maps to ONE 404. There is no unscoped read on this path and no masking assembled
    ///     endpoint-side: separate <c>if</c>s in an endpoint are separate chances to return a distinguishable body.
    /// </remarks>
    public async Task<IntegrationSessionDto?> GetForExternalCallerAsync(Guid sessionId,
        IntegrationCallerIdentity caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var access = await _access.ResolveSessionAsync(sessionId, caller, cancellationToken);
        return access.Outcome != IntegrationAccessOutcome.Allowed || access.Session is not { } session
            ? null
            : await ToDtoAsync(session, cancellationToken);
    }

    /// <summary>
    ///     Closes a session so nothing further may join it. Idempotent, and deliberately WITHOUT a busy refusal.
    /// </summary>
    /// <remarks>
    ///     Its only callers close a <c>PerInvocation</c> session whose execution has just terminalized, and the startup
    ///     sweep closing sessions for rows it has already failed; refusing there would leave such a session Active
    ///     forever. There is no operator close route — an operator deletes.
    /// </remarks>
    public async Task<bool> CloseAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        using var lease = await _gate.EnterAsync(sessionId, cancellationToken);
        var closed = await _sessions.CloseAsync(sessionId, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken);
        _gate.Forget(sessionId);
        return closed;
    }

    /// <summary>
    ///     Deletes a session by purging its OWNED CONVERSATION, which is the whole delete: that purge takes the session
    ///     row, its executions and their events with it.
    /// </summary>
    /// <remarks>
    ///     A session's executions carry conversation-derived content, and the conversation footprint purge is the node's
    ///     privacy single source of truth; only the content-free audit rows survive, because their
    ///     <c>ConversationId</c> is null. The busy check and the mutation sit inside ONE critical section: checking
    ///     outside it would let a delete that read "not busy" purge the conversation out from under an accept sitting
    ///     between its own read and the admission transaction.
    /// </remarks>
    public async Task<IntegrationSessionDeleteOutcome> DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        using var lease = await _gate.EnterAsync(sessionId, cancellationToken);

        var session = await _sessions.GetByIdAsync(sessionId, cancellationToken);
        if (session is null)
        {
            // Same reason as the invoke path: this call minted the entry, no row justifies keeping it, and the read
            // that proved absence is the one inside this section.
            _gate.Forget(sessionId);
            return IntegrationSessionDeleteOutcome.NotFound;
        }

        if (await _executions.CountActiveBySessionAsync(sessionId, cancellationToken) > 0)
        {
            return IntegrationSessionDeleteOutcome.Busy;
        }

        await DeleteConversationAsync(session.ConversationId);

        // The backstop, not the mechanism: the purge above already cascaded this row away, so this ordinarily deletes nothing. It matters only when the
        // purge could not run — an operator must not be left with a session whose conversation is gone.
        _ = await _sessions.DeleteAsync(sessionId, CancellationToken.None);
        _gate.Forget(sessionId);
        return IntegrationSessionDeleteOutcome.Deleted;
    }

    /// <summary>The sentence a busy delete answers with, kept beside the outcome it belongs to.</summary>
    public static string BusyMessage => BusyDeleteMessage;

    private static IntegrationSessionGateResult Accepted(IntegrationSessionSnapshot? existing) =>
        new()
        {
            Outcome = IntegrationAcceptOutcome.Accepted,
            Existing = existing,
            Message = "Accepted."
        };

    /// <summary>Unknown, foreign-principal, allowlist-excluded and another trigger's session are ONE answer.</summary>
    private static IntegrationSessionGateResult Masked =>
        new()
        {
            Outcome = IntegrationAcceptOutcome.SessionNotFound,
            Existing = null,
            Message = SessionNotFoundMessage
        };

    /// <summary>
    ///     The masked answer, plus the removal of a gate entry the accept path minted for an id with NO row behind it.
    /// </summary>
    /// <remarks>
    ///     Without it an authenticated integrator looping invoke with random GUIDs adds one <c>SemaphoreSlim</c> per
    ///     call, permanently — the per-principal limiter bounds the rate, not the total. ONLY when the row is absent,
    ///     proven by a read inside the caller's own critical section: dropping the entry of a session that belongs to
    ///     someone ELSE would let its owner's next accept mint a second semaphore and enter while a first accept is
    ///     still inside, which is the cross-request contamination the gate exists to prevent.
    /// </remarks>
    private async Task<IntegrationSessionGateResult> MaskAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (await _sessions.GetByIdAsync(sessionId, cancellationToken) is null)
        {
            _gate.Forget(sessionId);
        }

        return Masked;
    }

    private async Task<IntegrationSessionDto> ToDtoAsync(IntegrationSessionSnapshot session, CancellationToken cancellationToken)
    {
        var trigger = await _triggers.GetByIdAsync(session.TriggerId, cancellationToken);
        return ToDto(session, trigger?.Name ?? string.Empty);
    }

    private static IntegrationSessionDto ToDto(IntegrationSessionSnapshot session, string triggerName) =>
        new()
        {
            Id = session.Id,
            TriggerId = session.TriggerId,
            TriggerName = triggerName,
            PrincipalId = session.PrincipalId,
            AgentDefinitionId = session.AgentDefinitionId,
            Status = session.Status,
            CreatedAtUtc = session.CreatedAtUtc,
            LastActivityUtc = session.LastActivityUtc,
            ExecutionCount = session.ExecutionCount
        };

    /// <summary>
    ///     Best effort, exactly as the work-session delete is: the rows are already gone or about to be, and a failed
    ///     purge is a warning rather than a refusal the operator cannot act on.
    /// </summary>
    private async Task DeleteConversationAsync(Guid conversationId)
    {
        try
        {
            _ = await _persistence.DeleteConversationAsync(new NodeChatDeleteConversationRequest
                {
                    ConversationId = conversationId,
                    DeletedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                    PurgeImmediately = true
                },
                CancellationToken.None);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException)
        {
            _logger.LogWarning(exception, "Could not delete the conversation {ConversationId} an integration session owned.", conversationId);
        }
    }
}
