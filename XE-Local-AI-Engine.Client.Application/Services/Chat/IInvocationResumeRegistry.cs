namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     Tracks live invocations by <see cref="InvocationState.InvocationId" /> so a client that reconnects with a
///     NEW SignalR connection id can re-attach to a still-running invocation and resume the stream.
/// </summary>
/// <remarks>
///     This is the live-stream resume target. It is NOT <c>NodeChatMigrationRecoveryService</c> (which only
///     clears the <c>__EFMigrationsLock</c>) and NOT <c>NodeChatRestartRecoveryService</c> (which terminalizes
///     dangling interrupted rows after a process restart). A registry entry exists only while the invocation is
///     in a non-terminal state (<see cref="InvocationStatus.Assigned" />/<see cref="InvocationStatus.Running" />);
///     it is removed on terminal status. After a process restart the registry is empty by design, so the client
///     falls back to re-fetching the persisted (already-terminalized) conversation.
/// </remarks>
public interface IInvocationResumeRegistry
{
    /// <summary>
    ///     Returns the latest snapshot for a still-live invocation, or <see langword="null" /> when the invocation
    ///     is unknown or has already reached a terminal state.
    /// </summary>
    InvocationState? TryGetLiveInvocation(Guid invocationId);

    /// <summary>
    ///     Re-attaches a fresh stream consumer to a live invocation. The first event replays the content
    ///     accumulated so far (a snapshot delta), after which live deltas and the terminal event are emitted in
    ///     order until the invocation completes. Throws <see cref="InvalidOperationException" /> when the
    ///     invocation is unknown or already terminal (the caller falls back to re-fetching the persisted
    ///     conversation).
    /// </summary>
    IAsyncEnumerable<ChatStreamEvent> ResumeAsync(Guid invocationId, CancellationToken cancellationToken = default);
}
