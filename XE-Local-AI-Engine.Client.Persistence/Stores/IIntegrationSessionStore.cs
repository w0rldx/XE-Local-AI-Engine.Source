namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>A session as a reader sees it. Every value is plaintext structural; the transcript lives in the owned conversation.</summary>
public sealed record IntegrationSessionSnapshot(
    Guid Id,
    Guid TriggerId,
    Guid PrincipalId,
    Guid ConversationId,
    Guid AgentDefinitionId,
    IntegrationSessionStatus Status,
    long CreatedAtUtc,
    long LastActivityUtc,
    int ExecutionCount,
    long LastSequence);

/// <summary>
///     The new session an accept creates, or <see langword="null" /> on the command when the accept continues an
///     existing one. <see cref="ConversationId" /> is <b>pre-minted</b>: the caller generates the Guid before calling
///     <c>AcceptAsync</c> and creates the owning <c>NodeConversation</c> at that id only AFTER the accept transaction
///     commits. The session row therefore carries a conversation id with no conversation row behind it for the width of
///     that gap, on purpose — which is what makes an orphan conversation impossible.
/// </summary>
public sealed record IntegrationSessionCreate(
    Guid SessionId,
    Guid TriggerId,
    Guid ConversationId,
    Guid AgentDefinitionId);

/// <summary>
///     Persistence boundary for integration sessions.
///     <para>
///         <b>There is no <c>CreateAsync</c> and no <c>TouchAsync</c>, by ruling, not by omission.</b>
///         <c>IIntegrationExecutionStore.AcceptAsync</c> is the only path that inserts a session or bumps its counters,
///         because a second insert path would be a second admission gate. The session watermark already has writers:
///         <c>AppendEventAsync</c> moves it on every persisted event, and <c>TryTerminalizeAsync</c> on the terminal
///         one.
///     </para>
/// </summary>
public interface IIntegrationSessionStore
{
    Task<IntegrationSessionSnapshot?> GetByIdAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The session, but only when <paramref name="principalId" /> owns it. Returns <see langword="null" /> for a
    ///     missing row AND for a foreign one, so the two are indistinguishable to every caller — the masking rule is
    ///     the shape of the return, not an <c>if</c> a route has to remember.
    ///     <para>
    ///         Ownership is the row's own <c>PrincipalId</c> column, never a key prefix: two credentials of one
    ///         integrator must reach the same sessions, so a credential rotation does not strand them. The CURRENT
    ///         key's trigger allowlist is a second, separate limb applied by <c>IntegrationExternalAccess</c> — an
    ///         authorisation decision does not belong in a persistence method.
    ///     </para>
    /// </summary>
    Task<IntegrationSessionSnapshot?> GetForPrincipalAsync(Guid sessionId, Guid principalId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The session that owns <paramref name="conversationId" />, or <see langword="null" />. The lookup behind
    ///     <c>emit_output</c>: a tool handler has only the ambient conversation id the invocation runner seeded, and
    ///     this is what turns it back into the execution's session. At most one row — a conversation is owned by one
    ///     session.
    /// </summary>
    Task<IntegrationSessionSnapshot?> FindByConversationAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     A page of sessions, ordered <c>LastActivityUtc</c> then <c>Id</c> DESCENDING before <c>Skip</c>/<c>Take</c>.
    ///     The id tie-break is load-bearing for the same reason it is on the executions list: two sessions touched in
    ///     the same millisecond would otherwise page non-deterministically and drop or duplicate a row across pages. A
    ///     null filter argument does not constrain.
    /// </summary>
    Task<IReadOnlyList<IntegrationSessionSnapshot>> ListAsync(Guid? triggerId,
        IntegrationSessionStatus? status,
        int limit,
        int offset,
        CancellationToken cancellationToken = default);

    /// <summary>Closes a session so no further execution may join it. Idempotent; returns <see langword="false" /> only when no row matched.</summary>
    Task<bool> CloseAsync(Guid sessionId, long atUtc, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Removes the session row. This is the BACKSTOP of an operator delete, not its mechanism: deleting a session
    ///     means purging its owned conversation, and <c>ConversationFootprintPurge</c> already takes the session row,
    ///     its executions and their events with it. This runs afterwards so a purge that could not complete still
    ///     removes the row, rather than leaving an operator looking at a session whose conversation is gone. Returns
    ///     <see langword="false" /> when the purge already removed it, which is the ordinary case.
    /// </summary>
    Task<bool> DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
