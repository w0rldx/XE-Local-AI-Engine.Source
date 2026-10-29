namespace XE_Local_AI_Engine.Client.Services.Chat;

using System.Diagnostics.CodeAnalysis;
using XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     Tracks live invocations by <see cref="InvocationState.InvocationId" /> so a client that reconnects with a
///     NEW SignalR connection id can re-attach to a still-running invocation and resume the stream.
/// </summary>
/// <remarks>
///     This is the live-stream resume target, neither <c>NodeChatMigrationRecoveryService</c>, which only clears the
///     <c>__EFMigrationsLock</c>, nor <c>NodeChatRestartRecoveryService</c>, which terminalizes dangling rows after a
///     restart. An entry exists only while the invocation is <see cref="InvocationStatus.Assigned" /> or
///     <see cref="InvocationStatus.Running" /> and is removed on a terminal status. After a restart the registry is
///     empty by design, so the client refetches the already-terminalized conversation.
/// </remarks>
public interface IInvocationResumeRegistry
{
    /// <summary>
    ///     Gets the latest snapshot for a still-live invocation; <see langword="false" /> when the invocation is unknown
    ///     or has already reached a terminal state.
    /// </summary>
    bool TryGetLiveInvocation(Guid invocationId, [NotNullWhen(true)] out InvocationState? state);

    /// <summary>
    ///     Gets the id of the still-live invocation for <paramref name="conversationId" />; <see langword="false" />
    ///     when that conversation has no running turn.
    /// </summary>
    /// <remarks>
    ///     This is the COLD-LOAD re-attach, a different entry point from the reconnect one: a reconnecting client
    ///     still holds the invocation id and calls <see cref="ResumeAsync" /> directly, while a client that reloaded
    ///     holds nothing and the pending-prompt state it needs is never persisted to the conversation's parts.
    ///     Without this lookup such a client loses an in-flight question and the run parks until it times out. At most
    ///     one invocation is live on this node, so the scan is over a one-or-zero-element map.
    /// </remarks>
    bool TryGetLiveInvocationIdForConversation(Guid conversationId, out Guid invocationId);

    /// <summary>
    ///     Re-attaches a fresh stream consumer to a live invocation, replaying the content accumulated so far before
    ///     live deltas and the terminal event.
    /// </summary>
    /// <remarks>
    ///     It throws <see cref="InvalidOperationException" /> when the invocation is unknown or already terminal, and
    ///     the caller then refetches the persisted conversation.
    /// </remarks>
    IAsyncEnumerable<ChatStreamEvent> ResumeAsync(Guid invocationId, CancellationToken cancellationToken = default);
}
