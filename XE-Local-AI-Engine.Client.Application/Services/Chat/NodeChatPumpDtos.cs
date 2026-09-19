namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>The streamed content/reasoning persisted so far for one invocation. Drives delta diffing.</summary>
public readonly record struct NodeChatPumpCursor(string Content, string Reasoning)
{
    public static NodeChatPumpCursor Empty { get; } = new(string.Empty, string.Empty);
}

/// <summary>
///     Outcome of <c>NodeChatInvocationPump.FlushDeltaAsync</c>. When <see cref="Persisted" /> is null no
///     delta advanced and <see cref="Cursor" /> is unchanged.
/// </summary>
public sealed class NodeChatPumpFlushResult
{
    public required NodeChatPumpCursor Cursor { get; init; }

    public required NodeChatPersistedMessageDto? Persisted { get; init; }

    public required string? ContentDelta { get; init; }

    public required string? ReasoningDelta { get; init; }
}

/// <summary>Outcome of a terminalize call: the persisted terminal message plus the resolved status/event type.</summary>
public sealed class NodeChatPumpTerminalResult
{
    public required NodeChatPersistedMessageDto Persisted { get; init; }

    public required string TerminalStatus { get; init; }

    public required string EventType { get; init; }
}
