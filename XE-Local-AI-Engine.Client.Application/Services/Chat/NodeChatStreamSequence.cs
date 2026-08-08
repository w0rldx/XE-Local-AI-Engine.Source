namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Monotonic, thread-safe sequence source for a single chat stream. The local front door emits ordered
///     <see cref="ChatStreamEvent" />s from two concurrent producers — the streaming-transition emitted by
///     RunInvocationAsync and the deltas/terminal emitted by the pump — so the sequence must be shared and atomic
///     to keep client-side ordering correct.
/// </summary>
public sealed class NodeChatStreamSequence
{
    private long _next = -1;

    /// <summary>Returns the next sequence number, starting at 0.</summary>
    public long Next()
    {
        return Interlocked.Increment(ref _next);
    }
}
