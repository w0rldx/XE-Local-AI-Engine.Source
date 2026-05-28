namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>The streamed content/reasoning persisted so far for one invocation. Drives delta diffing.</summary>
public readonly record struct NodeChatPumpCursor(string Content, string Reasoning)
{
    public static NodeChatPumpCursor Empty { get; } = new(string.Empty, string.Empty);
}

/// <summary>
/// Outcome of <c>NodeChatInvocationPump.FlushDeltaAsync</c>. When <see cref="Persisted"/> is null no
/// delta advanced and <see cref="Cursor"/> is unchanged.
/// </summary>
public sealed record NodeChatPumpFlushResult(
    NodeChatPumpCursor Cursor,
    NodeChatPersistedMessageDto? Persisted,
    string? ContentDelta,
    string? ReasoningDelta);

/// <summary>Outcome of a terminalize call: the persisted terminal message plus the resolved status/event type.</summary>
public sealed record NodeChatPumpTerminalResult(
    NodeChatPersistedMessageDto Persisted,
    string TerminalStatus,
    string EventType);
