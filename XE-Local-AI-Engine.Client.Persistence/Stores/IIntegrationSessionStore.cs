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

    /// <summary>Closes a session so no further execution may join it. Idempotent; returns <see langword="false" /> only when no row matched.</summary>
    Task<bool> CloseAsync(Guid sessionId, long atUtc, CancellationToken cancellationToken = default);
}
